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

using System.Collections.Generic;
using Sharp.Shared.Types;
using Source2Surf.Timer.Shared.Models.Timer;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules.Timer;

/// <summary>
///     Every scalar field of a <see cref="TimerInfo"/> in a single value type. Holding the
///     state here (instead of as ~19 loose fields on <see cref="TimerInfo"/>) lets capture and
///     restore be a single struct copy rather than a field-by-field transcription — adding a
///     field touches only this struct and its forwarding property, not the snapshot, capture,
///     restore, and stage-capture sites separately.
/// </summary>
internal struct TimerCoreState
{
    public ETimerStatus Status;

    public uint TimerTick;

    public int Jumps;
    public int Strafes;
    public int TotalMeasures;
    public int GoodSync;
    public int Style;
    public int Track;
    public int Checkpoint;
    public int OnGroundTick;

    public EZoneType InZone;

    public Vector StartVelocity;
    public Vector EndVelocity;
    public Vector AvgVelocity;
    public Vector MaxVelocity;

    public float LastForwardMove;
    public float LastLeftMove;
    public float LastYaw;

    public bool WasOnGround;
}

/// <summary>
///     Full immutable snapshot of a <see cref="TimerInfo"/> instance — captured at
///     saveloc time and replayed back when the player teleports to a segmented loc.
///     Restoring deliberately bypasses <see cref="TimerInfo"/>'s lifecycle methods
///     so listeners do not refire.
/// </summary>
internal record TimerStateSnapshot
{
    public required TimerCoreState State { get; init; }

    public required IReadOnlyList<CheckpointInfo> Checkpoints           { get; init; }
    public required CheckpointInfo?               CurrentCheckpointInfo { get; init; }
}

internal sealed record StageTimerStateSnapshot : TimerStateSnapshot
{
    public required int Stage { get; init; }
}
