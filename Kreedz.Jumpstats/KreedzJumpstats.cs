/*
 * yappershq/Kreedz (KZ) — Jumpstats plugin (cs2kz src/kz/jumpstats)
 *
 * A standalone ModSharp module (split out of Core, like the mode/style/anticheat plugins). Depends only
 * on ISharedSystem primitives + the public IKzStyleRegistry (to void styled jumps), so a server can
 * install or omit it.
 *
 * Detects takeoffs/landings on the per-tick movement hook, computes jump distance, classifies LongJump
 * vs Bhop, and reports distance + tier (Meh→Wrecker). The full 1:1 stat set (sync/strafes/badAngles/
 * overlap/edge/block, per-mode tier tables, strict validation) needs the native movement AACall telemetry
 * — it layers on once the CKZ movement detours carry physics (they're pass-through today).
 */

using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Jumpstats;

// cs2kz JumpType (the classifiable subset from managed per-tick data).
public enum JumpType { LongJump, Bhop, MultiBhop, WeirdJump, LadderJump, Ladderhop, Fall, Other }

public enum DistanceTier { None, Meh, Impressive, Perfect, Godlike, Ownage, Wrecker }

public sealed class KreedzJumpstats : IModSharpModule
{
    private const float OffsetUnits = 32f;   // KZ block offset added to raw horizontal distance
    private const int   PerfTicks   = 2;     // ground ticks <= this before takeoff -> bhop (perf-ish)
    private const float MinTierDist = 217f;  // below the Meh threshold -> not announced

    private readonly ISharedSystem            _shared;
    private readonly IHookManager             _hookManager;
    private readonly IClientManager           _clientManager;
    private readonly ILogger<KreedzJumpstats> _logger;

    private IKzStyleRegistry? _styles;
    private IRequestManager?  _request; // resolved cross-plugin for jump persistence

    private readonly bool[]     _wasOnGround = new bool[PlayerSlot.MaxPlayerCount];
    private readonly int[]      _groundTicks = new int[PlayerSlot.MaxPlayerCount];
    private readonly bool[]     _tracking    = new bool[PlayerSlot.MaxPlayerCount];
    private readonly Vector[]   _takeoff     = new Vector[PlayerSlot.MaxPlayerCount];
    private readonly JumpType[] _type        = new JumpType[PlayerSlot.MaxPlayerCount];

    // Per-jump stat accumulators (cs2kz Jump stats — computed from per-tick velocity + view angle).
    private readonly float[] _takeoffSpeed = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _maxSpeed     = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _maxHeight    = new float[PlayerSlot.MaxPlayerCount];
    private readonly int[]   _airTicks     = new int[PlayerSlot.MaxPlayerCount];
    private readonly int[]   _gainTicks    = new int[PlayerSlot.MaxPlayerCount];
    private readonly int[]   _strafes      = new int[PlayerSlot.MaxPlayerCount];
    private readonly float[] _lastSpeed    = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _lastYaw      = new float[PlayerSlot.MaxPlayerCount];
    private readonly int[]   _lastYawDir   = new int[PlayerSlot.MaxPlayerCount];

    // Jump-type classification state.
    private readonly JumpType[] _prevType    = new JumpType[PlayerSlot.MaxPlayerCount];
    private readonly int[]      _ladderTicks = new int[PlayerSlot.MaxPlayerCount]; // ticks since on a ladder
    private const float JumpImpulseZ = 150f; // takeoff vz above this = a real jump (vs walking off = fall)
    private const int   LadderWindow = 4;    // takeoff within N ticks of a ladder = ladder jump/hop

    public string DisplayName   => "[Kreedz] Jumpstats";
    public string DisplayAuthor => "yappershq";

    public KreedzJumpstats(ISharedSystem shared, string? dllPath, string? sharpPath, Version? version, IConfiguration? coreConfiguration, bool hotReload)
    {
        _shared        = shared;
        _hookManager   = shared.GetHookManager();
        _clientManager = shared.GetClientManager();
        _logger        = shared.GetLoggerFactory().CreateLogger<KreedzJumpstats>();
    }

    public bool Init()
    {
        _hookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        return true;
    }

    public void OnAllModulesLoaded()
    {
        var mgr = _shared.GetSharpModuleManager();
        _styles  = mgr.GetOptionalSharpModuleInterface<IKzStyleRegistry>(IKzStyleRegistry.Identity)?.Instance;
        _request = mgr.GetOptionalSharpModuleInterface<IRequestManager>(IRequestManager.Identity)?.Instance;
    }

