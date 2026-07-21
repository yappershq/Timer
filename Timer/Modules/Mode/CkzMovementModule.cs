/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * CKZ prestrafe foundation (cs2kz src/movement + kz_mode_ckz). Turning while grounded builds a speed
 * bonus above the 250 base (up to ~26 u/s, cs2kz PS_SPEED_MAX), so you carry more speed into a jump —
 * the single most recognizable KZ movement mechanic. Applied ONLY to players in CKZ mode, via the
 * per-tick ProcessMove hook (accumulate a prestrafe ratio from the yaw turn-rate) + the GetMaxSpeed
 * hook (return 250 + bonus).
 *
 * This is the "feels like KZ" foundation. The BIT-EXACT engine — the exact 0.02s turn-rate window,
 * air-strafe prestrafe, the perf/bhop speed formula, rampbug/slopefix, and a reimplemented
 * TryPlayerMove — is the leaderboard-faithful refinement, validated tick-for-tick against recorded
 * cs2kz demos. It layers onto these same hooks.
 */

using System;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Source2Surf.Timer.Modules;

internal interface ICkzMovementModule;

internal sealed class CkzMovementModule : IModule, ICkzMovementModule
{
    private const float BaseSpeed  = 250f;
    private const float PsSpeedMax  = 26f;    // cs2kz PS_SPEED_MAX
    private const float PsMaxTime   = 0.5f;   // cs2kz PS_MAX_PS_TIME — seconds of turning to reach max
    private const float TickTime    = 1f / 64f;
    private const float TurnThresh  = 0.2f;   // deg/tick to count as "turning"
    private const float DecayRatio  = 3f;     // cs2kz PS_DECREMENT_RATIO

    private readonly InterfaceBridge            _bridge;
    private readonly IModeModule                _mode;
    private readonly ILogger<CkzMovementModule> _logger;

    private readonly float[] _ratio   = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _lastYaw = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _bonus   = new float[PlayerSlot.MaxPlayerCount];

    public CkzMovementModule(InterfaceBridge bridge, IModeModule mode, ILogger<CkzMovementModule> logger)
    {
        _bridge = bridge;
        _mode   = mode;
        _logger = logger;
    }

    public bool Init()
    {
        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        _bridge.HookManager.PlayerGetMaxSpeed.InstallHookPre(OnGetMaxSpeed);
        return true;
    }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);
        _bridge.HookManager.PlayerGetMaxSpeed.RemoveHookPre(OnGetMaxSpeed);
    }

    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient) return;

        var slot = client.Slot;
        if (!IsCkz(slot) || !arg.Pawn.IsAlive)
        {
            _bonus[slot] = 0f;
            return;
        }

        var yaw   = arg.Pawn.GetEyeAngles().Y;
        var delta = MathF.Abs(NormalizeYaw(yaw - _lastYaw[slot]));
        _lastYaw[slot] = yaw;

        var onGround = arg.Pawn.GroundEntityHandle.IsValid();
        if (onGround && delta > TurnThresh)
            _ratio[slot] = MathF.Min(1f, _ratio[slot] + TickTime / PsMaxTime);
        else
            _ratio[slot] = MathF.Max(0f, _ratio[slot] - TickTime / PsMaxTime * DecayRatio);

        _bonus[slot] = PsSpeedMax * MathF.Sqrt(_ratio[slot]);
    }

    private HookReturnValue<float> OnGetMaxSpeed(IPlayerGetMaxSpeedHookParams @params, HookReturnValue<float> ret)
    {
        var client = @params.Client;
        if (client.IsFakeClient || !IsCkz(client.Slot))
            return new();

        return new(EHookAction.SkipCallReturnOverride, BaseSpeed + _bonus[client.Slot]);
    }

    private bool IsCkz(PlayerSlot slot) => string.Equals(_mode.GetMode(slot), "ckz", StringComparison.OrdinalIgnoreCase);

    private static float NormalizeYaw(float a)
    {
        while (a >  180f) a -= 360f;
        while (a < -180f) a += 360f;
        return a;
    }
}
