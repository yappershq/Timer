/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LiteDB;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Managers.Request;

internal class RequestManagerLiteDB : IManager, IRequestManager, IDisposable
{
    private LiteDatabase?                          _database;
    private readonly string                        _dbPath;
    private readonly ILogger<RequestManagerLiteDB> _logger;
    private readonly ConcurrentDictionary<string, ulong> _mapIdCache = new (StringComparer.Ordinal);

    private LiteDatabase Database => _database ?? throw new ObjectDisposedException(nameof(RequestManagerLiteDB));

    private const string PlayerRecordTableName           = "player_records";
    private const string PlayerStageRecordTableName      = "player_stage_records";
    private const string PlayerCheckpointRecordTableName = "player_checkpoint_records";

    private const string MapTableName  = "maps";
    private const string UserTableName = "users";
    private const string ZoneTableName = "zones";
    private const string BanTableName  = "kz_bans";

    static RequestManagerLiteDB()
    {
        var mapper = BsonMapper.Global;

        mapper.Entity<MapProfile>()
              .Id(x => x.MapId);

        mapper.Entity<RunRecord>()
              .Id(x => x.Id);

        mapper.Entity<PlayerProfile>()
              .Id(x => x.Id);

        mapper.Entity<BanRecord>()
              .Id(x => x.Id);
    }

    public RequestManagerLiteDB(InterfaceBridge bridge, ILogger<RequestManagerLiteDB> logger)
    {
        _dbPath = Path.Combine(bridge.SharpPath, "data", "surftimer", "timer.db");
        _logger = logger;
    }

    public bool Init()
    {
        _database = new LiteDatabase(_dbPath);
        _mapIdCache.Clear();
        EnsureIndexes();

        return true;
    }

    public void Shutdown()
    {
        _database?.Dispose();
        _database = null;
    }

    public void Dispose()
    {
        _database?.Dispose();
        _database = null;
    }

    private void EnsureIndexes()
    {
        var mapCol = Database.GetCollection<MapProfile>(MapTableName);
        mapCol.EnsureIndex(x => x.MapId);
        mapCol.EnsureIndex(x => x.MapName);

        var recordCol = Database.GetCollection<RunRecord>(PlayerRecordTableName);
        recordCol.EnsureIndex(x => x.MapId);
        recordCol.EnsureIndex(x => x.SteamId);

        var stageRecordCol = Database.GetCollection<RunRecord>(PlayerStageRecordTableName);
        stageRecordCol.EnsureIndex(x => x.MapId);
        stageRecordCol.EnsureIndex(x => x.SteamId);

        var checkpointCol = Database.GetCollection<RunCheckpoint>(PlayerCheckpointRecordTableName);
        checkpointCol.EnsureIndex(x => x.RecordId);

        var userCol = Database.GetCollection<PlayerProfile>(UserTableName);
        userCol.EnsureIndex(x => x.SteamId);

        var zoneCol = Database.GetCollection<ZoneDocument>(ZoneTableName);
        zoneCol.EnsureIndex(x => x.MapName);

        var banCol = Database.GetCollection<BanRecord>(BanTableName);
        banCol.EnsureIndex(x => x.SteamId);
    }

    public Task AddBanAsync(SteamID steamId, string? reason, DateTime expiresAt)
    {
        var ban = new BanRecord
        {
            Id        = Guid.NewGuid().ToString(),
            SteamId   = steamId.AsPrimitive(),
            Reason    = reason,
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow,
        };

        Database.GetCollection<BanRecord>(BanTableName).Insert(ban);

        return Task.CompletedTask;
    }

    public Task<int> RemoveBansAsync(SteamID steamId)
    {
        var value = steamId.AsPrimitive();
        var count = Database.GetCollection<BanRecord>(BanTableName).DeleteMany(b => b.SteamId == value);

        return Task.FromResult(count);
    }

    public Task<BanRecord?> GetActiveBanAsync(SteamID steamId)
    {
        var value = steamId.AsPrimitive();
        var now   = DateTime.UtcNow;

        var ban = Database.GetCollection<BanRecord>(BanTableName)
                          .Find(b => b.SteamId == value && b.ExpiresAt > now)
                          .OrderByDescending(b => b.ExpiresAt)
                          .FirstOrDefault();

        return Task.FromResult<BanRecord?>(ban);
    }

