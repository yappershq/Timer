/*
 * yappershq/Kreedz (KZ) — Jumpstats plugin (cs2kz src/kz/jumpstats)
 *
 * A standalone ModSharp module (split out of Core, like the mode/style/anticheat plugins). Depends only
 * on ISharedSystem primitives + the public IKzStyleRegistry (to void styled jumps), so a server can
 * install or omit it.
 *
 * Detects takeoffs/landings on the per-tick movement hook, computes jump distance, classifies LongJump
 * vs Bhop, and reports distance + tier (Meh→Wrecker) + per-mode tier tables. The angle stats (sync/
 * badAngles/overlap/deadAir/width) come bit-exact from the Core AACall telemetry (IKzMovementTelemetry,
 * fed by the AirAccelerate detour) via the cs2kz Strafe::End classification, with a per-tick fallback when
 * that telemetry is absent. Block/edge on successful landings comes from cs2kz block_tracking.cpp
 * (CalcBlockStats hull sweeps). Still open: failstat/miss (pose-history), ladder block variant, strict validation.
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Jumpstats;

// cs2kz JumpType (kz_jumpstats.h) — order matches cs2kz so it indexes the tier tables directly.
// Jumpbug is present for enum-parity but not yet produced by Classify (needs native duckbug detection).
public enum JumpType { LongJump, Bhop, MultiBhop, WeirdJump, LadderJump, Ladderhop, Jumpbug, Fall, Other }

public enum DistanceTier { None, Meh, Impressive, Perfect, Godlike, Ownage, Wrecker }

public sealed class KreedzJumpstats : IModSharpModule
{
    private const float OffsetUnits = 32f;   // KZ block offset added to raw horizontal distance
    private const int   PerfTicks   = 2;     // ground ticks <= this before takeoff -> bhop (perf-ish)

    private readonly ISharedSystem            _shared;
    private readonly IHookManager             _hookManager;
    private readonly IClientManager           _clientManager;
    private readonly ILogger<KreedzJumpstats> _logger;

    private const float JsEpsilon = 0.03125f; // cs2kz JS_EPSILON

    // Block/edge tracking (cs2kz block_tracking.cpp). Regular jumps only — the LadderJump variant
    // needs m_vecLadderNormal, which ModSharp doesn't expose yet.
    private const int   MinBlockDistance = 186;   // JS_MIN_BLOCK_DISTANCE (includes the +32 model offset)
    private const float OffsetEpsilon    = 0.04f; // JS_OFFSET_EPSILON

    private const InteractionLayers WorldMask = // house world-collision mask (SpawnDuplicator)
        InteractionLayers.Solid | InteractionLayers.Sky | InteractionLayers.PlayerClip
        | InteractionLayers.WorldGeometry | InteractionLayers.PhysicsProp;

    private IPhysicsQueryManager? _physics;
    private readonly float[] _block       = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _edge        = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _landingEdge = new float[PlayerSlot.MaxPlayerCount];

    private IKzStyleRegistry?     _styles;
    private IKzModeRegistry?      _mode;      // resolved cross-plugin to pick the per-mode tier table
    private IRequestManager?      _request;   // resolved cross-plugin for jump persistence
    private IKzMovementTelemetry? _telemetry; // Core AACall stream — bit-exact badAngles/sync/gain/width
    private IConVar?              _airMaxWishspeed; // sv_air_max_wishspeed — the mode-set air wishspeed cap

    // Per-jump AACall buffer (cs2kz Jump::strafes[].aaCalls). Filled from the Core AirAccelerate detour while a
    // tracked jump is airborne; cleared on takeoff; drained on landing into the exact cs2kz Strafe::End stats.
    private readonly List<AaCall>[] _aaCalls = BuildBuffers();
    private static List<AaCall>[] BuildBuffers()
    {
        var a = new List<AaCall>[PlayerSlot.MaxPlayerCount];
        for (var i = 0; i < a.Length; i++) a[i] = new List<AaCall>(64);
        return a;
    }

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
    private readonly int[]   _overlapTicks = new int[PlayerSlot.MaxPlayerCount]; // both keys of an axis held (cs2kz overlap)
    private readonly int[]   _deadairTicks = new int[PlayerSlot.MaxPlayerCount]; // no directional key held (cs2kz deadAir)
    private readonly float[] _width        = new float[PlayerSlot.MaxPlayerCount]; // total |yaw delta| over the jump (cs2kz width)

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
        _airMaxWishspeed = _shared.GetConVarManager().FindConVar("sv_air_max_wishspeed");
        _physics         = _shared.GetPhysicsQueryManager();
        return true;
    }

    public void OnAllModulesLoaded()
    {
        var mgr = _shared.GetSharpModuleManager();
        _styles    = mgr.GetOptionalSharpModuleInterface<IKzStyleRegistry>(IKzStyleRegistry.Identity)?.Instance;
        _mode      = mgr.GetOptionalSharpModuleInterface<IKzModeRegistry>(IKzModeRegistry.Identity)?.Instance;
        _request   = mgr.GetOptionalSharpModuleInterface<IRequestManager>(IRequestManager.Identity)?.Instance;
        _telemetry = mgr.GetOptionalSharpModuleInterface<IKzMovementTelemetry>(IKzMovementTelemetry.Identity)?.Instance;
        if (_telemetry is { } t) t.AirAccelerate += OnAirAccelerate;
    }

    public void Shutdown()
    {
        _hookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);
        if (_telemetry is { } t) t.AirAccelerate -= OnAirAccelerate;
    }

    // Buffer each AACall while a tracked jump is airborne (cs2kz records them in the active Strafe). Fires on
    // the game thread inside the Core AirAccelerate detour — same thread as OnProcessMovePre, no locking needed.
    private void OnAirAccelerate(PlayerSlot slot, AaCall call)
    {
        if (_tracking[slot]) _aaCalls[slot].Add(call);
    }

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
            _overlapTicks[slot]  = 0;
            _deadairTicks[slot]  = 0;
            _width[slot]         = 0f;
            _aaCalls[slot].Clear();
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
            _width[slot] += MathF.Abs(dy2);                            // width: total yaw travelled (cs2kz)
            var dir = dy2 > 0.05f ? 1 : dy2 < -0.05f ? -1 : 0;         // strafes: mouse-direction reversals
            if (dir != 0 && _lastYawDir[slot] != 0 && dir != _lastYawDir[slot]) _strafes[slot]++;
            if (dir != 0) _lastYawDir[slot] = dir;

            // cs2kz overlap (both keys of an axis held) / deadAir (no directional key) — from the held buttons.
            var btns   = arg.Service.KeyButtons;
            var anyDir = (btns & (UserCommandButtons.Forward | UserCommandButtons.Back | UserCommandButtons.MoveLeft | UserCommandButtons.MoveRight)) != 0;
            if ((btns.HasFlag(UserCommandButtons.MoveLeft) && btns.HasFlag(UserCommandButtons.MoveRight))
                || (btns.HasFlag(UserCommandButtons.Forward) && btns.HasFlag(UserCommandButtons.Back)))
                _overlapTicks[slot]++;
            else if (!anyDir)
                _deadairTicks[slot]++;

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

            // cs2kz EndBlockDistance — block/edge on gap jumps past the minimum block distance.
            _block[slot] = 0f; _edge[slot] = -1f; _landingEdge[slot] = -1f;
            if (_type[slot] is not (JumpType.LadderJump or JumpType.Fall or JumpType.Other) && dist >= MinBlockDistance)
                CalcBlockStats(slot, _takeoff[slot], origin);

            if (_styles?.HasAnyStyle(slot) != true) // styled runs don't count (1:1); GetTier gates the distance
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
        JumpType.Jumpbug    => "JB",
        _                   => "JUMP",
    };

    private void Report(PlayerSlot slot, JumpType type, float distance)
    {
        if (type is JumpType.Fall or JumpType.Other) return; // not a scored jump

        var tier = GetTier(slot, type, distance);
        if (tier == DistanceTier.None) return;

        var label = Label(type);
        var gain  = _maxSpeed[slot] - _takeoffSpeed[slot];

        // Prefer the bit-exact cs2kz Strafe::End stats from the native AACall stream; fall back to the per-tick
        // approximations only when the Core movement telemetry is absent (kz_native_movement 0 / sig failed).
        var s     = ComputeStrafeStats(slot);
        var hasAa = s.TotalDuration > 0f;
        var sync    = hasAa ? 100f * s.Sync    / s.TotalDuration : (_airTicks[slot] > 0 ? 100f * _gainTicks[slot]    / _airTicks[slot] : 0f);
        var overlap = hasAa ? 100f * s.Overlap / s.TotalDuration : (_airTicks[slot] > 0 ? 100f * _overlapTicks[slot] / _airTicks[slot] : 0f);
        var deadair = hasAa ? 100f * s.DeadAir / s.TotalDuration : (_airTicks[slot] > 0 ? 100f * _deadairTicks[slot] / _airTicks[slot] : 0f);
        var badAng  = hasAa ? 100f * s.BadAngles / s.TotalDuration : 0f;
        var width   = hasAa ? s.Width : _width[slot];
        // External gain/loss only shows on boosters/pushes — hide the tiny takeoff-tick delta on normal jumps.
        var ext     = hasAa && (s.ExternalGain > 5f || s.ExternalLoss < -5f) ? $" · {s.ExternalGain:+0}/{s.ExternalLoss:0} ext" : "";

        if (_clientManager.GetGameClient(slot) is not { IsFakeClient: false } client) return;

        var effStr = hasAa ? $" · {s.GainEff:0}% eff" : "";
        // cs2kz jump_reporting: block + takeoff-edge (+ landing-edge) only when a real block jump was detected.
        var blockStr = _block[slot] > 0f
            ? $" · {_block[slot]:0} block · {_edge[slot]:0.0} edge · {_landingEdge[slot]:0.0} land"
            : "";

        client.Print(HudPrintChannel.Chat,
            $"{label}: {distance:0.0}u — {tier}!  |  {_strafes[slot]} strafes · {sync:0}% sync · {badAng:0}% bad{effStr} · " +
            $"{_maxSpeed[slot]:0} max · {gain:+0;-0} gain · {_maxHeight[slot]:0.0}u height · " +
            $"{overlap:0}% ovl · {deadair:0}% air · {width:0}° width{ext}{blockStr}");

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

    // ===========================================================================================
    // Block / edge (cs2kz block_tracking.cpp CalcBlockStats + BlockAreEdgesParallel, GOKZ lineage).
    // Sweeps a flat line-hull 1u below takeoff level from the gap midpoint toward each side to find
    // the two block faces, verifies they're parallel axis-aligned walls, then block = face-to-face
    // gap, edge = takeoff distance to the takeoff-block face, landingEdge = landing past the face.
    // Uses raw takeoff/landing origins (cs2kz uses ledge-adjusted ones — distbug-sized difference).
    // ===========================================================================================

    private bool TraceHullPos(Vector start, Vector end, Vector mins, Vector maxs, out Vector position)
    {
        var r = _physics!.TraceShapeNoPlayers(new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }),
            start, end, WorldMask, CollisionGroupType.Default, TraceQueryFlag.All);
        position = r.EndPosition;
        return r.DidHit();
    }

    // Short point trace; true if it hits a wall whose normal is axis-aligned with coordDist.
    private bool BlockTraceAligned(Vector start, Vector end, int coordDist)
    {
        var r = _physics!.TraceLineNoPlayers(start, end, WorldMask, CollisionGroupType.Default, TraceQueryFlag.All);
        return r.DidHit() && MathF.Abs(MathF.Abs(r.PlaneNormal[coordDist]) - 1f) <= JsEpsilon;
    }

    // cs2kz BlockAreEdgesParallel — verify the takeoff and landing block faces are parallel walls.
    private bool BlockAreEdgesParallel(Vector startBlock, Vector endBlock, float deviation, int coordDist, int coordDev)
    {
        var offset = startBlock[coordDist] > endBlock[coordDist] ? 0.1f : -0.1f;

        var start = default(Vector);
        var end   = default(Vector);
        start[coordDist] = startBlock[coordDist] - offset;
        start[coordDev]  = startBlock[coordDev] - deviation;
        start.Z          = startBlock.Z;
        end[coordDist]   = startBlock[coordDist] + offset;
        end[coordDev]    = startBlock[coordDev] - deviation;
        end.Z            = startBlock.Z;

        if (BlockTraceAligned(start, end, coordDist))
        {
            start[coordDist] = endBlock[coordDist] + offset;
            end[coordDist]   = endBlock[coordDist] - offset;
            if (BlockTraceAligned(start, end, coordDist)) return true;
            start[coordDist] = startBlock[coordDist] - offset;
            end[coordDist]   = startBlock[coordDist] + offset;
        }

        start[coordDev] = startBlock[coordDev] + deviation;
        end[coordDev]   = startBlock[coordDev] + deviation;

        if (BlockTraceAligned(start, end, coordDist))
        {
            start[coordDist] = endBlock[coordDist] + offset;
            end[coordDist]   = endBlock[coordDist] - offset;
            if (BlockTraceAligned(start, end, coordDist)) return true;
        }

        return false;
    }

    // cs2kz CalcBlockStats (success-landing path; the failstat checkOffset branch is failstat-only).
    private void CalcBlockStats(PlayerSlot slot, Vector takeoff, Vector landing)
    {
        // GetCoordOrientation: dominant horizontal axis + direction of the jump.
        var coordDist = MathF.Abs(landing.X - takeoff.X) < MathF.Abs(landing.Y - takeoff.Y) ? 1 : 0;
        var distSign  = landing[coordDist] > takeoff[coordDist] ? 1 : -1;
        var coordDev  = 1 - coordDist;
        var deviation = MathF.Abs(landing[coordDev] - takeoff[coordDev]);

        // Midpoint of the jump gap — assumed open air, 1u below takeoff floor so sweeps hit block walls.
        var middle = default(Vector);
        middle[coordDist] = (takeoff[coordDist] + landing[coordDist]) / 2f;
        middle[coordDev]  = (takeoff[coordDev] + landing[coordDev]) / 2f;
        middle.Z          = takeoff.Z - 1f;

        // Flat sweep line perpendicular to the jump direction, wide enough to catch the blocks despite deviation.
        var sweepMin = default(Vector);
        var sweepMax = default(Vector);
        sweepMin[coordDev] = -(deviation + 16f);
        sweepMax[coordDev] = deviation + 16f;

        var startBlock = middle;
        startBlock[coordDist] = takeoff[coordDist] - distSign * 16f; // player bbox puts the wall 16u back

        var endBlock = middle;
        endBlock[coordDist] = landing[coordDist] + distSign * (16f + OffsetEpsilon);

        if (!TraceHullPos(middle, startBlock, sweepMin, sweepMax, out startBlock)
            || !TraceHullPos(middle, endBlock, sweepMin, sweepMax, out endBlock))
            return;

        if (!BlockAreEdgesParallel(startBlock, endBlock, deviation + 32f, coordDist, coordDev))
            return; // _block already zeroed at landing

        var rawBlock = MathF.Abs(endBlock[coordDist] - startBlock[coordDist]);
        var block    = MathF.Round(rawBlock);
        if (block < MinBlockDistance) return;

        _block[slot] = block;
        // The trace stops OffsetEpsilon units in front of the actual block face; compensate.
        _edge[slot]        = MathF.Abs(startBlock[coordDist] - takeoff[coordDist] + (16f - OffsetEpsilon) * distSign);
        _landingEdge[slot] = (landing[coordDist] - endBlock[coordDist]) * distSign + 16f;
    }

    private readonly record struct StrafeStats(
        float TotalDuration, float BadAngles, float Sync, float Overlap, float DeadAir, float Width,
        float ExternalGain, float ExternalLoss, float GainEff);

    // ponytail: ModSharp doesn't expose m_flSurfaceFriction; it's 1.0 in air, which is where these AACalls run
    // (air-strafe jumpstats). Upgrade path: read the schema offset off the movement service if a surface-friction
    // KZ map ever appears.
    private const float SurfaceFriction = 1.0f;

    // cs2kz AACall::CalcAccelSpeed / CalcIdealYaw / CalcIdealGain — the ideal (theoretical-max) speed gain for a
    // call, used for gain-efficiency (actual airGain / ideal maxGain). CalcIdealYaw returns radians.
    private static float CalcAccelSpeed(in AaCall c)
        => (c.WishSpeed == 0f ? c.MaxSpeed : c.WishSpeed) * c.Accel * SurfaceFriction * c.Duration;

    private static float CalcIdealYaw(in AaCall c, float wishspeedCapped)
    {
        var accelspeed = CalcAccelSpeed(c);
        if (accelspeed <= 0f) return MathF.PI;
        var speed = MathF.Sqrt(c.VelocityPre.X * c.VelocityPre.X + c.VelocityPre.Y * c.VelocityPre.Y);
        if (speed == 0f) return 0f;
        var tmp = wishspeedCapped - accelspeed;
        if (tmp <= 0f) return MathF.PI / 2f;
        return tmp < speed ? MathF.Acos(tmp / speed) : 0f;
    }

    private static float CalcIdealGain(in AaCall c, float wishspeedCapped)
    {
        var preLen2Sqr  = c.VelocityPre.X * c.VelocityPre.X + c.VelocityPre.Y * c.VelocityPre.Y;
        var preLen      = MathF.Sqrt(preLen2Sqr);
        var accelCapped = MathF.Min(CalcAccelSpeed(c), wishspeedCapped);
        var idealSpeed  = MathF.Sqrt(preLen2Sqr + accelCapped * accelCapped
                                     + 2f * accelCapped * preLen * MathF.Cos(CalcIdealYaw(c, wishspeedCapped)));
        return idealSpeed - preLen;
    }

    // cs2kz Strafe::End (kz_jumpstats.cpp) over the jump's buffered AACalls. Classification per call is
    // bit-exact — it's a pure function of the captured wishspeed/buttons and velocity pre/post the engine
    // air-accel. Reported as fractions of total duration, so the exact tick-interval weighting only matters
    // relative to itself. maxGain/CalcIdealGain (gain-efficiency %) is intentionally omitted — it needs the
    // per-call accel + surfaceFriction, which aren't in the AACall yet.
    private StrafeStats ComputeStrafeStats(PlayerSlot slot)
    {
        float total = 0, bad = 0, sync = 0, ovl = 0, dead = 0, width = 0, extGain = 0, extLoss = 0, airGain = 0, maxGain = 0;
        var wishspeedCapped = _airMaxWishspeed?.GetFloat() ?? 30f; // sv_air_max_wishspeed default
        foreach (var c in _aaCalls[slot])
        {
            total += c.Duration;

            var preLen  = MathF.Sqrt(c.VelocityPre.X  * c.VelocityPre.X  + c.VelocityPre.Y  * c.VelocityPre.Y);
            var postLen = MathF.Sqrt(c.VelocityPost.X * c.VelocityPost.X + c.VelocityPost.Y * c.VelocityPost.Y);
            var ddx     = c.VelocityPost.X - c.VelocityPre.X;
            var ddy     = c.VelocityPost.Y - c.VelocityPre.Y;
            var deltaLen = MathF.Sqrt(ddx * ddx + ddy * ddy);

            if (c.WishSpeed == 0f)
            {
                if ((c.Buttons.HasFlag(UserCommandButtons.Forward)   && c.Buttons.HasFlag(UserCommandButtons.Back))
                    || (c.Buttons.HasFlag(UserCommandButtons.MoveLeft) && c.Buttons.HasFlag(UserCommandButtons.MoveRight)))
                    ovl += c.Duration;   // both keys of an axis cancel → no wish (cs2kz overlap)
                else
                    dead += c.Duration;  // no directional input at all (cs2kz deadAir)
            }
            else if (deltaLen <= JsEpsilon)
                bad += c.Duration;       // input, but velocity barely moved → quantization gain (cs2kz badAngles)
            else if (postLen - preLen > JsEpsilon)
                sync += c.Duration;      // real speed gained this call (cs2kz syncDuration)

            width += MathF.Abs(NormalizeYaw(c.CurrentYaw - c.PrevYaw));

            // Speed injected/removed between ticks by non-player sources (boosters, teleport pushes) — cs2kz
            // externalGain/externalLoss, split by sign of the cross-tick speed delta.
            if (c.ExternalSpeedDiff > 0) extGain += c.ExternalSpeedDiff;
            else                         extLoss += c.ExternalSpeedDiff;

            // Gain efficiency: actual speed gained vs the theoretical ideal (cs2kz airGain / maxGain).
            if (postLen - preLen > 0f) airGain += postLen - preLen;
            maxGain += CalcIdealGain(c, wishspeedCapped);
        }
        var gainEff = maxGain > 0f ? 100f * airGain / maxGain : 0f;
        return new StrafeStats(total, bad, sync, ovl, dead, width, extGain, extLoss, gainEff);
    }

    // cs2kz per-mode, per-jump-type distance-tier tables (kz_mode_ckz.h / kz_mode_vnl.h). Rows index by
    // JumpType (LongJump..Jumpbug = 0..6); 6 columns = the Meh/Impressive/Perfect/Godlike/Ownage/Wrecker
    // ascending thresholds. Replaces the old single LongJump-only table applied to every jump type + mode.
    private static readonly float[][] CkzTiers =
    [
        [217f, 265f, 270f, 275f, 280f, 284f], // LongJump
        [217f, 275f, 280f, 287f, 292f, 295f], // Bhop
        [217f, 275f, 280f, 287f, 292f, 295f], // MultiBhop
        [217f, 275f, 280f, 287f, 292f, 295f], // WeirdJump
        [120f, 160f, 170f, 180f, 190f, 200f], // LadderJump
        [217f, 260f, 265f, 270f, 275f, 278f], // Ladderhop
        [217f, 275f, 280f, 287f, 292f, 295f], // Jumpbug
    ];

    private static readonly float[][] VnlTiers =
    [
        [215f, 230f, 235f, 240f, 245f, 248f], // LongJump
        [150f, 230f, 233f, 238f, 240f, 242f], // Bhop
        [150f, 232f, 237f, 242f, 245f, 248f], // MultiBhop
        [150f, 230f, 235f, 240f, 244f, 246f], // WeirdJump
        [ 50f,  80f,  90f, 100f, 105f, 108f], // LadderJump
        [215f, 250f, 253f, 258f, 261f, 263f], // Ladderhop
        [215f, 255f, 260f, 265f, 270f, 272f], // Jumpbug
    ];

    // cs2kz KZ<mode>ModeService::GetDistanceTier: pick the active mode's table, index by jump type, return
    // the highest tier the distance beats. Non-scored types (Fall/Other) and distance > 500u → None.
    private DistanceTier GetTier(PlayerSlot slot, JumpType type, float distance)
    {
        var row = (int) type;
        if (type is JumpType.Fall or JumpType.Other || row > (int) JumpType.Jumpbug || distance > 500f)
            return DistanceTier.None;

        var isVnl = string.Equals(_mode?.GetPlayerMode(slot), "vnl", StringComparison.OrdinalIgnoreCase);
        var tiers = (isVnl ? VnlTiers : CkzTiers)[row];

        var tier = DistanceTier.None;
        while ((int) tier < tiers.Length && distance >= tiers[(int) tier])
            tier = (DistanceTier) ((int) tier + 1);
        return tier;
    }
}
