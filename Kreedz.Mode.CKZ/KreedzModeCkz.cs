/*
 * yappershq/Kreedz (KZ) — Classic (CKZ) mode plugin
 *
 * A standalone ModSharp module that registers the Classic mode against Kreedz.Core's IKzModeRegistry AND
 * implements its custom movement — a faithful port of cs2kz kz_mode_ckz (prestrafe/perf on ProcessMovePre;
 * the AirMove cap, CategorizePosition rampbug and TryPlayerMove slopefix as IKzMovementMode callbacks).
 *
 * Native movement is NOT detoured here: Kreedz.Core installs the movement detours once and dispatches to
 * this mode via IKzMovementMode (Core owns the trampolines so any mode shares them — cs2kz's architecture).
 * Core only calls these callbacks for players whose active mode is "ckz", so the physics is naturally gated.
 *
 * Fidelity vs cs2kz (from the Rampfix/cs2kz audit): DONE — CategorizePosition now nudges before the engine
 * (Core dispatch order), FrameTime reads engine globals. FOLLOW-UP — the OnTryPlayerMovePost commit gate,
 * IsValidMovementTrace backward stuck-trace, duck-aware hull, and trigger-touch replay are still pending.
 */

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

namespace Kreedz.Mode.Ckz;

public sealed unsafe class KreedzModeCkz : IModSharpModule, IKzMovementMode
{
    // cs2kz kz_mode_ckz.h — verbatim.
    private const float SpeedNormal          = 250.0f;
    private const float PsSpeedMax           = 26.0f;
    private const float PsMinRewardRate      = 2.0f;
    private const float PsMaxRewardRate      = 15.5f;
    private const float PsMaxPsTime          = 0.50f;
    private const float PsTurnRateWindow     = 0.02f;
    private const float PsDecrementRatio     = 3.0f;
    private const float PsRatioToSpeed       = 0.5f;
    private const float PsLandingGracePeriod = 0.25f;
    private const float BhPerfWindow         = 0.02f;
    private const float BhBaseMultiplier     = 51.5f;
    private const float BhLandingDecrement   = 75.0f;

    // rampbug/slopefix constants (cs2kz kz_mode_ckz.h).
    private const float RampBugThreshold   = 0.98f;   // RAMP_BUG_THRESHOLD
    private const float RampPierceDistance = 0.0625f; // RAMP_PIERCE_DISTANCE
    private const float NewRampThreshold   = 0.95f;   // NEW_RAMP_THRESHOLD
    private const int   MaxBumps           = 4;       // MAX_BUMPS
    private const float Epsilon            = 0.00001f;
    private const float FltEpsilon         = 1.192092896e-07f;

    private static readonly float BhNormalizeFactor =
        BhBaseMultiplier * MathF.Log(SpeedNormal + PsSpeedMax) - (SpeedNormal + PsSpeedMax);

    private readonly ISharedSystem          _shared;
    private readonly IModSharp              _modSharp;
    private readonly IHookManager           _hookManager;
    private readonly IPhysicsQueryManager   _physics;
    private readonly ILogger<KreedzModeCkz> _logger;

    private readonly IConVar? _tpm;

    private IKzModeRegistry? _registry;

    private readonly float[] _bonusSpeed    = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _leftPreRatio  = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _rightPreRatio = new float[PlayerSlot.MaxPlayerCount];

    private readonly bool[]   _wasGround       = new bool[PlayerSlot.MaxPlayerCount];
    private readonly float[]  _landingTime     = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[]  _takeoffTime     = new float[PlayerSlot.MaxPlayerCount];
    private readonly Vector[] _landingVelocity = new Vector[PlayerSlot.MaxPlayerCount];

    // slopefix per-player state (the native detours only get a CCSPlayer_MovementServices*; Core resolves
    // the slot and hands it to us, so no {ms->slot} map is needed here anymore — Core owns that).
    private readonly Vector[] _lastPlane = new Vector[PlayerSlot.MaxPlayerCount];
    private readonly Vector[] _tpmStart  = new Vector[PlayerSlot.MaxPlayerCount];
    private readonly Vector[] _tpmVel    = new Vector[PlayerSlot.MaxPlayerCount];
    private readonly bool[]   _tpmRun    = new bool[PlayerSlot.MaxPlayerCount];