    /// <summary>SteamIDs with an active (unexpired) ban — excluded from the public leaderboard reads.</summary>
    private HashSet<ulong> ActiveBanSet()
    {
        var now = DateTime.UtcNow;
        return Database.GetCollection<BanRecord>(BanTableName)
                       .Find(b => b.ExpiresAt > now)
                       .Select(b => b.SteamId)
                       .ToHashSet();
    }

    public Task<MapProfile> GetMapInfo(string map)
    {
        var mapNameKey = map.ToLowerInvariant();

        var col = Database.GetCollection<MapProfile>(MapTableName);

        if (col.FindOne(i => i.MapName == mapNameKey) is { } rec)
        {
            _mapIdCache[mapNameKey] = rec.MapId;

            return Task.FromResult(rec);
        }

        var newMap = new MapProfile
        {
            Bonuses = 0,
            Stages  = 0,

            MapName = mapNameKey,

            PlayCount     = 0,
            TotalPlayTime = 0.0f,

            Tier = new byte[MapProfile.DefaultTrackCount],
        };

        newMap.Tier[0] = 1;

        var newId = col.Insert(newMap);
        newMap.MapId = (ulong) newId.AsInt64;
        _mapIdCache[mapNameKey] = newMap.MapId;

        return Task.FromResult(newMap);
    }

