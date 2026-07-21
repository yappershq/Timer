/*
 * yappershq/Kreedz (KZ) — CKZ native movement detours
 *
 * The bit-exact path: detours the granular CS2 movement pipeline (AirAccelerate → FinishMove) the same
 * functions cs2kz raw-detours, resolved from sigs in kreedz-ckz.games.jsonc and hooked via ModSharp's
 * IDetourHook. This is what makes CKZ times leaderboard-identical (rampbug/slopefix in TryPlayerMove,
 * exact air-accel curve, ladder physics) rather than the ProcessMove-level approximation.
 *
 * ON by default (`kz_ckz_native_hooks`, default 1 — set 0 if a sig breaks after a CS2 update). Each
 * detour reads/writes native movement through ModSharp's ported `MoveData` struct (CMoveData) via
 * `Unsafe.AsRef` — no hand-ported offsets — so the CKZ physics is filled with typed field access
 * (`md.Velocity`, `md.AbsOrigin`, `md.MaxSpeed`, …).
 *
 * FILLED so far: **CategorizePosition → rampbug fix** (cs2kz OnCategorizePosition), a real physics
 * correction — the raw detour resolves the player via a `{movementService* → slot}` map populated from
 * the managed hook (`IPlayerMovementService.GetAbsPtr()`), tracks `lastValidPlane` best-effort from the
 * per-tick ground trace, and nudges the origin off a rampbug seam via `TraceShapePlayerMovement`. The
 * remaining detours are still pass-through; TryPlayerMove's full collision loop + FinishMove vhook are
 * the next fills. EXPERIMENTAL — this modifies live movement and is best-effort from source; it needs
 * tick-for-tick demo validation on a real server (per prefix's go-ahead to proceed without server tests).
 *
 * Native signatures are cs2kz's movement.h detour typedefs verbatim; x64 uses the platform default
 * calling convention (this in rcx/rdi). FinishMove is a vtable func (offset 38/39) — a virtual hook, TODO.
 */

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Hooks;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Kreedz.Mode.Ckz;

internal sealed unsafe class MovementDetours
{
    private const string Ns = "CCSPlayer_MovementServices::";

    // cs2kz kz_mode_ckz.h — rampbug/slopefix constants.
    private const float RampBugThreshold = 0.98f;
    private const float Epsilon          = 0.00001f;

    private readonly IHookManager             _hookManager;
    private readonly IPhysicsQueryManager     _physics;
    private readonly ILogger                  _logger;
    private readonly List<IRuntimeNativeHook> _hooks = [];

    // Reverse map {native CCSPlayer_MovementServices* -> slot}, filled from the managed ProcessMove hook
    // (which has both the pawn and the slot) so the raw native detours can resolve the player.
    private const float SpeedNormal = 250.0f; // cs2kz SPEED_NORMAL

    private readonly Dictionary<nint, int> _slotByMs   = new();
    private readonly Vector[]              _lastPlane   = new Vector[PlayerSlot.MaxPlayerCount];
    private readonly float[]               _gain        = new float[PlayerSlot.MaxPlayerCount];

    private static MovementDetours? _self;

    // Trampolines to the originals (static so the [UnmanagedCallersOnly] hooks can reach them).
    private static nint _tProcessMovement, _tAirAccelerate, _tFriction, _tWalkMove, _tAirMove, _tTryPlayerMove,
                        _tCategorizePosition, _tCheckJumpLegacy, _tCheckJumpModern, _tDuck, _tCanUnduck,
                        _tCheckVelocity, _tCheckWater, _tWaterMove, _tLadderMove, _tCheckFalling,
                        _tFullWalkMove, _tMoveInit;

    public MovementDetours(IHookManager hookManager, IPhysicsQueryManager physics, ILogger logger)
    {
        _hookManager = hookManager;
        _physics     = physics;
        _logger      = logger;
    }

    /// <summary>Register a player's native movement-service pointer → slot (call each tick from the
    /// managed hook where the pawn+slot are known). Lets the raw native detours resolve the player.</summary>
    public void Map(nint movementServicePtr, int slot)
    {
        if (movementServicePtr != 0)
            _slotByMs[movementServicePtr] = slot;
    }

    /// <summary>Share the player's current prestrafe gain (computed in the managed hook) so the AirMove
    /// detour can restore max speed to 250+gain after the air-move (cs2kz OnAirMovePost).</summary>
    public void SetGain(int slot, float gain) => _gain[slot] = gain;

    public bool Installed { get; private set; }

