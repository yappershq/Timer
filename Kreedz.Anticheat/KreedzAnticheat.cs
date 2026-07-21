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
using Kreedz.Shared.Interfaces;

namespace Kreedz.Anticheat;

public sealed class KreedzAnticheat : IModSharpModule
{
    private const int   BhopHackChain = 25;    // cs2kz — perfs in a row that no human can hit
    private const float PerfWindow    = 0.02f; // cs2kz BH_PERF_WINDOW
    private const float TickTime      = 1f / 64f;

    private readonly ISharedSystem           _shared;
    private readonly IModSharp               _modSharp;
    private readonly IHookManager            _hookManager;
    private readonly IClientManager          _clientManager;
    private readonly ILogger<KreedzAnticheat> _logger;

    private IRequestManager? _request; // resolved cross-plugin for infraction persistence
    private readonly IConVar                 _autokick;
    private readonly IConVar                 _autoban;
    private readonly IConVar                 _banThreshold;
    private readonly IConVar                 _banMinutes;
    private readonly IConVar?                _svCheats;

    // Autoban accumulation (cs2kz Infraction→Finalize, simplified): confirmed flags within a fixed window;
    // once the count crosses kz_ac_ban_threshold, ban for kz_ac_ban_minutes. Fixed-window auto-resets, so a
    // reused slot never inherits a prior player's count. Default OFF (admin opt-in, like autokick).
    private const float BanWindow = 600f; // 10 min
    private readonly int[]   _infractions      = new int[PlayerSlot.MaxPlayerCount];
    private readonly float[] _infractionWindow = new float[PlayerSlot.MaxPlayerCount];

    private const int NullsChain = 20; // clean counter-strafes in a row no human hits (cs2kz nulls detector)

    private readonly bool[]  _wasGround  = new bool[PlayerSlot.MaxPlayerCount];
    private readonly float[] _groundTime = new float[PlayerSlot.MaxPlayerCount];
    private readonly int[]    _perfChain = new int[PlayerSlot.MaxPlayerCount];
    private readonly Vector[] _lastPos   = new Vector[PlayerSlot.MaxPlayerCount]; // for the telehop guard
    private readonly int[]    _tpGuard   = new int[PlayerSlot.MaxPlayerCount];    // ticks left ignoring bhops after a teleport

    // Nulls detector: a "null" script swaps strafe keys with zero overlap/deadair — a perfectly clean
    // A↔D flip every tick. Humans pass through a both-pressed or neither-pressed tick. Count consecutive
    // clean counter-strafes; an inhuman run of them = nulls. (Tick-resolution; cs2kz uses subtick timing.)
    private readonly int[] _lastStrafeDir = new int[PlayerSlot.MaxPlayerCount]; // -1 left, +1 right, 0 none/both
    private readonly int[] _nullsChain    = new int[PlayerSlot.MaxPlayerCount];

    // Subtick snaptap detector (cs2kz nulls): perfect same-subtick counter-strafes no human hits.
    private const float SnaptapEpsilon = 0.0078125f; // ~1 subtick — release+press this close = perfect
    private const int   SnaptapChain   = 128;        // cs2kz NUM_CONSECUTIVE_PERFECT_CSTRAFE minimum
    private readonly int[] _snapChain   = new int[PlayerSlot.MaxPlayerCount];

    // Desubticking detector (cs2kz subtick.cpp). A legit KB+M subtick move carries a fractional `When`; a
    // "desubticking" cheat zeroes it. Over a ~20s window, if the vast majority of subtick-carrying commands
    // are all-zero-`When`, flag. NOTE: cs2kz's other subtick checks (VerifyCommand, the "suspicious moves
    // with angles" count that SUBTICK_SUSPICIOUS_MOVES_THRESHOLD actually gates) need the pitch/yaw-delta +
    // full buttonstate protobuf fields ModSharp's MoveData does NOT expose — so only this zero-`When` ratio
    // check is portable. The old raw-subtick-move count was WRONG (every legit strafe carries subtick moves).
    private const int   DesubtickWindowTicks = 1280; // ~20s @ 64t  (SUBTICK_SUBTICK_INPUTS_WINDOW)
    private const int   DesubtickMinCommands = 30;   //             (SUBTICK_SUBTICK_INPUTS_THRESHOLD)
    private const float DesubtickRatio       = 0.9f; //             (SUBTICK_ZERO_WHEN_RATIO_THRESHOLD)
    private const int   DesubtickWarmupTicks = 640;  // ~10s ignore on connect (SUBTICK_INITIAL_IGNORE_TIME)
    private readonly int[] _subtickCmds     = new int[PlayerSlot.MaxPlayerCount];
    private readonly int[] _subtickZeroWhen = new int[PlayerSlot.MaxPlayerCount];
    private readonly int[] _subtickWinTicks = new int[PlayerSlot.MaxPlayerCount];
    private readonly int[] _subtickSeen     = new int[PlayerSlot.MaxPlayerCount];

