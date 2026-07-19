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
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using FireEventCallback = Source2Surf.Timer.Managers.IEventHookManager.DelegateOnEventFired;
using HookEventCallback
    = System.Func<Source2Surf.Timer.Managers.EventHookParams, Sharp.Shared.Types.HookReturnValue<bool>>;

namespace Source2Surf.Timer.Managers;

internal interface IEventHookManager
{
    delegate void DelegateOnEventFired(IGameEvent e);

    void HookEvent(string eventName, HookEventCallback callback);

    void ListenEvent(string eventName, FireEventCallback callback);
}

internal readonly struct EventHookParams
{
    public EventHookParams(IGameEvent e, bool serverOnly)
    {
        Event      = e;
        ServerOnly = serverOnly;
    }

    public IGameEvent Event      { get; }
    public bool       ServerOnly { get; }
}

// ReSharper disable CanSimplifyDictionaryLookupWithTryAdd
internal class EventHookManager : IEventHookManager, IManager, IEventListener
{
    private readonly InterfaceBridge                                _bridge;
    private readonly HashSet<string>                                _events;
    private readonly Dictionary<string, HashSet<HookEventCallback>> _hooks;

    private readonly Dictionary<string, FireEventCallback?> _listeners;
    private readonly ILogger<EventHookManager>              _logger;

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    public EventHookManager(InterfaceBridge bridge, ILogger<EventHookManager> logger)
    {
        _bridge = bridge;
        _logger = logger;

        _events    = new (StringComparer.OrdinalIgnoreCase);
        _hooks     = new (StringComparer.OrdinalIgnoreCase);
        _listeners = new (StringComparer.OrdinalIgnoreCase);
    }

    public void HookEvent(string eventName, HookEventCallback callback)
    {
        if (_events.Add(eventName))
        {
            _bridge.EventManager.HookEvent(eventName);
        }

        if (!_hooks.TryGetValue(eventName, out var value))
        {
            value             = [];
            _hooks[eventName] = value;
        }

        value.Add(callback);
    }

    public void ListenEvent(string eventName, FireEventCallback callback)
    {
        if (_events.Add(eventName))
        {
            _bridge.EventManager.HookEvent(eventName);
        }

        if (!_listeners.ContainsKey(eventName))
        {
            _listeners[eventName] = callback;
        }
        else
        {
            _listeners[eventName] += callback;
        }
    }

    public bool HookFireEvent(IGameEvent e, ref bool serverOnly)
    {
        var eventName = e.Name;

        if (!_hooks.TryGetValue(eventName, out var callbacks))
        {
            return true;
        }

        var @params = new EventHookParams(e, serverOnly);
        var @return = EHookAction.Ignored;

        foreach (var callback in callbacks)
        {
            try
            {
                var action = callback(@params);

                if (action.Action > @return)
                {
                    @return = action.Action;
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "An error occurred while firing callback for event {event}", eventName);
            }
        }

        // Block Event
        if (@return == EHookAction.SkipCallReturnOverride)
        {
            return false;
        }

        // Allow Event
        if (@return != EHookAction.Ignored)
        {
            serverOnly = @params.ServerOnly;
        }

        return true;
    }

    public void FireGameEvent(IGameEvent e)
        => _listeners.GetValueOrDefault(e.Name)
                     ?.Invoke(e);

    public bool Init()
    {
        _bridge.EventManager.InstallEventListener(this);

        return true;
    }

    public void Shutdown()
    {
        _bridge.EventManager.RemoveEventListener(this);
    }
}
