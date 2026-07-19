using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Source2Surf.Timer.Common;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    private async Task<MapEntity?> FindMapByNameAsync(string mapName)
    {
        return await _db.Queryable<MapEntity>()
                        .Where(x => x.File == mapName)
                        .FirstAsync();
    }

    internal async Task<ulong?> ResolveMapIdByNameAsync(string mapName)
    {
        var mapKey = ToMapKey(mapName);

        if (_mapIdCache.TryGetValue(mapKey, out var cachedMapId))
        {
            return cachedMapId;
        }

        var mapEntity = await FindMapByNameAsync(mapKey);

        if (mapEntity is null)
        {
            return null;
        }

        _mapIdCache[mapKey] = mapEntity.MapId;

        return mapEntity.MapId;
    }

    internal async Task<ulong> EnsureMapIdByNameAsync(string mapName)
    {
        var mapKey = ToMapKey(mapName);

        if (_mapIdCache.TryGetValue(mapKey, out var cachedMapId))
        {
            return cachedMapId;
        }

        return (await EnsureMapEntityByKeyAsync(mapKey, mapName)).MapId;
    }

    private async Task<MapEntity> EnsureMapEntityByKeyAsync(string mapKey, string mapName)
    {
        var mapEntity = await FindMapByNameAsync(mapKey);

        if (mapEntity is null)
        {
            mapEntity = new ()
            {
                File   = mapKey,
                Tier   = 1,
                Stages = 0,
            };

            try
            {
                var newId = await _db.Insertable(mapEntity).ExecuteReturnBigIdentityAsync();
                mapEntity.MapId = unchecked((ulong) newId);
            }
            catch (Exception ex)
            {
                // Usually a duplicate-key race with another server inserting the same map —
                // the re-read below picks up the winning row. If the re-read finds nothing,
                // the insert genuinely failed: surface it instead of returning (and caching)
                // an unsaved entity with MapId 0.
                if (await FindMapByNameAsync(mapKey) is not { } raced)
                {
                    _logger.LogError(ex, "Failed to insert map row for {map}", mapName);

                    throw;
                }

                _logger.LogDebug(ex, "Map insert raced for map {map}; using the winning row.", mapName);
                mapEntity = raced;
            }
        }

        _mapIdCache[mapKey] = mapEntity.MapId;

        return mapEntity;
    }

    private async Task SyncMapTrackTiersAsync(ulong mapId, byte[]? tiers)
    {
        await _db.Deleteable<MapTrackEntity>()
                 .Where(x => x.MapId == mapId)
                 .ExecuteCommandAsync();

        if (tiers is null || tiers.Length <= 1)
        {
            return;
        }

        var entities = new List<MapTrackEntity>(tiers.Length - 1);

        for (var index = 1; index < Math.Min(tiers.Length, MapProfile.DefaultTrackCount); index++)
        {
            var tier = tiers[index];

            if (tier == 0)
            {
                continue;
            }

            entities.Add(new ()
            {
                MapId = mapId,
                Track = (ushort) index,
                Tier  = tier,
            });
        }

        if (entities.Count > 0)
        {
            await _db.Insertable(entities).ExecuteCommandAsync();
        }
    }

    private static SqlSugarScope CreateClient(DbType dbType, string connectionString) =>
        new (new ConnectionConfig
        {
            DbType                = dbType,
            ConnectionString      = connectionString,
            IsAutoCloseConnection = true,
            InitKeyType           = InitKeyType.Attribute,
            ConfigureExternalServices
                = new ConfigureExternalServices { SerializeService = new SteamIdAwareSerializeService() },
        });

    /// <summary>
    ///     Custom SqlSugar serialize service that registers <see cref="SteamIdJsonConverter" />
    ///     so that SteamID can be deserialized from Int64 in anonymous/POCO projections.
    /// </summary>
    private sealed class SteamIdAwareSerializeService : ISerializeService
    {
        private static readonly JsonSerializerSettings Settings = new () { Converters = { new SteamIdJsonConverter() } };
        private static readonly SerializeService       Default  = new ();

        public string SerializeObject(object value) => Default.SerializeObject(value);

        public string SugarSerializeObject(object value) => Default.SugarSerializeObject(value);

        public T DeserializeObject<T>(string value) =>
            JsonConvert.DeserializeObject<T>(value, Settings)!;
    }

    private static byte GetTier(byte[]? tiers, int track)
    {
        if (tiers is null || track < 0 || track >= tiers.Length || tiers[track] == 0)
        {
            return 1;
        }

        return tiers[track];
    }

    private static ushort ToUInt16(int value)
    {
        if (value <= 0)
        {
            return 0;
        }

        if (value >= ushort.MaxValue)
        {
            return ushort.MaxValue;
        }

        return (ushort) value;
    }

    private static uint ToUInt32(int value)
    {
        if (value <= 0)
        {
            return 0;
        }

        if (value == int.MaxValue)
        {
            return int.MaxValue;
        }

        return (uint) value;
    }

    private static int ToInt32(uint value)
    {
        if (value >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int) value;
    }

    private static int NormalizeLimit(int limit)
    {
        if (limit <= 0)
        {
            return 1;
        }

        return limit >= IRequestManager.DefaultRecordLimit ? IRequestManager.DefaultRecordLimit : limit;
    }

    private static string ToMapKey(string mapName)
        => mapName.ToLowerInvariant();

    private void InvalidateTrackScoreConfigCache(ulong mapId)
    {
        // Iterate the ConcurrentDictionary's allocation-free struct enumerator directly. Accessing .Keys
        // would snapshot the entire keyset into a fresh List + ReadOnlyCollection on every call; the
        // enumerator allocates nothing and is safe to remove from during enumeration.
        foreach (var kvp in _trackScoreConfigCache)
        {
            if (kvp.Key.mapId == mapId)
            {
                _trackScoreConfigCache.TryRemove(kvp.Key, out _);
            }
        }
    }

    public async Task<IReadOnlyList<string>> GetAllMapNamesAsync()
    {
        var maps = await _db.Queryable<MapEntity>()
            .Select(x => x.File)
            .ToListAsync();

        return maps;
    }

    private sealed class AttemptBestTimesRow
    {
        public float? ServerBestTime { get; set; }

        public float? PlayerBestTime { get; set; }
    }

}
