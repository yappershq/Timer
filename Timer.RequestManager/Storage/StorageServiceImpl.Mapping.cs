using System.Collections.Generic;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Models;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    private static MapProfile ToMapProfile(MapEntity mapInfo, IReadOnlyList<MapTrackEntity> trackInfos)
    {
        var tiers = new byte[MapProfile.DefaultTrackCount];
        tiers[0] = (byte) mapInfo.Tier;

        foreach (var trackInfo in trackInfos)
        {
            if (trackInfo.Track >= tiers.Length)
            {
                continue;
            }

            tiers[trackInfo.Track] = (byte) trackInfo.Tier;
        }

        return new ()
        {
            MapId         = mapInfo.MapId,
            MapName       = mapInfo.File,
            Stages        = mapInfo.Stages,
            Bonuses       = mapInfo.Bonuses,
            Tier          = tiers,
            PlayCount     = mapInfo.PlayCount,
            TotalPlayTime = mapInfo.TotalPlayTime,
        };
    }

    private static RunRecord ToRunRecord(RunEntity run)
        => new ()
        {
            Id             = (long) run.Id,
            RunDate        = run.Date,
            SteamId        = run.SteamId.AsPrimitive(),
            MapId          = run.MapId,
            Style          = (int) run.Style,
            Track          = run.Track,
            Stage          = run.Stage,
            Time           = run.Time,
            Jumps          = ToInt32(run.Jumps),
            Strafes        = ToInt32(run.Strafes),
            Sync           = run.Sync,
            VelocityStartX = run.VelocityStartX,
            VelocityStartY = run.VelocityStartY,
            VelocityStartZ = run.VelocityStartZ,
            VelocityAvgX   = run.VelocityAvgX,
            VelocityAvgY   = run.VelocityAvgY,
            VelocityAvgZ   = run.VelocityAvgZ,
            VelocityEndX   = run.VelocityEndX,
            VelocityEndY   = run.VelocityEndY,
            VelocityEndZ   = run.VelocityEndZ,
        };
}
