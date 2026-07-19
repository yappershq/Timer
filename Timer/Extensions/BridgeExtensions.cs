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

using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace Source2Surf.Timer.Extensions;

internal static class BridgeExtensions
{
    /// <summary>
    ///     Resolves the live player controller for a slot — the standard validity preamble
    ///     for chat-command handlers and for main-thread callbacks after an await (the
    ///     player may have disconnected in between).
    /// </summary>
    public static bool TryGetController(this InterfaceBridge bridge, PlayerSlot slot, out IPlayerController controller)
    {
        if (bridge.ClientManager.GetGameClient(slot) is { } client
            && client.GetPlayerController() is { IsValidEntity: true } valid)
        {
            controller = valid;

            return true;
        }

        controller = null!;

        return false;
    }

    /// <inheritdoc cref="TryGetController" />
    public static bool TryGetClientController(this InterfaceBridge   bridge,
                                              PlayerSlot             slot,
                                              out IGameClient        client,
                                              out IPlayerController  controller)
    {
        if (bridge.ClientManager.GetGameClient(slot) is { } c
            && c.GetPlayerController() is { IsValidEntity: true } valid)
        {
            client     = c;
            controller = valid;

            return true;
        }

        client     = null!;
        controller = null!;

        return false;
    }
}
