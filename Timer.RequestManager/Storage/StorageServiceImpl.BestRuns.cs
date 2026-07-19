using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using SqlSugar;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    private ISugarQueryable<PlayerBestRunEntity> QueryBestRuns() =>
        _db.Queryable<PlayerBestRunEntity>();

    private async Task EnsureBestRunsSeededForMapAsync(ulong mapId, RunType runType)
    {
        var seedKey = (mapId, runType);

        if (!_bestRunMapSeededCache.TryAdd(seedKey, 0))
        {
            return;
        }

        try
        {
            var baseQuery = _db.Queryable<RunEntity>()
                               .Where(x => x.MapId      == mapId
                                           && x.RunType == runType);

            if (runType == RunType.Main)
            {
                baseQuery = baseQuery.Where(x => x.Stage == 0);
            }
            else
            {
                baseQuery = baseQuery.Where(x => x.Stage > 0);
            }

            var rows = await baseQuery.Select(x => new SeedBestRunRow
                                      {
                                          SteamId  = x.SteamId,
                                          Style    = x.Style,
                                          Track    = x.Track,
                                          Stage    = x.Stage,
                                          RunId    = x.Id,
                                          BestTime = x.Time,
                                          RowNum
                                              = SqlFunc.RowNumber($"{nameof(RunEntity.Time)} ASC, {nameof(RunEntity.Id)} ASC",
                                                                  $"{nameof(RunEntity.Style)}, {nameof(RunEntity.Track)}, {nameof(RunEntity.Stage)}, {nameof(RunEntity.SteamId)}"),
                                      })
                                      .MergeTable()
                                      .Where(t => t.RowNum == 1)
                                      .ToListAsync();

            // Clear RowNum helper field before upsert
            foreach (var row in rows)
            {
                row.RowNum = 0;
            }

            await UpsertSeedBestRowsAsync(mapId, runType, rows);
        }
        catch
        {
            _bestRunMapSeededCache.TryRemove(seedKey, out _);

            throw;
        }
    }

    private async Task EnsureBestRunsSeededAsync(ulong mapId, RunType runType, int style, ushort track, ushort stage)
    {
        if (_bestRunMapSeededCache.ContainsKey((mapId, runType)))
        {
            return;
        }

        var seedKey = (mapId, runType, style, track, stage);

        if (!_bestRunSeededCache.TryAdd(seedKey, 0))
        {
            return;
        }

        try
        {
            var hasBestRows = await QueryBestRuns().Where(x => x.MapId      == mapId
                                                               && x.RunType == runType
                                                               && x.Style   == style
                                                               && x.Track   == track
                                                               && x.Stage   == stage)
                                                   .AnyAsync();

            if (hasBestRows)
            {
                return;
            }

            var rows = await _db.Queryable<RunEntity>()
                                .Where(x => x.MapId      == mapId
                                            && x.RunType == runType
                                            && x.Style   == style
                                            && x.Track   == track
                                            && x.Stage   == stage)
                                .Select(x => new SeedBestRunRow
                                {
                                    SteamId  = x.SteamId,
                                    Style    = style,
                                    Track    = track,
                                    Stage    = stage,
                                    RunId    = x.Id,
                                    BestTime = x.Time,
                                    RowNum = SqlFunc.RowNumber($"{nameof(RunEntity.Time)} ASC, {nameof(RunEntity.Id)} ASC",
                                                               nameof(RunEntity.SteamId)),
                                })
                                .MergeTable()
                                .Where(t => t.RowNum == 1)
                                .ToListAsync();

            // Clear RowNum helper field before upsert
            foreach (var row in rows)
            {
                row.RowNum = 0;
            }

            await UpsertSeedBestRowsAsync(mapId, runType, rows);
        }
        catch
        {
            _bestRunSeededCache.TryRemove(seedKey, out _);

            throw;
        }
    }

    private async Task UpsertPlayerBestRunAsync(RunEntity run)
    {
        var now = DateTime.UtcNow;

        var entity = new PlayerBestRunEntity
        {
            SteamId   = run.SteamId,
            MapId     = run.MapId,
            RunType   = run.RunType,
            Stage     = run.Stage,
            Style     = run.Style,
            Track     = run.Track,
            RunId     = run.Id,
            BestTime  = run.Time,
            UpdatedAt = now,
        };

        // Engine-agnostic upsert via SqlSugar Storageable (same pattern as UpsertSeedBestRowsAsync, so no
        // per-dialect SQL). ToStorage() probes existence by the unique key and routes to insert-vs-update.
        //
        // The probe + insert are two non-atomic statements, so a concurrent writer (the cross-player seed
        // path, or a delete) can race between them. We therefore still guard the INSERT and, on a unique
        // violation (row appeared after the probe), fall through to the conditional UPDATE — this self-heals
        // the race the way the old UPDATE->INSERT->catch path did. The key difference from the old code is
        // that the COMMON non-improving finish now takes the UPDATE branch directly via the probe and never
        // throws; the exception is hit only on a genuine concurrent insert, not on every returning player.
        var storage = _db.Storageable(entity)
                         .WhereColumns(x => new
                         {
                             x.SteamId,
                             x.MapId,
                             x.RunType,
                             x.Style,
                             x.Track,
                             x.Stage,
                         })
                         .ToStorage();

        if (storage.InsertList.Count > 0)
        {
            try
            {
                await storage.AsInsertable.ExecuteCommandAsync();

                return;
            }
            catch
            {
                // Row was inserted by a concurrent writer between the probe and this insert (the seed race).
                // Fall through to the conditional UPDATE so a better time still wins. On PostgreSQL the
                // surrounding transaction would be aborted by the violation, so rethrow there and let the
                // caller roll back+retry rather than run a doomed UPDATE on a poisoned transaction.
                if (_db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
                {
                    throw;
                }
            }
        }

        // Row exists (or just appeared): overwrite ONLY when the new run is strictly better under
        // (Time ASC, RunId ASC). The conditional WHERE preserves "faster time wins, lower RunId breaks
        // ties"; a slower or equal finish updates 0 rows and leaves the stored best untouched. A concurrent
        // delete between probe and update simply matches 0 rows here — the next finish re-seeds the row.
        await _db.Updateable<PlayerBestRunEntity>()
                 .SetColumns(x => x.RunId     == run.Id)
                 .SetColumns(x => x.BestTime  == run.Time)
                 .SetColumns(x => x.UpdatedAt == now)
                 .Where(x => x.SteamId    == run.SteamId
                             && x.MapId   == run.MapId
                             && x.RunType == run.RunType
                             && x.Style   == run.Style
                             && x.Track   == run.Track
                             && x.Stage   == run.Stage
                             && (x.BestTime > run.Time
                                 || (x.BestTime == run.Time && x.RunId > run.Id)))
                 .ExecuteCommandAsync();
    }

    private async Task UpsertSeedBestRowsAsync(ulong mapId, RunType runType, List<SeedBestRunRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var now      = DateTime.UtcNow;
        var entities = new List<PlayerBestRunEntity>(rows.Count);

        foreach (var row in rows)
        {
            entities.Add(new PlayerBestRunEntity
            {
                SteamId   = row.SteamId,
                MapId     = mapId,
                RunType   = runType,
                Stage     = row.Stage,
                Style     = row.Style,
                Track     = row.Track,
                RunId     = row.RunId,
                BestTime  = row.BestTime,
                UpdatedAt = now,
            });
        }

        var storage = _db.Storageable(entities)
                         .WhereColumns(x => new
                         {
                             x.SteamId,
                             x.MapId,
                             x.RunType,
                             x.Style,
                             x.Track,
                             x.Stage,
                         })
                         .ToStorage();

        if (storage.InsertList.Count > 0)
        {
            await storage.AsInsertable.ExecuteCommandAsync();
        }

        if (storage.UpdateList.Count > 0)
        {
            await storage.AsUpdateable
                         .UpdateColumns(x => new { x.RunId, x.BestTime, x.UpdatedAt })
                         .ExecuteCommandAsync();
        }
    }

    private void RemoveBestRunSeedCacheForMap(ulong mapId)
    {
        // Iterate the ConcurrentDictionary struct enumerators directly; .Keys would snapshot each entire
        // keyset into a fresh List + ReadOnlyCollection per call. The enumerator allocates nothing and is
        // safe to remove from while enumerating.
        foreach (var kvp in _bestRunMapSeededCache)
        {
            if (kvp.Key.mapId == mapId)
            {
                _bestRunMapSeededCache.TryRemove(kvp.Key, out _);
            }
        }

        foreach (var kvp in _bestRunSeededCache)
        {
            if (kvp.Key.mapId == mapId)
            {
                _bestRunSeededCache.TryRemove(kvp.Key, out _);
            }
        }
    }

    private sealed class SeedBestRunRow
    {
        [SugarColumn(ColumnDataType = "bigint", SqlParameterDbType = typeof(SteamIdDataConvert))]
        public SteamID SteamId { get; set; }

        public int Style { get; set; }

        public ushort Track { get; set; }

        public ushort Stage { get; set; }

        public ulong RunId { get; set; }

        public float BestTime { get; set; }

        [SugarColumn(IsOnlyIgnoreInsert = true, IsOnlyIgnoreUpdate = true)]
        public int RowNum { get; set; }
    }
}
