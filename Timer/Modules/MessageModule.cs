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
using Sharp.Shared.Definition;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Shared.Events;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Timer;

namespace Source2Surf.Timer.Modules;

internal interface IMessageModule
{
}

internal class MessageModule : IModule, IMessageModule, IRecordModuleListener, ITimerModuleListener
{
    private readonly InterfaceBridge _bridge;
    private readonly IRecordModule   _recordModule;
    private readonly ITimerModule    _timerModule;

    public MessageModule(InterfaceBridge bridge,
                         IRecordModule   recordModule,
                         ITimerModule    timerModule)
    {
        _bridge       = bridge;
        _recordModule = recordModule;
        _timerModule  = timerModule;
    }

    public bool Init()
    {
        _recordModule.RegisterListener(this);
        _timerModule.RegisterListener(this);

        return true;
    }

    public void Shutdown()
    {
        _recordModule.UnregisterListener(this);
        _timerModule.UnregisterListener(this);
    }

    public void OnRecordSaved(PlayerRecordSavedEvent recordEvent)
    {
        switch (recordEvent.RecordType)
        {
            case EAttemptResult.NewPersonalRecord:
            {
                PrintNewPersonalBestMessage(recordEvent.PlayerName,
                                            recordEvent.SavedRecord,
                                            recordEvent.PbRecord,
                                            recordEvent.IsStageRecord);

                break;
            }
            case EAttemptResult.NewServerRecord:
            {
                PrintNewServerRecordMessage(recordEvent.PlayerName,
                                            recordEvent.SavedRecord,
                                            recordEvent.WrRecord,
                                            recordEvent.IsStageRecord);

                break;
            }
            case EAttemptResult.NoNewRecord:
            {
                PrintNoNewRecordMessage(recordEvent.SteamId,
                                        recordEvent.SavedRecord,
                                        recordEvent.PbRecord,
                                        recordEvent.IsStageRecord);

                break;
            }
            default:
                throw new NotImplementedException($"Type {recordEvent.RecordType} is not implemented");
        }
    }

    public void OnReachCheckpoint(IPlayerController controller,
                                  IPlayerPawn       pawn,
                                  ITimerInfo        timerInfo,
                                  int               checkpoint)
    {
        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("CP");
            sb.Append(checkpoint);
            sb.Append(": ");
            Utils.AppendColoredTime(ref sb, timerInfo.Time);

            // WR checkpoint diff
            var wrCheckpoints = _recordModule.GetWRCheckpoints(timerInfo.Style, timerInfo.Track);

            if (wrCheckpoints is { Count: > 0 } && checkpoint >= 1 && checkpoint <= wrCheckpoints.Count)
            {
                sb.Append(" | WR ");
                Utils.AppendSignedDelta(ref sb, timerInfo.Time - wrCheckpoints[checkpoint - 1].Time);
            }

            // PB checkpoint comparison would require caching PB checkpoints separately.

            pawn.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }
    }

    private void PrintNewPersonalBestMessage(string    playerName,
                                             RunRecord savedRecord,
                                             RunRecord? pbRecord,
                                             bool      isStageRecord)
    {
        var rank = TryGetCurrentRank(savedRecord);

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append(ChatColor.LightGreen);
            sb.Append(playerName);
            sb.Append(ChatColor.White);
            sb.Append(" PB ");
            AppendRecordScope(ref sb, savedRecord);
            sb.Append(": ");
            sb.Append(ChatColor.LightGreen);
            Utils.FormatTime(ref sb, savedRecord.Time, true);
            sb.Append(ChatColor.White);

            if (pbRecord is not null)
            {
                var improvedBy = MathF.Max(pbRecord.Time - savedRecord.Time, 0f);
                sb.Append(" (-");
                Utils.FormatTime(ref sb, improvedBy, true);
                sb.Append(')');
            }

            if (!isStageRecord)
            {
                AppendRankSuffix(ref sb, rank);
            }

            _bridge.ModSharp.PrintToChatWithPrefix(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }
    }

    private void PrintNewServerRecordMessage(string    playerName,
                                             RunRecord savedRecord,
                                             RunRecord? wrRecord,
                                             bool      isStageRecord)
    {
        _ = isStageRecord;

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append(ChatColor.LightGreen);
            sb.Append(playerName);
            sb.Append(ChatColor.White);
            sb.Append(" WR ");
            AppendRecordScope(ref sb, savedRecord);
            sb.Append(": ");
            sb.Append(ChatColor.LightGreen);
            Utils.FormatTime(ref sb, savedRecord.Time, true);
            sb.Append(ChatColor.White);

            if (wrRecord is not null)
            {
                var improvedBy = MathF.Max(wrRecord.Time - savedRecord.Time, 0f);
                sb.Append(" (-");
                Utils.FormatTime(ref sb, improvedBy, true);
                sb.Append(')');
            }

            _bridge.ModSharp.PrintToChatWithPrefix(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }
    }

    private void PrintNoNewRecordMessage(SteamID   steamId,
                                         RunRecord savedRecord,
                                         RunRecord? pbRecord,
                                         bool      isStageRecord)
    {
        // Find the client by SteamID through ClientManager
        var controller = FindPlayerControllerBySteamId(steamId);

        if (controller is not { IsValidEntity: true })
        {
            return;
        }

        _ = isStageRecord;

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            AppendRecordScope(ref sb, savedRecord);
            sb.Append(": ");
            sb.Append(ChatColor.LightGreen);
            Utils.FormatTime(ref sb, savedRecord.Time, true);
            sb.Append(ChatColor.White);

            if (pbRecord is not null)
            {
                var delta = savedRecord.Time - pbRecord.Time;

                sb.Append(" | PB ");
                sb.Append(ChatColor.LightGreen);
                Utils.FormatTime(ref sb, pbRecord.Time, true);
                sb.Append(ChatColor.White);
                sb.Append(" (");
                sb.Append(delta >= 0f ? '+' : '-');
                Utils.FormatTime(ref sb, MathF.Abs(delta), true);
                sb.Append(')');
            }

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }
    }

    private int TryGetCurrentRank(RunRecord record)
    {
        if (record.Stage > 0)
        {
            return 0;
        }

        try
        {
            return _recordModule.GetRankForTime(record.Style, record.Track, record.Time);
        }
        catch (Exception)
        {
            // GetRankForTime may throw on out-of-bounds style/track
            return 0;
        }
    }

    private static void AppendRecordScope(ref Utf16ValueStringBuilder sb, RunRecord record)
    {
        AppendTrackName(ref sb, record.Track);

        if (record.Stage > 0)
        {
            sb.Append(" S");
            sb.Append(record.Stage);
        }
    }

    private static void AppendTrackName(ref Utf16ValueStringBuilder sb, int track)
    {
        if (track <= 0)
        {
            sb.Append("Main");
        }
        else
        {
            sb.Append("Bonus ");
            sb.Append(track);
        }
    }

    private static void AppendRankSuffix(ref Utf16ValueStringBuilder sb, int rank)
    {
        if (rank > 0)
        {
            sb.Append(" (#");
            sb.Append(rank);
            sb.Append(')');
        }
    }

    private IPlayerController? FindPlayerControllerBySteamId(SteamID steamId)
        => _bridge.ClientManager.GetGameClient(steamId)?.GetPlayerController();
}
