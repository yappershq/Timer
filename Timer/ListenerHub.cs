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

namespace Source2Surf.Timer;

internal sealed class ListenerHub<T> where T : class
{
    private readonly List<T> _listeners = [];
    private readonly ILogger _logger;

    public T[] Snapshot { get; private set; } = [];

    public ListenerHub(ILogger logger)
    {
        _logger = logger;
    }

    public void Register(T listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        if (!_listeners.Contains(listener))
        {
            _listeners.Add(listener);
            Snapshot = [.. _listeners];
        }
    }

    public void Unregister(T? listener)
    {
        if (listener is null)
        {
            return;
        }

        if (_listeners.Remove(listener))
        {
            Snapshot = [.. _listeners];
        }
    }

    public void Clear()
    {
        _listeners.Clear();
        Snapshot = [];
    }

    /// <summary>
    ///     Invokes <paramref name="action" /> on every registered listener, logging (never
    ///     propagating) per-listener exceptions. Pass a static lambda and explicit args to
    ///     keep the dispatch closure-free.
    /// </summary>
    public void NotifyAll(string name, Action<T> action)
    {
        foreach (var listener in Snapshot)
        {
            try
            {
                action(listener);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error when calling {name} listener", name);
            }
        }
    }

    /// <inheritdoc cref="NotifyAll(string, Action{T})" />
    public void NotifyAll<T1>(string name, Action<T, T1> action, T1 arg1)
    {
        foreach (var listener in Snapshot)
        {
            try
            {
                action(listener, arg1);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error when calling {name} listener", name);
            }
        }
    }

    /// <inheritdoc cref="NotifyAll(string, Action{T})" />
    public void NotifyAll<T1, T2, T3>(string name, Action<T, T1, T2, T3> action, T1 arg1, T2 arg2, T3 arg3)
    {
        foreach (var listener in Snapshot)
        {
            try
            {
                action(listener, arg1, arg2, arg3);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error when calling {name} listener", name);
            }
        }
    }

    /// <inheritdoc cref="NotifyAll(string, Action{T})" />
    public void NotifyAll<T1, T2, T3, T4>(string name, Action<T, T1, T2, T3, T4> action, T1 arg1, T2 arg2, T3 arg3, T4 arg4)
    {
        foreach (var listener in Snapshot)
        {
            try
            {
                action(listener, arg1, arg2, arg3, arg4);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error when calling {name} listener", name);
            }
        }
    }

    /// <summary>
    ///     Returns false as soon as any listener's <paramref name="predicate" /> returns
    ///     false. A throwing listener is logged and treated as consenting (true), matching
    ///     the historical hand-rolled loops.
    /// </summary>
    public bool All<T1, T2>(string name, Func<T, T1, T2, bool> predicate, T1 arg1, T2 arg2)
    {
        foreach (var listener in Snapshot)
        {
            try
            {
                if (!predicate(listener, arg1, arg2))
                {
                    return false;
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error when calling {name} listener", name);
            }
        }

        return true;
    }
}
