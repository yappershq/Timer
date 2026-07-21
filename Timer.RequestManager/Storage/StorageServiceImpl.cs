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
                                     typeof(ZoneEntity),
                                     typeof(BanEntity));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when initializing tables");
        }

        MigrateReplaySteamIdColumn();
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
                DbType.PostgreSQL => $"ALTER TABLE \"{tableName}\" ALTER COLUMN \"{columnName}\" TYPE BIGINT USING \"{columnName}\"::bigint",
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

        await _db.Ado.BeginTranAsync();

        try
        {
            var mapInfo = await FindMapByNameAsync(mapKey);

            if (mapInfo is null)
            {
                mapInfo = new ()
                {
                    File    = mapKey,
                    Tier    = GetTier(info.Tier, 0),
                    Stages  = ToUInt16(info.Stages),
                    BasePot = 0,
                };

                await _db.Insertable(mapInfo).ExecuteReturnIdentityAsync();
            }
            else
            {
                mapInfo.File   = mapKey;
                mapInfo.Tier   = GetTier(info.Tier, 0);
                mapInfo.Stages = ToUInt16(info.Stages);

                await _db.Updateable(mapInfo).ExecuteCommandAsync();
            }

            await SyncMapTrackTiersAsync(mapInfo.MapId, info.Tier);

            _mapIdCache[mapKey] = mapInfo.MapId;
            InvalidateTrackScoreConfigCache(mapInfo.MapId);

            await _db.Ado.CommitTranAsync();
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();

            throw;
        }

    }

}