    public void Shutdown() => _hookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);

    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient) return;

        var pawn = arg.Pawn;
        if (!pawn.IsAlive) return;

        var slot     = client.Slot;
        var onGround = pawn.GroundEntityHandle.IsValid();
        var origin   = pawn.GetAbsOrigin();

        var vel   = pawn.GetAbsVelocity();
        var horiz = MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y);
        var yaw   = pawn.GetEyeAngles().Y;

        var onLadder = pawn.ActualMoveType is MoveType.Ladder;
        _ladderTicks[slot] = onLadder ? 0 : _ladderTicks[slot] + 1;

        if (_wasOnGround[slot] && !onGround)
        {
            // Takeoff — classify the jump + reset accumulators.
            _takeoff[slot]       = origin;
            _type[slot]          = Classify(slot, vel.Z, _ladderTicks[slot] <= LadderWindow);
            _prevType[slot]      = _type[slot];
            _tracking[slot]      = pawn.ActualMoveType is MoveType.Walk; // ignore noclip starts
            _takeoffSpeed[slot]  = horiz;
            _maxSpeed[slot]      = horiz;
            _maxHeight[slot]     = 0f;
            _airTicks[slot]      = 0;
            _gainTicks[slot]     = 0;
            _strafes[slot]       = 0;
            _lastSpeed[slot]     = horiz;
            _lastYaw[slot]       = yaw;
            _lastYawDir[slot]    = 0;
        }
        else if (!onGround && _tracking[slot])
        {
            // Airborne — accumulate per-tick stats.
            _airTicks[slot]++;
            if (horiz > _lastSpeed[slot] + 0.01f) _gainTicks[slot]++;   // sync: a gaining tick
            if (horiz > _maxSpeed[slot]) _maxSpeed[slot] = horiz;
            var h = origin.Z - _takeoff[slot].Z;
            if (h > _maxHeight[slot]) _maxHeight[slot] = h;

            var dy2 = NormalizeYaw(yaw - _lastYaw[slot]);
            var dir = dy2 > 0.05f ? 1 : dy2 < -0.05f ? -1 : 0;         // strafes: mouse-direction reversals
            if (dir != 0 && _lastYawDir[slot] != 0 && dir != _lastYawDir[slot]) _strafes[slot]++;
            if (dir != 0) _lastYawDir[slot] = dir;

            _lastSpeed[slot] = horiz;
            _lastYaw[slot]   = yaw;
        }
        else if (!_wasOnGround[slot] && onGround && _tracking[slot])
        {
            // Landing.
            _tracking[slot] = false;
            var dx   = origin.X - _takeoff[slot].X;
            var dy   = origin.Y - _takeoff[slot].Y;
            var dist = MathF.Sqrt(dx * dx + dy * dy) + OffsetUnits;

            if (dist >= MinTierDist && _styles?.HasAnyStyle(slot) != true) // styled runs don't count (1:1)
                Report(slot, _type[slot], dist);
        }

        _groundTicks[slot] = onGround ? _groundTicks[slot] + 1 : 0;
        _wasOnGround[slot] = onGround;
    }

    // cs2kz KZJumpstatsService::DetermineJumpType (the subset classifiable from managed per-tick data):
    // jumped-vs-fell from takeoff vz, ladder recency, and the previous jump's type for bhop chaining.
    private JumpType Classify(PlayerSlot slot, float takeoffVz, bool fromLadder)
    {
        var jumped = takeoffVz > JumpImpulseZ;
        if (fromLadder) return jumped ? JumpType.Ladderhop : JumpType.LadderJump;
        if (!jumped)    return JumpType.Fall;

        if (_groundTicks[slot] <= PerfTicks) // took off within the perf window → a bhop of some kind
            return _prevType[slot] switch
            {
                JumpType.Fall                       => JumpType.WeirdJump,
                JumpType.LongJump                   => JumpType.Bhop,
                JumpType.Bhop or JumpType.MultiBhop => JumpType.MultiBhop,
                _                                   => JumpType.Other,
            };

        return JumpType.LongJump;
    }

    private static string Label(JumpType t) => t switch
    {
        JumpType.LongJump   => "LJ",
        JumpType.Bhop       => "BH",
        JumpType.MultiBhop  => "MBH",
        JumpType.WeirdJump  => "WJ",
        JumpType.LadderJump => "LAJ",
        JumpType.Ladderhop  => "LAH",
        _                   => "JUMP",
    };

    private void Report(PlayerSlot slot, JumpType type, float distance)
    {
        if (type is JumpType.Fall or JumpType.Other) return; // not a scored jump

        var tier = Tier(distance);
        if (tier == DistanceTier.None) return;

        var label   = Label(type);
        var sync    = _airTicks[slot] > 0 ? 100f * _gainTicks[slot] / _airTicks[slot] : 0f;
        var gain    = _maxSpeed[slot] - _takeoffSpeed[slot];

        if (_clientManager.GetGameClient(slot) is not { IsFakeClient: false } client) return;

        client.Print(HudPrintChannel.Chat,
            $"{label}: {distance:0.0}u — {tier}!  |  {_strafes[slot]} strafes · {sync:0}% sync · " +
            $"{_maxSpeed[slot]:0} max · {gain:+0;-0} gain · {_maxHeight[slot]:0.0}u height");

        // Persist the jump (jumpstats DB) — fire-and-forget, degrades to no-op without the request manager.
        if (_request is { } req)
            _ = SaveJumpAsync(req, client.SteamId, label, distance, _strafes[slot], sync, gain, _maxSpeed[slot], _maxHeight[slot]);
    }

    private async System.Threading.Tasks.Task SaveJumpAsync(IRequestManager req, Sharp.Shared.Units.SteamID sid,
        string type, float dist, int strafes, float sync, float gain, float maxSpeed, float height)
    {
        try { await req.SaveJumpAsync(sid, type, dist, strafes, sync, gain, maxSpeed, height); }
        catch (Exception e) { _logger.LogError(e, "[KZ.Jumpstats] failed to persist jump for {Sid}", sid); }
    }

    private static float NormalizeYaw(float a)
    {
        while (a >  180f) a -= 360f;
        while (a < -180f) a += 360f;
        return a;
    }

    // VNL/CKZ LongJump tier thresholds (ascending). Full per-mode/per-type tables land with the port.
    private static DistanceTier Tier(float d) => d switch
    {
        >= 284f => DistanceTier.Wrecker,
        >= 280f => DistanceTier.Ownage,
        >= 275f => DistanceTier.Godlike,
        >= 270f => DistanceTier.Perfect,
        >= 265f => DistanceTier.Impressive,
        >= 217f => DistanceTier.Meh,
        _       => DistanceTier.None,
    };
}
