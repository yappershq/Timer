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
using Sharp.Shared.Types;

namespace Source2Surf.Timer.Shared.Models;

public class RunRecord : IComparable<RunRecord>
{
    public long Id { get; set; }

    public DateTime RunDate { get; set; }

    public ulong  SteamId    { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public ulong  MapId      { get; set; }

    public int Style { get; set; }
    public int Track { get; set; }
    public int Stage { get; set; }

    public float Time { get; set; }

    public int   Jumps   { get; set; }
    public int   Strafes { get; set; }
    public float Sync    { get; set; }

    public float VelocityStartX { get; set; }
    public float VelocityStartY { get; set; }
    public float VelocityStartZ { get; set; }

    public float VelocityAvgX { get; set; }
    public float VelocityAvgY { get; set; }
    public float VelocityAvgZ { get; set; }

    public float VelocityEndX { get; set; }
    public float VelocityEndY { get; set; }
    public float VelocityEndZ { get; set; }

    public void SetStartVelocity(Vector velocity)
    {
        VelocityStartX = velocity.X;
        VelocityStartY = velocity.Y;
        VelocityStartZ = velocity.Z;
    }

    public void SetAverageVelocity(Vector velocity)
    {
        VelocityAvgX = velocity.X;
        VelocityAvgY = velocity.Y;
        VelocityAvgZ = velocity.Z;
    }

    public void SetEndVelocity(Vector velocity)
    {
        VelocityEndX = velocity.X;
        VelocityEndY = velocity.Y;
        VelocityEndZ = velocity.Z;
    }

    public int CompareTo(RunRecord? other)
        => other is null ? 1 : Time.CompareTo(other.Time);
}

public class RunCheckpoint : IComparable<RunCheckpoint>
{
    public long Id { get; set; }

    public long RecordId { get; set; }

    public uint CheckpointIndex { get; set; }

    public float Time { get; set; }
    public float Sync { get; set; }

    public float VelocityStartX { get; set; }
    public float VelocityStartY { get; set; }
    public float VelocityStartZ { get; set; }

    public float VelocityAvgX { get; set; }
    public float VelocityAvgY { get; set; }
    public float VelocityAvgZ { get; set; }

    public float VelocityMaxX { get; set; }
    public float VelocityMaxY { get; set; }
    public float VelocityMaxZ { get; set; }

    public float VelocityEndX { get; set; }
    public float VelocityEndY { get; set; }
    public float VelocityEndZ { get; set; }

    public void SetStartVelocity(Vector velocity)
    {
        VelocityStartX = velocity.X;
        VelocityStartY = velocity.Y;
        VelocityStartZ = velocity.Z;
    }

    public void SetAverageVelocity(Vector velocity)
    {
        VelocityAvgX = velocity.X;
        VelocityAvgY = velocity.Y;
        VelocityAvgZ = velocity.Z;
    }

    public void SetMaxVelocity(Vector velocity)
    {
        VelocityMaxX = velocity.X;
        VelocityMaxY = velocity.Y;
        VelocityMaxZ = velocity.Z;
    }

    public void SetEndVelocity(Vector velocity)
    {
        VelocityEndX = velocity.X;
        VelocityEndY = velocity.Y;
        VelocityEndZ = velocity.Z;
    }

    public int CompareTo(RunCheckpoint? other)
        => other is null ? 1 : Time.CompareTo(other.Time);
}