    // Autostrafe detector (cs2kz jumps.cpp) — a script strafes far more per second than a human. Per jump
    // (airtime >= 0.6s, sync > 0.7): if strafes/sec exceeds thresholds it's suspicious; too many suspicious
    // jumps in a rolling window of 20 flags a strafe-hack.
    private const float AsMinAirtime  = 0.6f;
    private const float AsMinSync     = 0.7f;
    private const float AsBaseSps     = 18.0f;  // REAL_STRAFE_PER_SECOND_THRESHOLD
    private const float AsMaxSps      = 30.0f;  // MAX_STRAFES_PER_SECOND_THRESHOLD
    private const int   AsWindow      = 20;
    private const int   AsBaseSusp    = 15;
    private const int   AsMinSusp     = 5;
    private readonly bool[]  _jumpTracking = new bool[PlayerSlot.MaxPlayerCount];
    private readonly int[]   _jumpAir      = new int[PlayerSlot.MaxPlayerCount];
    private readonly int[]   _jumpGain     = new int[PlayerSlot.MaxPlayerCount];
    private readonly int[]   _jumpStrafes  = new int[PlayerSlot.MaxPlayerCount];
    private readonly float[] _jumpLastSpd  = new float[PlayerSlot.MaxPlayerCount];
    private readonly float[] _jumpLastYaw  = new float[PlayerSlot.MaxPlayerCount];
    private readonly int[]   _jumpYawDir   = new int[PlayerSlot.MaxPlayerCount];
    private readonly bool[][] _susWindow   = NewJaggedBool(AsWindow); // rolling suspicious flags
    private readonly bool[][] _veryHighWin = NewJaggedBool(AsWindow);
    private readonly int[]   _susIdx       = new int[PlayerSlot.MaxPlayerCount];

    private static bool[][] NewJaggedBool(int depth)
    {
        var a = new bool[PlayerSlot.MaxPlayerCount][];
        for (var i = 0; i < a.Length; i++) a[i] = new bool[depth];
        return a;
    }

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
        _shared        = shared;
        _modSharp      = shared.GetModSharp();
        _hookManager   = shared.GetHookManager();
        _clientManager = shared.GetClientManager();
        _logger        = shared.GetLoggerFactory().CreateLogger<KreedzAnticheat>();

