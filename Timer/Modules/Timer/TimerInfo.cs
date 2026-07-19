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
using System.Runtime.CompilerServices;
using Sharp.Shared.Types;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models.Timer;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules.Timer;

internal class TimerInfo : ITimerInfo
{
    // All scalar state lives in this one value; the properties below just forward to it so that
    // CaptureState/RestoreState are a single struct copy. _state is a field (not a property), so
    // forwarding setters mutate it in place.
    private TimerCoreState _state;

    public uint TimerTick { get => _state.TimerTick; set => _state.TimerTick = value; }

    public int TotalMeasures { get => _state.TotalMeasures; set => _state.TotalMeasures = value; }
    public int GoodSync      { get => _state.GoodSync;      set => _state.GoodSync = value; }

    public float LastForwardMove { get => _state.LastForwardMove; set => _state.LastForwardMove = value; }
    public float LastLeftMove    { get => _state.LastLeftMove;    set => _state.LastLeftMove = value; }
    public float LastYaw         { get => _state.LastYaw;         set => _state.LastYaw = value; }

    public bool WasOnGround  { get => _state.WasOnGround;  set => _state.WasOnGround = value; }
    public int  OnGroundTick { get => _state.OnGroundTick; set => _state.OnGroundTick = value; }

    public int Jumps   { get => _state.Jumps;   set => _state.Jumps = value; }
    public int Strafes { get => _state.Strafes; set => _state.Strafes = value; }

    public ETimerStatus Status { get => _state.Status; private set => _state.Status = value; }

    public float Time => TimerTick * TimerConstants.TickInterval;

    public Vector AvgVelocity   { get => _state.AvgVelocity;   set => _state.AvgVelocity = value; }
    public Vector EndVelocity   { get => _state.EndVelocity;   set => _state.EndVelocity = value; }
    public Vector StartVelocity { get => _state.StartVelocity; private set => _state.StartVelocity = value; }

    public Vector MaxVelocity { get => _state.MaxVelocity; set => _state.MaxVelocity = value; }

    public float Sync => TotalMeasures > 0 ? GoodSync / (float) TotalMeasures : 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Reset(bool resetJumps)
    {
        TimerTick = 0;

        if (resetJumps)
        {
            Jumps = 0;
        }

        Strafes       = 0;
        TotalMeasures = 0;
        GoodSync      = 0;
        MaxVelocity   = new ();
        AvgVelocity   = new ();
        StartVelocity = new ();
        EndVelocity   = new ();
        Checkpoint    = 0;
        CheckpointInfoInternal.Clear();
        CurrentCheckpointInfo = null;

        LastForwardMove = 0;
        LastLeftMove    = 0;
        LastYaw         = 0;
        WasOnGround     = false;
        OnGroundTick    = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Start(Vector velocity)
    {
        Reset(Math.Abs(velocity.Z) < float.Epsilon || Jumps > 1);
        Status        = ETimerStatus.Running;
        StartVelocity = velocity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Stop()
    {
        Reset(true);
        Status = ETimerStatus.Stopped;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected internal bool IsTimerRunning()
        => Status == ETimerStatus.Running;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected internal bool IsTimerPaused()
        => Status == ETimerStatus.Paused;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected internal bool PauseTimer()
    {
        if (Status != ETimerStatus.Running)
            return false;

        Status = ETimerStatus.Paused;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected internal bool ResumeTimer()
    {
        if (Status != ETimerStatus.Paused)
            return false;

        Status = ETimerStatus.Running;
        return true;
    }

    public TimerInfo()
    {
        // Seed the fields Reset() deliberately leaves alone. EZoneType.Invalid is -1, so a
        // default-initialized _state would have InZone = 0, not Invalid.
        _state.Status = ETimerStatus.Stopped;
        _state.InZone = EZoneType.Invalid;

        Reset(true);
    }

    private List<CheckpointInfo> CheckpointInfoInternal { get; } = [];

    // ITimerInfo
    public int Checkpoint { get => _state.Checkpoint; set => _state.Checkpoint = value; }

    public IReadOnlyList<CheckpointInfo> Checkpoints => CheckpointInfoInternal;

    public void ChangeStyle(int style)
    {
        StopTimer();
        Style = Math.Max(style, 0);
    }

    public void ChangeTrack(int track)
    {
        StopTimer();
        Track = Math.Max(track, 0);
    }

    public EZoneType InZone { get => _state.InZone; private set => _state.InZone = value; }
    public int       Style  { get => _state.Style;  private set => _state.Style = value; }
    public int       Track  { get => _state.Track;  private set => _state.Track = value; }

    public CheckpointInfo? CurrentCheckpointInfo { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateInZone(EZoneType type)
        => InZone = type;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StopTimer()
    {
        Stop();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StartTimer(int track, Vector velocity)
    {
        Start(velocity);

        Track      = track;
        Checkpoint = 0;
        CheckpointInfoInternal.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddCheckpoint(CheckpointInfo info)
    {
        CheckpointInfoInternal.Add(info);
    }

    internal virtual TimerStateSnapshot CaptureState()
        => new ()
        {
            State                 = _state,
            Checkpoints           = CloneCheckpoints(CheckpointInfoInternal),
            CurrentCheckpointInfo = CloneCheckpoint(CurrentCheckpointInfo),
        };

    internal virtual void RestoreState(TimerStateSnapshot snapshot)
    {
        _state = snapshot.State;

        CheckpointInfoInternal.Clear();

        foreach (var cp in snapshot.Checkpoints)
        {
            if (CloneCheckpoint(cp) is { } cloned)
            {
                CheckpointInfoInternal.Add(cloned);
            }
        }

        CurrentCheckpointInfo = CloneCheckpoint(snapshot.CurrentCheckpointInfo);
    }

    private static List<CheckpointInfo> CloneCheckpoints(IReadOnlyList<CheckpointInfo> source)
    {
        var copy = new List<CheckpointInfo>(source.Count);

        foreach (var cp in source)
        {
            if (CloneCheckpoint(cp) is { } cloned)
            {
                copy.Add(cloned);
            }
        }

        return copy;
    }

    private static CheckpointInfo? CloneCheckpoint(CheckpointInfo? source)
    {
        if (source is null)
        {
            return null;
        }

        return new CheckpointInfo
        {
            TimerTick       = source.TimerTick,
            Sync            = source.Sync,
            StartVelocity   = source.StartVelocity,
            AverageVelocity = source.AverageVelocity,
            MaxVelocity     = source.MaxVelocity,
            EndVelocity     = source.EndVelocity,
        };
    }
}

internal class StageTimerInfo : TimerInfo, IStageTimerInfo
{
    public int Stage { get; set; } = 1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void StartTimer(int track, Vector velocity, int stage)
    {
        base.StartTimer(track, velocity);
        Stage = stage;
    }

    internal override TimerStateSnapshot CaptureState()
    {
        var baseSnapshot = base.CaptureState();

        return new StageTimerStateSnapshot
        {
            State                 = baseSnapshot.State,
            Checkpoints           = baseSnapshot.Checkpoints,
            CurrentCheckpointInfo = baseSnapshot.CurrentCheckpointInfo,
            Stage                 = Stage,
        };
    }

    internal override void RestoreState(TimerStateSnapshot snapshot)
    {
        base.RestoreState(snapshot);

        if (snapshot is StageTimerStateSnapshot stageSnapshot)
        {
            Stage = stageSnapshot.Stage;
        }
    }
}