    public Task UpdateMapInfo(MapProfile info)
    {
        var mapNameKey = info.MapName.ToLowerInvariant();

        var col = Database.GetCollection<MapProfile>(MapTableName);

        if (col.FindOne(i => i.MapName == mapNameKey) is { } existingInfo)
        {
            existingInfo.Tier    = info.Tier;
            existingInfo.Bonuses = info.Bonuses;
            existingInfo.Stages  = info.Stages;

            existingInfo.PlayCount     = info.PlayCount;
            existingInfo.TotalPlayTime = info.TotalPlayTime;
            col.Update(existingInfo);
            _mapIdCache[mapNameKey] = existingInfo.MapId;
        }
        else
        {
            var newInfo = new MapProfile
            {
                MapId         = info.MapId,
                MapName       = mapNameKey,
                Stages        = info.Stages,
                Bonuses       = info.Bonuses,
                Tier          = info.Tier,
                TotalPlayTime = info.TotalPlayTime,
                PlayCount     = info.PlayCount,
            };

            col.Insert(newInfo);
            _mapIdCache[mapNameKey] = newInfo.MapId;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RunRecord>> GetMapRecords(string mapName, int limit = IRequestManager.DefaultRecordLimit)
    {
        var mapId = ResolveMapIdByName(mapName);

        if (mapId is null)
        {
            return Task.FromResult<IReadOnlyList<RunRecord>>([]);
        }

        var normalizedLimit  = NormalizeLimit(limit);
        var banned           = ActiveBanSet();

        var col = Database.GetCollection<RunRecord>(PlayerRecordTableName);

        var bestRuns = col.Query()
                          .Where(i => i.MapId == mapId.Value && i.Stage == 0)
                          .ToEnumerable()
                          .Where(run => !banned.Contains(run.SteamId))
                          .GroupBy(run => new
                          {
                              run.SteamId,
                              run.Style,
                              run.Track,
                          })
                          .Select(group => group.OrderBy(run => run.Time)
                                                .ThenBy(run => run.Id)
                                                .First())
                          .OrderBy(run => run.Time)
                          .ThenBy(run => run.Id)
                          .Take(normalizedLimit)
                          .ToList();

        return Task.FromResult<IReadOnlyList<RunRecord>>(bestRuns);
    }

    public Task<IReadOnlyList<RunRecord>> GetMapStageRecords(string mapName, int limit = IRequestManager.DefaultRecordLimit)
    {
        var mapId = ResolveMapIdByName(mapName);

        if (mapId is null)
        {
            return Task.FromResult<IReadOnlyList<RunRecord>>([]);
        }

        var normalizedLimit  = NormalizeLimit(limit);
        var banned           = ActiveBanSet();

        var col = Database.GetCollection<RunRecord>(PlayerStageRecordTableName);

        var bestRuns = col.Query()
                          .Where(i => i.MapId == mapId.Value && i.Stage > 0)
                          .ToEnumerable()
                          .Where(run => !banned.Contains(run.SteamId))
                          .GroupBy(run => new
                          {
                              run.SteamId,
                              run.Style,
                              run.Track,
                              run.Stage,
                          })
                          .Select(group => group.OrderBy(run => run.Time)
                                                .ThenBy(run => run.Id)
                                                .First())
                          .OrderBy(run => run.Stage)
                          .ThenBy(run => run.Time)
                          .ThenBy(run => run.Id)
                          .Take(normalizedLimit)
                          .ToList();

        return Task.FromResult<IReadOnlyList<RunRecord>>(bestRuns);
    }

    public Task<IReadOnlyList<RunRecord>> GetMapRecords(string mapName,
                                                        int    style,
                                                        int    track,
                                                        int    limit = IRequestManager.DefaultRecordLimit)
    {
        var mapId = ResolveMapIdByName(mapName);

        if (mapId is null)
        {
            return Task.FromResult<IReadOnlyList<RunRecord>>([]);
        }

        var normalizedLimit  = NormalizeLimit(limit);
        var banned           = ActiveBanSet();

        var col = Database.GetCollection<RunRecord>(PlayerRecordTableName);

        var bestRuns = col.Query()
                          .Where(i => i.MapId == mapId.Value && i.Style == style && i.Track == track && i.Stage == 0)
                          .ToEnumerable()
                          .Where(run => !banned.Contains(run.SteamId))
                          .GroupBy(run => run.SteamId)
                          .Select(group => group.OrderBy(run => run.Time)
                                                .ThenBy(run => run.Id)
                                                .First())
                          .OrderBy(run => run.Time)
                          .ThenBy(run => run.Id)
                          .Take(normalizedLimit)
                          .ToList();

        return Task.FromResult<IReadOnlyList<RunRecord>>(bestRuns);
    }

    public Task<IReadOnlyList<RunRecord>> GetMapStageRecords(string mapName,
                                                             int    style,
                                                             int    track,
                                                             int    stage,
                                                             int    limit = IRequestManager.DefaultRecordLimit)
    {
        var mapId = ResolveMapIdByName(mapName);

        if (mapId is null)
        {
            return Task.FromResult<IReadOnlyList<RunRecord>>([]);
        }

        var normalizedLimit  = NormalizeLimit(limit);
        var banned           = ActiveBanSet();

        var col = Database.GetCollection<RunRecord>(PlayerStageRecordTableName);

        var bestRuns = col.Query()
                          .Where(i => i.MapId    == mapId.Value
                                      && i.Stage == stage
                                      && i.Style == style
                                      && i.Track == track)
                          .ToEnumerable()
                          .Where(run => !banned.Contains(run.SteamId))
                          .GroupBy(run => run.SteamId)
                          .Select(group => group.OrderBy(run => run.Time)
                                                .ThenBy(run => run.Id)
                                                .First())
                          .OrderBy(run => run.Time)
                          .ThenBy(run => run.Id)
                          .Take(normalizedLimit)
                          .ToList();

        return Task.FromResult<IReadOnlyList<RunRecord>>(bestRuns);
    }

    public Task<(EAttemptResult, RunRecord, int rank)> AddPlayerRecord(SteamID       steamId,
                                                                       string        mapName,
                                                                       RecordRequest recordRequest)
    {
        var mapInfo = GetMapInfo(mapName).GetAwaiter().GetResult();
        var mapId   = mapInfo.MapId;

        var recordCol     = Database.GetCollection<RunRecord>(PlayerRecordTableName);
        var checkpointCol = Database.GetCollection<RunCheckpoint>(PlayerCheckpointRecordTableName);
        var steamIdValue  = steamId.AsPrimitive();

        var newRecord = new RunRecord
        {
            MapId    = mapId,
            SteamId  = steamId.AsPrimitive(),

            Time = recordRequest.Time,

            Style = recordRequest.Style,
            Track = recordRequest.Track,

            Jumps   = recordRequest.Jumps,
            Strafes = recordRequest.Strafes,
            Sync    = recordRequest.Sync,

            Stage = 0,

            RunDate = DateTime.UtcNow,
        };

        newRecord.SetStartVelocity(recordRequest.GetStartVelocity());
        newRecord.SetAverageVelocity(recordRequest.GetAverageVelocity());
        newRecord.SetEndVelocity(recordRequest.GetEndVelocity());

        Database.BeginTrans();

        try
        {
            var newRecordId = recordCol.Insert(newRecord);
            newRecord.Id = newRecordId.AsInt64;

            if (recordRequest.Checkpoints?.Count > 0)
            {
                var checkpoints = recordRequest.Checkpoints.Select((cp, i) =>
                {
                    var cpInfo = new RunCheckpoint
                    {
                        RecordId        = newRecord.Id,
                        CheckpointIndex = (uint) (i + 1),
                        Time            = cp.Time,
                        Sync            = cp.Sync,
                    };

                    cpInfo.SetAverageVelocity(cp.GetAverageVelocity());
                    cpInfo.SetMaxVelocity(cp.GetMaxVelocity());
                    cpInfo.SetStartVelocity(cp.GetStartVelocity());
                    cpInfo.SetEndVelocity(cp.GetEndVelocity());

                    return cpInfo;
                });

                checkpointCol.InsertBulk(checkpoints);
            }

            var allRuns = recordCol.Query()
                                   .Where(r => r.MapId    == newRecord.MapId
                                               && r.Stage == newRecord.Stage
                                               && r.Style == newRecord.Style
                                               && r.Track == newRecord.Track)
                                   .ToEnumerable();

            var bestByPlayer = allRuns.GroupBy(r => r.SteamId)
                                      .Select(group => group.OrderBy(r => r.Time)
                                                            .ThenBy(r => r.Id)
                                                            .First())
                                      .ToList();

            var serverBest = bestByPlayer.OrderBy(r => r.Time)
                                         .ThenBy(r => r.Id)
                                         .FirstOrDefault();

            var playerBest = bestByPlayer.FirstOrDefault(r => r.SteamId == steamIdValue);

            EAttemptResult result;

            if (serverBest == null || CompareByTimeThenId(newRecord, serverBest) < 0)
            {
                result = EAttemptResult.NewServerRecord;
            }
            else if (playerBest == null || CompareByTimeThenId(newRecord, playerBest) < 0)
            {
                result = EAttemptResult.NewPersonalRecord;
            }
            else
            {
                result = EAttemptResult.NoNewRecord;
            }

            var rank = 0;

            if (result >= EAttemptResult.NewPersonalRecord)
            {
                rank = recordCol.Query()
                                .Where(r => r.MapId    == newRecord.MapId
                                            && r.Stage == newRecord.Stage
                                            && r.Style == newRecord.Style
                                            && r.Track == newRecord.Track
                                            && r.Time  < newRecord.Time)
                                .ToEnumerable()
                                .Select(r => r.SteamId)
                                .Distinct()
                                .Count() + 1;
            }

            Database.Commit();

            return Task.FromResult((result, newRecord, rank));
        }
        catch (Exception)
        {
            Database.Rollback();

            throw;
        }
    }

    public Task<IReadOnlyList<RunRecord>> GetPlayerRecords(SteamID steamId, string mapName)
    {
        var mapId = ResolveMapIdByName(mapName);

        if (mapId is null)
        {
            return Task.FromResult<IReadOnlyList<RunRecord>>([]);
        }

        var col = Database.GetCollection<RunRecord>(PlayerRecordTableName);

        var steamIdValue = steamId.AsPrimitive();

        var allPlayerRuns = col.Query()
                               .Where(r => r.SteamId == steamIdValue && r.MapId == mapId.Value && r.Stage == 0)
                               .ToEnumerable();

        List<RunRecord> records = allPlayerRuns
                                  .GroupBy(run => new
                                  {
                                      run.Style,
                                      run.Track,
                                  })
                                  .Select(group => group.OrderBy(run => run.Time)
                                                        .First())
                                  .ToList();

        return Task.FromResult<IReadOnlyList<RunRecord>>(records);
    }

    public Task<RunRecord?> GetPlayerRecord(SteamID steamId, string mapName, int style, int track)
    {
        var mapId = ResolveMapIdByName(mapName);

        if (mapId is null)
        {
            return Task.FromResult<RunRecord?>(null);
        }

        var col = Database.GetCollection<RunRecord>(PlayerRecordTableName);

        var steamIdValue = steamId.AsPrimitive();

        return Task.FromResult(col.Query()
                                  .Where(r => r.SteamId == steamIdValue
                                              && r.MapId == mapId.Value
                                              && r.Stage == 0
                                              && r.Style == style
                                              && r.Track == track)
                                  .OrderBy(r => r.Time)
                                  .FirstOrDefault()
                               ?? null);
    }

    public Task<(EAttemptResult, RunRecord, int rank)> AddPlayerStageRecord(SteamID steamId, string mapName, RecordRequest recordRequest)
    {
        var mapInfo = GetMapInfo(mapName).GetAwaiter().GetResult();
        var mapId   = mapInfo.MapId;

        var recordCol     = Database.GetCollection<RunRecord>(PlayerStageRecordTableName);
        var checkpointCol = Database.GetCollection<RunCheckpoint>(PlayerCheckpointRecordTableName);

        var steamIdValue = steamId.AsPrimitive();

        var newRecord = new RunRecord
        {
            MapId    = mapId,
            SteamId  = steamIdValue,

            Time = recordRequest.Time,

            Style = recordRequest.Style,
            Track = recordRequest.Track,

            Jumps   = recordRequest.Jumps,
            Strafes = recordRequest.Strafes,
            Sync    = recordRequest.Sync,

            Stage = recordRequest.Stage,

            RunDate = DateTime.UtcNow,
        };

        newRecord.SetStartVelocity(recordRequest.GetStartVelocity());
        newRecord.SetAverageVelocity(recordRequest.GetAverageVelocity());
        newRecord.SetEndVelocity(recordRequest.GetEndVelocity());

        Database.BeginTrans();

        try
        {
            var newRecordId = recordCol.Insert(newRecord);
            newRecord.Id = newRecordId.AsInt64;

            if (recordRequest.Checkpoints?.Count > 0)
            {
                var checkpoints = recordRequest.Checkpoints.Select((cp, i) =>
                {
                    var cpInfo = new RunCheckpoint
                    {
                        RecordId        = newRecord.Id,
                        CheckpointIndex = (uint) (i + 1),
                        Time            = cp.Time,
                        Sync            = cp.Sync,
                    };

                    cpInfo.SetAverageVelocity(cp.GetAverageVelocity());
                    cpInfo.SetMaxVelocity(cp.GetMaxVelocity());
                    cpInfo.SetStartVelocity(cp.GetStartVelocity());
                    cpInfo.SetEndVelocity(cp.GetEndVelocity());

                    return cpInfo;
                });

                checkpointCol.InsertBulk(checkpoints);
            }

            var existingBests = recordCol.Query()
                                         .Where(r => r.MapId    == newRecord.MapId
                                                     && r.Stage == newRecord.Stage
                                                     && r.Style == newRecord.Style
                                                     && r.Track == newRecord.Track
                                                     && r.Id    != newRecord.Id)
                                         .OrderBy(r => r.Time)
                                         .Select(r => new
                                         {
                                             r.Id,
                                             r.SteamId,
                                         })
                                         .ToList();

            var serverBest = existingBests.FirstOrDefault();
            var playerBest = existingBests.FirstOrDefault(r => r.SteamId == steamIdValue);

            EAttemptResult result;

            if (serverBest == null || newRecord.Time < recordCol.FindById(serverBest.Id).Time)
            {
                result = EAttemptResult.NewServerRecord;
            }
            else if (playerBest == null || newRecord.Time < recordCol.FindById(playerBest.Id).Time)
            {
                result = EAttemptResult.NewPersonalRecord;
            }
            else
            {
                result = EAttemptResult.NoNewRecord;
            }

            var rank = 0;

            if (result >= EAttemptResult.NewPersonalRecord)
            {
                rank = recordCol.Query()
                                .Where(r => r.MapId    == newRecord.MapId
                                            && r.Stage == newRecord.Stage
                                            && r.Style == newRecord.Style
                                            && r.Track == newRecord.Track
                                            && r.Time  < newRecord.Time)
                                .ToEnumerable()
                                .Select(r => r.SteamId)
                                .Distinct()
                                .Count() + 1;
            }

            Database.Commit();

            return Task.FromResult((result, newRecord, rank));
        }
        catch (Exception)
        {
            Database.Rollback();

            throw;
        }
    }

    public Task<IReadOnlyList<RunRecord>> GetPlayerStageRecords(SteamID steamId, string mapName)
    {
        var mapId = ResolveMapIdByName(mapName);

        if (mapId is null)
        {
            return Task.FromResult<IReadOnlyList<RunRecord>>([]);
        }

        var col = Database.GetCollection<RunRecord>(PlayerStageRecordTableName);

        var steamIdValue = steamId.AsPrimitive();

        var allPlayerRunsOnMap = col.Query()
                                    .Where(i => i.SteamId == steamIdValue && i.MapId == mapId.Value)
                                    .ToEnumerable();

        var records = allPlayerRunsOnMap
                      .GroupBy(run => run.Stage)
                      .Select(stageGroup => stageGroup.OrderBy(run => run.Time)
                                                      .First())
                      .OrderBy(bestRun => bestRun.Stage)
                      .ToList();

        return Task.FromResult<IReadOnlyList<RunRecord>>(records);
    }

    public Task<PlayerProfile> GetPlayerProfile(SteamID steamId, string name)
    {
        var col = Database.GetCollection<PlayerProfile>(UserTableName);

        var user = col.Query()
                      .Where(i => i.SteamId == steamId)
                      .FirstOrDefault();

        if (user != null)
        {
            user.UpdateName(name);
            col.Update(user);

            return Task.FromResult(user);
        }

        user = new ()
        {
            SteamId = steamId,
            Points  = 0,
        };

        user.UpdateName(name);

        var newId = col.Insert(user);
        user.Id = newId.AsInt64;

        return Task.FromResult(user);
    }

    public Task<(int rank, int total)> GetPlayerPointsRank(SteamID steamId)
    {
        var col = Database.GetCollection<PlayerProfile>(UserTableName);

        var user = col.Query()
                      .Where(i => i.SteamId == steamId)
                      .FirstOrDefault();

        if (user is null || user.Points == 0)
        {
            return Task.FromResult((0, 0));
        }

        var rank  = col.Query().Where(i => i.Points > user.Points).Count() + 1;
        var total = col.Query().Where(i => i.Points > 0).Count();

        return Task.FromResult((rank, total));
    }

    public Task UpdatePlayerMapStatsAsync(SteamID steamId, string mapName, float deltaSeconds)
    {
        if (deltaSeconds <= 0f)
        {
            return Task.CompletedTask;
        }

        var mapId = ResolveMapIdByName(mapName);

        if (mapId is null)
        {
            return Task.CompletedTask;
        }

        var col = Database.GetCollection<PlayerMapStatsDoc>("player_map_stats");

        var existing = col.Query()
                          .Where(x => x.SteamId == steamId.AsPrimitive() && x.MapId == mapId.Value)
                          .FirstOrDefault();

        if (existing is null)
        {
            col.Insert(new PlayerMapStatsDoc
            {
                SteamId   = steamId.AsPrimitive(),
                MapId     = mapId.Value,
                PlayTime  = deltaSeconds,
                PlayCount = 1,
            });
        }
        else
        {
            existing.PlayTime  += deltaSeconds;
            existing.PlayCount += 1;
            col.Update(existing);
        }

        return Task.CompletedTask;
    }

    public Task<(float playTime, int playCount)> GetPlayerMapStatsAsync(SteamID steamId, string mapName)
    {
        var mapId = ResolveMapIdByName(mapName);

        if (mapId is null)
        {
            return Task.FromResult((0f, 0));
        }

        var col = Database.GetCollection<PlayerMapStatsDoc>("player_map_stats");

        var stats = col.Query()
                       .Where(x => x.SteamId == steamId.AsPrimitive() && x.MapId == mapId.Value)
                       .FirstOrDefault();

        if (stats is null)
        {
            return Task.FromResult((0f, 0));
        }

        return Task.FromResult((stats.PlayTime, stats.PlayCount));
    }

    public Task RemoveMapRecords(string mapName)
    {
        var mapId = ResolveMapIdByName(mapName);

        if (mapId is null)
        {
            return Task.CompletedTask;
        }

        var recordCol      = Database.GetCollection<RunRecord>(PlayerRecordTableName);
        var stageRecordCol = Database.GetCollection<RunRecord>(PlayerStageRecordTableName);
        var checkpointCol  = Database.GetCollection<RunCheckpoint>(PlayerCheckpointRecordTableName);

        // Collect record IDs before deleting so we can clean up checkpoints
        var mainRecordIds = recordCol.Query()
                                     .Where(i => i.MapId == mapId.Value)
                                     .Select(i => i.Id)
                                     .ToList();

        var stageRecordIds = stageRecordCol.Query()
                                           .Where(i => i.MapId == mapId.Value)
                                           .Select(i => i.Id)
                                           .ToList();

        var allRecordIds = new HashSet<long>(mainRecordIds.Count + stageRecordIds.Count);

        foreach (var id in mainRecordIds)
        {
            allRecordIds.Add(id);
        }

        foreach (var id in stageRecordIds)
        {
            allRecordIds.Add(id);
        }

        if (allRecordIds.Count > 0)
        {
            checkpointCol.DeleteMany(cp => allRecordIds.Contains(cp.RecordId));
        }

        recordCol.DeleteMany(i => i.MapId == mapId.Value);
        stageRecordCol.DeleteMany(i => i.MapId == mapId.Value);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RunCheckpoint>> GetRecordCheckpoints(long recordId)
    {
        var col = Database.GetCollection<RunCheckpoint>(PlayerCheckpointRecordTableName);

        var checkpoints = col.Query()
                             .Where(cp => cp.RecordId == recordId)
                             .OrderBy(cp => cp.CheckpointIndex)
                             .ToList();

        return Task.FromResult<IReadOnlyList<RunCheckpoint>>(checkpoints);
    }

    public Task<IReadOnlyList<RunRecord>> GetRecentRecords(string mapName, SteamID steamId, int limit = 10)
    {
        var mapId = ResolveMapIdByName(mapName);

        if (mapId is null)
        {
            return Task.FromResult<IReadOnlyList<RunRecord>>([]);
        }

        var normalizedLimit = NormalizeLimit(limit);
        var steamIdValue    = steamId.AsPrimitive();

        var col = Database.GetCollection<RunRecord>(PlayerRecordTableName);

        var runs = col.Query()
                      .Where(i => i.MapId == mapId.Value && i.SteamId == steamIdValue && i.Stage == 0)
                      .OrderByDescending(i => i.RunDate)
                      .Limit(normalizedLimit)
                      .ToList();

        return Task.FromResult<IReadOnlyList<RunRecord>>(runs);
    }

    /// <summary>
    /// LiteDB implementation does not support score calculation; returns 0.
    /// </summary>
    public Task<int> RecalculateMapScoresAsync(string mapName, IReadOnlyDictionary<int, double>? styleFactors = null)
    {
        return Task.FromResult(0);
    }

    public Task<IReadOnlyList<string>> GetAllMapNamesAsync()
    {
        var col = Database.GetCollection<MapProfile>(MapTableName);
        var names = col.Query().Select(x => x.MapName).ToList();
        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    private static int CompareByTimeThenId(RunRecord left, RunRecord right)
    {
        var timeCompare = left.Time.CompareTo(right.Time);

        return timeCompare != 0 ? timeCompare : left.Id.CompareTo(right.Id);
    }

    private static int NormalizeLimit(int limit)
    {
        if (limit <= 0)
        {
            return 1;
        }

        return limit >= IRequestManager.DefaultRecordLimit ? IRequestManager.DefaultRecordLimit : limit;
    }

    private ulong? ResolveMapIdByName(string mapName)
    {
        var mapNameKey = mapName.ToLowerInvariant();

        if (_mapIdCache.TryGetValue(mapNameKey, out var cachedMapId))
        {
            return cachedMapId;
        }

        var col = Database.GetCollection<MapProfile>(MapTableName);
        var mapId = col.Query()
                       .Where(i => i.MapName == mapNameKey)
                       .Select(i => i.MapId)
                       .FirstOrDefault();

        if (mapId != 0)
        {
            _mapIdCache[mapNameKey] = mapId;

            return mapId;
        }

        return null;
    }

    #region Zone

    public Task<IReadOnlyList<ZoneData>> GetZonesAsync(string mapName)
    {
        var mapNameKey = mapName.ToLowerInvariant();
        var col = Database.GetCollection<ZoneDocument>(ZoneTableName);

        var docs = col.Query()
                      .Where(z => z.MapName == mapNameKey)
                      .ToList();

        var zones = docs.Select(d => d.ToZoneData()).ToList();

        return Task.FromResult<IReadOnlyList<ZoneData>>(zones);
    }

    public Task SaveZonesAsync(string mapName, IReadOnlyList<ZoneData> zones)
    {
        var mapNameKey = mapName.ToLowerInvariant();
        var col = Database.GetCollection<ZoneDocument>(ZoneTableName);

        col.DeleteMany(z => z.MapName == mapNameKey);

        if (zones.Count > 0)
        {
            var docs = zones.Select(z => ZoneDocument.FromZoneData(z, mapNameKey)).ToList();
            col.InsertBulk(docs);
        }

        return Task.CompletedTask;
    }

    public Task<ulong> AddZoneAsync(string mapName, ZoneData zone)
    {
        var mapNameKey = mapName.ToLowerInvariant();
        var col = Database.GetCollection<ZoneDocument>(ZoneTableName);

        var doc = ZoneDocument.FromZoneData(zone, mapNameKey);
        var bsonId = col.Insert(doc);

        return Task.FromResult((ulong)bsonId.AsInt64);
    }

    public Task DeleteZonesAsync(string mapName)
    {
        var mapNameKey = mapName.ToLowerInvariant();
        var col = Database.GetCollection<ZoneDocument>(ZoneTableName);

        col.DeleteMany(z => z.MapName == mapNameKey);

        return Task.CompletedTask;
    }

    #endregion

    /// <summary>
    /// Internal LiteDB document for storing Zone data with map name association.
    /// </summary>
    private sealed class ZoneDocument
    {
        [BsonId]
        public long       Id             { get; set; }
        public string     MapName        { get; set; } = string.Empty;
        public EZoneType  Type           { get; set; }
        public int        Track          { get; set; }
        public int        Sequence       { get; set; }
        public Vector     Mins           { get; set; }
        public Vector     Maxs           { get; set; }
        public Vector     Center         { get; set; }
        public Vector?    TeleportOrigin { get; set; }
        public Vector?    TeleportAngles { get; set; }
        public string?    Config         { get; set; }

        public ZoneData ToZoneData()
        {
            return new ZoneData
            {
                Id             = (ulong)Id,
                Type           = Type,
                Track          = Track,
                Sequence       = Sequence,
                Mins           = Mins,
                Maxs           = Maxs,
                Center         = Center,
                TeleportOrigin = TeleportOrigin,
                TeleportAngles = TeleportAngles,
                Config         = Config,
            };
        }

        public static ZoneDocument FromZoneData(ZoneData data, string mapNameKey)
        {
            return new ZoneDocument
            {
                MapName        = mapNameKey,
                Type           = data.Type,
                Track          = data.Track,
                Sequence       = data.Sequence,
                Mins           = data.Mins,
                Maxs           = data.Maxs,
                Center         = data.Center,
                TeleportOrigin = data.TeleportOrigin,
                TeleportAngles = data.TeleportAngles,
                Config         = data.Config,
            };
        }
    }

    private sealed class PlayerMapStatsDoc
    {
        [BsonId]
        public long  Id        { get; set; }
        public ulong SteamId   { get; set; }
        public ulong MapId     { get; set; }
        public float PlayTime  { get; set; }
        public int   PlayCount { get; set; }
    }
}
