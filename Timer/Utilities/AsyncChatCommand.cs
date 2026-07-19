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
using Microsoft.Extensions.Logging;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;

namespace Source2Surf.Timer.Utilities;

internal static class AsyncChatCommand
{
    /// <summary>
    ///     The standard pattern for chat commands needing a DB round-trip: run the fetch
    ///     off-thread with transient-error retry, then marshal back to the main thread and
    ///     re-resolve the player by slot (they may have disconnected during the await)
    ///     before printing. Fire-and-forget; failures are logged under
    ///     <paramref name="operationName" />.
    /// </summary>
    public static void Run<TResult>(InterfaceBridge                    bridge,
                                    ILogger                            logger,
                                    PlayerSlot                         slot,
                                    string                             operationName,
                                    Func<Task<TResult>>                fetch,
                                    Action<IPlayerController, TResult> print)
    {
        Task.Run(async () =>
                 {
                     try
                     {
                         var result = await RetryHelper.RetryAsync(fetch, RetryHelper.IsTransient, logger, operationName)
                                                       .ConfigureAwait(false);

                         await bridge.ModSharp.InvokeFrameActionAsync(() =>
                         {
                             if (!bridge.TryGetController(slot, out var controller))
                             {
                                 return;
                             }

                             print(controller, result);
                         }).ConfigureAwait(false);
                     }
                     catch (Exception e)
                     {
                         logger.LogError(e, "Error when running {operation}", operationName);
                     }
                 },
                 bridge.CancellationToken);
    }
}
