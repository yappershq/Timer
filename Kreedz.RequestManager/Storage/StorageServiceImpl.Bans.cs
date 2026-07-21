using System;
using System.Threading.Tasks;
using Sharp.Shared.Units;
using Kreedz.Common.Entities;
using Kreedz.Shared.Models;
using SqlSugar;

namespace Kreedz.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    public async Task AddBanAsync(SteamID steamId, string? reason, DateTime expiresAt)
    {
        var entity = new BanEntity
        {
            Id        = Guid.NewGuid().ToString(),
            SteamId   = steamId,
            Reason    = reason,
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow,
        };

        await _db.Insertable(entity).ExecuteCommandAsync();
    }

    public async Task<int> RemoveBansAsync(SteamID steamId)
        => await _db.Deleteable<BanEntity>()
                    .Where(b => b.SteamId == steamId)
                    .ExecuteCommandAsync();

    public async Task SaveInfractionAsync(SteamID steamId, string type, string? details)
        => await _db.Insertable(new InfractionEntity
                    {
                        Id        = Guid.NewGuid().ToString(),
                        SteamId   = steamId,
                        Type      = type,
                        Details   = details,
                        CreatedAt = DateTime.UtcNow,
                    }).ExecuteCommandAsync();

    public async Task SaveJumpAsync(SteamID steamId, string jumpType, float distance, int strafes, float sync, float gain, float maxSpeed, float height)
        => await _db.Insertable(new JumpEntity
                    {
                        Id        = Guid.NewGuid().ToString(),
                        SteamId   = steamId,
                        JumpType  = jumpType,
                        Distance  = distance,
                        Strafes   = strafes,
                        Sync      = sync,
                        Gain      = gain,
                        MaxSpeed  = maxSpeed,
                        Height    = height,
                        CreatedAt = DateTime.UtcNow,
                    }).ExecuteCommandAsync();

    public async Task<BanRecord?> GetActiveBanAsync(SteamID steamId)
    {
        var now = DateTime.UtcNow;

        var entity = await _db.Queryable<BanEntity>()
                              .Where(b => b.SteamId == steamId && b.ExpiresAt > now)
                              .OrderBy(b => b.ExpiresAt, OrderByType.Desc)
                              .FirstAsync();

        return entity is null
            ? null
            : new BanRecord
            {
                Id        = entity.Id,
                SteamId   = entity.SteamId.AsPrimitive(),
                Reason    = entity.Reason,
                ExpiresAt = entity.ExpiresAt,
                CreatedAt = entity.CreatedAt,
            };
    }
}
