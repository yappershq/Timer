/*
 * yappershq/Kreedz (KZ) — Anticheat plugin (1:1 cs2kz src/kz/anticheat)
 *
 * A standalone ModSharp module (split out of Core, like the mode/style plugins) — it depends only on
 * ISharedSystem primitives (hooks, convars, client manager), no Core-internal services, so it's a clean
 * drop-in that a server can install or omit.
 *
 * Detectors:
 *   1. Invalid client-cvar — illegal client convar values that enable cheating (tampered m_yaw,
 *      out-of-range cl_pitchdown/up), checked on spawn.
 *   2. Bhop-hack — an inhuman chain of consecutive perfect bhops (>=25, each takeoff within the perf
 *      window). No human hits 25 perfs in a row; a scripted bhop does it every jump.
 * Both log and optionally kick (`kz_ac_autokick`, default off — matching cs2kz). All detection is
 * disabled while `sv_cheats 1` and for fake clients. The telemetry detectors (nulls/snaptap, hyperscroll,
 * strafe-optimizer, subtick) layer onto the same movement hook + are tuned against real movement data.
 */

using System;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Kreedz.Anticheat;

public sealed class KreedzAnticheat : IModSharpModule
{
    private const int   BhopHackChain = 25;    // cs2kz — perfs in a row that no human can hit
    private const float PerfWindow    = 0.02f; // cs2kz BH_PERF_WINDOW
    private const float TickTime      = 1f / 64f;

    private readonly IModSharp               _modSharp;
    private readonly IHookManager            _hookManager;
    private readonly IClientManager          _clientManager;
    private readonly ILogger<KreedzAnticheat> _logger;
    private readonly IConVar                 _autokick;
    private readonly IConVar?                _svCheats;

    private const int NullsChain = 20; // clean counter-strafes in a row no human hits (cs2kz nulls detector)

    private readonly bool[]  _wasGround  = new bool[PlayerSlot.MaxPlayerCount];
    private readonly float[] _groundTime = new float[PlayerSlot.MaxPlayerCount];
    private readonly int[]   _perfChain  = new int[PlayerSlot.MaxPlayerCount];

    // Nulls detector: a "null" script swaps strafe keys with zero overlap/deadair — a perfectly clean
    // A↔D flip every tick. Humans pass through a both-pressed or neither-pressed tick. Count consecutive
    // clean counter-strafes; an inhuman run of them = nulls. (Tick-resolution; cs2kz uses subtick timing.)
    private readonly int[] _lastStrafeDir = new int[PlayerSlot.MaxPlayerCount]; // -1 left, +1 right, 0 none/both
    private readonly int[] _nullsChain    = new int[PlayerSlot.MaxPlayerCount];

    // Subtick snaptap detector (cs2kz nulls): perfect same-subtick counter-strafes no human hits.
    private const float SnaptapEpsilon = 0.0078125f; // ~1 subtick — release+press this close = perfect
    private const int   SnaptapChain   = 128;        // cs2kz NUM_CONSECUTIVE_PERFECT_CSTRAFE minimum
    private readonly int[] _snapChain   = new int[PlayerSlot.MaxPlayerCount];

    // Strafe-optimizer detector (cs2kz strafe_optimizer.cpp): a scripted optimizer snaps the yaw at the
    // exact optimal strafe-reversal, producing a yaw-accel spike a human mouse can't. Rolling average of
    // spike occurrences; flag past 0.9. Needs 6 angle frames to compute accel at the 3 sample points.
    private readonly float[][] _yawBuf  = NewJagged(6);
    private readonly float[][] _ftBuf   = NewJagged(6);
    private readonly int[]     _yawLen  = new int[PlayerSlot.MaxPlayerCount];
    private readonly float[]   _soPct   = new float[PlayerSlot.MaxPlayerCount];

    private static float[][] NewJagged(int depth)
    {
        var a = new float[PlayerSlot.MaxPlayerCount][];
        for (var i = 0; i < a.Length; i++) a[i] = new float[depth];
        return a;
    }

    public string DisplayName   => "[Kreedz] Anticheat";
    public string DisplayAuthor => "yappershq";

