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
using System.Threading.Tasks;
using Cysharp.Text;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Utilities;

// ReSharper disable once CheckNamespace
namespace Source2Surf.Timer.Modules;

internal partial class RecordModule
{
    private (int style, int track) GetStyleTrack(PlayerSlot slot)
    {
        var timerInfo = _timerModule.GetTimerInfo(slot);

        return (timerInfo?.Style ?? 0, timerInfo?.Track ?? 0);
    }

    private ECommandAction OnCommandStageWR(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var (style, track) = GetStyleTrack(slot);

        var stage = command.TryGet<byte>(1) is { } s ? (int)s : 0;

        if (stage < 1)
        {
            controller.PrintToChat("Usage: !swr <stage>");
            return ECommandAction.Handled;
        }

        var wr = _mapCache.GetWR(style, track, stage);

        if (wr is null)
        {
            controller.PrintToChat($"No WR found for stage {stage}.");
            return ECommandAction.Handled;
        }

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("Stage ");
            sb.Append(stage);
            sb.Append(" WR: ");
            Utils.AppendColoredTime(ref sb, wr.Time);
            sb.Append(" by ");
            sb.Append(ChatColor.LightGreen);
            sb.Append(wr.PlayerName);
            sb.Append(ChatColor.White);

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandBonusTop(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var (style, _) = GetStyleTrack(slot);

        var bonus = command.TryGet<byte>(1) is { } b ? (int)b : 1;

        if (bonus < 1)
        {
            controller.PrintToChat("Usage: !btop [bonus]");
            return ECommandAction.Handled;
        }

        var records = _mapCache.GetRecords(style, bonus);

        if (records.Count == 0)
        {
            controller.PrintToChat($"No records found for bonus {bonus}.");
            return ECommandAction.Handled;
        }

        controller.PrintToChat($"Top records for Bonus {bonus}:");

        var count = Math.Min(records.Count, 10);

        for (var i = 0; i < count; i++)
        {
            var rec = records[i];
            var sb  = ZString.CreateStringBuilder(true);
            try
            {
                sb.Append('#');
                sb.Append(i + 1);
                sb.Append(": ");
                Utils.AppendColoredTime(ref sb, rec.Time);
                sb.Append(" - ");
                sb.Append(ChatColor.LightGreen);
                sb.Append(rec.PlayerName);
                sb.Append(ChatColor.White);

                controller.PrintToChat(sb.ToString());
            }
            finally
            {
                sb.Dispose();
            }
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandBonusWR(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var (style, _) = GetStyleTrack(slot);

        var bonus = command.TryGet<byte>(1) is { } b ? (int)b : 1;

        if (bonus < 1)
        {
            controller.PrintToChat("Usage: !bwr [bonus]");
            return ECommandAction.Handled;
        }

        var wr = GetWR(style, bonus);

        if (wr is null)
        {
            controller.PrintToChat($"No WR found for bonus {bonus}.");
            return ECommandAction.Handled;
        }

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("Bonus ");
            sb.Append(bonus);
            sb.Append(" WR: ");
            Utils.AppendColoredTime(ref sb, wr.Time);
            sb.Append(" by ");
            sb.Append(ChatColor.LightGreen);
            sb.Append(wr.PlayerName);
            sb.Append(ChatColor.White);

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandBonusPB(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var (style, _) = GetStyleTrack(slot);

        var bonus = command.TryGet<byte>(1) is { } b ? (int)b : 1;

        if (bonus < 1 || bonus >= TimerConstants.MAX_TRACK)
        {
            controller.PrintToChat("Usage: !bpb [bonus]");
            return ECommandAction.Handled;
        }

        var pb = GetPlayerRecord(slot, style, bonus);

        if (pb is null)
        {
            controller.PrintToChat($"No PB found for bonus {bonus}.");
            return ECommandAction.Handled;
        }

        var records = _mapCache.GetRecords(style, bonus);
        var rank    = _mapCache.GetRankForTime(style, bonus, pb.Time);

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("Bonus ");
            sb.Append(bonus);
            sb.Append(" PB: ");
            Utils.AppendColoredTime(ref sb, pb.Time);
            sb.Append(" (#");
            sb.Append(rank);
            sb.Append('/');
            sb.Append(records.Count);
            sb.Append(')');

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandStagePB(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var (style, track) = GetStyleTrack(slot);

        var stage = command.TryGet<byte>(1) is { } s ? (int)s : 0;

        if (stage < 1)
        {
            controller.PrintToChat("Usage: !spb <stage>");
            return ECommandAction.Handled;
        }

        var pb = GetPlayerRecord(slot, style, track, stage);

        if (pb is null)
        {
            controller.PrintToChat($"No PB found for stage {stage}.");
            return ECommandAction.Handled;
        }

        var wr = _mapCache.GetWR(style, track, stage);

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("Stage ");
            sb.Append(stage);
            sb.Append(" PB: ");
            Utils.AppendColoredTime(ref sb, pb.Time);

            if (wr is not null)
            {
                sb.Append(" (WR ");
                Utils.AppendSignedDelta(ref sb, pb.Time - wr.Time);
                sb.Append(')');
            }

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandClearRecords(StringCommand stringCommand)
    {
        _request.RemoveMapRecords(_bridge.CurrentMapName);

        _mapCache.Clear();
        _playerCache.ClearAll();

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandWR(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var (style, track) = GetStyleTrack(slot);

        var wr = GetWR(style, track);

        if (wr is null)
        {
            controller.PrintToChat($"No WR found for this track.");
            return ECommandAction.Handled;
        }

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("WR: ");
            Utils.AppendColoredTime(ref sb, wr.Time);

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandPB(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var (style, track) = GetStyleTrack(slot);

        var pb = GetPlayerRecord(slot, style, track);

        if (pb is null)
        {
            controller.PrintToChat($"No personal best found for this track.");
            return ECommandAction.Handled;
        }

        var rank  = GetRankForTime(style, track, pb.Time);
        var total = GetTotalRecordCount(style, track);

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("PB: ");
            Utils.AppendColoredTime(ref sb, pb.Time);
            sb.Append(" (#");
            sb.Append(rank);
            sb.Append('/');
            sb.Append(total);
            sb.Append(')');

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandRank(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var (style, track) = GetStyleTrack(slot);

        var pb = GetPlayerRecord(slot, style, track);

        if (pb is null)
        {
            controller.PrintToChat($"No record found. Complete the map first.");
            return ECommandAction.Handled;
        }

        var rank  = GetRankForTime(style, track, pb.Time);
        var total = GetTotalRecordCount(style, track);

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("Rank: ");
            sb.Append(ChatColor.LightGreen);
            sb.Append('#');
            sb.Append(rank);
            sb.Append(ChatColor.White);
            sb.Append('/');
            sb.Append(total);
            sb.Append(" | PB: ");
            Utils.AppendColoredTime(ref sb, pb.Time);

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandTop(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var (style, track) = GetStyleTrack(slot);

        var wr = GetWR(style, track);

        if (wr is null)
        {
            controller.PrintToChat($"No records found for this track.");
            return ECommandAction.Handled;
        }

        var total = GetTotalRecordCount(style, track);

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("#1: ");
            Utils.AppendColoredTime(ref sb, wr.Time);
            sb.Append(" (");
            sb.Append(total);
            sb.Append(" records)");

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandCpr(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var (style, track) = GetStyleTrack(slot);

        var pb = GetPlayerRecord(slot, style, track);

        if (pb is null)
        {
            controller.PrintToChat("No personal best found for this track.");
            return ECommandAction.Handled;
        }

        var wrCheckpoints = _mapCache.GetWRCheckpoints(style, track);

        if (wrCheckpoints is not { Count: > 0 })
        {
            controller.PrintToChat("No WR checkpoints available.");
            return ECommandAction.Handled;
        }

        AsyncChatCommand.Run(_bridge, _logger, slot, "GetRecordCheckpoints",
                             () => _request.GetRecordCheckpoints(pb.Id),
                             (ctrl, pbCheckpoints) =>
                             {
                                 if (pbCheckpoints.Count == 0)
                                 {
                                     ctrl.PrintToChat("No checkpoint data for your PB.");

                                     return;
                                 }

                                 var count = Math.Min(pbCheckpoints.Count, wrCheckpoints.Count);

                                 ctrl.PrintToChat("PB vs WR checkpoints:");

                                 for (var i = 0; i < count; i++)
                                 {
                                     var pbCp = pbCheckpoints[i];
                                     var wrCp = wrCheckpoints[i];

                                     var sb = ZString.CreateStringBuilder(true);
                                     try
                                     {
                                         sb.Append("CP");
                                         sb.Append(i + 1);
                                         sb.Append(": ");
                                         Utils.AppendColoredTime(ref sb, pbCp.Time);
                                         sb.Append(" | WR ");
                                         Utils.AppendSignedDelta(ref sb, pbCp.Time - wrCp.Time);

                                         ctrl.PrintToChat(sb.ToString());
                                     }
                                     finally
                                     {
                                         sb.Dispose();
                                     }
                                 }

                                 // Final time diff
                                 if (GetWR(style, track) is not { } wr)
                                 {
                                     return;
                                 }

                                 var sb2 = ZString.CreateStringBuilder(true);
                                 try
                                 {
                                     sb2.Append("Final: ");
                                     Utils.AppendColoredTime(ref sb2, pb.Time);
                                     sb2.Append(" | WR ");
                                     Utils.AppendSignedDelta(ref sb2, pb.Time - wr.Time);

                                     ctrl.PrintToChat(sb2.ToString());
                                 }
                                 finally
                                 {
                                     sb2.Dispose();
                                 }
                             });

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandProfile(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetClientController(slot, out var client, out var controller))
        {
            return ECommandAction.Handled;
        }

        var profile = _playerManager.GetPlayerProfile(slot);

        if (profile is null)
        {
            controller.PrintToChat("Profile not loaded yet.");
            return ECommandAction.Handled;
        }

        var (style, track) = GetStyleTrack(slot);
        var steamId        = client.SteamId;

        // PB + rank (sync, from cache)
        var pb    = GetPlayerRecord(slot, style, track);
        var pbStr = "";

        {
            var sb = ZString.CreateStringBuilder(true);
            try
            {
                sb.Append("Map PB: ");

                if (pb is not null)
                {
                    var rank  = GetRankForTime(style, track, pb.Time);
                    var total = GetTotalRecordCount(style, track);

                    Utils.AppendColoredTime(ref sb, pb.Time);
                    sb.Append(" (#");
                    sb.Append(rank);
                    sb.Append('/');
                    sb.Append(total);
                    sb.Append(')');
                }
                else
                {
                    sb.Append(ChatColor.Grey);
                    sb.Append("None");
                    sb.Append(ChatColor.White);
                }

                pbStr = sb.ToString();
            }
            finally
            {
                sb.Dispose();
            }
        }

        AsyncChatCommand.Run(_bridge, _logger, slot, "GetPlayerPointsRank",
                             () => _request.GetPlayerPointsRank(steamId),
                             (ctrl, pointsRankResult) =>
                             {
                                 var (pointsRank, totalRanked) = pointsRankResult;

                                 // Line 1: Player name + Points + Rank
                                 var sb1 = ZString.CreateStringBuilder(true);
                                 try
                                 {
                                     sb1.Append("Player: ");
                                     sb1.Append(ChatColor.LightGreen);
                                     sb1.Append(profile.Name);
                                     sb1.Append(ChatColor.White);
                                     sb1.Append(" | Points: ");
                                     sb1.Append(ChatColor.LightGreen);
                                     sb1.Append(profile.Points);
                                     sb1.Append(ChatColor.White);

                                     if (pointsRank > 0)
                                     {
                                         sb1.Append(" (#");
                                         sb1.Append(pointsRank);
                                         sb1.Append('/');
                                         sb1.Append(totalRanked);
                                         sb1.Append(')');
                                     }

                                     ctrl.PrintToChat(sb1.ToString());
                                 }
                                 finally
                                 {
                                     sb1.Dispose();
                                 }

                                 // Line 2: Map PB + Rank
                                 ctrl.PrintToChat(pbStr);

                                 // Line 3: Join date + Last seen
                                 var sb3 = ZString.CreateStringBuilder(true);
                                 try
                                 {
                                     sb3.Append("Joined: ");
                                     sb3.Append(ChatColor.Grey);
                                     sb3.Append(profile.JoinDate.ToString("yyyy-MM-dd"));
                                     sb3.Append(ChatColor.White);
                                     sb3.Append(" | Last seen: ");
                                     sb3.Append(ChatColor.Grey);
                                     sb3.Append(profile.LastSeenDate.ToString("yyyy-MM-dd"));
                                     sb3.Append(ChatColor.White);

                                     ctrl.PrintToChat(sb3.ToString());
                                 }
                                 finally
                                 {
                                     sb3.Dispose();
                                 }
                             });

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandRecent(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetClientController(slot, out var client, out _))
        {
            return ECommandAction.Handled;
        }

        var mapName = _bridge.CurrentMapName;
        var steamId = client.SteamId;

        AsyncChatCommand.Run(_bridge, _logger, slot, "GetRecentRecords",
                             () => _request.GetRecentRecords(mapName, steamId),
                             (ctrl, records) =>
                             {
                                 if (records.Count == 0)
                                 {
                                     ctrl.PrintToChat("No recent records.");

                                     return;
                                 }

                                 ctrl.PrintToChat("Recent records:");

                                 foreach (var record in records)
                                 {
                                     var sb = ZString.CreateStringBuilder(true);
                                     try
                                     {
                                         Utils.AppendColoredTime(ref sb, record.Time);

                                         if (record.Track > 0)
                                         {
                                             sb.Append(" B");
                                             sb.Append(record.Track);
                                         }

                                         sb.Append(" | ");
                                         sb.Append(ChatColor.Grey);
                                         sb.Append(record.RunDate.ToString("MM-dd HH:mm"));
                                         sb.Append(ChatColor.White);

                                         ctrl.PrintToChat(sb.ToString());
                                     }
                                     finally
                                     {
                                         sb.Dispose();
                                     }
                                 }
                             });

        return ECommandAction.Handled;
    }
}