    private bool _tpmEnabled;

    private readonly List<(float Rate, float Duration)>[] _angleHistory =
        new List<(float, float)>[PlayerSlot.MaxPlayerCount];

    public string DisplayName   => "[Kreedz] Mode - Classic";
    public string DisplayAuthor => "yappershq";

    public KreedzModeCkz(ISharedSystem shared,
                         string?        dllPath,
                         string?        sharpPath,
                         Version?       version,
                         IConfiguration? coreConfiguration,
                         bool           hotReload)
    {
        _shared      = shared;
        _modSharp    = shared.GetModSharp();
        _hookManager = shared.GetHookManager();
        _physics     = shared.GetPhysicsQueryManager();
        _logger      = shared.GetLoggerFactory().CreateLogger<KreedzModeCkz>();

        _tpm = shared.GetConVarManager().CreateConVar("kz_ckz_tpm", false,
            "Enable the experimental TryPlayerMove slopefix reimplementation. Default 0 — validate against demos before enabling.");

        for (var i = 0; i < _angleHistory.Length; i++)
            _angleHistory[i] = [];
    }

    public bool Init()
    {
        _hookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        _hookManager.PlayerGetMaxSpeed.InstallHookPre(OnGetMaxSpeed);
        _tpmEnabled = _tpm?.GetBool() == true;
        return true;
    }

    public void OnAllModulesLoaded()
    {
        _registry = _shared.GetSharpModuleManager()
                          .GetOptionalSharpModuleInterface<IKzModeRegistry>(IKzModeRegistry.Identity)?.Instance;

        if (_registry is null)
        {
            _logger.LogError("[Kreedz.Mode.CKZ] Kreedz.Core mode registry not found — is the core loaded?");
            return;
        }

        _registry.RegisterMode("ckz", "Classic", "CKZ", Convars);
        _registry.RegisterMovementMode("ckz", this); // Core routes the native movement callbacks to us
        _logger.LogInformation("[Kreedz.Mode.CKZ] registered.");
    }

