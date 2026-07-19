using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.RequestManager.Scheduling;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl : IRequestManager
{
    private readonly SqlSugarScope               _db;
    private readonly ILogger<StorageServiceImpl> _logger;
    private readonly ConcurrentDictionary<string, ulong> _mapIdCache = new (StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(ulong mapId, ushort track), (int tier, int basePot)> _trackScoreConfigCache = new();
    private readonly ConcurrentDictionary<(ulong mapId, RunType runType), byte> _bestRunMapSeededCache = new();
    private readonly ConcurrentDictionary<(ulong mapId, RunType runType, int style, ushort track, ushort stage), byte> _bestRunSeededCache = new();
    private readonly ScoreRecalcScheduler        _scoreRecalcScheduler;

    internal SqlSugarScope Db => _db;

    public StorageServiceImpl(DbType dbType, string connectionString, ILogger<StorageServiceImpl> logger)
    {
        _db     = CreateClient(dbType, connectionString);
        _logger = logger;
        _scoreRecalcScheduler = new ScoreRecalcScheduler(HandleScoreRecalcAsync, logger);
    }

    private async Task HandleScoreRecalcAsync(RecalcRequest request)
    {
        // No cross-server lock needed: PlayerEntity.Points is recomputed by a single atomic
        // UPDATE ... = (SELECT SUM ...) statement (see UpdatePlayerTotalPointsAsync), which is immune to the
        // lost-update race even when two servers recalc overlapping players concurrently.
        await RecalculateTrackScoresAsync(request.MapId, request.Style, request.Track, request.Tier, request.BasePot, request.StyleFactor);
    }

    public void Init()
    {
        _mapIdCache.Clear();
        _trackScoreConfigCache.Clear();
        _bestRunMapSeededCache.Clear();
        _bestRunSeededCache.Clear();

        try
        {
            _db.CodeFirst.InitTables(typeof(MapEntity),
                                     typeof(MapTrackEntity),
                                     typeof(PlayerEntity),
                                     typeof(PlayerMapStatsEntity),
                                     typeof(PlayerBestRunEntity),
                                     typeof(PlayerTrackScoreEntity),
                                     typeof(RunEntity),
                                     typeof(RunSegmentEntity),
                                     typeof(ReplayEntity),
                                     typeof(ZoneEntity));
        }
        catch (Exception e)
        {
            // Propagate: a failed InitTables means the DB is unreachable/broken. Swallowing
            // it would report Init success, register this backend, and displace the working
            // LiteDB fallback. The idempotent index/column migrations below may self-swallow;
            // table creation must not.
            _logger.LogError(e, "Error when initializing tables");

            throw;
        }

        MigrateReplaySteamIdColumn();
        EnsureTrackScoreCoveringIndex();
    }

    /// <summary>
    /// Ensures the covering index that serves the score-recalc delta read
    /// (WHERE MapId=? AND Style=? AND Track=?, no SteamId predicate) exists. SqlSugar's
    /// <c>InitTables</c> creates declared indexes only when it first creates the table, so an existing
    /// production table would otherwise keep full-scanning the scores table on every recalc.
    /// </summary>
    private void EnsureTrackScoreCoveringIndex()
    {
        const string tableName = "surf_player_track_scores";
        const string indexName = "idx_player_track_scores_map_style_track";

        try
        {
            if (!_db.DbMaintenance.IsAnyTable(tableName, false))
            {
                return;
            }

            if (_db.DbMaintenance.IsAnyIndex(indexName))
            {
                return;
            }

            var sql = _db.CurrentConnectionConfig.DbType switch
            {
                DbType.MySql =>
                    $"CREATE INDEX `{indexName}` ON `{tableName}` (`MapId`,`Style`,`Track`,`SteamId`,`Points`)",

                // Unquoted identifiers: SqlSugar creates PG columns unquoted (folded to
                // lowercase); quoted PascalCase never matched, so this silently failed.
                DbType.PostgreSQL =>
                    $"CREATE INDEX IF NOT EXISTS {indexName} ON {tableName} (MapId,Style,Track,SteamId,Points)",
                _ => null,
            };

            if (sql is null)
            {
                return;
            }

            _db.Ado.ExecuteCommand(sql);
            _logger.LogInformation("Created covering index {Index} on {Table}", indexName, tableName);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to ensure covering index {Index} on {Table}", indexName, tableName);
        }
    }

    private void MigrateReplaySteamIdColumn()
    {
        const string tableName  = "surf_runs_replay";
        const string columnName = "SteamId";

        try
        {
            if (!_db.DbMaintenance.IsAnyTable(tableName, false))
            {
                return;
            }

            var columns = _db.DbMaintenance.GetColumnInfosByTableName(tableName, false);

            var steamIdCol = columns?.Find(c => string.Equals(c.DbColumnName, columnName, StringComparison.OrdinalIgnoreCase));

            if (steamIdCol is null)
            {
                return;
            }

            var rawType = steamIdCol.DataType?.Trim().ToLowerInvariant() ?? string.Empty;

            if (rawType is "bigint" or "int8" or "long")
            {
                return;
            }

            _logger.LogWarning("Migrating {Table}.{Column} from '{Type}' to BIGINT (steam64 stored as signed Int64)",
                               tableName, columnName, rawType);

            var dbType = _db.CurrentConnectionConfig.DbType;

            var sql = dbType switch
            {
                DbType.MySql      => $"ALTER TABLE `{tableName}` MODIFY COLUMN `{columnName}` BIGINT NOT NULL",
                DbType.PostgreSQL => $"ALTER TABLE {tableName} ALTER COLUMN {columnName} TYPE BIGINT USING {columnName}::bigint",
                _                 => null,
            };

            if (sql is null)
            {
                _logger.LogError("Unsupported DbType {DbType} for {Table}.{Column} migration", dbType, tableName, columnName);
                return;
            }

            _db.Ado.ExecuteCommand(sql);
            _logger.LogInformation("Migrated {Table}.{Column} to BIGINT", tableName, columnName);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to migrate {Table}.{Column} to BIGINT", tableName, columnName);
        }
    }

    public void Shutdown()
    {
        _scoreRecalcScheduler.Dispose();
        _db.Dispose();
    }

    public async Task<MapProfile> GetMapInfo(string map)
    {
        var mapKey  = ToMapKey(map);
        var mapInfo = await EnsureMapEntityByKeyAsync(mapKey, map);

        var trackTiers = await _db.Queryable<MapTrackEntity>()
                                  .Where(x => x.MapId == mapInfo.MapId)
                                  .ToListAsync();

        return ToMapProfile(mapInfo, trackTiers);
    }

    public async Task UpdateMapInfo(MapProfile info)
    {
        var mapKey = ToMapKey(info.MapName);

        ulong mapId;

        await _db.Ado.BeginTranAsync();

        try
        {
            var mapInfo = await FindMapByNameAsync(mapKey);

            if (mapInfo is null)
            {
                mapInfo = new ()
                {
                    File          = mapKey,
                    Tier          = GetTier(info.Tier, 0),
                    Stages        = ToUInt16(info.Stages),
                    BasePot       = 0,
                    Bonuses       = info.Bonuses,
                    PlayCount     = info.PlayCount,
                    TotalPlayTime = info.TotalPlayTime,
                };

                // ExecuteReturn*Identity does NOT write the id back into the entity —
                // without this, MapId stays 0 and poisons the track-tier rows + map-id cache.
                var newId = await _db.Insertable(mapInfo).ExecuteReturnBigIdentityAsync();
                mapInfo.MapId = unchecked((ulong) newId);
            }
            else
            {
                mapInfo.File          = mapKey;
                mapInfo.Tier          = GetTier(info.Tier, 0);
                mapInfo.Stages        = ToUInt16(info.Stages);
                mapInfo.Bonuses       = info.Bonuses;
                mapInfo.PlayCount     = info.PlayCount;
                mapInfo.TotalPlayTime = info.TotalPlayTime;

                await _db.Updateable(mapInfo).ExecuteCommandAsync();
            }

            await SyncMapTrackTiersAsync(mapInfo.MapId, info.Tier);

            await _db.Ado.CommitTranAsync();

            mapId = mapInfo.MapId;
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();

            throw;
        }

        // Cache writes only after a successful commit, so a rollback can never leave
        // the caches holding state from a transaction that never happened.
        _mapIdCache[mapKey] = mapId;
        InvalidateTrackScoreConfigCache(mapId);
    }

}
