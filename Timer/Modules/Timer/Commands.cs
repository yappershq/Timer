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

using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules;

internal partial class TimerModule
{
    private void InitCommands()
    {
        _commandManager.AddClientChatCommand("r", OnCommandRestart);
        _commandManager.AddClientChatCommand("pause", OnCommandPause);
        _commandManager.AddClientChatCommand("resume", OnCommandResume);
        _commandManager.AddClientChatCommand("end", OnCommandEnd);
        _commandManager.AddClientChatCommand("stop", OnCommandStop);
        _commandManager.AddClientChatCommand("nc", OnCommandNoclip);
        _commandManager.AddClientChatCommand("noclip", OnCommandNoclip);

        _commandManager.AddClientChatCommand("stage", OnCommandStage);
        _commandManager.AddClientChatCommand("s", OnCommandStage);

        _commandManager.AddClientChatCommand("main",
                                             (slot, _) =>
                                             {
                                                 Restart(slot, 0);

                                                 return ECommandAction.Handled;
                                             });

        _commandManager.AddClientChatCommand("b",
                                             (slot, _) =>
                                             {
                                                 Restart(slot, 1);

                                                 return ECommandAction.Handled;
                                             });

        for (var i = 1; i < 24; i++)
        {
            var i1 = i;

            _commandManager.AddClientChatCommand($"b{i}",
                                                 (slot, _) =>
                                                 {
                                                     Restart(slot, i1);

                                                     return ECommandAction.Handled;
                                                 });
        }
    }

    private ECommandAction OnCommandRestart(PlayerSlot slot, StringCommand command)
    {
        Restart(slot, 0);

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandPause(PlayerSlot slot, StringCommand command)
    {
        if (_timerInfo[slot] is { } timerInfo && timerInfo.IsTimerPaused())
        {
            // Toggle: if already paused, resume
            ResumeTimer(slot);
        }
        else
        {
            PauseTimer(slot);
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandResume(PlayerSlot slot, StringCommand command)
    {
        ResumeTimer(slot);

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandEnd(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller
            || controller.GetPlayerPawn() is not { IsValidEntity: true, IsAlive: true } pawn)
        {
            return ECommandAction.Handled;
        }

        var track = _timerInfo[slot]?.Track ?? 0;

        _zoneModule.TeleportToZone(pawn, track, EZoneType.End);

        _bridge.ModSharp.InvokeFrameAction(() => { StopTimer(slot); });

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandStop(PlayerSlot slot, StringCommand command)
    {
        StopTimer(slot);

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandStage(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller
            || controller.GetPlayerPawn() is not { IsValidEntity: true, IsAlive: true } pawn)
        {
            return ECommandAction.Handled;
        }

        if (command.ArgCount < 1 || !int.TryParse(command.GetArg(1), out var stage) || stage < 1)
        {
            return ECommandAction.Handled;
        }

        var track = _timerInfo[slot]?.Track ?? 0;

        var totalStages = _zoneModule.GetTotalStages(track);

        if (totalStages <= 0 || stage > totalStages)
        {
            return ECommandAction.Handled;
        }

        // Stage 1 = start zone
        if (stage == 1)
        {
            _zoneModule.TeleportToZone(pawn, track, EZoneType.Start);
        }
        else
        {
            _zoneModule.TeleportToStage(pawn, track, stage);
        }

        _bridge.ModSharp.InvokeFrameAction(() => { StopTimer(slot); });

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandNoclip(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller
            || controller.GetPlayerPawn() is not { IsValidEntity: true, IsAlive: true } pawn)
        {
            return ECommandAction.Handled;
        }

        if (pawn.ActualMoveType == MoveType.NoClip)
        {
            pawn.SetMoveType(MoveType.Walk);
        }
        else
        {
            StopTimer(slot);
            pawn.SetMoveType(MoveType.NoClip);
        }

        return ECommandAction.Handled;
    }
}