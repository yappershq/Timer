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

namespace Source2Surf.Timer.Shared.Models;

public class MapProfile
{
    // Must match MAX_TRACK: consumers validate/index tracks against TimerConstants.MAX_TRACK
    // while this sizes the Tier array — diverging values would read out of bounds.
    public const int DefaultTrackCount = TimerConstants.MAX_TRACK;

    public ulong MapId { get; set; }

    public required string MapName { get; init; }

    public int Stages  { get; set; }
    public int Bonuses { get; set; }

    public byte[] Tier { get; set; } = new byte[DefaultTrackCount];

    public float TotalPlayTime { get; set; }
    public int   PlayCount     { get; set; }
}
