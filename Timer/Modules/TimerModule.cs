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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Modules.Timer;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Models.Style;
using Source2Surf.Timer.Shared.Models.Timer;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules;

internal interface ITimerModule
{
    void RegisterListener(ITimerModuleListener listener);

    void UnregisterListener(ITimerModuleListener listener);

    ITimerInfo? GetTimerInfo(PlayerSlot slot);

    ITimerInfo? GetStageTimerInfo(PlayerSlot slot);

    void StopTimer(PlayerSlot slot);

    bool PauseTimer(PlayerSlot slot);

    bool ResumeTimer(PlayerSlot slot);

    /// <summary>
    ///     Capture the full timer state for the given slot, or null if no timer
    ///     exists. Used by the practice saveloc system to take a restorable
    ///     snapshot of the main timer.
    /// </summary>
    TimerStateSnapshot? CaptureTimerSnapshot(PlayerSlot slot);

    /// <summary>
    ///     Capture the full stage timer state. Mirrors
    ///     <see cref="CaptureTimerSnapshot"/> for the stage track.
    /// </summary>
    StageTimerStateSnapshot? CaptureStageTimerSnapshot(PlayerSlot slot);

    /// <summary>
    ///     Restore a previously captured main timer snapshot. Deliberately does
    ///     not fire any timer listener callbacks — restoring is not the same as
    ///     a fresh timer start.
    /// </summary>
    void RestoreTimerSnapshot(PlayerSlot slot, TimerStateSnapshot snapshot);

    /// <summary>
    ///     Restore a previously captured stage timer snapshot. Like
    ///     <see cref="RestoreTimerSnapshot"/>, no listeners are notified.
    /// </summary>
    void RestoreStageTimerSnapshot(PlayerSlot slot, StageTimerStateSnapshot snapshot);
}

// TODO:
// WRCP time

internal partial class TimerModule : ITimerModule, IModule, IZoneModuleListener, IPlayerManagerListener
{
    private readonly InterfaceBridge      _bridge;
    private readonly ICommandManager      _commandManager;
    private readonly IEventHookManager    _eventHook;
    private readonly ILogger<TimerModule> _logger;
    private readonly ListenerHub<ITimerModuleListener> _listenerHub;

    private readonly IPlayerManager  _playerManager;
    private readonly IMapInfoModule  _mapInfoModule;
    private readonly IStyleModule    _styleModule;

    private readonly TimerInfo?[]      _timerInfo;
    private readonly StageTimerInfo?[] _stageTimerInfo;
    private readonly bool[]            _authenticated;
    private readonly PauseState?[]     _pauseState;

    private static readonly TraceShapeHull StandingHull = new()
    {
        Mins = new (-16, -16, -16),
        Maxs = new (16, 16, 72),
    };

    private static readonly TraceShapeHull DuckedHull = new()
    {
        Mins = new (-16, -16, -16),
        Maxs = new (16, 16, 54),
    };

    // Downward trace distance for the surf-slope check — one ducked hull height
    // (DuckedHull.Maxs.Z) below the origin.
    private const float SurfTraceDepth = 54f;

    // Prejump-limit grace window: jumps only count against GetMaxPrejumps while the
    // player has been grounded for at most this many ticks.
    private const int PrejumpGraceTicks = 10;

    private readonly IZoneModule _zoneModule;

    private readonly IConVar sv_standable_normal;

