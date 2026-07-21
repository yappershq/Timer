/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ cp/tp save-loc practice system. Net-new for the KZ port: Timer (surf) had position-save/teleport
 * primitives but no cp/tp loop. Save a checkpoint on the ground/ladder, teleport back, undo, cycle
 * prev/next, and set a custom start position. Every teleport bumps the run's teleport counter — a run
 * with ≥1 teleport is "Standard" (not "Pro"), matching cs2kz. 1:1 with cs2kz `src/kz/checkpoint/`.
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal interface ICheckpointModule
{
    /// <summary>Teleports used this run (≥1 → the run is Standard, not Pro).</summary>
    int GetTeleportCount(PlayerSlot slot);

    /// <summary>Number of checkpoints the player currently has saved.</summary>
    int GetCheckpointCount(PlayerSlot slot);

    /// <summary>Clear a player's checkpoints + teleport counter (call on respawn / map change).</summary>
    void ResetCheckpoints(PlayerSlot slot);

    /// <summary>Reset only the teleport counter, keeping the checkpoint stack (a fresh run starts Pro).</summary>
    void ResetTeleportCount(PlayerSlot slot);
}

internal sealed class CheckpointModule : IModule, ICheckpointModule
{
    private readonly InterfaceBridge           _bridge;
    private readonly ICommandManager           _commandManager;
    private readonly ILogger<CheckpointModule> _logger;

    private readonly List<Checkpoint>[] _checkpoints = new List<Checkpoint>[PlayerSlot.MaxPlayerCount];
    private readonly int[]              _cpIndex     = new int[PlayerSlot.MaxPlayerCount];
    private readonly int[]              _tpCount     = new int[PlayerSlot.MaxPlayerCount];
    private readonly Checkpoint?[]      _startPos    = new Checkpoint?[PlayerSlot.MaxPlayerCount];

    public CheckpointModule(InterfaceBridge bridge, ICommandManager commandManager, ILogger<CheckpointModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _logger         = logger;

        for (var i = 0; i < _checkpoints.Length; i++)
            _checkpoints[i] = new List<Checkpoint>();
    }

    public bool Init()
    {
        Bind("cp",            slot => SetCheckpoint(slot));
        Bind("checkpoint",    slot => SetCheckpoint(slot));
        Bind("tp",            slot => TeleportToCurrent(slot));
        Bind("teleport",      slot => TeleportToCurrent(slot));
        Bind("undo",          slot => Undo(slot));
        Bind("prevcp",        slot => CycleCheckpoint(slot, -1));
        Bind("pcp",           slot => CycleCheckpoint(slot, -1));
        Bind("nextcp",        slot => CycleCheckpoint(slot, +1));
        Bind("ncp",           slot => CycleCheckpoint(slot, +1));
        Bind("setstartpos",   SetStartPos);
        Bind("ssp",           SetStartPos);
        Bind("clearstartpos", ClearStartPos);
        Bind("csp",           ClearStartPos);
        return true;
    }

    private void Bind(string command, Action<PlayerSlot> action)
        => _commandManager.AddClientChatCommand(command, (slot, _) =>
        {
            action(slot);
            return ECommandAction.Handled;
        });

    // ── ICheckpointModule ──────────────────────────────────────────────────

    public int GetTeleportCount(PlayerSlot slot) => _tpCount[slot];

    public int GetCheckpointCount(PlayerSlot slot) => _checkpoints[slot]?.Count ?? 0;

    public void ResetCheckpoints(PlayerSlot slot)
    {
        _checkpoints[slot].Clear();
        _cpIndex[slot] = 0;
        _tpCount[slot] = 0;
    }

    public void ResetTeleportCount(PlayerSlot slot) => _tpCount[slot] = 0;

    // ── cp / tp ────────────────────────────────────────────────────────────

    private void SetCheckpoint(PlayerSlot slot)
    {
        if (GetAlivePawn(slot) is not { } pawn) return;

        var onLadder = pawn.MoveType == MoveType.Ladder;
        if (!pawn.Flags.HasFlag(EntityFlags.OnGround) && !onLadder)
        {
            Msg(slot, "Kreedz_Cp_GroundOnly");
            return;
        }

        var cps = _checkpoints[slot];
        cps.Add(new Checkpoint(pawn.GetAbsOrigin(), pawn.GetEyeAngles(), onLadder));
        _cpIndex[slot] = cps.Count - 1;
        Msg(slot, "Kreedz_Cp_Set", cps.Count);
    }

    private void TeleportToCurrent(PlayerSlot slot)
    {
        var cps = _checkpoints[slot];
        if (cps.Count == 0)
        {
            if (_startPos[slot] is { } sp) TeleportTo(slot, sp);
            else Msg(slot, "Kreedz_Cp_NoneYet");
            return;
        }

        TeleportTo(slot, cps[_cpIndex[slot]]);
    }

    private void Undo(PlayerSlot slot)
    {
        var cps = _checkpoints[slot];
        if (cps.Count == 0) { Msg(slot, "Kreedz_Cp_NothingUndo"); return; }

        cps.RemoveAt(cps.Count - 1);
        _cpIndex[slot] = cps.Count == 0 ? 0 : cps.Count - 1;
        if (cps.Count > 0) TeleportTo(slot, cps[_cpIndex[slot]]);
        Msg(slot, "Kreedz_Cp_Removed", cps.Count);
    }

    private void CycleCheckpoint(PlayerSlot slot, int dir)
    {
        var cps = _checkpoints[slot];
        if (cps.Count == 0) { Msg(slot, "Kreedz_Cp_None"); return; }

        _cpIndex[slot] = Math.Clamp(_cpIndex[slot] + dir, 0, cps.Count - 1);
        TeleportTo(slot, cps[_cpIndex[slot]]);
        Msg(slot, "Kreedz_Cp_Index", _cpIndex[slot] + 1, cps.Count);
    }

    private void SetStartPos(PlayerSlot slot)
    {
        if (GetAlivePawn(slot) is not { } pawn) return;
        _startPos[slot] = new Checkpoint(pawn.GetAbsOrigin(), pawn.GetEyeAngles(), pawn.MoveType == MoveType.Ladder);
        Msg(slot, "Kreedz_Cp_StartSet");
    }

    private void ClearStartPos(PlayerSlot slot)
    {
        _startPos[slot] = null;
        Msg(slot, "Kreedz_Cp_StartClear");
    }

    private void TeleportTo(PlayerSlot slot, Checkpoint cp)
    {
        if (GetAlivePawn(slot) is not { } pawn) return;
        pawn.Teleport(cp.Origin, cp.Angles, new Vector()); // KZ teleport lands you standing (zero velocity)
        _tpCount[slot]++;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private IPlayerPawn? GetAlivePawn(PlayerSlot slot)
        => _bridge.ClientManager.GetGameClient(slot) is { } client
           && client.GetPlayerController() is { IsValidEntity: true } controller
           && controller.GetPlayerPawn() is { IsValidEntity: true, IsAlive: true } pawn
            ? pawn
            : null;

    private void Msg(PlayerSlot slot, string key, params object?[] args)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            Loc.Chat(_bridge.LocalizerManager, client, key, args);
    }
}

/// <summary>A saved player position. Velocity is intentionally not stored — KZ teleports land you standing.</summary>
internal readonly record struct Checkpoint(Vector Origin, Vector Angles, bool OnLadder);