    public void Install()
    {
        if (Installed) return;

        _tProcessMovement    = Hook("ProcessMovement",       (nint)(delegate* unmanaged<nint, nint, void>)&Hk_ProcessMovement);
        _tAirAccelerate      = Hook("AirAccelerate",         (nint)(delegate* unmanaged<nint, nint, nint, float, float, void>)&Hk_AirAccelerate);
        _tFriction           = Hook("Friction",              (nint)(delegate* unmanaged<nint, nint, void>)&Hk_Friction);
        _tWalkMove           = Hook("WalkMove",              (nint)(delegate* unmanaged<nint, nint, void>)&Hk_WalkMove);
        _tAirMove            = Hook("AirMove",               (nint)(delegate* unmanaged<nint, nint, void>)&Hk_AirMove);
        _tTryPlayerMove      = Hook("TryPlayerMove",         (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&Hk_TryPlayerMove);
        _tCategorizePosition = Hook("CategorizePosition",    (nint)(delegate* unmanaged<nint, nint, byte, void>)&Hk_CategorizePosition);
        _tCheckJumpLegacy    = Hook("CheckJumpButtonLegacy", (nint)(delegate* unmanaged<nint, nint, void>)&Hk_CheckJumpLegacy);
        _tCheckJumpModern    = Hook("CheckJumpButtonModern", (nint)(delegate* unmanaged<nint, nint, void>)&Hk_CheckJumpModern);
        _tDuck               = Hook("Duck",                  (nint)(delegate* unmanaged<nint, nint, void>)&Hk_Duck);
        _tCanUnduck          = Hook("CanUnduck",             (nint)(delegate* unmanaged<nint, nint, byte>)&Hk_CanUnduck);
        _tCheckVelocity      = Hook("CheckVelocity",         (nint)(delegate* unmanaged<nint, nint, nint, void>)&Hk_CheckVelocity);
        _tCheckWater         = Hook("CheckWater",            (nint)(delegate* unmanaged<nint, nint, byte>)&Hk_CheckWater);
        _tWaterMove          = Hook("WaterMove",             (nint)(delegate* unmanaged<nint, nint, void>)&Hk_WaterMove);
        _tLadderMove         = Hook("LadderMove",            (nint)(delegate* unmanaged<nint, nint, byte>)&Hk_LadderMove);
        _tCheckFalling       = Hook("CheckFalling",          (nint)(delegate* unmanaged<nint, nint, void>)&Hk_CheckFalling);
        _tFullWalkMove       = Hook("FullWalkMove",          (nint)(delegate* unmanaged<nint, nint, byte, void>)&Hk_FullWalkMove);
        _tMoveInit           = Hook("MoveInit",              (nint)(delegate* unmanaged<nint, nint, byte>)&Hk_MoveInit);

        // TODO: FinishMove is a vtable func (offset 38/39) — install via CreateVirtualHook, not a sig detour.

        _self    = this;
        Installed = true;
        _logger.LogWarning("[CKZ] native movement detours INSTALLED — rampbug fix live (EXPERIMENTAL, validate on a test server).");
    }

    public void Uninstall()
    {
        foreach (var hook in _hooks)
            hook.Uninstall();
        _hooks.Clear();
        Installed = false;
    }

    /// <summary>Typed view over the raw CMoveData* — ModSharp's ported struct, platform-correct offsets.</summary>
    private static ref MoveData Move(nint mv) => ref Unsafe.AsRef<MoveData>((void*)mv);

    private nint Hook(string name, nint hookFn)
    {
        var hook = _hookManager.CreateDetourHook();
        hook.Prepare(Ns + name, hookFn);

        if (!hook.Install())
        {
            _logger.LogError("[CKZ] failed to install movement detour {Name} (bad sig for this build?)", name);
            return 0;
        }

        _hooks.Add(hook);
        return hook.Trampoline;
    }

    // ── Detours: verified-signature PASS-THROUGH. Fill CKZ physics per function, then validate live. ──
    // cs2kz reference: KZClassicModeService::On<Fn> in src/kz/mode/kz_mode_ckz.cpp.

    [UnmanagedCallersOnly]
    private static void Hk_ProcessMovement(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tProcessMovement)(ms, mv);

    [UnmanagedCallersOnly] // CKZ: custom air-accel curve (high sv_airaccelerate strafing)
    private static void Hk_AirAccelerate(nint ms, nint mv, nint wishdir, float wishspeed, float accel)
        => ((delegate* unmanaged<nint, nint, nint, float, float, void>)_tAirAccelerate)(ms, mv, wishdir, wishspeed, accel);

    [UnmanagedCallersOnly]
    private static void Hk_Friction(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tFriction)(ms, mv);

    [UnmanagedCallersOnly]
    private static void Hk_WalkMove(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tWalkMove)(ms, mv);

    [UnmanagedCallersOnly] // CKZ OnAirMove/Post: cap air wishspeed at 250 during the air-move, restore 250+gain after
    private static void Hk_AirMove(nint ms, nint mv)
    {
        var slot = _self is not null && _self._slotByMs.TryGetValue(ms, out var s) ? s : -1;

        if (slot >= 0) Move(mv).MaxSpeed = SpeedNormal;                    // OnAirMove
        ((delegate* unmanaged<nint, nint, void>)_tAirMove)(ms, mv);        // engine air-move
        if (slot >= 0) Move(mv).MaxSpeed = SpeedNormal + _self!._gain[slot]; // OnAirMovePost
    }

    [UnmanagedCallersOnly] // CKZ: rampbug/slopefix
    private static void Hk_TryPlayerMove(nint ms, nint mv, nint firstDest, nint firstTrace, nint blocked)
        => ((delegate* unmanaged<nint, nint, nint, nint, nint, void>)_tTryPlayerMove)(ms, mv, firstDest, firstTrace, blocked);

    [UnmanagedCallersOnly]
    private static void Hk_CategorizePosition(nint ms, nint mv, byte stayOnGround)
    {
        ((delegate* unmanaged<nint, nint, byte, void>)_tCategorizePosition)(ms, mv, stayOnGround); // engine categorize first
        _self?.RampBugFix(ms, mv, stayOnGround != 0);
    }

    // cs2kz KZClassicModeService::OnCategorizePosition — rampbug fix. When dropping fast onto a plane
    // steeper than the last valid one we stood on, nudge the origin back along that plane so the engine
    // doesn't "rampbug" (lose all speed on a seam). EXPERIMENTAL: lastValidPlane is tracked best-effort
    // from the per-tick ground trace (cs2kz threads it through TryPlayerMove) — needs live validation.
    private void RampBugFix(nint ms, nint mv, bool stayOnGround)
    {
        if (!_slotByMs.TryGetValue(ms, out var slot)) return;

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

    // Returns plain values (not the GameTrace ref struct) so nothing aliases the local query.
    private (float Fraction, Vector PlaneNormal, bool StartInSolid) TraceDown(Vector mins, Vector maxs, Vector origin)
    {
        var end = origin; end.Z -= 2.0f;
        var query = RnQueryShapeAttr.PlayerMovement(InteractionLayers.Solid);
        var t = _physics.TraceShapePlayerMovement(new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs }), origin, end, in query);
        return (t.Fraction, t.PlaneNormal, t.StartInSolid);
    }

