using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.RequestManager.Scheduling;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    /// <summary>
    /// Full recalculation of all player scores for a given track.
    /// 1. SELECT the best run per player ordered by (Time ASC, RunId ASC); rank is the 1-based position
    ///    in that ordered list and total is its count — computed in C#, NOT via SQL window functions.
    /// 2. In-memory ScoreCalculator pass to compute each player's score
    /// 3. Batch UPSERT: update PlayerTrackScoreEntity (changed records only)
    /// 4. Atomic per-player UPDATE of PlayerEntity.Points = SUM(track scores) (see UpdatePlayerTotalPointsAsync) —
    ///    a single read-modify-write statement so concurrent recalcs on a shared DB cannot lose the cross-map total.
    /// </summary>
    internal async Task RecalculateTrackScoresAsync(ulong mapId, int style, ushort track, int tier, int basePot, double styleFactor)
    {
        var isBonus = ScoreCalculator.IsBonus(track);
        var trackPool = ScoreCalculator.CalculateTrackPool(tier, isBonus, basePot, styleFactor);

        // 1. Use window functions to get the ranked player list and total count
        var rankedPlayers = await GetRankedPlayersAsync(mapId, style, track);

        if (rankedPlayers.Count == 0)
        {
            return;
        }

        var total = rankedPlayers.Count;

        // 2. Query existing scores for delta comparison, building the lookup dict directly (no
        //    intermediate List + ToDictionary). Covered index-only by idx_player_track_scores_map_style_track.
        var existingScores = await _db.Queryable<PlayerTrackScoreEntity>()
                                      .Where(x => x.MapId == mapId && x.Style == style && x.Track == track)
                                      .Select(x => new ExistingTrackScoreRow
                                      {
                                          SteamId = x.SteamId,
                                          Points = x.Points,
                                      })
                                      .ToListAsync();

        var existingDict = new Dictionary<SteamID, uint>(existingScores.Count);
        foreach (var row in existingScores)
        {
            existingDict[row.SteamId] = row.Points;
        }

        // 3. Single pre-sized pass: compute each rank's score and keep only the rows whose stored value
        //    actually changed. Replaces the former anonymous-type Select.ToList -> Where.Select.ToList
        //    pipeline (3+ intermediate O(n) collections + one heap object per player) with one list.
        var now = DateTime.UtcNow;
        var changedScores = new List<PlayerTrackScoreEntity>();

        for (var index = 0; index < total; index++)
        {
            var steamId = rankedPlayers[index].SteamId;
            var points = (uint) Math.Round(ScoreCalculator.CalculatePlayerTrackScore(trackPool, index + 1, total));

            if (existingDict.TryGetValue(steamId, out var oldPoints) && oldPoints == points)
            {
                continue; // unchanged — skip write
            }

            changedScores.Add(new PlayerTrackScoreEntity
            {
                SteamId = steamId,
                MapId = mapId,
                Style = style,
                Track = track,
                Points = points,
                UpdatedAt = now,
            });
        }

        if (changedScores.Count == 0)
        {
            return; // No changes, skip write
        }

        // 4. Batch UPSERT only changed records (insert new, update existing)
        var storage = _db.Storageable(changedScores)
            .WhereColumns(x => new { x.SteamId, x.MapId, x.Style, x.Track })
            .ToStorage();

        if (storage.InsertList.Count > 0)
        {
            await storage.AsInsertable.ExecuteCommandAsync();
        }

        if (storage.UpdateList.Count > 0)
        {
            await storage.AsUpdateable
                .UpdateColumns(x => new { x.Points, x.UpdatedAt })
                .ExecuteCommandAsync();
        }

        // 5. Aggregate and update PlayerEntity.Points for affected players. changedScores already holds
        //    one row per distinct player (from the ranked best-run set), so no Distinct pass is needed.
        var affectedIds = new List<SteamID>(changedScores.Count);
        foreach (var s in changedScores)
        {
            affectedIds.Add(s.SteamId);
        }

        await UpdatePlayerTotalPointsAsync(affectedIds);
    }


    /// <summary>
    /// Recompute PlayerEntity.Points for the given players as a single atomic statement per chunk:
    /// UPDATE players SET Points = COALESCE((SELECT SUM(Points) FROM track_scores WHERE SteamId = players.SteamId), 0).
    /// Computing the sum and writing it in ONE statement (instead of SELECT-into-memory then a blind UPDATE)
    /// closes the cross-server lost-update window on the GLOBAL cross-map Points total without any locking:
    /// each row's new value is derived from the currently-committed track scores at write time, so two servers
    /// recomputing the same player from different maps can no longer clobber each other with a stale total.
    /// Chunked to bound the generated statement size: the SteamId IN(...) list renders as inlined numeric
    /// literals (the SteamId converter), so the limit that matters is statement text size (e.g. MySQL
    /// max_allowed_packet), not a bound-parameter count.
    /// </summary>
    private async Task UpdatePlayerTotalPointsAsync(IReadOnlyList<SteamID> idList)
    {
        if (idList.Count == 0)
        {
            return;
        }

        const int chunkSize = 5000;
        var       now       = DateTime.UtcNow;

        for (var offset = 0; offset < idList.Count; offset += chunkSize)
        {
            var take  = Math.Min(chunkSize, idList.Count - offset);
            var chunk = new List<SteamID>(take);

            for (var i = 0; i < take; i++)
            {
                chunk.Add(idList[offset + i]);
            }

            await _db.Updateable<PlayerEntity>()
                     .SetColumns(p => p.Points == SqlFunc.IsNull(
                                          SqlFunc.Subqueryable<PlayerTrackScoreEntity>()
                                                 .Where(s => s.SteamId == p.SteamId)
                                                 .Sum(s => s.Points),
                                          0u))
                     .SetColumns(p => p.UpdatedAt == now)
                     .Where(p => chunk.Contains(p.SteamId))
                     .ExecuteCommandAsync();
        }
    }

    /// <summary>
    /// Get the score configuration (Tier and BasePot) for a given track.
    /// </summary>
    internal async Task<(int Tier, int BasePot)> GetTrackScoreConfigAsync(ulong mapId, ushort track)
    {
        var key = (mapId, track);

        if (_trackScoreConfigCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var row = await _db.Queryable<MapEntity>()
                           .LeftJoin<MapTrackEntity>((map, trackTier) => map.MapId == trackTier.MapId
                                                                          && trackTier.Track == track)
                           .Where(map => map.MapId == mapId)
                           .Select((map, trackTier) => new
                           {
                               map.Tier,
                               map.BasePot,
                               TrackTier = trackTier.Tier,
                           })
                           .FirstAsync();

        var config = ((int)(track == 0 ? row?.Tier ?? 1 : row?.TrackTier ?? 1),
                      (int)(row?.BasePot ?? 0));

        _trackScoreConfigCache[key] = config;

        return config;
    }

    /// <summary>
    /// Get the ranked player list for a given track (sorted by time, best per player).
    /// </summary>
    private async Task<List<RankedPlayerRow>> GetRankedPlayersAsync(ulong mapId, int style, ushort track)
    {
        const ushort stage = 0;

        await EnsureBestRunsSeededAsync(mapId, RunType.Main, style, track, stage);

        var players = await QueryBestRuns()
            .Where(r => r.MapId == mapId
                        && r.RunType == RunType.Main
                        && r.Style == style
                        && r.Track == track
                        && r.Stage == stage)
            .OrderBy(r => r.BestTime)
            .OrderBy(r => r.RunId)
            .Select(r => new RankedPlayerRow
            {
                SteamId = r.SteamId,
                BestTime = r.BestTime,
            })
            .ToListAsync();

        return players;
    }

    private sealed class RankedPlayerRow
    {
        [SugarColumn(ColumnDataType = "bigint", SqlParameterDbType = typeof(SteamIdDataConvert))]
        public SteamID SteamId { get; set; }
        public float BestTime { get; set; }
    }

    private sealed class ExistingTrackScoreRow
    {
        [SugarColumn(ColumnDataType = "bigint", SqlParameterDbType = typeof(SteamIdDataConvert))]
        public SteamID SteamId { get; set; }

        public uint Points { get; set; }
    }

    /// <summary>
    /// Manually trigger score recalculation for all tracks on a given map.
    /// </summary>
    public async Task<int> RecalculateMapScoresAsync(string mapName, IReadOnlyDictionary<int, double>? styleFactors = null)
    {
        var mapId = await ResolveMapIdByNameAsync(mapName);
        if (mapId is null)
        {
            return 0;
        }

        // Get map configuration
        var mapEntity = await _db.Queryable<MapEntity>()
            .Where(x => x.MapId == mapId.Value)
            .FirstAsync();

        if (mapEntity is null)
        {
            return 0;
        }

        var basePot = mapEntity.BasePot;
        var mainTier = mapEntity.Tier;

        // Query all (style, track) combinations with optional bonus track tier.
        var trackCombinations = await _db.Queryable<RunEntity>()
            .LeftJoin<MapTrackEntity>((run, bonusTier) => run.MapId == bonusTier.MapId && run.Track == bonusTier.Track)
            .Where((run, bonusTier) => run.MapId == mapId.Value && run.RunType == RunType.Main && run.Stage == 0)
            .GroupBy((run, bonusTier) => new { run.Style, run.Track, bonusTier.Tier })
            .Select((run, bonusTier) => new RecalcTrackCombinationRow
            {
                Style = run.Style,
                Track = run.Track,
                BonusTier = bonusTier.Tier,
            })
            .ToListAsync();

        if (trackCombinations.Count == 0)
        {
            return 0;
        }

        // Enqueue a recalculation request for each (style, track) combination
        foreach (var combo in trackCombinations)
        {
            var track = combo.Track;
            var tier = track == 0
                ? mainTier
                : combo.BonusTier > 0 ? combo.BonusTier : (byte)1;

            _trackScoreConfigCache[(mapId.Value, track)] = (tier, basePot);

            // Look up the style factor from the dictionary; default to 1.0 if not found
            var styleFactor = styleFactors?.TryGetValue(combo.Style, out var factor) == true ? factor : 1.0;

            _scoreRecalcScheduler.Enqueue(new RecalcRequest(mapId.Value, combo.Style, track, tier, basePot, styleFactor));
        }

        return trackCombinations.Count;
    }

    private sealed class RecalcTrackCombinationRow
    {
        public int Style { get; set; }

        public ushort Track { get; set; }

        public byte BonusTier { get; set; }
    }
}
