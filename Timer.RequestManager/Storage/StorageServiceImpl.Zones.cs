using System.Collections.Generic;
using System.Threading.Tasks;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Models.Zone;
using SqlSugar;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    public async Task<IReadOnlyList<ZoneData>> GetZonesAsync(string mapName)
    {
        var mapId = await ResolveMapIdByNameAsync(mapName);

        if (mapId is null)
        {
            return [];
        }

        var entities = await _db.Queryable<ZoneEntity>()
                                .Where(z => z.MapId == mapId.Value)
                                .ToListAsync();

        var result = new List<ZoneData>(entities.Count);

        foreach (var entity in entities)
        {
            result.Add(ZoneEntityMapper.ToData(entity));
        }

        return result;
    }

    public async Task SaveZonesAsync(string mapName, IReadOnlyList<ZoneData> zones)
    {
        var mapId = await EnsureMapIdByNameAsync(mapName);

        await _db.Ado.BeginTranAsync();

        try
        {
            await _db.Deleteable<ZoneEntity>()
                     .Where(z => z.MapId == mapId)
                     .ExecuteCommandAsync();

            if (zones.Count > 0)
            {
                var entities = new List<ZoneEntity>(zones.Count);

                foreach (var zone in zones)
                {
                    entities.Add(ZoneEntityMapper.ToEntity(zone, mapId));
                }

                await _db.Insertable(entities).ExecuteCommandAsync();
            }

            await _db.Ado.CommitTranAsync();
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();

            throw;
        }
    }
}
