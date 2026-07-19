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
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

internal unsafe partial class MiscModule
{
    private void AddCommands()
    {
        _commandManager.AddClientChatCommand("usp",   OnCommandGiveWeapon);
        _commandManager.AddClientChatCommand("glock", OnCommandGiveWeapon);
        _commandManager.AddClientChatCommand("knife", OnCommandGiveWeapon);
        _commandManager.AddClientChatCommand("spec",  OnCommandSpec);
    }

    private ECommandAction OnCommandGiveWeapon(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.EntityManager.FindPlayerPawnBySlot(slot) is not { } basePawn
            || basePawn.AsPlayer() is not { IsAlive: true } pawn)
        {
            return ECommandAction.Handled;
        }

        var weapon = command.CommandName switch
        {
            "glock" or "usp" => pawn.GetWeaponBySlot(GearSlot.Pistol),
            "knife"          => pawn.GetWeaponBySlot(GearSlot.Knife),
            _                => null,
        };

        if (weapon is not null)
        {
            _bridge.ModSharp.InvokeFrameAction(() =>
            {
                if (pawn is { IsValidEntity: true } && weapon is { IsValidEntity: true })
                {
                    pawn.RemovePlayerItem(weapon);
                }
            });
        }

        if (command.CommandName.Equals("knife", StringComparison.OrdinalIgnoreCase))
        {
            pawn.GiveNamedItem("weapon_knife");
        }
        else
        {
            pawn.GiveNamedItem(command.CommandName.Equals("glock", StringComparison.OrdinalIgnoreCase)
                                   ? EconItemId.Glock
                                   : EconItemId.UspSilencer);
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandSpec(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.EntityManager.FindPlayerPawnBySlot(slot) is not { } basePawn)
        {
            return ECommandAction.Handled;
        }

        if (basePawn.AsPlayer() is { IsAlive: true } player)
        {
            player.ChangeTeam(CStrikeTeam.Spectator);
        }

        if (basePawn.AsObserver() is { } observer && observer.GetObserverService() is { } observerService)
        {
            observerService.ObserverMode = observerService.ObserverLastMode = ObserverMode.InEye;

            if (_replayModule.GetReplayBotByIndex(0) is { } replayBot
                && _bridge.EntityManager.FindPlayerPawnBySlot(replayBot.Slot)?.AsPlayerPawn() is { } botPawn)
            {
                observerService.ObserverTarget = botPawn.Handle;
            }
        }

        return ECommandAction.Handled;
    }
}
