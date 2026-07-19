/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using Cysharp.Text;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.Practice;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Interfaces.Modules;
using Source2Surf.Timer.Shared.Models.Replay;
using Source2Surf.Timer.Shared.Models.Timer;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules;

internal interface IHudModule
{
}

internal class HudModule : IModule, IHudModule, ITimerModuleListener, IZoneModuleListener
{
    private const float HudUpdateInterval = 0.10f;

    private readonly InterfaceBridge _bridge;

    private readonly ITimerModule    _timerModule;
    private readonly IReplayModule   _replayModule;
    private readonly IRecordModule   _recordModule;
    private readonly IZoneModule     _zoneModule;
    private readonly IPracticeModule _practiceModule;

    private readonly float[] _nextHudUpdateTime = new float[PlayerSlot.MaxPlayerCount];

    // Cached WRCP diff from the last completed stage, shown inline after the main timer
    private readonly float?[] _lastStageDelta = new float?[PlayerSlot.MaxPlayerCount];

    // ReSharper disable InconsistentNaming
    private readonly IGameEvent show_survival_respawn_status_event;

    // ReSharper restore InconsistentNaming

    public HudModule(InterfaceBridge bridge,
                     ITimerModule    timerModule,
                     IReplayModule   replayModule,
                     IRecordModule   recordModule,
                     IZoneModule     zoneModule,
                     IPracticeModule practiceModule)
    {
        _bridge         = bridge;
        _timerModule    = timerModule;
        _replayModule   = replayModule;
        _recordModule   = recordModule;
        _zoneModule     = zoneModule;
        _practiceModule = practiceModule;

        show_survival_respawn_status_event = bridge.EventManager.CreateEvent("show_survival_respawn_status", true)
                                             ?? throw new
                                                 NullReferenceException("Failed to create show_survival_respawn_status event, this should never happen?!?!?!");

        show_survival_respawn_status_event.SetInt("duration", 1);
        show_survival_respawn_status_event.SetInt("userid", -1);
    }

    public bool Init()
    {
        _bridge.HookManager.PlayerRunCommand.InstallHookPost(OnPlayerRunCommandPost);

        _bridge.ModSharp.InstallGameFrameHook(null, OnGameFramePost);

        _timerModule.RegisterListener(this);
        _zoneModule.RegisterListener(this);

        return true;
    }

    public void Shutdown()
    {
        show_survival_respawn_status_event.Dispose();
        _timerModule.UnregisterListener(this);
        _zoneModule.UnregisterListener(this);

        _bridge.HookManager.PlayerRunCommand.RemoveHookPost(OnPlayerRunCommandPost);
        _bridge.ModSharp.RemoveGameFrameHook(null, OnGameFramePost);
    }

    public void OnPlayerTimerStart(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        _lastStageDelta[controller.PlayerSlot] = null;
    }

    public void OnZoneStartTouch(IZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
        if (info.ZoneType == EZoneType.Start)
        {
            _lastStageDelta[controller.PlayerSlot] = null;
        }
    }

    public void OnPlayerStageTimerFinish(IPlayerController controller, IPlayerPawn pawn, IStageTimerInfo stageTimerInfo)
    {
        var stage = stageTimerInfo.Stage;

        if (_recordModule.GetWR(stageTimerInfo.Style, stageTimerInfo.Track, stage) is { } stageWr)
        {
            _lastStageDelta[controller.PlayerSlot] = stageTimerInfo.Time - stageWr.Time;
        }
        else
        {
            _lastStageDelta[controller.PlayerSlot] = null;
        }
    }

    private void OnGameFramePost(bool arg1, bool arg2, bool arg3)
    {
        var gameRules = _bridge.GameRules;

        if (!gameRules.IsWarmupPeriod)
        {
            gameRules.IsGameRestart = gameRules.RestartRoundTime < _bridge.GlobalVars.CurTime;
        }
    }