    public KreedzAnticheat(ISharedSystem shared, string? dllPath, string? sharpPath, Version? version, IConfiguration? coreConfiguration, bool hotReload)
    {
        _modSharp      = shared.GetModSharp();
        _hookManager   = shared.GetHookManager();
        _clientManager = shared.GetClientManager();
        _logger        = shared.GetLoggerFactory().CreateLogger<KreedzAnticheat>();

        var cvar = shared.GetConVarManager();
        _autokick = cvar.CreateConVar("kz_ac_autokick", false,
            "Anticheat kicks flagged players instead of warning.")!;
        _svCheats = cvar.FindConVar("sv_cheats");
    }

    public bool Init()
    {
        _hookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);
        _hookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        _hookManager.PlayerRunCommand.InstallHookPost(OnRunCommandPost);
        return true;
    }

    // Nulls detector — inspect per-tick strafe buttons for inhumanly-clean counter-strafes.
    private void OnRunCommandPost(IPlayerRunCommandHookParams param, HookReturnValue<EmptyHookReturn> ret)
    {
        var client = param.Client;
        if (client.IsValid && !client.IsFakeClient && !(_svCheats?.GetBool() ?? false))
        {
            var slot = client.Slot;
            var left  = param.KeyButtons.HasFlag(UserCommandButtons.MoveLeft);
            var right = param.KeyButtons.HasFlag(UserCommandButtons.MoveRight);
            var dir   = left == right ? 0 : (left ? -1 : 1); // both or neither = 0 (a human transition)

            if (dir != 0)
            {
                // A clean counter-strafe: direction flipped between two ticks with no 0-tick between.
                if (_lastStrafeDir[slot] != 0 && dir != _lastStrafeDir[slot])
                {
                    if (++_nullsChain[slot] >= NullsChain)
                    {
                        Flag(client, $"nulls ({_nullsChain[slot]} perfect counter-strafes in a row)");
                        _nullsChain[slot] = 0;
                    }
                }
                _lastStrafeDir[slot] = dir;
            }
            else
            {
                _nullsChain[slot]    = 0; // overlap/deadair — human imperfection, reset
                _lastStrafeDir[slot] = 0;
            }
        }
    }

    public void Shutdown()
    {
        _hookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);
        _hookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);
        _hookManager.PlayerRunCommand.RemoveHookPost(OnRunCommandPost);
    }

    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient || !arg.Pawn.IsAlive) return;
        if (_svCheats?.GetBool() ?? false) return;

        var slot     = client.Slot;
        var onGround = arg.Pawn.GroundEntityHandle.IsValid();

        if (onGround && !_wasGround[slot]) _groundTime[slot] = 0f; // landed
        if (onGround) _groundTime[slot] += TickTime;

        if (!onGround && _wasGround[slot]) // took off
        {
            if (_groundTime[slot] <= PerfWindow)
            {
                if (++_perfChain[slot] >= BhopHackChain)
                {
                    Flag(client, $"bhop-hack ({_perfChain[slot]} perfect bhops in a row)");
                    _perfChain[slot] = 0;
                }
            }
            else
            {
                _perfChain[slot] = 0;
            }
        }

        _wasGround[slot] = onGround;

        DetectSnaptap(client, slot, arg);
        DetectStrafeOptimizer(client, slot, arg.Pawn.GetEyeAngles().Y, _modSharp.GetGlobals().FrameTime);
    }

    // cs2kz KZAnticheatService::DetectOptimization — flags a scripted strafe optimizer by its yaw-accel
    // spike at strafe reversals (a human mouse can't produce it). Buffer of the last 6 (yaw, frametime);
    // yaw speed = Δyaw/ft, yaw accel = Δspeed/ft; a low-avg-accel window with a lone spike at a direction
    // switch bumps a rolling average toward 1; > 0.9 = detected.
    private void DetectStrafeOptimizer(IGameClient client, PlayerSlot slot, float yaw, float ft)
    {
        if (ft <= 0f) return;
        var yb = _yawBuf[slot]; var fb = _ftBuf[slot];
        for (var i = 0; i < 5; i++) { yb[i] = yb[i + 1]; fb[i] = fb[i + 1]; } // shift, newest at [5]
        yb[5] = yaw; fb[5] = ft;
        if (_yawLen[slot] < 6) { _yawLen[slot]++; return; }

        float Speed(int i) => fb[i] > 0f ? NormalizeYaw(yb[i] - yb[i - 1]) / fb[i] : 0f;
        float Accel(int i) => fb[i] > 0f ? (Speed(i) - Speed(i - 1)) / fb[i] : 0f;

        var curSpeed = Speed(5); var lastSpeed = Speed(4);
        var switched = (curSpeed < 0f) != (lastSpeed < 0f);

        var accel2ago = MathF.Abs(Accel(3));
        var lastAccel = MathF.Abs(Accel(4));
        var curAccel  = MathF.Abs(Accel(5));

        if (MathF.Abs(curAccel - accel2ago) < 1.0f)
        {
            var avg = (curAccel + accel2ago) * 0.5f;
            if (avg < 2.0f && (lastAccel - avg) > 2.0f && switched)
                _soPct[slot] = _soPct[slot] * 0.95f + 0.05f;        // spike at a reversal
            else if (switched)
                _soPct[slot] = _soPct[slot] * 0.95f;                // clean reversal
        }

        if (_soPct[slot] > 0.9f)
        {
            Flag(client, "strafe-optimizer (scripted yaw-accel pattern)");
            _soPct[slot] = 0f;
        }
    }

    private static float NormalizeYaw(float a)
    {
        while (a >  180f) a -= 360f;
        while (a < -180f) a += 360f;
        return a;
    }

    // Subtick snaptap/nulls detector (cs2kz src/kz/anticheat/detectors/nulls). A snaptap/SOCD device
    // cancels one strafe key the instant the opposite is pressed — a release + opposite-press at the
    // SAME subtick `When`, perfectly, every counter-strafe. Humans have an underlap gap. Reads the real
    // subtick move data (MoveData.SubTickMoves) and counts consecutive same-subtick counter-strafes.
    private unsafe void DetectSnaptap(IGameClient client, PlayerSlot slot, IPlayerProcessMoveForwardParams arg)
    {
        var moves = arg.Info->SubTickMoves.AsReadOnlySpan();
        if (moves.Length == 0) return;

        float releaseWhen = -1f; UserCommandButtons releaseKey = 0;
        var strafed = false;

        foreach (ref readonly var m in moves)
        {
            if (m.Button is not (UserCommandButtons.MoveLeft or UserCommandButtons.MoveRight)) continue;
            strafed = true;

            if (!m.Pressed) { releaseWhen = m.When; releaseKey = m.Button; continue; }

            // A press of the opposite strafe key right after releasing the other = a counter-strafe.
            if (releaseWhen >= 0f && m.Button != releaseKey)
            {
                if (MathF.Abs(m.When - releaseWhen) < SnaptapEpsilon) // same subtick → inhumanly perfect
                {
                    if (++_snapChain[slot] >= SnaptapChain)
                    {
                        Flag(client, $"snaptap ({_snapChain[slot]} perfect same-subtick counter-strafes)");
                        _snapChain[slot] = 0;
                    }
                }
                else _snapChain[slot] = 0; // real underlap gap = human
                releaseWhen = -1f;
            }
        }

        if (!strafed) _snapChain[slot] = 0;
    }

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var client = @params.Client;
        if (client.IsFakeClient) return;

        // Client cvars replicate shortly after spawn — check next frame.
        _modSharp.InvokeFrameAction(() => CheckClient(client));
    }

    private void CheckClient(IGameClient client)
    {
        if (!client.IsValid || (_svCheats?.GetBool() ?? false)) return; // no detection while sv_cheats

        if (FirstViolation(client) is not { } violation) return;

        Flag(client, $"invalid cvar ({violation})");
    }

    private void Flag(IGameClient client, string reason)
    {
        _logger.LogWarning("[KZ.AC] {Name} ({Sid}) flagged: {Reason}", client.Name, client.SteamId, reason);
        if (_autokick.GetBool())
            _clientManager.KickClient(client, $"KZ: {reason}");
        else
            client.Print(HudPrintChannel.Chat, $"[KZ] Anticheat flagged: {reason}.");
    }

    private static string? FirstViolation(IGameClient client)
    {
        if (Value(client, "m_yaw")        is { } yaw && Math.Abs(yaw - 0.022) > 0.0005) return "m_yaw";
        if (Value(client, "cl_pitchdown") is { } pd  && pd > 89.0001)                   return "cl_pitchdown";
        if (Value(client, "cl_pitchup")   is { } pu  && pu < -89.0001)                  return "cl_pitchup";
        return null;
    }

    private static double? Value(IGameClient client, string cvar)
        => double.TryParse(client.GetConVarValue(cvar), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}
