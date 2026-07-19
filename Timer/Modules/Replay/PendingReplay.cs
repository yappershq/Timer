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

namespace Source2Surf.Timer.Modules.Replay;

internal sealed class PendingReplay
{
    public required ReplaySaveSnapshot Snapshot { get; init; }
    public required string TempFilePath { get; init; }

    /// <summary>
    /// Map name captured when the replay was recorded. Used to build the final replay path so a
    /// record-saved event arriving after a map change still writes under the correct map.
    /// </summary>
    public required string MapName { get; init; }

    public Guid? TimeoutTimerId { get; set; }
}