    public TimerModule(InterfaceBridge      bridge,
                       IPlayerManager       playerManager,
                       IZoneModule          zoneModule,
                       IMapInfoModule       mapInfoModule,
                       IStyleModule         styleModule,
                       IEventHookManager    eventHook,
                       ICommandManager      commandManager,
                       ILogger<TimerModule> logger)
    {
        _bridge         = bridge;
        _playerManager  = playerManager;
        _zoneModule     = zoneModule;
        _mapInfoModule  = mapInfoModule;
        _styleModule    = styleModule;
        _eventHook      = eventHook;
        _commandManager = commandManager;

        _logger      = logger;
        _listenerHub = new ListenerHub<ITimerModuleListener>(logger);

        _timerInfo      = new TimerInfo?[PlayerSlot.MaxPlayerCount];
        _stageTimerInfo = new StageTimerInfo?[PlayerSlot.MaxPlayerCount];
        _authenticated  = new bool[PlayerSlot.MaxPlayerCount];
        _pauseState     = new PauseState?[PlayerSlot.MaxPlayerCount];

        sv_standable_normal = bridge.ConVarManager.FindConVar("sv_standable_normal")!;

        // ReSharper disable InconsistentNaming
        if (bridge.ConVarManager.FindConVar("view_punch_decay") is { } view_punch_decay)
        {
            view_punch_decay.Flags &= ~ConVarFlags.Cheat;

            view_punch_decay.Set(1000000f);
        }

        if (bridge.ConVarManager.FindConVar("sv_suppress_viewpunch", true) is { } sv_suppress_viewpunch)
        {
            sv_suppress_viewpunch.Flags &= ~ConVarFlags.DevelopmentOnly;

            sv_suppress_viewpunch.Set(1);
        }

        // ReSharper restore InconsistentNaming
    }

