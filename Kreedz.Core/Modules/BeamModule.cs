/*
 * !beam — cs2kz beam service (src/kz/beam), env_beam edition. Draws a trail behind the player while
 * airborne: a fixed ring of env_beam segments per enabled player is repositioned round-robin each tick
 * (no spawn/kill churn), giving a ~1.5s trail at 64t. Preference-persisted ("beam").
 *
 * Deviations from cs2kz (documented): cs2kz renders via its own shipped particle file (kz.vpcf) with
 * feet/ground variants and a configurable offset — we have no custom particle addon, so this is the
 * classic beam-segment trail at feet height; one style, fixed color. Teleport artifacts are guarded by
 * a segment-length cap instead of cs2kz's teleportedThisTick flag.
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

internal sealed class BeamModule : IModule
{
    private const int   RingSize      = 96;   // segments per player (~1.5s trail at 64t)
    private const float MaxSegmentLen = 100f; // longer = teleport artifact, skip
    private const float FeetOffsetZ   = 2f;

    private readonly InterfaceBridge     _bridge;
    private readonly ICommandManager     _commandManager;
    private readonly IPreferencesModule  _prefs;
    private readonly ILogger<BeamModule> _logger;

    private readonly bool[]          _enabled  = new bool[PlayerSlot.MaxPlayerCount];
    private readonly IBaseEntity?[][] _rings   = new IBaseEntity?[PlayerSlot.MaxPlayerCount][];
    private readonly int[]           _head     = new int[PlayerSlot.MaxPlayerCount];
    private readonly Vector[]        _lastPos  = new Vector[PlayerSlot.MaxPlayerCount];
    private readonly bool[]          _wasAir   = new bool[PlayerSlot.MaxPlayerCount];

    public BeamModule(InterfaceBridge bridge, ICommandManager commandManager, IPreferencesModule prefs, ILogger<BeamModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _prefs          = prefs;
        _logger         = logger;
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("beam", (slot, _) =>
        {
            Toggle(slot);
            return ECommandAction.Handled;
        });

        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        _prefs.Loaded += OnPreferencesLoaded;
        return true;
    }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);
        _prefs.Loaded -= OnPreferencesLoaded;
    }

    private void Toggle(PlayerSlot slot)
    {
        _enabled[slot] = !_enabled[slot];
        _prefs.Set(slot, "beam", _enabled[slot] ? "1" : "0");

        if (!_enabled[slot])
            KillRing(slot);

        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            Loc.Chat(_bridge.LocalizerManager, client, _enabled[slot] ? "Kreedz_Beam_On" : "Kreedz_Beam_Off");
    }

    private void OnPreferencesLoaded(PlayerSlot slot)
        => _enabled[slot] = _prefs.Get(slot, "beam") == "1";

    private void OnProcessMovePre(Sharp.Shared.HookParams.IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient || !arg.Pawn.IsAlive)
            return;

        var slot = client.Slot;
        if (!_enabled[slot])
            return;

        var origin   = arg.Pawn.GetAbsOrigin();
        var airborne = !arg.Pawn.GroundEntityHandle.IsValid() && arg.Pawn.ActualMoveType == MoveType.Walk;

        if (airborne && _wasAir[slot])
        {
            var dx = origin.X - _lastPos[slot].X;
            var dy = origin.Y - _lastPos[slot].Y;
            var dz = origin.Z - _lastPos[slot].Z;
            var len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

            if (len is > 1f and < MaxSegmentLen)
                DrawSegment(slot,
                    new Vector(_lastPos[slot].X, _lastPos[slot].Y, _lastPos[slot].Z + FeetOffsetZ),
                    new Vector(origin.X, origin.Y, origin.Z + FeetOffsetZ));
        }

        _lastPos[slot] = origin;
        _wasAir[slot]  = airborne;
    }

    private void DrawSegment(PlayerSlot slot, Vector from, Vector to)
    {
        var ring = _rings[slot] ??= new IBaseEntity?[RingSize];
        var i    = _head[slot];
        _head[slot] = (i + 1) % RingSize;

        var beam = ring[i];
        if (beam is not { IsValidEntity: true })
        {
            var kv = new Dictionary<string, KeyValuesVariantValueItem>
            {
                { "rendercolor", "0 255 128" },
                { "BoltWidth", "1.5" },
            };
            beam = _bridge.EntityManager.SpawnEntitySync<IBaseModelEntity>("env_beam", kv);
            if (beam is not { IsValidEntity: true })
                return;
            ring[i] = beam;
        }

        beam.SetAbsOrigin(from);
        beam.SetNetVar("m_vecEndPos", to);
    }

    private void KillRing(PlayerSlot slot)
    {
        if (_rings[slot] is not { } ring)
            return;

        foreach (var beam in ring)
            if (beam is { IsValidEntity: true })
                beam.Kill();

        Array.Clear(ring);
        _head[slot] = 0;
    }
}
