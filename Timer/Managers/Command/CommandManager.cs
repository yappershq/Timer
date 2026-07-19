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
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Managers.Command;

internal class CommandManager : IManager, ICommandManager, IClientListener
{
    private readonly Dictionary<string, ICommandManager.ClientCommandDelegate> _adminChatCommands;
    private readonly InterfaceBridge                                           _bridge;

    private readonly Dictionary<string, Func<StringCommand, ECommandAction>> _serverCommands;

    private readonly Dictionary<string, ICommandManager.ClientCommandDelegate> _clientChatCommands;
    private readonly Dictionary<string, ICommandManager.ClientCommandDelegate> _styleCommands;
    private readonly FrozenSet<char>                                           _commandTriggers;

    private readonly ILogger<CommandManager> _logger;

    public CommandManager(InterfaceBridge bridge, ILogger<CommandManager> logger)
    {
        _bridge        = bridge;
        _logger        = logger;

        // OrdinalIgnoreCase enables allocation-free ReadOnlySpan<char> alternate lookups
        // in OnClientSayCommand while keeping mixed-case chat input working.
        _clientChatCommands = new (StringComparer.OrdinalIgnoreCase);
        _styleCommands      = new (StringComparer.OrdinalIgnoreCase);
        _adminChatCommands  = new (StringComparer.OrdinalIgnoreCase);
        _serverCommands     = [];

        HashSet<char> set = ['!', '/', '.', '！', '．', '／', '。'];
        _commandTriggers = set.ToFrozenSet();
    }

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 10;

    public ECommandAction OnClientSayCommand(IGameClient client, bool teamOnly, bool isCommand, string commandName,
                                             string      message)
    {
        if (string.IsNullOrEmpty(message) || !_commandTriggers.Contains(message[0]))
        {
            return ECommandAction.Skipped;
        }

        var text = message.AsSpan(1).Trim(' ');

        var spaceIndex  = text.IndexOf(' ');
        var commandSpan = spaceIndex < 0 ? text : text[..spaceIndex];

        if (commandSpan.IsEmpty)
        {
            return ECommandAction.Skipped;
        }

        if (_styleCommands.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(commandSpan, out var callback)
            || _clientChatCommands.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(commandSpan, out callback))
        {
            return callback(client.Slot, BuildCommand(commandSpan, text, spaceIndex));
        }

        if (_adminChatCommands.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(commandSpan, out callback))
        {
            // The built-in CommandManager has no permission provider, so it cannot honor
            // the permissions passed to AddAdminChatCommand.
#if DEBUG
            _logger.LogWarning("Admin command '{cmd}' executed WITHOUT a permission check (built-in CommandManager, DEBUG builds only).",
                               commandSpan.ToString());

            return callback(client.Slot, BuildCommand(commandSpan, text, spaceIndex));
#else
            _logger.LogWarning("Admin command '{cmd}' ignored: the built-in CommandManager has no permission provider.",
                               commandSpan.ToString());

            return ECommandAction.Handled;
#endif
        }

        return ECommandAction.Skipped;
    }

    private static StringCommand BuildCommand(ReadOnlySpan<char> command, ReadOnlySpan<char> text, int spaceIndex)
    {
        string? arguments = null;

        if (spaceIndex >= 0)
        {
            var argsSpan = text[(spaceIndex + 1)..].TrimStart(' ');

            if (!argsSpan.IsEmpty)
            {
                arguments = argsSpan.ToString();
            }
        }

        // Handlers switch on CommandName with lowercase literals — keep it normalized.
        return new (command.ToString().ToLowerInvariant(), true, arguments);
    }

    public void AddClientChatCommand(string command, ICommandManager.ClientCommandDelegate handler)
    {
        if (_clientChatCommands.TryAdd(command, handler))
        {
            return;
        }

        _logger.LogWarning("{cmd} is already added in _clientChatCommands.", command);
    }

    /// <remarks>
    /// The built-in CommandManager has no permission provider: <paramref name="permissions"/>
    /// is ignored, and registered admin commands only execute in DEBUG builds (unchecked).
    /// An external ICommandManager module is required for real admin-permission handling.
    /// </remarks>
    public void AddAdminChatCommand(string command, ImmutableArray<string> permissions, ICommandManager.ClientCommandDelegate handler)
    {
        if (_adminChatCommands.TryAdd(command, handler))
        {
            return;
        }

        _logger.LogWarning("{cmd} is already added in _adminChatCommands.", command);
    }

    public void AddServerCommand(string command, Func<StringCommand, ECommandAction> handler)
    {
        if (_serverCommands.TryAdd(command, handler))
        {
            _bridge.ConVarManager.CreateServerCommand(command, handler);

            return;
        }

        _logger.LogWarning("{cmd} is already added in _serverCommands.", command);
    }

    public void AddStyleCommand(string command, ICommandManager.ClientCommandDelegate handler)
    {
        if (_styleCommands.TryAdd(command, handler))
        {
            return;
        }

        _logger.LogWarning("Style command {cmd} is already added", command);
    }

    public void ClearStyleCommands()
    {
        _styleCommands.Clear();
    }

    public bool Init()
    {
        _bridge.ClientManager.InstallClientListener(this);

        return true;
    }

    public void Shutdown()
    {
        foreach (var (command, _) in _serverCommands)
        {
            _bridge.ConVarManager.ReleaseCommand(command);
        }

        _bridge.ClientManager.RemoveClientListener(this);
    }
}
