using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    public Task<IReadOnlyList<RunRecord>> GetMapRecords(string mapName, int limit = IRequestManager.DefaultRecordLimit)
        => QueryBestRecordsByMapAsync(mapName,
                                      limit,
                                      RunType.Main,
                                      style: null,
                                      track: null,
                                      stage: null,
                                      orderByStageThenTime: false);

    public Task<IReadOnlyList<RunRecord>> GetMapStageRecords(string mapName, int limit = IRequestManager.DefaultRecordLimit)
        => QueryBestRecordsByMapAsync(mapName,
                                      limit,
                                      RunType.Stage,
                                      style: null,
                                      track: null,
                                      stage: null,
                                      orderByStageThenTime: true);

    public Task<IReadOnlyList<RunRecord>> GetMapRecords(string mapName,
                                                        int    style,
                                                        int    track,
                                                        int    limit = IRequestManager.DefaultRecordLimit)
    {
        var trackValue = ToUInt16(track);

        return QueryBestRecordsByMapAsync(mapName,
                                          limit,
                                          RunType.Main,
                                          style,
                                          trackValue,
                                          stage: 0,
                                          orderByStageThenTime: false);
    }

    public Task<IReadOnlyList<RunRecord>> GetMapStageRecords(string mapName,
                                                             int    style,
                                                             int    track,
                                                             int    stage,
                                                             int    limit = IRequestManager.DefaultRecordLimit)
    {
        var trackValue = ToUInt16(track);
        var stageValue = ToUInt16(stage);

        return QueryBestRecordsByMapAsync(mapName,
                                          limit,
                                          RunType.Stage,
                                          style,
                                          trackValue,
                                          stageValue,
                                          orderByStageThenTime: false);
    }

    private async Task<IReadOnlyList<RunRecord>> QueryBestRecordsByMapAsync(string    mapName,
                                                                             int       limit,
                                                                             RunType   runType,
                                                                             int?      style,
                                                                             ushort?   track,
                                                                             ushort?   stage,
                                                                             bool      orderByStageThenTime)
    {
        var mapId = await ResolveMapIdByNameAsync(mapName);

        if (mapId is null)
        {
            return [];
        }

        if (style.HasValue && track.HasValue && stage.HasValue)
        {
            await EnsureBestRunsSeededAsync(mapId.Value, runType, style.Value, track.Value, stage.Value);
        }
        else
        {
            await EnsureBestRunsSeededForMapAsync(mapId.Value, runType);
        }

        var normalizedLimit = NormalizeLimit(limit);

        var query = QueryBestRuns().InnerJoin<RunEntity>((best, run) => best.RunId == run.Id)
                                  .Where((best, run) => best.MapId == mapId.Value
                                                        && best.RunType == runType);

        if (style.HasValue)
        {
            query = query.Where((best, run) => best.Style == style.Value);
        }

        if (track.HasValue)
        {
            query = query.Where((best, run) => best.Track == track.Value);
        }

        if (stage.HasValue)
        {
            query = query.Where((best, run) => best.Stage == stage.Value);
        }
        else if (runType == RunType.Main)
        {
            query = query.Where((best, run) => best.Stage == 0);
        }
        else
        {
            query = query.Where((best, run) => best.Stage > 0);
        }

        if (orderByStageThenTime)
        {
            query = query.OrderBy((best, run) => best.Stage)
                         .OrderBy((best, run) => best.BestTime)
                         .OrderBy((best, run) => best.RunId);
        }
        else
        {
            query = query.OrderBy((best, run) => best.BestTime)
                         .OrderBy((best, run) => best.RunId);
        }

        var runs = await query.Select((best, run) => run)
                              .Take(normalizedLimit)
                              .ToListAsync();

        var result = new List<RunRecord>(runs.Count);

        foreach (var run in runs)
        {
            result.Add(ToRunRecord(run));
        }

        await PopulatePlayerNamesAsync(result);

        return result;
    }

    /// <summary>
    ///     Fills <see cref="RunRecord.PlayerName" /> from surf_players in one batched query.
    ///     Run rows only store SteamId, but leaderboard output (!wr/!top) renders the name.
    /// </summary>
    private async Task PopulatePlayerNamesAsync(List<RunRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        var seen     = new HashSet<ulong>(records.Count);
        var steamIds = new List<SteamID>(records.Count);

        foreach (var record in records)
        {
            if (seen.Add(record.SteamId))
            {
                steamIds.Add(new SteamID(record.SteamId));
            }
        }

        var rows = await _db.Queryable<PlayerEntity>()
                            .Where(x => steamIds.Contains(x.SteamId))
                            .Select(x => new PlayerNameRow { SteamId = x.SteamId, Name = x.Name })
                            .ToListAsync();

        var names = new Dictionary<ulong, string>(rows.Count);

        foreach (var row in rows)
        {
            names[row.SteamId.AsPrimitive()] = row.Name;
        }

        foreach (var record in records)
        {
            if (names.TryGetValue(record.SteamId, out var name))
            {
                record.PlayerName = name;
            }
        }
    }

    private sealed class PlayerNameRow
    {
        [SugarColumn(ColumnDataType = "bigint", SqlParameterDbType = typeof(SteamIdDataConvert))]
        public SteamID SteamId { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    public async Task<IReadOnlyList<RunRecord>> GetRecentRecords(string mapName, SteamID steamId, int limit = 10)
    {
        var mapId = await ResolveMapIdByNameAsync(mapName);

        if (mapId is null)
        {
            return [];
        }

        var normalizedLimit = NormalizeLimit(limit);
        var steamIdValue    = steamId.AsPrimitive();

        var rows = await _db.Queryable<RunEntity>()
                            .Where(x => x.MapId == mapId.Value
                                        && x.SteamId == steamIdValue
                                        && x.RunType == RunType.Main
                                        && x.Stage == 0)
                            .OrderByDescending(x => x.Date)
                            .OrderByDescending(x => x.Id)
                            .Select(x => new RecentRunRow
                            {
                                Id = x.Id,
                                Date = x.Date,
                                SteamId = x.SteamId,
                                MapId = x.MapId,
                                Style = x.Style,
                                Track = x.Track,
                                Stage = x.Stage,
                                Time = x.Time,
                            })
                            .Take(normalizedLimit)
                            .ToListAsync();

        var result = new List<RunRecord>(rows.Count);

        foreach (var row in rows)
        {
            result.Add(new RunRecord
            {
                Id = (long)row.Id,
                RunDate = row.Date,
                SteamId = row.SteamId.AsPrimitive(),
                MapId = row.MapId,
                Style = row.Style,
                Track = row.Track,
                Stage = row.Stage,
                Time = row.Time,
            });
        }

        return result;
    }

    private sealed class RecentRunRow
    {
        public ulong Id { get; set; }

        public DateTime Date { get; set; }

        [SugarColumn(ColumnDataType = "bigint", SqlParameterDbType = typeof(SteamIdDataConvert))]
        public SteamID SteamId { get; set; }

        public ulong MapId { get; set; }

        public int Style { get; set; }

        public ushort Track { get; set; }

        public ushort Stage { get; set; }

        public float Time { get; set; }
    }
}