    private void OnPlayerRunCommandPost(IPlayerRunCommandHookParams param, HookReturnValue<EmptyHookReturn> ret)
    {
        var client = param.Client;

        if (client.IsFakeClient)
        {
            return;
        }

        var slot = client.Slot;
        var now  = _bridge.GlobalVars.CurTime;
        var next = _nextHudUpdateTime[slot];

        // Skip while within one interval of the scheduled update; if `next` is further
        // ahead than one interval it is stale (CurTime reset on map change) — fall
        // through and re-anchor.
        if (now < next && next - now <= HudUpdateInterval)
        {
            return;
        }

        _nextHudUpdateTime[slot] = now + HudUpdateInterval;

        var pawn = param.Pawn;

        if (pawn.AsObserver() is { } observer && observer.GetObserverService() is { } observerService)
        {
            if (observerService.ObserverMode is ObserverMode.None or ObserverMode.Roaming)
            {
                return;
            }

            var observerTarget = observerService.ObserverTarget;

            if (!observerTarget.IsValid()
                || _bridge.EntityManager.FindEntityByHandle(observerTarget)?.AsPlayerPawn() is not { } targetPawn
                || targetPawn.GetController() is not { } targetController)
            {
                return;
            }

            if (targetController.IsFakeClient)
            {
                if (_replayModule.GetReplayBotData(targetController.PlayerSlot) is not ReplayBotData replayData)
                {
                    return;
                }

                PrintReplayHud(client, targetPawn, replayData);

                return;
            }

            if (_timerModule.GetTimerInfo(targetController.PlayerSlot) is not { } targetTimerInfo)
            {
                return;
            }

            PrintPlayerHud(client, targetController.PlayerSlot, targetPawn, targetTimerInfo);

            return;
        }

        if (_timerModule.GetTimerInfo(slot) is not { } timerInfo)
        {
            return;
        }

        PrintPlayerHud(client, slot, pawn, timerInfo);
    }

