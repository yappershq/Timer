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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Source2Surf.Timer;

internal sealed class TaskTracker
{
    private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly List<Task> _pendingTasks = [];
    private readonly ILogger _logger;

    public TaskTracker(ILogger logger)
    {
        _logger = logger;
    }

    public void Track(Task task)
    {
        lock (_pendingTasks)
        {
            // Observe faulted tasks as they are pruned — otherwise their exceptions are
            // silently dropped (surfacing only as UnobservedTaskException at GC time).
            _pendingTasks.RemoveAll(t =>
            {
                if (!t.IsCompleted)
                {
                    return false;
                }

                if (t.IsFaulted)
                {
                    _logger.LogError(t.Exception, "Tracked task failed");
                }

                return true;
            });

            _pendingTasks.Add(task);
        }
    }

    public void DrainPendingTasks(TimeSpan? timeout = null)
    {
        Task[] snapshot;

        lock (_pendingTasks)
        {
            snapshot = [.. _pendingTasks];
            _pendingTasks.Clear();
        }

        if (snapshot.Length == 0)
            return;

        var completed = false;

        try
        {
            completed = Task.WaitAll(snapshot, timeout ?? DefaultDrainTimeout);
        }
        catch (AggregateException ex)
        {
            completed = true;
            _logger.LogWarning(ex, "Some pending record tasks failed during shutdown");
        }

        if (completed)
        {
            return;
        }

        var unfinished = 0;

        foreach (var task in snapshot)
        {
            if (!task.IsCompleted)
            {
                unfinished++;
            }
        }

        _logger.LogWarning("Drain timed out with {count} pending task(s) still running; their results/failures are abandoned.",
                           unfinished);
    }
}
