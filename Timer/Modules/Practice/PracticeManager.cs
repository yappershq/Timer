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
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Models.Timer;

namespace Source2Surf.Timer.Modules.Practice;

[Flags]
internal enum EPracticeFlags : byte
{
    None      = 0,
    Practice  = 1 << 0,
    Segmented = 1 << 1,
}

internal sealed partial class PracticeManager : IModule,
                                                IPracticeModule,
                                                IPlayerManagerListener,
                                                ITimerModuleListener
{
    private const int MaxLocsPerPlayer = 64;

    private readonly InterfaceBridge            _bridge;
    private readonly ITimerModule               _timerModule;
    private readonly IPlayerManager             _playerManager;
    private readonly ICommandManager            _commandManager;
    private readonly ILogger<PracticeManager>   _logger;

    private readonly EPracticeFlags[]   _state  = new EPracticeFlags[PlayerSlot.MaxPlayerCount];
    private readonly List<SavedLoc>?[]  _locs   = new List<SavedLoc>?[PlayerSlot.MaxPlayerCount];
    private readonly int[]              _cursor = new int[PlayerSlot.MaxPlayerCount];

    public PracticeManager(InterfaceBridge          bridge,
                           ITimerModule             timerModule,
                           IPlayerManager           playerManager,
                           ICommandManager          commandManager,
                           ILogger<PracticeManager> logger)
    {
        _bridge         = bridge;
        _timerModule    = timerModule;
        _playerManager  = playerManager;
        _commandManager = commandManager;
        _logger         = logger;
    }

    public bool Init()
    {
        _playerManager.RegisterListener(this);
        _timerModule.RegisterListener(this);

        InitCommands();

        return true;
    }

    public void Shutdown()
    {
        _playerManager.UnregisterListener(this);
        _timerModule.UnregisterListener(this);
    }

    public bool IsInPractice(IGameClient client)
        => IsInPractice(client.Slot);

    public bool IsInPractice(PlayerSlot slot) => (_state[slot] & EPracticeFlags.Practice) != 0;

    public bool IsInSegmented(IGameClient client) => IsInSegmented(client.Slot);

    public bool IsInSegmented(PlayerSlot slot) => (_state[slot] & EPracticeFlags.Segmented) != 0;

    public bool SaveLoc(IGameClient client)
    {
        if (TryResolveAlivePawn(client, out var pawn) is not { } controller)
        {
            return false;
        }

        var moveType = pawn.ActualMoveType;

        if (moveType is MoveType.NoClip or MoveType.Observer)
        {
            controller.PrintToChat("Cannot saveloc while noclipping or spectating.");
            return false;
        }

        if (_timerModule.GetTimerInfo(client.Slot) is { Status: ETimerStatus.Paused })
        {
            controller.PrintToChat("Cannot saveloc while the timer is paused.");

            return false;
        }

        var slot = client.Slot;
        var locs = _locs[slot] ??= new List<SavedLoc>(8);

        var timerSnapshot      = _timerModule.CaptureTimerSnapshot(slot);
        var stageTimerSnapshot = _timerModule.CaptureStageTimerSnapshot(slot);
        var track              = timerSnapshot?.State.Track ?? 0;

        var segmented = IsSegmentedStyle(timerSnapshot?.State.Style ?? 0);

        var loc = new SavedLoc
        {
            SteamId    = client.SteamId,
            Segmented  = segmented,
            Track      = track,
            Physics    = CapturePhysics(pawn),
            Timer      = timerSnapshot,
            StageTimer = stageTimerSnapshot,
        };

        locs.Add(loc);

        if (locs.Count > MaxLocsPerPlayer)
        {
            locs.RemoveAt(0);
        }

        _cursor[slot] = locs.Count - 1;

        _state[slot] |= segmented ? EPracticeFlags.Segmented : EPracticeFlags.Practice;

        controller.PrintToChat($"Saved location #{locs.Count}.");
        return true;
    }

    public bool TeleportToLoc(IGameClient client, int index = -1)
    {
        if (TryResolveAlivePawn(client, out var pawn) is not { } controller)
        {
            return false;
        }

        var slot = client.Slot;

        if (_locs[slot] is not { Count: > 0 } locs)
        {
            controller.PrintToChat("No saved locations.");
            return false;
        }

        if (index < 0)
        {
            index = _cursor[slot];
        }

        if ((uint) index >= (uint) locs.Count)
        {
            controller.PrintToChat($"Invalid loc index. Valid: 1..{locs.Count}");
            return false;
        }

        var loc = locs[index];

        ApplyPhysics(pawn, loc.Physics);

        var forcePractice = !loc.Segmented || loc.SteamId != client.SteamId;

        if (forcePractice)
        {
            _state[slot] |= EPracticeFlags.Practice;
        }
        else
        {
            _state[slot] |= EPracticeFlags.Segmented;
        }

        if (loc.Timer is { } timerSnapshot)
        {
            _timerModule.RestoreTimerSnapshot(slot, timerSnapshot);
        }

        if (loc.StageTimer is { } stageSnapshot)
        {
            _timerModule.RestoreStageTimerSnapshot(slot, stageSnapshot);
        }

        _cursor[slot] = index;

        controller.PrintToChat($"Teleported to loc #{index + 1}/{locs.Count}.");
        return true;
    }

    public bool TeleportNext(IGameClient client)
    {
        var slot = client.Slot;

        if (_locs[slot] is not { Count: > 0 } locs)
        {
            return TeleportToLoc(client, -1);
        }

        var nextIndex = _cursor[slot] + 1;

        if (nextIndex >= locs.Count)
        {
            client.GetPlayerController()?.PrintToChat("Already at the last loc.");
            return false;
        }

        return TeleportToLoc(client, nextIndex);
    }

    public bool TeleportPrev(IGameClient client)
    {
        var slot = client.Slot;

        if (_locs[slot] is not { Count: > 0 } locs)
        {
            return TeleportToLoc(client, -1);
        }

        var prevIndex = _cursor[slot] - 1;

        if (prevIndex < 0)
        {
            client.GetPlayerController()?.PrintToChat("Already at the first loc.");
            return false;
        }

        return TeleportToLoc(client, prevIndex);
    }

    public int GetLocCount(IGameClient client) => GetLocCount(client.Slot);

    public int GetLocCount(PlayerSlot slot) => _locs[slot]?.Count ?? 0;

    public int GetCurrentLoc(IGameClient client) => _cursor[client.Slot];

    public void ClearLocs(IGameClient client)
    {
        var slot = client.Slot;

        _locs[slot]?.Clear();
        _cursor[slot] = 0;

        client.GetPlayerController()?.PrintToChat("Cleared all saved locations.");
    }

    public void OnClientPutInServer(PlayerSlot slot)
    {
        _state[slot]  = EPracticeFlags.None;
        _locs[slot]   = null;
        _cursor[slot] = 0;
    }

    public void OnClientDisconnected(PlayerSlot slot)
    {
        _state[slot]  = EPracticeFlags.None;
        _locs[slot]   = null;
        _cursor[slot] = 0;
    }

    public void OnPlayerTimerStart(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        _state[controller.PlayerSlot] = EPracticeFlags.None;
    }

    private static bool IsSegmentedStyle(int style)
        => false;

    private static PhysicsSnapshot CapturePhysics(IPlayerPawn pawn)
    {
        var movement = pawn.GetMovementService()?.AsPlayerMovementService();

        return new PhysicsSnapshot(
            Origin:         pawn.GetAbsOrigin(),
            Angles:         pawn.GetEyeAngles(),
            Velocity:       pawn.GetAbsVelocity(),
            BaseVelocity:   pawn.BaseVelocity,
            MoveType:       pawn.ActualMoveType,
            Flags:          pawn.Flags,
            GravityScale:   pawn.GravityScale,
            LaggedMovement: pawn.GetNetVar<float>("m_flLaggedMovementValue"),
            Stamina:        movement?.Stamina   ?? 0f,
            Ducked:         pawn.GetNetVar<bool>("m_bDucked"),
            Ducking:        pawn.GetNetVar<bool>("m_bDucking"),
            DuckAmount:     pawn.GetNetVar<float>("m_flDuckAmount"),
            DuckSpeed:      movement?.DuckSpeed ?? 7.0f,
            LadderNormal:   pawn.GetNetVar<Vector>("m_vecLadderNormal"));
    }

    private static void ApplyPhysics(IPlayerPawn pawn, PhysicsSnapshot p)
    {
        pawn.SetMoveType(p.MoveType);
        pawn.Flags        = p.Flags;
        pawn.GravityScale = p.GravityScale;
        pawn.BaseVelocity = p.BaseVelocity;

        pawn.SetNetVar("m_flLaggedMovementValue", p.LaggedMovement);
        pawn.SetNetVar("m_bDucked",               p.Ducked);
        pawn.SetNetVar("m_bDucking",              p.Ducking);
        pawn.SetNetVar("m_flDuckAmount",          p.DuckAmount);
        pawn.SetNetVar("m_vecLadderNormal",       p.LadderNormal);

        if (pawn.GetMovementService()?.AsPlayerMovementService() is { } movement)
        {
            movement.Stamina   = p.Stamina;
            movement.DuckSpeed = p.DuckSpeed;
        }

        pawn.Teleport(p.Origin, p.Angles, p.Velocity);
    }

    private static IPlayerController? TryResolveAlivePawn(IGameClient client, out IPlayerPawn pawn)
    {
        pawn = null!;

        if (client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return null;
        }

        if (controller.GetPlayerPawn() is not { IsValidEntity: true, IsAlive: true } resolved)
        {
            controller.PrintToChat("You must be alive to use practice commands.");
            return null;
        }

        pawn = resolved;
        return controller;
    }
}