    private void PrintPlayerHud(IGameClient client, PlayerSlot slot, IBasePlayerPawn pawn, ITimerInfo timerInfo)
    {
        var velocity = pawn.GetAbsVelocity();

        var sb = ZString.CreateStringBuilder(true);

        try
        {
            // Two complementary diffs:
            //   posDelta: live position projection vs WR replay (continuous, updates every HUD tick)
            //   cpDelta:  checkpoint-anchored (precise at gates), or last stage WRCP if no CPs yet
            var posDelta = TryComputePositionDelta(pawn, timerInfo);
            var cpDelta  = TryComputeCheckpointDelta(timerInfo) ?? _lastStageDelta[slot];

            // Timer color based on status: running=green, paused=yellow, stopped=white
            var timeColor = timerInfo.Status switch
            {
                ETimerStatus.Running => "#00FF00",
                ETimerStatus.Paused  => "#FFD700",
                _                    => "#FFFFFF",
            };

            sb.AppendFormat("<span color='{0}'>", timeColor);
            Utils.FormatTime(ref sb, timerInfo.Time);

            if (timerInfo.Status == ETimerStatus.Paused)
            {
                sb.Append(" ‖ PAUSED");
            }

            sb.Append("</span>");

            // Practice marker — visible whenever the player has saved/teleported.
            if (_practiceModule.IsInPractice(slot))
            {
                sb.Append(" <span color='#FF8800'>[PRACTICE]</span>");
            }

            // WR diff inline after time. The whole parens block is gated on cpDelta —
            // matches the pre-time-diff behavior where nothing was shown if there was no
            // checkpoint/stage delta available. The live position diff is layered IN FRONT
            // of WRCP when available, so the result reads "(±posDelta | WRCP: ±cpDelta)".
            if (cpDelta is { } cd)
            {
                sb.Append(" <span color='#888888'>(</span>");

                if (posDelta is { } pd)
                {
                    AppendColoredDelta(ref sb, pd);
                    sb.Append("<span color='#888888'> | </span>");
                }

                sb.Append("<span color='#888888'>WRCP: </span>");
                AppendColoredDelta(ref sb, cd);
                sb.Append("<span color='#888888'>)</span>");
            }

            sb.Append("<br>");

            sb.AppendFormat("<span color='#FFFFFF'>{0}&nbsp;&nbsp;&nbsp;&nbsp;",
                            (int) velocity.Length2D());

            sb.Append("Sync: ");
            AppendFixedPoint1(ref sb, timerInfo.Sync * 100);
            sb.Append("%</span>");

            sb.Append("<br>");

            sb.Append("<span color='#808080'>PB: ");

            if (_recordModule.GetPlayerRecord(slot, timerInfo.Style, timerInfo.Track) is { } pb)
            {
                Utils.FormatTime(ref sb, pb.Time, true);
            }
            else
            {
                sb.Append("N/A");
            }

            sb.Append(" ‖ WR: ");

            if (_recordModule.GetWRTime(timerInfo.Style, timerInfo.Track) is { } wr)
            {
                Utils.FormatTime(ref sb, wr, true);
            }
            else
            {
                sb.Append("N/A");
            }

            sb.Append("</span>");

            PrintHtmlToPlayer(client, sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }
    }

    // Hide live diff if the closest WR frame is farther than this — beyond ~one ramp width the
    // projection is meaningless (player on a wholly different path).
    private const float MaxPositionDiffDistSq = 256f * 256f;

    // Suppress nonsense large deltas (wrong replay associated, teleport mid-run, etc).
    private const float MaxAbsPositionDelta = 600f;

    private float? TryComputePositionDelta(IBasePlayerPawn pawn, ITimerInfo timerInfo)
    {
        // Only meaningful while the clock is actually counting and after the start zone.
        if (timerInfo.Status != ETimerStatus.Running)
        {
            return null;
        }

        if (timerInfo.Time <= 0f)
        {
            return null;
        }

        var pos = pawn.GetAbsOrigin();
        var idx = _replayModule.FindClosestFrameIndex(timerInfo.Style, timerInfo.Track, stage: 0, pos, out var distSq);

        if (idx < 0 || distSq > MaxPositionDiffDistSq)
        {
            return null;
        }

        var replay = _replayModule.GetCachedReplay(timerInfo.Style, timerInfo.Track, stage: 0);
        if (replay is null)
        {
            return null;
        }

        // Closest frame falls inside the pre-run prefix — player is still in / near the start zone,
        // no meaningful comparison yet.
        var wrTimeFrames = idx - replay.Header.PreFrame;
        if (wrTimeFrames <= 0)
        {
            return null;
        }

        var wrTime = wrTimeFrames * TimerConstants.TickInterval;
        var delta  = timerInfo.Time - wrTime;

        if (delta < -MaxAbsPositionDelta || delta > MaxAbsPositionDelta)
        {
            return null;
        }

        return delta;
    }

    private float? TryComputeCheckpointDelta(ITimerInfo timerInfo)
    {
        var wrCheckpoints = _recordModule.GetWRCheckpoints(timerInfo.Style, timerInfo.Track);
        var cpIndex       = timerInfo.Checkpoint;

        if (wrCheckpoints is not { Count: > 0 } || cpIndex < 1 || cpIndex > wrCheckpoints.Count)
        {
            return null;
        }

        var wrCpTime = wrCheckpoints[cpIndex - 1].Time;

        var playerCpTime = timerInfo.Checkpoints.Count >= cpIndex
            ? timerInfo.Checkpoints[cpIndex - 1].Time
            : timerInfo.Time;

        return playerCpTime - wrCpTime;
    }

    private void PrintReplayHud(IGameClient client, IPlayerPawn pawn, ReplayBotData bot)
    {
        if (bot.Status == EReplayBotStatus.Idle)
        {
            var idleSb = ZString.CreateStringBuilder(true);
            try
            {
                idleSb.Append("<span class='fontSize-xl' color='");
                AppendRainbowHex(ref idleSb, _bridge.GlobalVars.CurTime);
                idleSb.Append("'>IDLE</span>");
                PrintHtmlToPlayer(client, idleSb.ToString());
            }
            finally
            {
                idleSb.Dispose();
            }

            return;
        }

        var          sb              = ZString.CreateStringBuilder(true);
        const string colorLabel      = "#AAAAAA";
        const string colorData       = "#E0E0E0";
        const string colorStageBot   = "#2196F3";
        const string colorFullRunBot = "#FFD700";
        const string colorSubtleInfo = "#B0B0B0";

        try
        {
            if (bot.Stage > 0)
            {
                sb.AppendFormat("<span color='{0}'>Stage {1} Replay Bot</span>", colorStageBot, bot.Stage);
            }
            else
            {
                sb.AppendFormat("<span color='{0}'>Replay Bot</span>", colorFullRunBot);

                var currentStage = bot.GetCurrentStage();

                if (currentStage > 0)
                {
                    sb.AppendFormat(" <span color='{0}'>(Stage {1})</span>", colorSubtleInfo, currentStage);
                }
            }

            sb.Append("<br>");

            var header = bot.Header!;

            sb.AppendFormat("<span color='{0}'>Player:</span> <span color='{1}'>{2}</span>",
                            colorLabel,
                            colorData,
                            header.PlayerName);

            sb.Append("<br>");

            sb.AppendFormat("<span color='{0}'>Time: </span>", colorLabel);
            var timedFrame = Math.Clamp(bot.CurrentFrame, header.PreFrame, header.PostFrame);

            sb.AppendFormat("<span color='{0}'>", colorData);
            Utils.FormatTime(ref sb, TimerConstants.TickInterval * (timedFrame - header.PreFrame));
            sb.Append('/');
            Utils.FormatTime(ref sb, bot.Time);
            sb.Append("</span>");

            sb.Append("<br>");

            sb.AppendFormat("<span color='{0}'>Speed:</span> <span color='{1}'>{2}</span>",
                            colorLabel,
                            colorData,
                            (int) MathF.Round(pawn.GetAbsVelocity().Length2D()));

            PrintHtmlToPlayer(client, sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }
    }

    private static void AppendRainbowHex(ref Utf16ValueStringBuilder sb, float curtime)
    {
        const float frequency = 3.5f;
        const float amplitude = 127f;
        const float center    = 128f;

        const float sin120 = 0.86602540f;
        const float cos120 = -0.5f;

        var (sin, cos) = MathF.SinCos(frequency * curtime);

        var rBase = sin;
        var gBase = (sin * cos120) + (cos * sin120);
        var bBase = (sin * cos120) - (cos * sin120);
        var r     = (int) ((rBase * amplitude) + center);
        var g     = (int) ((gBase * amplitude) + center);
        var b     = (int) ((bBase * amplitude) + center);

        sb.Append('#');
        AppendHex2(ref sb, r);
        AppendHex2(ref sb, g);
        AppendHex2(ref sb, b);
    }

    private static void AppendHex2(ref Utf16ValueStringBuilder sb, int value)
    {
        var h1 = (value >> 4) & 0xF;
        var h2 = value        & 0xF;

        sb.Append((char) (h1 < 10 ? h1 + '0' : h1 + ('A' - 10)));
        sb.Append((char) (h2 < 10 ? h2 + '0' : h2 + ('A' - 10)));
    }

    // Compact signed delta: "+1.3", "-0.4", "+1:23.4" etc. One decimal of seconds.
    // Mirrors what surf/bhop players are used to seeing inline next to their timer.
    private static void AppendDelta(ref Utf16ValueStringBuilder sb, float delta)
    {
        sb.Append(delta >= 0f ? '+' : '-');

        var abs = MathF.Abs(delta);

        if (abs < 60f)
        {
            var seconds = (int) abs;
            var deci    = (int) ((abs - seconds) * 10f);
            if (deci > 9) deci = 9;

            sb.Append(seconds);
            sb.Append('.');
            sb.Append((char) ('0' + deci));
        }
        else
        {
            // Re-use existing MM:SS.D formatter for long deltas (rare; usually sub-minute).
            Utils.FormatTime(ref sb, abs);
        }
    }

    private static void AppendColoredDelta(ref Utf16ValueStringBuilder sb, float delta)
    {
        // Positive delta = behind WR (red). Negative delta = ahead of WR (green).
        var color = delta >= 0f ? "#FF4444" : "#44FF44";
        sb.AppendFormat("<span color='{0}'>", color);
        AppendDelta(ref sb, delta);
        sb.Append("</span>");
    }

    private static void AppendFixedPoint1(ref Utf16ValueStringBuilder sb, float value)
    {
        var intPart      = (int) value;
        var decimalDigit = (int) ((value - intPart) * 10);

        sb.Append(intPart);
        sb.Append('.');
        sb.Append((char) ('0' + Math.Abs(decimalDigit)));
    }

    private void PrintHtmlToPlayer(IGameClient client, string html)
    {
        show_survival_respawn_status_event.SetString("loc_token", html);

        show_survival_respawn_status_event.FireToClient(client);
    }
}
