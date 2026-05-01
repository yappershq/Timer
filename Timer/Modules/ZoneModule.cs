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
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Listeners;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Modules.Zone;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules;

internal interface IZoneModule
{
    void RegisterListener(IZoneModuleListener listener);

    void UnregisterListener(IZoneModuleListener listener);

    void AddZone(ZoneInfo info);

    bool TeleportToZone(IPlayerPawn pawn, int track, EZoneType type);

    bool IsCurrentTrackLinear(int track);

    bool HasZone(int track, EZoneType type);

    bool CurrentTrackHasCheckpoints(int track);

    int GetTotalStages(int track);

    int GetCurrentTrackCheckpointCount(int track);

    bool TeleportToStage(IPlayerPawn pawn, int track, int stage);
}

// TODO:

internal partial class ZoneModule : IModule, IZoneModule, IEntityListener, IGameListener
{
    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    private readonly InterfaceBridge      _bridge;
    private readonly ICommandManager      _commandManager;
    private readonly IRequestManager      _requestManager;

    private readonly ILogger<ZoneModule> _logger;
    private readonly ListenerHub<IZoneModuleListener> _listenerHub;

    private readonly int[] _currentMaxStages = new int[TimerConstants.MAX_TRACK];

    private readonly Dictionary<uint, ZoneInfo> _zones         = [];
    private readonly BuildZoneInfo?[]           _buildZoneInfo;

    // ReSharper disable InconsistentNaming
    private unsafe delegate* unmanaged<Vector*, Vector*, Vector*, nint> CreateTrigger;

    // ReSharper restore InconsistentNaming

    public ZoneModule(InterfaceBridge     bridge,
                      ICommandManager     commandManager,
                      IRequestManager     requestManager,
                      ILogger<ZoneModule> logger)
    {
        _bridge         = bridge;
        _logger         = logger;
        _listenerHub    = new ListenerHub<IZoneModuleListener>(logger);
        _commandManager = commandManager;
        _requestManager = requestManager;
        _buildZoneInfo  = new BuildZoneInfo?[PlayerSlot.MaxPlayerCount];
    }

    public void OnEntitySpawned(IBaseEntity entity)
    {
        if (entity.Classname != "trigger_multiple")
        {
            return;
        }

        var targetName = entity.Name;
#if DEBUG
        _logger.LogInformation("Entity {classname} spawned with name {targetname}", entity.Classname, targetName);
#endif

        if (string.IsNullOrEmpty(targetName) || string.IsNullOrWhiteSpace(targetName))
        {
            return;
        }

        // Skip zones created by the plugin itself
        if (targetName.StartsWith("surftimer_zone_"))
        {
            return;
        }

        if (AddPrebuiltZone(entity, targetName, EZoneType.Start) || AddPrebuiltZone(entity, targetName, EZoneType.End))
        {
            CreateBeam(entity.Handle);
#if DEBUG
            _logger.LogInformation("Added prebuilt zone: {name}", targetName);
#endif

            entity.SetNetVar("m_flWait", TimerConstants.TickInterval);

            return;
        }

        if (AddPrebuiltZone(entity, targetName, EZoneType.Stage) || AddPrebuiltZone(entity, targetName, EZoneType.Checkpoint))
        {
#if DEBUG
            _logger.LogInformation("Added prebuilt zone: {name}", targetName);
#endif
            entity.SetNetVar("m_flWait", TimerConstants.TickInterval);

            return;
        }

        _logger.LogWarning("{t} is not the zone we want", targetName);
    }

    public void OnEntityDeleted(IBaseEntity entity)
    {
        if (!_zones.Remove(entity.Handle.GetValue(), out var info) || info.Beams is not { } beams)
        {
            return;
        }

        foreach (var beam in beams)
        {
            if (beam is { IsValidEntity: true })
            {
                beam.Kill();
            }
        }
    }

    public EHookAction OnEntityFireOutput(IBaseEntity entity, string output, IBaseEntity? activator, float delay)
    {
        if (activator?.AsPlayerPawn() is not { IsValidEntity: true } pawn
            || pawn.GetController() is not { IsValidEntity : true } controller
            || controller.IsFakeClient)
        {
            return EHookAction.Ignored;
        }

        var entityHandle = entity.Handle.GetValue();

        if (!_zones.TryGetValue(entityHandle, out var info))
        {
            return EHookAction.Ignored;
        }

        switch (output)
        {
            case "onstarttouch":
            {
                NotifyZoneStartTouch(info, controller, pawn);

                break;
            }
            case "onendtouch":
            {
                NotifyZoneEndTouch(info, controller, pawn);

                break;
            }
            case "ontrigger":
            {
                NotifyZoneTrigger(info, controller, pawn);

                break;
            }
        }

        return EHookAction.Ignored;
    }

