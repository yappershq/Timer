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

namespace Source2Surf.Timer.Modules.Replay;

/// <summary>
/// Fallback-saved temporary replay file, created when a pending replay times out
/// or is force-saved during map change before the record result arrives.
/// </summary>
internal sealed class FallbackReplayRecord
{
    public required string TempFilePath { get; init; }

    /// <summary>
    /// Map name captured when the replay was recorded. Used to build the final replay path so a
    /// late record-saved event arriving after a map change still writes under the correct map.
    /// </summary>
    public required string MapName { get; init; }

    /// <summary>
    /// Background write task — must be awaited before File.Move or File.ReadAllBytes.
    /// </summary>
    public required Task WriteTask { get; init; }

    /// <summary>
    /// Creation timestamp for TTL-based expiry cleanup.
    /// </summary>
    public required DateTime CreatedAt { get; init; }
}
