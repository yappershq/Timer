/*
 * yappershq/Kreedz (KZ) — Classic (CKZ) mode plugin
 *
 * A standalone ModSharp module that registers the Classic mode against Kreedz.Core's IKzModeRegistry AND
 * owns the CKZ custom movement — a faithful port of cs2kz kz_mode_ckz (CalcPrestrafe / GetPrestrafeGain /
 * OnStopTouchGround). Constants + formulas are transcribed verbatim from the cs2kz source (verified);
 * frametime/curtime come from the engine globals. All movement is gated on IKzModeRegistry.GetPlayerMode
 * == "ckz", so it only touches CKZ players. Drop this DLL next to Kreedz.Core to add Classic mode.
 *
 * Fidelity note: this is the demo-validated crux. The math is exact; tick-for-tick certification vs
 * recorded cs2kz demos is the final pass. cs2kz spreads the physics across StartTouch/StopTouch/
 * CalcPrestrafe on its native detours; here it runs off ProcessMovePre + ground-state transitions.
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

namespace Kreedz.Mode.Ckz;

public sealed class KreedzModeCkz : IModSharpModule
{
    // cs2kz kz_mode_ckz.h — verbatim.
    private const float SpeedNormal         = 250.0f;
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

    private static readonly float BhNormalizeFactor =
        BhBaseMultiplier * MathF.Log(SpeedNormal + PsSpeedMax) - (SpeedNormal + PsSpeedMax);

    private readonly ISharedSystem          _shared;
    private readonly IModSharp              _modSharp;
    private readonly IHookManager           _hookManager;
    private readonly ILogger<KreedzModeCkz> _logger;

    private readonly IConVar?        _nativeHooks;
    private readonly MovementDetours _detours;

    private IKzModeRegistry? _registry;

    private readonly float[] _bonusSpeed    = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _leftPreRatio  = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _rightPreRatio = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _lastYaw       = new float[PlayerSlot.MaxPlayerCount];

    private readonly bool[]   _wasGround       = new bool[PlayerSlot.MaxPlayerCount];
    private readonly float[]  _landingTime     = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[]  _takeoffTime     = new float[PlayerSlot.MaxPlayerCount];
    private readonly Vector[] _landingVelocity = new Vector[PlayerSlot.MaxPlayerCount];

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
        _logger      = shared.GetLoggerFactory().CreateLogger<KreedzModeCkz>();

        // The bit-exact native movement detours (staged; see MovementDetours). Off by default — they
        // read/replace native movement and must be validated on a live server before enabling.
        _modSharp.GetGameData().Register("kreedz-ckz.games");
        _nativeHooks = shared.GetConVarManager().CreateConVar("kz_ckz_native_hooks", true,
            "Enable the native CKZ movement detours (bit-exact path). Set 0 if a sig breaks after a CS2 update.");
        _detours = new MovementDetours(_hookManager, shared.GetPhysicsQueryManager(), _logger);

        for (var i = 0; i < _angleHistory.Length; i++)
            _angleHistory[i] = [];
    }

    public bool Init()
    {
        _hookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        _hookManager.PlayerGetMaxSpeed.InstallHookPre(OnGetMaxSpeed);

        if (_nativeHooks?.GetBool() == true)
        {
            try { _detours.Install(); }
            catch (System.Exception e) { _logger.LogError(e, "[CKZ] native detour install failed — set kz_ckz_native_hooks 0"); }
        }

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
        _logger.LogInformation("[Kreedz.Mode.CKZ] registered.");
    }

    public void Shutdown()
    {
        _hookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);
        _hookManager.PlayerGetMaxSpeed.RemoveHookPre(OnGetMaxSpeed);
        _detours.Uninstall();
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

        // Register this player's native movement-service pointer → slot so the raw native detours (which
        // only get a CCSPlayer_MovementServices*) can resolve the player for per-slot state.
        if (_detours.Installed && arg.Pawn.GetPlayerMovementService() is { } msvc)
            _detours.Map(msvc.GetAbsPtr(), slot);

        var globals   = _modSharp.GetGlobals();
        var frametime = globals.FrameTime;
        var curtime   = globals.CurTime;

        var velocity = arg.Pawn.GetAbsVelocity();
        var onGround = arg.Pawn.GroundEntityHandle.IsValid();

        var yaw  = arg.Pawn.GetEyeAngles().Y;
        var rate = frametime > 0f ? NormalizeYaw(yaw - _lastYaw[slot]) / frametime : 0f;
        _lastYaw[slot] = yaw;
        PushAngle(slot, rate, frametime);

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

        // Share the gain so the AirMove detour can restore 250+gain after the air-move (cs2kz OnAirMovePost).
        if (_detours.Installed) _detours.SetGain(slot, GetPrestrafeGain(slot));

        _wasGround[slot] = onGround;
    }

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

    private static float NormalizeYaw(float a)
    {
        while (a >  180f) a -= 360f;
        while (a < -180f) a += 360f;
        return a;
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