        var cvar = shared.GetConVarManager();
        _autokick = cvar.CreateConVar("kz_ac_autokick", false,
            "Anticheat kicks flagged players instead of warning.")!;
        _autoban = cvar.CreateConVar("kz_ac_autoban", false,
            "Anticheat bans a player after kz_ac_ban_threshold flags within 10 minutes.")!;
        _banThreshold = cvar.CreateConVar("kz_ac_ban_threshold", 3,
            "Number of flags within the window that triggers an autoban (see kz_ac_autoban).")!;
        _banMinutes = cvar.CreateConVar("kz_ac_ban_minutes", 1440,
            "Autoban duration in minutes (default 1440 = 1 day).")!;
        _svCheats = cvar.FindConVar("sv_cheats");
    }

    public bool Init()
    {
        _hookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);
        _hookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        _hookManager.PlayerRunCommand.InstallHookPost(OnRunCommandPost);
        return true;
    }

    public void OnAllModulesLoaded()
        => _request = _shared.GetSharpModuleManager()
                            .GetOptionalSharpModuleInterface<IRequestManager>(IRequestManager.Identity)?.Instance;

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

        // cs2kz bhop guards: don't count perfs while noclipping/on a ladder (MoveType != Walk), or within 4
        // ticks of a teleport (BHOP_IGNORE_DURATION) — a large one-tick position jump is treated as a telehop.
        var origin = arg.Pawn.GetAbsOrigin();
        var dx = origin.X - _lastPos[slot].X;
        var dy = origin.Y - _lastPos[slot].Y;
        if (MathF.Sqrt(dx * dx + dy * dy) > 128f) _tpGuard[slot] = 4;
        else if (_tpGuard[slot] > 0)              _tpGuard[slot]--;
        _lastPos[slot] = origin;

        if (arg.Pawn.ActualMoveType != MoveType.Walk || _tpGuard[slot] > 0)
        {
            _perfChain[slot] = 0;
        }
        else if (!onGround && _wasGround[slot]) // took off
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

        DetectAutostrafe(client, slot, arg.Pawn, onGround);

        _wasGround[slot] = onGround;

        DetectSnaptap(client, slot, arg);
        DetectStrafeOptimizer(client, slot, arg.Pawn.GetEyeAngles().Y, _modSharp.GetGlobals().FrameTime);
    }

    // cs2kz jumps.cpp autostrafe detector — per-jump strafes/sec over a rolling window of jumps.
    private void DetectAutostrafe(IGameClient client, PlayerSlot slot, Sharp.Shared.GameEntities.IPlayerPawn pawn, bool onGround)
    {
        var vel   = pawn.GetAbsVelocity();
        var horiz = MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y);
        var yaw   = pawn.GetEyeAngles().Y;

        if (_wasGround[slot] && !onGround) // takeoff
        {
            _jumpTracking[slot] = pawn.ActualMoveType is MoveType.Walk;
            _jumpAir[slot] = _jumpGain[slot] = _jumpStrafes[slot] = 0;
            _jumpLastSpd[slot] = horiz; _jumpLastYaw[slot] = yaw; _jumpYawDir[slot] = 0;
        }
        else if (!onGround && _jumpTracking[slot]) // airborne
        {
            _jumpAir[slot]++;
            if (horiz > _jumpLastSpd[slot] + 0.01f) _jumpGain[slot]++;
            var dy  = NormalizeYaw(yaw - _jumpLastYaw[slot]);
            var dir = dy > 0.05f ? 1 : dy < -0.05f ? -1 : 0;
            if (dir != 0 && _jumpYawDir[slot] != 0 && dir != _jumpYawDir[slot]) _jumpStrafes[slot]++;
            if (dir != 0) _jumpYawDir[slot] = dir;
            _jumpLastSpd[slot] = horiz; _jumpLastYaw[slot] = yaw;
        }
        else if (!_wasGround[slot] && onGround && _jumpTracking[slot]) // landing
        {
            _jumpTracking[slot] = false;
            var airtime = _jumpAir[slot] * TickTime;
            var sync    = _jumpAir[slot] > 0 ? (float) _jumpGain[slot] / _jumpAir[slot] : 0f;

            var suspicious = false; var veryHigh = false;
            if (airtime >= AsMinAirtime && sync > AsMinSync)
            {
                var sps = _jumpStrafes[slot] / airtime;
                if (sps > AsMaxSps)      { suspicious = true; veryHigh = true; }
                else if (sps > AsBaseSps) suspicious = true;
            }

            var i = _susIdx[slot];
            _susWindow[slot][i]   = suspicious;
            _veryHighWin[slot][i] = veryHigh;
            _susIdx[slot] = (i + 1) % AsWindow;

            int susCount = 0, vhCount = 0;
            for (var k = 0; k < AsWindow; k++) { if (_susWindow[slot][k]) susCount++; if (_veryHighWin[slot][k]) vhCount++; }

            if (susCount >= AsBaseSusp || (susCount >= AsMinSusp && vhCount > 0))
            {
                Flag(client, $"autostrafe ({susCount}/{AsWindow} high-strafe jumps)");
                for (var k = 0; k < AsWindow; k++) { _susWindow[slot][k] = false; _veryHighWin[slot][k] = false; }
            }
        }
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

        // ── Desubticking check (cs2kz subtick.cpp): over ~20s, if ≥90% of subtick-carrying commands have
        // all-zero `When` on their button moves, it's a desubticking cheat. Skip a ~10s warmup on connect.
        if (_subtickSeen[slot] <= DesubtickWarmupTicks) _subtickSeen[slot]++;
        if (_subtickSeen[slot] > DesubtickWarmupTicks)
        {
            var hasButtonMove = false;
            var allZeroWhen   = true;
            foreach (ref readonly var mv in moves)
            {
                if ((int) mv.Button == 0) continue; // no button = analog/mouse — cs2kz excludes these
                hasButtonMove = true;
                if (mv.When != 0f) allZeroWhen = false;
            }

            if (hasButtonMove)
            {
                _subtickCmds[slot]++;
                if (allZeroWhen) _subtickZeroWhen[slot]++;
            }

            if (++_subtickWinTicks[slot] >= DesubtickWindowTicks)
            {
                if (_subtickCmds[slot] >= DesubtickMinCommands
                    && (float) _subtickZeroWhen[slot] / _subtickCmds[slot] >= DesubtickRatio)
                    Flag(client, $"desubticking ({_subtickZeroWhen[slot]}/{_subtickCmds[slot]} zero-when subtick cmds in 20s)");

                _subtickCmds[slot] = _subtickZeroWhen[slot] = _subtickWinTicks[slot] = 0;
            }
        }

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

        // Persist the infraction for review (cs2kz infractions.cpp) — fire-and-forget, degrades to no-op
        // if the request manager isn't available. Split the "<type> (<details>)" reason for the columns.
        if (_request is { } req)
        {
            var sid = client.SteamId;
            var paren = reason.IndexOf('(');
            var type = (paren > 0 ? reason[..paren] : reason).Trim();
            var details = paren > 0 ? reason[paren..].Trim('(', ')', ' ') : null;
            _ = SaveInfractionAsync(req, sid, type, details);
        }

        // Autoban accumulation (cs2kz Infraction→Finalize, simplified): once flags cross the threshold within
        // the window, ban + kick. Fixed-window so a reused slot never inherits a prior player's count.
        if (_autoban.GetBool() && _request is { } banReq)
        {
            var slot = client.Slot;
            var now  = _modSharp.GetGlobals().CurTime;

            if (now - _infractionWindow[slot] > BanWindow)
            {
                _infractions[slot]      = 0;
                _infractionWindow[slot] = now;
            }

            if (++_infractions[slot] >= _banThreshold.GetInt32())
            {
                _infractions[slot] = 0;
                var expiresAt = DateTime.UtcNow.AddMinutes(_banMinutes.GetInt32());
                _ = BanAsync(banReq, client.SteamId, reason, expiresAt);
                _clientManager.KickClient(client, $"KZ: Anticheat ban ({reason})");
                return;
            }
        }

        if (_autokick.GetBool())
            _clientManager.KickClient(client, $"KZ: {reason}");
        else
            client.Print(HudPrintChannel.Chat, $"[KZ] Anticheat flagged: {reason}.");
    }

    private async System.Threading.Tasks.Task BanAsync(IRequestManager req, SteamID sid, string reason, DateTime expiresAt)
    {
        try
        {
            await req.AddBanAsync(sid, $"Anticheat: {reason}", expiresAt);
            _logger.LogWarning("[KZ.AC] auto-banned {Sid} until {Exp} ({Reason})", sid, expiresAt, reason);
        }
        catch (Exception e) { _logger.LogError(e, "[KZ.AC] failed to auto-ban {Sid}", sid); }
    }

    private async System.Threading.Tasks.Task SaveInfractionAsync(IRequestManager req, SteamID sid, string type, string? details)
    {
        try { await req.SaveInfractionAsync(sid, type, details); }
        catch (Exception e) { _logger.LogError(e, "[KZ.AC] failed to persist infraction for {Sid}", sid); }
    }

    // cs2kz anticheat/detectors/cvars.cpp — the 11 checked client cvars + exact thresholds. This runs only
    // with the server's sv_cheats off (gated at the caller), so the cheat-cvar checks are always enforceable.
    private static string? FirstViolation(IGameClient client)
    {
        // Movement-integrity cvars (checked regardless).
        if (Value(client, "m_yaw")          is { } yaw && yaw > 0.3)                    return "m_yaw";        // MAXIMUM_M_YAW
        if (Value(client, "fps_max")        is { } fps && fps > 0.0 && fps < 64.0)      return "fps_max";      // MINIMUM_FPS_MAX
        if (Value(client, "sensitivity")    is { } s   && (s < 0.0001 || s > 20.0))     return "sensitivity";  // capped 0.0001..8 (20 for headroom)
        if (Value(client, "cl_pitchdown")   is { } pd  && Math.Abs(pd - 89.0)  > 0.001) return "cl_pitchdown"; // must be 89
        if (Value(client, "cl_pitchup")     is { } pu  && Math.Abs(pu - 89.0)  > 0.001) return "cl_pitchup";   // must be 89 (was wrongly -89)
        if (Value(client, "cl_yawspeed")    is { } ys  && Math.Abs(ys - 210.0) > 0.001) return "cl_yawspeed";  // must be 210

        // Cheat cvars (client should mirror the server's sv_cheats=0).
        if (Value(client, "sv_cheats")      is { } sc  && sc != 0.0)                     return "sv_cheats";
        if (Value(client, "cl_showpos")     is { } cp  && cp != 0.0)                     return "cl_showpos";
        if (Value(client, "cam_showangles") is { } ca  && ca != 0.0)                     return "cam_showangles";
        if (Value(client, "cl_drawhud")     is { } dh  && dh == 0.0)                     return "cl_drawhud";   // default 1, disabled = cheat
        if (Value(client, "fov_cs_debug")   is { } fd  && fd != 0.0)                     return "fov_cs_debug";

        return null;
    }

    private static double? Value(IGameClient client, string cvar)
        => double.TryParse(client.GetConVarValue(cvar), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
}
