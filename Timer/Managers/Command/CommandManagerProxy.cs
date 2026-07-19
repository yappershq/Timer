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
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Managers.Command;

internal sealed class CommandManagerProxy : ExternalModuleProxy<ICommandManager>, ICommandManager
{
    private readonly CommandManager _fallbackManager;

    public CommandManagerProxy(ISharedSystem                shared,
                               CommandManager               fallback,
                               ILogger<CommandManagerProxy> logger)
        : base(shared, fallback, logger)
    {
        _fallbackManager = fallback;
    }

    protected override string Identity     => ICommandManager.Identity;
    protected override string ContractName => "ICommandManager";

    protected override bool InitFallback()
        => _fallbackManager.Init();

    protected override void ShutdownFallback()
        => _fallbackManager.Shutdown();

    public void AddClientChatCommand(string command, ICommandManager.ClientCommandDelegate handler)
        => Current.AddClientChatCommand(command, handler);

    public void AddAdminChatCommand(string command, ImmutableArray<string> permissions, ICommandManager.ClientCommandDelegate handler)
        => Current.AddAdminChatCommand(command, permissions, handler);

    public void AddServerCommand(string command, Func<StringCommand, ECommandAction> handler)
        => Current.AddServerCommand(command, handler);

    public void AddStyleCommand(string command, ICommandManager.ClientCommandDelegate handler)
        => Current.AddStyleCommand(command, handler);

    public void ClearStyleCommands()
        => Current.ClearStyleCommands();
}