    public void OnGamePostInit()
    {
        if (!FindTrigger())
        {
            throw new InvalidOperationException("Failed to find CreateTriggerInternal");
        }

        for (var i = 0; i < TimerConstants.MAX_TRACK; i++)
        {
            _currentMaxStages[i] = -1;
        }
    }

    public void OnGameShutdown()
    {
        for (var i = 0; i < TimerConstants.MAX_TRACK; i++)
        {
            _currentMaxStages[i] = -1;
        }

        _zones.Clear();
    }

    public void OnServerActivate()
    {
        Task.Run(async () =>
                 {
                     try
                     {
                         var mapName = _bridge.GlobalVars.MapName;
                         var zones   = await RetryHelper.RetryAsync(
                             () => _requestManager.GetZonesAsync(mapName),
                             RetryHelper.IsTransient, _logger, "GetZonesAsync"
                         ).ConfigureAwait(false);

                         await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                         {
                             foreach (var zoneData in zones)
                             {
                                 var zoneInfo = ZoneMapper.ToZoneInfo(zoneData);
                                 AddZone(zoneInfo);
                             }

                             FindZoneStartPosition();
                         }).ConfigureAwait(false);
                     }
                     catch (Exception e)
                     {
                         _logger.LogError(e, "Failed to load custom zones from database");
                     }
                 },
                 _bridge.CancellationToken);
    }

    public bool Init()
    {
        _bridge.EntityManager.HookEntityOutput("trigger_multiple", "OnStartTouch");
        _bridge.EntityManager.HookEntityOutput("trigger_multiple", "OnEndTouch");
        _bridge.EntityManager.HookEntityOutput("trigger_multiple", "OnTouching");
        _bridge.EntityManager.HookEntityOutput("trigger_multiple", "OnTrigger");
        _bridge.EntityManager.HookEntityOutput("trigger_multiple", "OnTouchingEachEntity");

        _bridge.ModSharp.InstallGameListener(this);
        _bridge.EntityManager.InstallEntityListener(this);

        _bridge.HookManager.PlayerRunCommand.InstallHookPre(OnPlayerRunCommandPre);

        _commandManager.AddAdminChatCommand("zone", [], OnCommandZone);

        return true;
    }

    public void Shutdown()
    {
        _bridge.EntityManager.RemoveEntityListener(this);
        _bridge.ModSharp.RemoveGameListener(this);
        _bridge.HookManager.PlayerRunCommand.RemoveHookPre(OnPlayerRunCommandPre);
        _listenerHub.Clear();
    }

    public void RegisterListener(IZoneModuleListener listener)
        => _listenerHub.Register(listener);

    public void UnregisterListener(IZoneModuleListener listener)
        => _listenerHub.Unregister(listener);

    private void NotifyZoneStartTouch(ZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
        foreach (var listener in _listenerHub.Snapshot)
        {
            try
            {
                listener.OnZoneStartTouch(info, controller, pawn);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error when calling OnZoneStartTouch listener");
            }
        }
    }

    private void NotifyZoneEndTouch(ZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
        foreach (var listener in _listenerHub.Snapshot)
        {
            try
            {
                listener.OnZoneEndTouch(info, controller, pawn);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error when calling OnZoneEndTouch listener");
            }
        }
    }

    private void NotifyZoneTrigger(ZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
        foreach (var listener in _listenerHub.Snapshot)
        {
            try
            {
                listener.OnZoneTrigger(info, controller, pawn);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error when calling OnZoneTrigger listener");
            }
        }
    }

    public unsafe void AddZone(ZoneInfo info)
    {
        var targetName = $"surftimer_{info.Track}_{info.ZoneType}_{info.Data}".ToLowerInvariant();

        var infoCorner1 = info.Corner1;
        var infoCorner2 = info.Corner2;

        var mins = new Vector();
        var maxs = new Vector();

        for (var i = 0; i < 3; i++)
        {
            maxs[i] = Math.Abs(infoCorner1[i] - infoCorner2[i]) / 2.0f;
            mins[i] = -maxs[i];
        }

        var origin = (infoCorner1 + infoCorner2) / 2.0f;
        origin.Z = infoCorner1.Z + 2f;

        var entPtr = CreateTrigger(&origin, &mins, &maxs);

        if (entPtr == 0)
        {
            throw new ("Failed to create trigger");
        }

        if (_bridge.EntityManager.MakeEntityFromPointer<IBaseTrigger>(entPtr) is not { } ent)
        {
            return;
        }

        ent.SetName(targetName);

        ent.SpawnFlags =  4097;
        ent.Effects    |= EntityEffects.NoDraw;
        ent.SetNetVar("m_flWait", TimerConstants.TickInterval);

        info.Origin = origin;
        info.Index  = ent.Index;

        _zones.TryAdd(ent.Handle.GetValue(), info);

        if (info.ZoneType is EZoneType.Start or EZoneType.End)
        {
            CreateBeam(ent.Handle);
        }
    }

    public bool TeleportToZone(IPlayerPawn pawn, int track, EZoneType type)
    {
        foreach (var (_, zoneInfo) in _zones)
        {
            if (zoneInfo.Track != track || type != zoneInfo.ZoneType)
            {
                continue;
            }

            pawn.Teleport(zoneInfo.TeleportOrigin ?? zoneInfo.Origin, null, new Vector());

            return true;
        }

        return false;
    }

    public bool TeleportToStage(IPlayerPawn pawn, int track, int stage)
    {
        foreach (var (_, zoneInfo) in _zones)
        {
            if (zoneInfo.Track != track || zoneInfo.ZoneType != EZoneType.Stage || zoneInfo.Data != stage)
            {
                continue;
            }

            pawn.Teleport(zoneInfo.TeleportOrigin ?? zoneInfo.Origin, null, new Vector());

            return true;
        }

        return false;
    }

    public bool IsCurrentTrackLinear(int track)
        => _currentMaxStages[track] <= 1;

    public bool HasZone(int track, EZoneType type)
    {
        foreach (var (_, info) in _zones)
        {
            if (info.Track == track && info.ZoneType == type)
            {
                return true;
            }
        }

        return false;
    }

    public bool CurrentTrackHasCheckpoints(int track)
    {
        foreach (var (_, info) in _zones)
        {
            if (info.Track == track && info.ZoneType == EZoneType.Checkpoint)
            {
                return true;
            }
        }

        return false;
    }

    public int GetTotalStages(int track)
        => _currentMaxStages[track];

    public int GetCurrentTrackCheckpointCount(int track)
    {
        var count = 0;

        foreach (var (_, info) in _zones)
        {
            if (info.Track == track && info.ZoneType == EZoneType.Checkpoint)
            {
                count++;
            }
        }

        return count;
    }

    private bool AddPrebuiltZone(IBaseEntity entity, string targetName, EZoneType type)
    {
        if (entity.GetCollisionProperty() is not { } collision)
        {
            return false;
        }

        var handle = entity.Handle.GetValue();
        var origin = entity.GetAbsOrigin();
        origin.Z += 2;
        var corner1 = collision.Mins + origin;
        var corner2 = collision.Maxs + origin;

        // NormalizeZonePoints(ref corner1, ref corner2);

        var info = new ZoneInfo
        {
            ZoneType   = type,
            Corner1    = corner1,
            Corner2    = corner2,
            Origin     = origin,
            Index      = entity.Index,
            Prebuilt   = true,
            TargetName = targetName,
        };

        int track;

        switch (type)
        {
            case EZoneType.Start:
            {
                if (!ZoneMatcher.IsBonusStartZone(targetName, out track))
                {
                    return ZoneMatcher.IsStartZone(targetName) && _zones.TryAdd(handle, info);
                }

                if (_currentMaxStages[track] < 1)
                {
                    _currentMaxStages[track] = 1;
                }

                info.Track = track;

                return _zones.TryAdd(handle, info);
            }
            case EZoneType.End:
            {
                if (!ZoneMatcher.IsBonusEndZone(targetName, out track))
                {
                    return ZoneMatcher.IsEndZone(targetName) && _zones.TryAdd(handle, info);
                }

                info.Track = track;

                return _zones.TryAdd(handle, info);
            }
            case EZoneType.Stage:
            {
                if (!ZoneMatcher.IsStageZone(targetName, out var stage))
                {
                    return false;
                }

                if (stage >= _currentMaxStages[0])
                {
                    _currentMaxStages[0] = stage;
                }

                info.Data = stage;

                return _zones.TryAdd(handle, info);
            }
            case EZoneType.Checkpoint:
            {
                if (ZoneMatcher.IsCheckpointZone(targetName, out var cp))
                {
                    info.Data = cp;

                    return _zones.TryAdd(handle, info);
                }

                if (ZoneMatcher.IsBonusCheckpointZone(targetName, out var bonusTrack, out cp))
                {
                    info.Track = bonusTrack;
                    info.Data  = cp;

                    return _zones.TryAdd(handle, info);
                }

                break;
            }
        }

        return false;
    }

    private void CreateBeam(uint handle)
    {
        if (!_zones.TryGetValue(handle, out var val))
        {
            _logger.LogInformation("Failed to get value for entity handle 0x{hand:X}", handle);

            return;
        }

        var p1 = val.Corner1;
        var p2 = val.Corner2;

        Span<Vector> points =
        [
            p1,                     // back,  left,  bottom
            new (p1.X, p2.Y, p1.Z), // back,  right, bottom
            new (p2.X, p2.Y, p1.Z), // front, right, bottom
            new (p2.X, p1.Y, p1.Z), // front, left,  bottom
        ];

        var kv = new Dictionary<string, KeyValuesVariantValueItem>
        {
            { "rendercolor", val.ZoneType == EZoneType.Start ? "0 255 0" : "255 0 0" },
            { "BoltWidth", "6" },
        };

        val.Beams = new IBaseEntity[points.Length];

        for (var i = 0; i < points.Length; i++)
        {
            if (_bridge.EntityManager.SpawnEntitySync<IBaseModelEntity>("env_beam", kv) is not { IsValidEntity: true } beam)
            {
                continue;
            }

            beam.SetAbsOrigin(points[i]);
            beam.SetNetVar("m_vecEndPos", points[i == 3 ? 0 : i + 1]);
            val.Beams[i] = beam;
        }
    }

    private void FindZoneStartPosition()
    {
        IBaseEntity? ent = null;

        while ((ent = _bridge.EntityManager.FindEntityByClassname(ent, "info_teleport_destination")) != null)
        {
            var origin = ent.GetAbsOrigin();

            foreach (var (handle, info) in _zones)
            {
                if (_bridge.EntityManager.FindEntityByHandle(new CEntityHandle<IBaseTrigger>(handle)) is not { } trigger
                    || trigger.GetCollisionProperty() is not { } collision)
                {
                    continue;
                }

                var triggerOrigin = trigger.GetAbsOrigin();

                if (!IsPointInBox(origin, triggerOrigin, collision.BoundingRadius))
                {
                    continue;
                }

                info.TeleportAngles = ent.GetAbsAngles();
                info.TeleportOrigin = origin;

#if DEBUG
                _logger.LogInformation("{name} @ {origin} with angles: {angle} is within zone {zonename}",
                                       ent.Name,
                                       ent.GetAbsOrigin(),
                                       ent.GetAbsAngles(),
                                       info.TargetName);
#endif

                break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPointInBox(in Vector point, in Vector origin, float boundingRadius)
    {
        var mins = origin - boundingRadius;
        var maxs = origin + boundingRadius;

        return mins.X     <= point.X
               && point.X <= maxs.X
               && mins.Y  <= point.Y
               && point.Y <= maxs.Y
               && mins.Z  <= point.Z
               && point.Z <= maxs.Z;
    }

    private unsafe bool FindTrigger()
    {
        if (CreateTrigger != null)
        {
            return true;
        }

        var server = _bridge.Modules.Server;

        var strScript_CreateTrigger = server.FindString("Script_CreateTrigger");

        if (strScript_CreateTrigger == nint.Zero)
        {
            return false;
        }

        var ptr = server.FindPtr(strScript_CreateTrigger);

        if (ptr == nint.Zero)
        {
            return false;
        }

        var func = *(nint*) (ptr + 0x38);

        try
        {
            var reader  = new UnsafeCodeReader(func, 256);
            var decoder = Decoder.Create(64, reader, (ulong) func, DecoderOptions.AMD);

            while (reader.CanReadByte)
            {
                var instruction = decoder.Decode();

                if (instruction.IsInvalid)
                {
                    continue;
                }

                if (instruction is { Code: Code.Call_rel32_64, Op0Kind: OpKind.NearBranch64 })
                {
                    CreateTrigger = (delegate* unmanaged<Vector*, Vector*, Vector*, nint>) instruction.MemoryDisplacement64;

                    return true;
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Error while decoding instructions in FindTrigger");
        }

        return false;
    }
}