    public void Shutdown()
    {
        _hookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);
        _hookManager.PlayerGetMaxSpeed.RemoveHookPre(OnGetMaxSpeed);
    }

    private bool IsCkz(PlayerSlot slot)
        => string.Equals(_registry?.GetPlayerMode(slot), "ckz", StringComparison.OrdinalIgnoreCase);

    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient) return;

        var slot = client.Slot;
        if (!IsCkz(slot) || !arg.Pawn.IsAlive)
        {
            _bonusSpeed[slot] = _leftPreRatio[slot] = _rightPreRatio[slot] = 0f;
            _angleHistory[slot].Clear();
            _wasGround[slot] = arg.Pawn.GroundEntityHandle.IsValid();
            return;
        }

        var globals   = _modSharp.GetGlobals();
        var frametime = globals.FrameTime;
        var curtime   = globals.CurTime;

        var velocity = arg.Pawn.GetAbsVelocity();
        var onGround = arg.Pawn.GroundEntityHandle.IsValid();

        // cs2kz UpdateAngleHistory: the prestrafe "turn rate" is the angle between wishdir (the strafe-key
        // input direction) and velocity — accumulated on-ground only. NOT raw mouse-yaw delta (the old bug).
        if (onGround)
            PushAngle(slot, ComputeTurnRate(arg), frametime);

        if (onGround && !_wasGround[slot])
        {
            _landingTime[slot]     = curtime;
            _landingVelocity[slot] = velocity;
        }
        else if (!onGround && _wasGround[slot])
        {
            _takeoffTime[slot] = curtime;
            ApplyPerf(arg, slot, velocity);
        }

        CalcPrestrafe(slot, arg.Pawn.GetAbsVelocity(), onGround, frametime, curtime);

        _wasGround[slot] = onGround;
    }

    // ── IKzMovementMode — native movement callbacks (Core installs the detours; we supply the physics) ──

    /// <summary>cs2kz OnAirMove — cap air wishspeed to SPEED_NORMAL for the engine air-move.</summary>
    public void OnAirMove(PlayerSlot slot, nint ms, nint mv)
        => Move(mv).MaxSpeed = SpeedNormal;

    /// <summary>cs2kz OnAirMovePost — restore max speed to 250 + prestrafe gain after the air-move.</summary>
    public void OnAirMovePost(PlayerSlot slot, nint ms, nint mv)
        => Move(mv).MaxSpeed = SpeedNormal + GetPrestrafeGain(slot);

    /// <summary>cs2kz OnCategorizePosition — the rampbug origin nudge (Core calls this BEFORE the engine).</summary>
    public void OnCategorizePosition(PlayerSlot slot, nint ms, nint mv, bool stayOnGround)
    {
        ref var md = ref Move(mv);
        var (mins, maxs) = Hull();
        var origin = md.AbsOrigin;
        var plane  = _lastPlane[slot];

        // Only fix while dropping (vz < -64) onto a plane steeper than a valid last plane we had.
        if (!stayOnGround && Dot(plane, plane) >= Epsilon * Epsilon && plane.Z <= 0.7f && md.Velocity.Z <= -64.0f)
        {
            var trace = TraceDown(mins, maxs, origin);
            if (trace.Fraction != 1.0f
                && trace.Fraction < 0.95f && trace.PlaneNormal.Z > 0.7f && Dot(plane, trace.PlaneNormal) < RampBugThreshold)
            {
                var nudged = origin + plane * 0.0625f;
                var trace2 = TraceDown(mins, maxs, nudged);
                if (!trace2.StartInSolid && (trace2.Fraction == 1.0f || Dot(plane, trace2.PlaneNormal) >= RampBugThreshold))
                {
                    md.AbsOrigin = nudged;
                    origin       = nudged;
                }
            }
        }

        // Track lastValidPlane: the surface we're currently resting on, if it's genuinely standable.
        var g = TraceDown(mins, maxs, origin);
        if (g.Fraction < 1.0f && g.PlaneNormal.Z > 0.7f)
            _lastPlane[slot] = g.PlaneNormal;
    }

    /// <summary>cs2kz OnTryPlayerMove — capture pre-move state for the slopefix (gated on kz_ckz_tpm).</summary>
    public void OnTryPlayerMovePre(PlayerSlot slot, nint ms, nint mv)
    {
        if (!_tpmEnabled) { _tpmRun[slot] = false; return; }
        ref var pm = ref Move(mv);
        _tpmStart[slot] = pm.AbsOrigin;
        _tpmVel[slot]   = pm.Velocity;
        _tpmRun[slot]   = true;
    }

    /// <summary>cs2kz OnTryPlayerMovePost — the slopefix collision reimplementation.</summary>
    public void OnTryPlayerMovePost(PlayerSlot slot, nint ms, nint mv)
    {
        if (!_tpmRun[slot]) return;
        TryPlayerMoveFix(slot, mv, _tpmStart[slot], _tpmVel[slot]);
    }

    // cs2kz KZClassicModeService::OnTryPlayerMove — MAX_BUMPS bump loop + 3x3x3 offset pierce search that
    // detects+corrects rampbugs. overrideTPM is set only when a rampbug is found; only then is the computed
    // origin/velocity written back. FOLLOW-UP: the post-move "velocity heavily modified" commit gate.
    private void TryPlayerMoveFix(PlayerSlot slot, nint mv, Vector start, Vector velocity)
    {
        if (Len(velocity) == 0.0f) return;

        var (mins, maxs) = Hull();
        var plane        = _lastPlane[slot];
        var primal       = velocity;
        var frametime    = FrameTime();

        var timeLeft   = frametime;
        var allFraction = 0f;
        var planes      = new Vector[5];
        var numPlanes   = 0;
        var overrideTPM = false;
        var potentiallyStuck = false;
        (float Frac, Vector Normal, Vector End, bool Solid) pm = default;

        for (var bump = 0; bump < MaxBumps; bump++)
        {
            var end = start + velocity * timeLeft;
            pm = Trace(mins, maxs, start, end);
            if (end == start) continue;
            if (IsValidMovementTrace(pm, mins, maxs) && pm.Frac == 1.0f) break;

            var normalChanged = Dot(pm.Normal, plane) < RampBugThreshold;
            var stuck         = potentiallyStuck && pm.Frac == 0.0f;
            var lastWasWall   = plane.Z < 0.03125f;
            var consider      = (normalChanged && !lastWasWall) || stuck;

            if (Len(plane) > FltEpsilon && consider)
            {
                var offsets = new[] { 0.0f, -1.0f, 1.0f };
                var success = false;
                for (var i = 0; i < 3 && !success; i++)
                for (var j = 0; j < 3 && !success; j++)
                for (var k = 0; k < 3 && !success; k++)
                {
                    Vector offDir;
                    if (i == 0 && j == 0 && k == 0) offDir = plane;
                    else
                    {
                        offDir = new Vector(offsets[i], offsets[j], offsets[k]);
                        if (Dot(plane, offDir) <= 0.0f) continue;
                        var test0 = Trace(mins, maxs, start + offDir * RampPierceDistance, start);
                        if (!IsValidMovementTrace(test0, mins, maxs)) continue;
                    }

                    var good = false; var hitNew = false; var validPlane = false;
                    (float Frac, Vector Normal, Vector End, bool Solid) pierce = default;
                    for (var ratio = 0.25f; ratio <= 1.0f; ratio += 0.25f)
                    {
                        pierce = Trace(mins, maxs, start + offDir * RampPierceDistance * ratio, end + offDir * RampPierceDistance * ratio);
                        if (!IsValidMovementTrace(pierce, mins, maxs)) continue;
                        validPlane = pierce.Frac < 1.0f && pierce.Frac > 0.1f && Dot(pierce.Normal, plane) >= RampBugThreshold;
                        hitNew     = Dot(pm.Normal, pierce.Normal) < NewRampThreshold && Dot(plane, pierce.Normal) > NewRampThreshold;
                        good       = MathF.Abs(pierce.Frac - 1.0f) < FltEpsilon || validPlane;
                        if (good) break;
                    }

                    if (good || hitNew)
                    {
                        var test = Trace(mins, maxs, pierce.End, end);
                        var denom = Len(end - start);
                        var frac  = denom > 0f ? Math.Clamp(Len(pierce.End - start) / denom, 0.0f, 1.0f) : 0f;
                        var normal = Len(pierce.Normal) > 0.0f ? pierce.Normal : test.Normal;
                        pm = (frac, normal, test.End, pm.Solid);
                        _lastPlane[slot] = normal;
                        plane = normal;
                        success = true;
                        overrideTPM = true;
                    }
                }
            }

            if (Len(pm.Normal) > 0.99f) _lastPlane[slot] = pm.Normal;
            potentiallyStuck = pm.Frac == 0.0f;

            if (pm.Frac * Len(velocity) > 0.03125f || pm.Frac > 0.03125f)
            {
                allFraction += pm.Frac;
                start = pm.End;
                numPlanes = 0;
            }

            if (allFraction == 1.0f) break;
            timeLeft -= frametime * pm.Frac;

            if (numPlanes >= 5 || (pm.Normal.Z >= 0.7f && Len2D(velocity) < 1.0f)) { velocity = default; break; }

            planes[numPlanes++] = pm.Normal;

            if (numPlanes == 1) // (cs2kz also checks WALK + no ground entity; approximated as air-clip — FOLLOW-UP)
            {
                velocity = ClipVelocity(velocity, planes[0]);
            }
            else
            {
                int i, j;
                for (i = 0; i < numPlanes; i++)
                {
                    velocity = ClipVelocity(velocity, planes[i]);
                    for (j = 0; j < numPlanes; j++)
                        if (j != i && Dot(velocity, planes[j]) < 0) break;
                    if (j == numPlanes) break;
                }

                if (i == numPlanes)
                {
                    if (numPlanes != 2) { velocity = default; break; }
                    var cd = Normalize(Cross(planes[0], planes[1]));
                    velocity = cd * Dot(cd, velocity);
                    if (Dot(velocity, primal) <= 0) { velocity = default; break; }
                }
            }
        }

        // Apply only when a rampbug was detected+fixed — otherwise the engine's result stands.
        if (overrideTPM)
        {
            ref var md = ref Move(mv);
            md.AbsOrigin = pm.End;
            md.Velocity  = velocity;
        }
    }

    // cs2kz ClipVelocity (1:1 with CS2): reflect velocity off a plane with the 0.03125 overbounce.
    private static Vector ClipVelocity(Vector inV, Vector normal)
    {
        var backoff = -(inV.X * normal.X + normal.Z * inV.Z + inV.Y * normal.Y);
        backoff = MathF.Max(backoff, 0.0f) + 0.03125f;
        return normal * backoff + inV;
    }

    // cs2kz IsValidMovementTrace — reject start-in-solid, degenerate/deformed normals, and stuck spots.
    // FOLLOW-UP: cs2kz also does a backward end->start trace rejecting StartInSolid (dropped here).
    private bool IsValidMovementTrace((float Frac, Vector Normal, Vector End, bool Solid) tr, Vector mins, Vector maxs)
    {
        if (tr.Solid) return false;
        if (tr.Frac < 1.0f && MathF.Abs(tr.Normal.X) < FltEpsilon && MathF.Abs(tr.Normal.Y) < FltEpsilon && MathF.Abs(tr.Normal.Z) < FltEpsilon) return false;
        if (MathF.Abs(tr.Normal.X) > 1.0f || MathF.Abs(tr.Normal.Y) > 1.0f || MathF.Abs(tr.Normal.Z) > 1.0f) return false;

        var stuck = Trace(mins, maxs, tr.End, tr.End);
        return !stuck.Solid && stuck.Frac >= 1.0f - FltEpsilon;
    }

    // General hull trace start->end, values only (no ref-struct escape).
    private (float Frac, Vector Normal, Vector End, bool Solid) Trace(Vector mins, Vector maxs, Vector start, Vector end)
    {
        var query = RnQueryShapeAttr.PlayerMovement(InteractionLayers.Solid);
        var t = _physics.TraceShapePlayerMovement(new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }), start, end, in query);
        return (t.Fraction, t.PlaneNormal, t.EndPosition, t.StartInSolid);
    }

    // Returns plain values (not the GameTrace ref struct) so nothing aliases the local query.
    private (float Fraction, Vector PlaneNormal, bool StartInSolid) TraceDown(Vector mins, Vector maxs, Vector origin)
    {
        var end = origin; end.Z -= 2.0f;
        var query = RnQueryShapeAttr.PlayerMovement(InteractionLayers.Solid);
        var t = _physics.TraceShapePlayerMovement(new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }), origin, end, in query);
        return (t.Fraction, t.PlaneNormal, t.StartInSolid);
    }

    // Standard CS2 standing player hull. Ducking (maxs.z 54) is a FOLLOW-UP refinement.
    private static (Vector Mins, Vector Maxs) Hull()
        => (new Vector(-16f, -16f, 0f), new Vector(16f, 16f, 72f));

    // #5 fidelity fix: read the engine's live frametime rather than a hardcoded 1/64 (matters off 64-tick).
    private float FrameTime() => _modSharp.GetGlobals().FrameTime;

    private static float Len(Vector v)   => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    private static float Len2D(Vector v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);
    private static Vector Normalize(Vector v) { var l = Len(v); return l > 0f ? new Vector(v.X / l, v.Y / l, v.Z / l) : default; }
    private static Vector Cross(Vector a, Vector b)
        => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static float Dot(Vector a, Vector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static ref MoveData Move(nint mv) => ref Unsafe.AsRef<MoveData>((void*)mv);

    /// <summary>cs2kz KZClassicModeService::CalcPrestrafe.</summary>
    private void CalcPrestrafe(PlayerSlot slot, Vector velocity, bool onGround, float frametime, float curtime)
    {
        var averageRate = AverageTurnRate(slot);

        var rewardRate = Math.Clamp(MathF.Abs(averageRate) / PsMaxRewardRate, 0f, 1f) * frametime;
        var punishRate = _landingTime[slot] + PsLandingGracePeriod < curtime ? frametime * PsDecrementRatio : 0f;

        if (onGround)
        {
            var speed = Math.Clamp(Length2D(velocity), 0f, SpeedNormal);

            var currentPreRatio = speed <= 0f
                ? 0f
                : MathF.Pow(_bonusSpeed[slot] / PsSpeedMax * SpeedNormal / speed, 1f / PsRatioToSpeed) * PsMaxPsTime;

            _leftPreRatio[slot]  = MathF.Min(_leftPreRatio[slot], currentPreRatio);
            _rightPreRatio[slot] = MathF.Min(_rightPreRatio[slot], currentPreRatio);

            _leftPreRatio[slot]  += averageRate > PsMinRewardRate  ? rewardRate : -punishRate;
            _rightPreRatio[slot] += averageRate < -PsMinRewardRate ? rewardRate : -punishRate;

            _leftPreRatio[slot]  = Math.Clamp(_leftPreRatio[slot],  0f, PsMaxPsTime);
            _rightPreRatio[slot] = Math.Clamp(_rightPreRatio[slot], 0f, PsMaxPsTime);

            _bonusSpeed[slot] = GetPrestrafeGain(slot) / SpeedNormal * speed;
        }
        else
        {
            var airReward = frametime;
            if (_leftPreRatio[slot] < _rightPreRatio[slot])
                _leftPreRatio[slot]  = Math.Clamp(_leftPreRatio[slot]  + airReward, 0f, _rightPreRatio[slot]);
            else
                _rightPreRatio[slot] = Math.Clamp(_rightPreRatio[slot] + airReward, 0f, _leftPreRatio[slot]);
        }
    }

    /// <summary>cs2kz KZClassicModeService::OnStopTouchGround — perf/bhop landing-speed preservation.</summary>
    private void ApplyPerf(IPlayerProcessMoveForwardParams arg, PlayerSlot slot, Vector takeoffVelocity)
    {
        var timeOnGround = _takeoffTime[slot] - _landingTime[slot];
        if (timeOnGround > BhPerfWindow) return;

        var landing    = _landingVelocity[slot];
        var landingLen = Length2D(landing);
        if (landingLen <= 0f) return;

        var nx = landing.X / landingLen;
        var ny = landing.Y / landingLen;

        var newSpeed = MathF.Max(landingLen, Length2D(takeoffVelocity));
        var floor    = SpeedNormal + GetPrestrafeGain(slot);

        if (newSpeed > floor)
        {
            newSpeed = MathF.Min(newSpeed, (BhBaseMultiplier - timeOnGround * BhLandingDecrement) * MathF.Log(newSpeed) - BhNormalizeFactor);
            newSpeed = MathF.Max(newSpeed, floor);
        }

        arg.Velocity = new Vector(newSpeed * nx, newSpeed * ny, takeoffVelocity.Z);
    }

    /// <summary>cs2kz KZClassicModeService::GetPrestrafeGain.</summary>
    private float GetPrestrafeGain(PlayerSlot slot)
        => PsSpeedMax * MathF.Pow(MathF.Max(_leftPreRatio[slot], _rightPreRatio[slot]) / PsMaxPsTime, PsRatioToSpeed);

    private HookReturnValue<float> OnGetMaxSpeed(IPlayerGetMaxSpeedHookParams @params, HookReturnValue<float> ret)
    {
        var client = @params.Client;
        if (client.IsFakeClient || !IsCkz(client.Slot))
            return new();

        return new(EHookAction.SkipCallReturnOverride, SpeedNormal + GetPrestrafeGain(client.Slot));
    }

    private void PushAngle(PlayerSlot slot, float rate, float duration)
    {
        var history = _angleHistory[slot];
        history.Add((rate, duration));

        var total = 0f;
        foreach (var (_, d) in history) total += d;
        while (history.Count > 1 && total - history[0].Duration >= PsTurnRateWindow)
        {
            total -= history[0].Duration;
            history.RemoveAt(0);
        }
    }

    private float AverageTurnRate(PlayerSlot slot)
    {
        float weighted = 0f, total = 0f;
        foreach (var (rate, duration) in _angleHistory[slot])
        {
            weighted += rate * duration;
            total    += duration;
        }

        return total == 0f ? 0f : weighted / total;
    }

    private static float Length2D(Vector v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    // cs2kz KZClassicModeService::UpdateAngleHistory rate: the signed angle between the player's wishdir
    // (forward*fmove + right*smove from the strafe keys) and their velocity — the strafe-alignment metric
    // that drives prestrafe, not raw mouse-yaw delta. Roll is 0 in CS2 movement, so the flattened forward/
    // right reduce to (cos yaw, sin yaw) / (sin yaw, -cos yaw). Reads MoveData exactly like cs2kz reads mv->.
    private static float ComputeTurnRate(IPlayerProcessMoveForwardParams arg)
    {
        var mv = arg.Info;
        float vx = mv->Velocity.X, vy = mv->Velocity.Y;
        if (vx == 0f && vy == 0f) return 0f; // not turning if velocity is null (cs2kz)

        var   yawRad = mv->ViewAngles.Y * (MathF.PI / 180f);
        float cy = MathF.Cos(yawRad), sy = MathF.Sin(yawRad);
        float fmove = mv->ForwardMove, smove = -mv->SideMove; // cs2kz negates side move
        float wx = cy * fmove + sy * smove;
        float wy = sy * fmove - cy * smove;
        if (wx == 0f && wy == 0f) return 0f; // no wishdir → not turning

        var accelYaw = MathF.Atan2(wy, wx) * (180f / MathF.PI);
        var velYaw   = MathF.Atan2(vy, vx) * (180f / MathF.PI);
        return AngleDiff(accelYaw - velYaw); // cs2kz GetAngleDifference(velYaw, accelYaw, 180, relative=true)
    }

    // cs2kz GetAngleDifference(source, target, 180, relative=true): (target-source) wrapped to [-180, 180].
    private static float AngleDiff(float d)
    {
        d %= 360f;
        if (d > 180f) d -= 360f;
        else if (d < -180f) d += 360f;
        return d;
    }

    // cs2kz kz_mode_ckz.h modeCvarValues — the full 33-cvar mode layer, verbatim.
    private static readonly IReadOnlyDictionary<string, string> Convars = new Dictionary<string, string>
    {
        ["sv_accelerate"]                   = "6.5",
        ["sv_accelerate_use_weapon_speed"]  = "false",
        ["sv_airaccelerate"]                = "100",
        ["sv_air_max_wishspeed"]            = "30",
        ["sv_autobunnyhopping"]             = "false",
        ["sv_bounce"]                       = "0",
        ["sv_enablebunnyhopping"]           = "true",
        ["sv_friction"]                     = "5.2",
        ["sv_gravity"]                      = "800",
        ["sv_jump_impulse"]                 = "302",
        ["sv_jump_precision_enable"]        = "false",
        ["sv_jump_spam_penalty_time"]       = "0",
        ["sv_ladder_angle"]                 = "-0.707",
        ["sv_ladder_dampen"]                = "1",
        ["sv_ladder_scale_speed"]           = "1",
        ["sv_maxspeed"]                     = "320",
        ["sv_maxvelocity"]                  = "3500",
        ["sv_staminajumpcost"]              = "0",
        ["sv_staminalandcost"]              = "0",
        ["sv_staminamax"]                   = "0",
        ["sv_staminarecoveryrate"]          = "9999",
        ["sv_standable_normal"]             = "0.7",
        ["sv_step_move_vel_min"]            = "64",
        ["sv_timebetweenducks"]             = "0",
        ["sv_walkable_normal"]              = "0.7",
        ["sv_wateraccelerate"]              = "10",
        ["sv_waterfriction"]                = "1",
        ["sv_water_slow_amount"]            = "0.9",
        ["mp_solid_teammates"]              = "0",
        ["mp_solid_enemies"]                = "0",
        ["sv_subtick_movement_view_angles"] = "false",
        ["sv_legacy_jump"]                  = "true",
        ["sv_bhop_time_window"]             = "0.02",
    };
}
