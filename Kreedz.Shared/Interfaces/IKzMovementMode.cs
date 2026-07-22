using Sharp.Shared.Units;

namespace Kreedz.Shared.Interfaces;

/// <summary>
/// A mode's native-movement callbacks. Kreedz.Core installs the granular CS2 movement detours
/// (AirAccelerate→FinishMove surface) <b>once</b> and dispatches, per player, to that player's active
/// mode through this interface — mirroring how cs2kz's core installs the hooks and calls the active
/// <c>KZModeService</c>'s virtual <c>On*</c> callbacks. Two mode plugins can therefore share one set of
/// trampolines instead of each trying to detour the same function (which collides).
///
/// A mode registers its implementation via <see cref="IKzModeRegistry.RegisterMovementMode"/>. Every
/// method is a default no-op, so a mode overrides only the callbacks it needs — Vanilla (VNL) registers
/// nothing and gets pure stock movement.
///
/// Parameters are raw native pointers, matching the detour signatures: <paramref name="ms"/> is a
/// <c>CCSPlayer_MovementServices*</c> and <paramref name="mv"/> a <c>CMoveData*</c>. Read/write the move
/// with <c>Unsafe.AsRef&lt;Sharp.Shared.Types.MoveData&gt;((void*)mv)</c>; obtain physics queries from the
/// mode's own <c>ISharedSystem.GetPhysicsQueryManager()</c>. <paramref name="slot"/> is resolved by Core.
/// </summary>
public interface IKzMovementMode
{
    static readonly string Identity = typeof(IKzMovementMode).FullName!;

    /// <summary>Before the engine air-move (cs2kz OnAirMove) — e.g. cap air wishspeed to SPEED_NORMAL.</summary>
    void OnAirMove(PlayerSlot slot, nint ms, nint mv) { }

    /// <summary>After the engine air-move (cs2kz OnAirMovePost) — e.g. restore max speed to 250 + prestrafe gain.</summary>
    void OnAirMovePost(PlayerSlot slot, nint ms, nint mv) { }

    /// <summary>Before the engine CategorizePosition (cs2kz OnCategorizePosition runs the fix, then calls the
    /// original) — the rampbug origin nudge. Runs BEFORE the trampoline so the engine sees the corrected origin.</summary>
    void OnCategorizePosition(PlayerSlot slot, nint ms, nint mv, bool stayOnGround) { }

    /// <summary>Before the engine TryPlayerMove (cs2kz OnTryPlayerMove) — capture pre-move state.</summary>
    void OnTryPlayerMovePre(PlayerSlot slot, nint ms, nint mv) { }

    /// <summary>After the engine TryPlayerMove (cs2kz OnTryPlayerMovePost) — the slopefix collision reimpl.</summary>
    void OnTryPlayerMovePost(PlayerSlot slot, nint ms, nint mv) { }

    /// <summary>After the whole tick's ProcessMovement (cs2kz OnProcessMovementPost, dispatched from the
    /// FinishMove-equivalent post hook) — where cs2kz's VNL runs TriggerFix and modes apply per-tick trigger
    /// interception. Core has already captured moveDataPost by the time this fires.</summary>
    void OnProcessMovementPost(PlayerSlot slot, nint ms, nint mv) { }

    /// <summary>cs2kz CanTouchTimerZone — whether timer-zone touch events are accepted right now. Modes gate
    /// this to tick boundaries so subtick-time zone touches can't shave run time (VNL: full ticks only;
    /// CKZ: full + half ticks). Default: always.</summary>
    bool CanTouchTimerZone(PlayerSlot slot) => true;

    /// <summary>cs2kz IsPerfing — is the player currently airborne off a perf (bhop within the perf window)?
    /// The HUD reads this to tint the speed. Default false (VNL / modes without a perf concept).</summary>
    bool IsPerfing(PlayerSlot slot) => false;
}