    public bool Init()
    {
        _eventHook.ListenEvent("player_jump", OnPlayerJump);
        _eventHook.HookEvent("player_team", hk_OnPlayerJoinTeam);

        _bridge.HookManager.HandleCommandJoinTeam.InstallHookPost(OnHandleCommandJoinTeamPost);

        _bridge.HookManager.PlayerRunCommand.InstallHookPost(OnPlayerRunCommandPost);
        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnPlayerProcessMovePre);
        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);

        _zoneModule.RegisterListener(this);

        _playerManager.RegisterListener(this);

        InitCommands();

        return true;
    }

    public void OnPostInit(ServiceProvider provider)
    {
    }

    public void Shutdown()
    {
        _bridge.HookManager.HandleCommandJoinTeam.RemoveHookPost(OnHandleCommandJoinTeamPost);

        _bridge.HookManager.PlayerRunCommand.RemoveHookPost(OnPlayerRunCommandPost);

        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnPlayerProcessMovePre);
        _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);

        _zoneModule.UnregisterListener(this);

        _playerManager.UnregisterListener(this);
    }

    public void OnZoneStartTouch(IZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
        if (!pawn.IsAlive || controller.IsFakeClient)
        {
            return;
        }

        if (_timerInfo[controller.PlayerSlot] is not { } timerInfo
            || _stageTimerInfo[controller.PlayerSlot] is not { } stageTimer)
        {
            return;
        }

        if (info.Track != timerInfo.Track)
        {
            return;
        }

        timerInfo.UpdateInZone(info.ZoneType);

        var velocity = pawn.GetAbsVelocity();

        switch (info.ZoneType)
        {
            case EZoneType.Start:
            {
                timerInfo.StopTimer();

                // Clamp speed on entering start zone: kill momentum from flying in, don't preserve Z velocity
                var enterLimit = _mapInfoModule.GetEnterSpeedLimit(info.Track);

                if (enterLimit > 0.0f)
                {
                    if (LimitSpeed(ref velocity, enterLimit, false))
                    {
                        pawn.SetAbsVelocity(velocity);
                    }
                }

                break;
            }
            case EZoneType.End:
            {
                if (stageTimer.IsTimerRunning())
                {
                    stageTimer.EndVelocity = velocity;
                    NotifyPlayerStageTimerFinish(controller, pawn, stageTimer);
                    stageTimer.StopTimer();
                }

                if (timerInfo.IsTimerRunning())
                {
                    timerInfo.EndVelocity = velocity;

                    if (_zoneModule.CurrentTrackHasCheckpoints(timerInfo.Track)
                        && timerInfo.CurrentCheckpointInfo is { } currentCp)
                    {
                        currentCp.Sync = timerInfo.Sync;
                        timerInfo.AddCheckpoint(currentCp);
                    }

                    NotifyPlayerFinishMap(controller, pawn, timerInfo);

                    timerInfo.StopTimer();
                }

                break;
            }
            case EZoneType.Stage:
            {
                if (!stageTimer.IsTimerRunning())
                {
                    return;
                }

                var newStageIndex = info.Data;

                // Re-entering the current stage (e.g. teleported back after failing) — restart stage timer
                if (newStageIndex == stageTimer.Stage)
                {
                    stageTimer.StopTimer();
                    return;
                }

                // Going backwards or skipping stages
                if (newStageIndex < stageTimer.Stage || newStageIndex != stageTimer.Stage + 1)
                {
                    timerInfo.StopTimer();
                    stageTimer.StopTimer();

                    controller.PrintToChat("Missing stages, stopping timer");

                    return;
                }

                stageTimer.EndVelocity = velocity;

                NotifyPlayerStageTimerFinish(controller, pawn, stageTimer);

                stageTimer.StopTimer();
                stageTimer.Stage = newStageIndex;

                // Clamp speed on entering stage start: only applies when main timer isn't running (practicing a stage),
                // to avoid ruining a full run when passing through a stage zone
                if (!timerInfo.IsTimerRunning())
                {
                    var enterLimit = _mapInfoModule.GetStageEnterSpeedLimit(info.Track);

                    if (enterLimit > 0.0f)
                    {
                        if (LimitSpeed(ref velocity, enterLimit, false))
                        {
                            pawn.SetAbsVelocity(velocity);
                        }
                    }
                }

                break;
            }
            case EZoneType.Checkpoint:
            {
                if (!timerInfo.IsTimerRunning())
                {
                    return;
                }

                if (timerInfo.CurrentCheckpointInfo is not { } checkpointInfo)
                {
                    return;
                }

                var newCheckpointIndex = info.Data;

                // going backwards or touching the same checkpoint? 
                if (newCheckpointIndex <= timerInfo.Checkpoint)
                {
                    return;
                }

                if (newCheckpointIndex != timerInfo.Checkpoint + 1)
                {
                    timerInfo.StopTimer();
                    pawn.PrintToChat("Timer stopped: missing checkpoints");

                    return;
                }

                checkpointInfo.TimerTick   = timerInfo.TimerTick;
                checkpointInfo.EndVelocity = velocity;
                checkpointInfo.Sync        = timerInfo.Sync;

                timerInfo.AddCheckpoint(checkpointInfo);

                timerInfo.CurrentCheckpointInfo = new ()
                {
                    StartVelocity = velocity,
                    MaxVelocity   = velocity,
                };

                timerInfo.Checkpoint = newCheckpointIndex;

                NotifyReachCheckpoint(controller, pawn, timerInfo, newCheckpointIndex);

                break;
            }
            case EZoneType.StopTimer:
            {
                timerInfo.StopTimer();
                stageTimer.StopTimer();

                break;
            }
        }
    }

    public void OnZoneTrigger(IZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
        if (_timerInfo[controller.PlayerSlot] is not { } timerInfo
            || _stageTimerInfo[controller.PlayerSlot] is not { } stageTimer
            || !pawn.IsAlive
            || controller.IsFakeClient)
        {
            return;
        }

        if (info.ZoneType == EZoneType.StopTimer)
        {
            // should never happen but just in case
            if (info.Track == timerInfo.Track && timerInfo.IsTimerRunning())
            {
                timerInfo.StopTimer();
            }

            if (info.Track == stageTimer.Track && stageTimer.IsTimerRunning())
            {
                stageTimer.StopTimer();
            }
        }

        timerInfo.UpdateInZone(info.ZoneType);
        stageTimer.UpdateInZone(info.ZoneType);
    }

    public void OnZoneEndTouch(IZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
        if (_timerInfo[controller.PlayerSlot] is not { } timerInfo
            || _stageTimerInfo[controller.PlayerSlot] is not { } stageTimer
            || !pawn.IsAlive
            || controller.IsFakeClient)
        {
            return;
        }

        if (info.Track != timerInfo.Track)
        {
            return;
        }

        timerInfo.UpdateInZone(EZoneType.Invalid);
        stageTimer.UpdateInZone(EZoneType.Invalid);

        var velocity = pawn.GetAbsVelocity();

        switch (info.ZoneType)
        {
            case EZoneType.Start:
            {
                if (!_zoneModule.HasZone(timerInfo.Track, EZoneType.End))
                {
                    return;
                }

                if (!_authenticated[controller.PlayerSlot])
                {
                    controller.PrintToChat("Your Steam account has not been verified yet. The server may not be connected to Steam. Please wait and try again.");
                    return;
                }

                if (!CanAllListenersStartTimer(controller, pawn))
                {
                    return;
                }

                // Clamp speed on leaving start zone: ZoneConfig > Style > GameMode priority, preserve Z velocity
                var style = _styleModule.GetStyleSetting(timerInfo.Style);
                var exitLimit = GetEffectiveExitSpeedLimit(info.Track, style);

                if (exitLimit > 0.0f)
                {
                    if (LimitSpeed(ref velocity, exitLimit, true))
                    {
                        pawn.SetAbsVelocity(velocity);
                    }
                }

                timerInfo.StartTimer(info.Track, velocity);
                NotifyPlayerTimerStart(controller, pawn, timerInfo);

                if (_zoneModule.CurrentTrackHasCheckpoints(timerInfo.Track))
                {
                    timerInfo.CurrentCheckpointInfo = new ()
                    {
                        StartVelocity = velocity,
                        MaxVelocity   = velocity,
                    };
                }
                else
                {
                    timerInfo.CurrentCheckpointInfo = null;
                }

                if (_zoneModule.IsCurrentTrackLinear(timerInfo.Track))
                {
                    return;
                }

                stageTimer.StartTimer(info.Track, velocity, 1);
                NotifyPlayerStageTimerStart(controller, pawn, stageTimer);

                break;
            }
            case EZoneType.Stage:
            {
                if (_zoneModule.IsCurrentTrackLinear(timerInfo.Track) || stageTimer.Stage != info.Data)
                {
                    return;
                }

                // Clamp speed on leaving stage start: only applies when main timer isn't running (practicing a stage),
                // to avoid ruining a full run when passing through a stage zone
                if (!timerInfo.IsTimerRunning())
                {
                    var style = _styleModule.GetStyleSetting(timerInfo.Style);
                    var exitLimit = GetEffectiveStageExitSpeedLimit(info.Track, style);

                    if (exitLimit > 0.0f)
                    {
                        if (LimitSpeed(ref velocity, exitLimit, true))
                        {
                            pawn.SetAbsVelocity(velocity);
                        }
                    }
                }

                stageTimer.StartTimer(info.Track, velocity, info.Data);
                NotifyPlayerStageTimerStart(controller, pawn, stageTimer);

                break;
            }
        }
    }

    public ITimerInfo? GetTimerInfo(PlayerSlot slot)
        => _timerInfo[slot];

    public ITimerInfo? GetStageTimerInfo(PlayerSlot slot)
        => _stageTimerInfo[slot];

    public TimerStateSnapshot? CaptureTimerSnapshot(PlayerSlot slot)
        => _timerInfo[slot]?.CaptureState();

    public StageTimerStateSnapshot? CaptureStageTimerSnapshot(PlayerSlot slot)
        => _stageTimerInfo[slot]?.CaptureState() as StageTimerStateSnapshot;

    public void RestoreTimerSnapshot(PlayerSlot slot, TimerStateSnapshot snapshot)
        => _timerInfo[slot]?.RestoreState(snapshot);

    public void RestoreStageTimerSnapshot(PlayerSlot slot, StageTimerStateSnapshot snapshot)
        => _stageTimerInfo[slot]?.RestoreState(snapshot);

    public void RegisterListener(ITimerModuleListener listener)
        => _listenerHub.Register(listener);

    public void UnregisterListener(ITimerModuleListener listener)
        => _listenerHub.Unregister(listener);

    private void Restart(PlayerSlot slot, int track = -1)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client)
        {
            return;
        }

        if (client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return;
        }

        if (controller.GetPlayerPawn() is not { IsValidEntity: true } pawn)
        {
            return;
        }

        if (!pawn.IsAlive)
        {
            return;
        }

        if (_timerInfo[slot] is { } timerInfo)
        {
            timerInfo.StopTimer();
            timerInfo.ChangeTrack(track);
        }

        if (_stageTimerInfo[slot] is { } stageTimer)
        {
            stageTimer.StopTimer();
            stageTimer.ChangeTrack(track);
        }

        _zoneModule.TeleportToZone(pawn, track, EZoneType.Start);
    }

    private void NotifyPlayerTimerStart(IPlayerController controller, IPlayerPawn pawn, TimerInfo timerInfo)
        => _listenerHub.NotifyAll("OnPlayerTimerStart",
                                  static (l, c, p, t) => l.OnPlayerTimerStart(c, p, t),
                                  controller, pawn, timerInfo);

    private void NotifyPlayerFinishMap(IPlayerController controller, IPlayerPawn pawn, TimerInfo timerInfo)
        => _listenerHub.NotifyAll("OnPlayerFinishMap",
                                  static (l, c, p, t) => l.OnPlayerFinishMap(c, p, t),
                                  controller, pawn, timerInfo);

    private void NotifyPlayerStageTimerStart(IPlayerController controller, IPlayerPawn pawn, StageTimerInfo stageTimerInfo)
        => _listenerHub.NotifyAll("OnPlayerStageTimerStart",
                                  static (l, c, p, t) => l.OnPlayerStageTimerStart(c, p, t),
                                  controller, pawn, stageTimerInfo);

    private void NotifyPlayerStageTimerFinish(IPlayerController controller, IPlayerPawn pawn, StageTimerInfo stageTimerInfo)
        => _listenerHub.NotifyAll("OnPlayerStageTimerFinish",
                                  static (l, c, p, t) => l.OnPlayerStageTimerFinish(c, p, t),
                                  controller, pawn, stageTimerInfo);

    private void NotifyReachCheckpoint(IPlayerController controller, IPlayerPawn pawn, TimerInfo timerInfo, int checkpoint)
        => _listenerHub.NotifyAll("OnReachCheckpoint",
                                  static (l, c, p, t, cp) => l.OnReachCheckpoint(c, p, t, cp),
                                  controller, pawn, timerInfo, checkpoint);

    private bool CanAllListenersStartTimer(IPlayerController controller, IPlayerPawn pawn)
        => _listenerHub.All("CanStartTimer",
                            static (l, c, p) => l.CanStartTimer(c, p),
                            controller, pawn);

    public void StopTimer(PlayerSlot slot)
    {
        if (_timerInfo[slot] is { } timerInfo)
        {
            timerInfo.StopTimer();
        }

        if (_stageTimerInfo[slot] is { } stageTimer)
        {
            stageTimer.StopTimer();
        }

        _pauseState[slot] = null;
    }

    public bool PauseTimer(PlayerSlot slot)
    {
        if (_timerInfo[slot] is not { } timerInfo || !timerInfo.IsTimerRunning())
        {
            return false;
        }

        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller
            || controller.GetPlayerPawn() is not { IsValidEntity: true, IsAlive: true } pawn)
        {
            return false;
        }

        // Must be on ground, not on a ladder, and not moving
        var onGround = pawn.GroundEntityHandle.IsValid();
        var onLadder = pawn.ActualMoveType == MoveType.Ladder;
        var velocity = pawn.GetAbsVelocity();
        var isMoving = velocity.LengthSqr() > 0;

        if (!onGround || onLadder || isMoving)
        {
            return false;
        }

        // Save position and angles before pausing
        _pauseState[slot] = new PauseState(pawn.GetAbsOrigin(), pawn.GetEyeAngles());

        timerInfo.PauseTimer();

        if (_stageTimerInfo[slot] is { } stageTimer && stageTimer.IsTimerRunning())
        {
            stageTimer.PauseTimer();
        }

        return true;
    }

    public bool ResumeTimer(PlayerSlot slot)
    {
        if (_timerInfo[slot] is not { } timerInfo || !timerInfo.IsTimerPaused())
        {
            return false;
        }

        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller
            || controller.GetPlayerPawn() is not { IsValidEntity: true, IsAlive: true } pawn)
        {
            return false;
        }

        // Restore saved position and angles
        if (_pauseState[slot] is { } state)
        {
            pawn.Teleport(state.Origin, state.Angles, new Vector());
            _pauseState[slot] = null;
        }

        timerInfo.ResumeTimer();

        if (_stageTimerInfo[slot] is { } stageTimer && stageTimer.IsTimerPaused())
        {
            stageTimer.ResumeTimer();
        }

        return true;
    }

    private static bool LimitSpeed(ref Vector velocity, float speedLimit, bool saveZ)
    {
        var currentSpeed2D = velocity.Length2D();

        var needsScale  = currentSpeed2D > speedLimit;
        var needsClearZ = !saveZ && velocity.Z != 0;

        if (!needsScale && !needsClearZ)
        {
            return false;
        }

        if (needsScale)
        {
            var scale = speedLimit / currentSpeed2D;
            velocity.X *= scale;
            velocity.Y *= scale;
        }

        if (needsClearZ)
        {
            velocity.Z = 0;
        }

        return true;
    }

    private float GetEffectiveExitSpeedLimit(int track, StyleSetting style)
    {
        if (_mapInfoModule.GetZoneExitSpeedOverride(track) is { } zoneOverride)
        {
            return zoneOverride;
        }

        if (style.CustomPreSpeed)
        {
            return style.PreSpeed;
        }

        return _mapInfoModule.GetGameModeExitSpeedLimit();
    }

    private float GetEffectiveStageExitSpeedLimit(int track, StyleSetting style)
    {
        if (_mapInfoModule.GetStageExitSpeedOverride(track) is { } stageOverride)
        {
            return stageOverride;
        }

        if (style.CustomPreSpeed)
        {
            return style.PreSpeed;
        }

        return _mapInfoModule.GetGameModeExitSpeedLimit();
    }

    private static HookReturnValue<bool> hk_OnPlayerJoinTeam(EventHookParams arg)
        => new (EHookAction.SkipCallReturnOverride);

    private static void OnHandleCommandJoinTeamPost(IHandleCommandJoinTeamHookParams @params, HookReturnValue<bool> hook)
    {
        if (@params.Team == 1)
        {
            return;
        }

        @params.Controller.Respawn();
    }

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams arg)
    {
        var client = arg.Client;

        var pawn = arg.Pawn;

        if (_timerInfo[client.Slot] is { } timerInfo)
        {
            _zoneModule.TeleportToZone(pawn, timerInfo.Track, EZoneType.Start);
        }
        else
        {
            _zoneModule.TeleportToZone(pawn, 0, EZoneType.Start);
        }
    }

    public void OnClientPutInServer(PlayerSlot slot)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: true })
        {
            return;
        }

        _timerInfo[slot]      = new ();
        _stageTimerInfo[slot] = new ();

        // when the server is not connected to the steam server, or maybe GC server,
        // it won't trigger OnClientPostAdminCheck, and we want to save records from authenticated players
        _authenticated[slot]  = false;
    }

    public void OnClientDisconnected(PlayerSlot slot)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: true })
        {
            return;
        }

        _timerInfo[slot]      = null;
        _stageTimerInfo[slot] = null;
        _authenticated[slot]  = false;
        _pauseState[slot]     = null;
    }

    public void OnClientInfoLoaded(SteamID steamId)
    {
        if (_bridge.ClientManager.GetGameClient(steamId) is { IsFakeClient: false } client)
        {
            _authenticated[client.Slot] = true;
        }
    }

}

internal readonly record struct PauseState(Vector Origin, Vector Angles);