    // Standard CS2 standing player hull. Ducking (maxs.z 54) is a refinement for the validated pass.
    private static (Vector Mins, Vector Maxs) Hull()
        => (new Vector(-16f, -16f, 0f), new Vector(16f, 16f, 72f));

    private static float Dot(Vector a, Vector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    [UnmanagedCallersOnly] // CKZ: perf/bhop timing window
    private static void Hk_CheckJumpLegacy(nint jump, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tCheckJumpLegacy)(jump, mv);

    [UnmanagedCallersOnly]
    private static void Hk_CheckJumpModern(nint jump, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tCheckJumpModern)(jump, mv);

    [UnmanagedCallersOnly]
    private static void Hk_Duck(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tDuck)(ms, mv);

    [UnmanagedCallersOnly]
    private static byte Hk_CanUnduck(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, byte>)_tCanUnduck)(ms, mv);

    [UnmanagedCallersOnly]
    private static void Hk_CheckVelocity(nint ms, nint mv, nint desc)
        => ((delegate* unmanaged<nint, nint, nint, void>)_tCheckVelocity)(ms, mv, desc);

    [UnmanagedCallersOnly]
    private static byte Hk_CheckWater(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, byte>)_tCheckWater)(ms, mv);

    [UnmanagedCallersOnly]
    private static void Hk_WaterMove(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tWaterMove)(ms, mv);

    [UnmanagedCallersOnly] // CKZ: ladder physics
    private static byte Hk_LadderMove(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, byte>)_tLadderMove)(ms, mv);

    [UnmanagedCallersOnly]
    private static void Hk_CheckFalling(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, void>)_tCheckFalling)(ms, mv);

    [UnmanagedCallersOnly]
    private static void Hk_FullWalkMove(nint ms, nint mv, byte waterMoveOnly)
        => ((delegate* unmanaged<nint, nint, byte, void>)_tFullWalkMove)(ms, mv, waterMoveOnly);

    [UnmanagedCallersOnly]
    private static byte Hk_MoveInit(nint ms, nint mv)
        => ((delegate* unmanaged<nint, nint, byte>)_tMoveInit)(ms, mv);
}
