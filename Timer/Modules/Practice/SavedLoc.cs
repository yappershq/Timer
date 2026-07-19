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

using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.Timer;

namespace Source2Surf.Timer.Modules.Practice;

internal readonly record struct PhysicsSnapshot(
    Vector      Origin,
    Vector      Angles,
    Vector      Velocity,
    Vector      BaseVelocity,
    MoveType    MoveType,
    EntityFlags Flags,
    float       GravityScale,
    float       LaggedMovement,
    float       Stamina,
    bool        Ducked,
    bool        Ducking,
    float       DuckAmount,
    float       DuckSpeed,
    Vector      LadderNormal);

internal sealed class SavedLoc
{
    public required SteamID SteamId { get; init; }

    public required bool Segmented { get; init; }

    public required int Track { get; init; }

    public required PhysicsSnapshot Physics { get; init; }

    public TimerStateSnapshot? Timer { get; init; }

    public StageTimerStateSnapshot? StageTimer { get; init; }
}
