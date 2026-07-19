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
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Models.Replay;

namespace Source2Surf.Timer.Modules.Replay;

internal class ReplayBotData : IReplayBotData
{
    public required ReplayBotConfig Config      { get; init; }
    public required int             ConfigIndex { get; init; }

    public          EntityIndex       Index      { get; init; }
    public required IPlayerController Controller { get; init; }
    public required IGameClient       Client     { get; init; }

    public PlayerSlot Slot => Client.Slot;

    public int   Track { get; set; } = -1;
    public int   Style { get; set; } = -1;
    public int   Stage { get; set; } = 0;
    public float Time  { get; set; } = 0f;

    public ReplayFileHeader? Header { get; set; } = null;

    public IReadOnlyList<ReplayFrameData> Frames       { get; set; } = [];
    public int                            CurrentFrame { get; set; }

    public EReplayBotStatus Status { get; set; }  = EReplayBotStatus.Idle;

    public EReplayBotType Type { get; init; } = EReplayBotType.Looping;

    public Guid? Timer { get; set; } = null;

    // Cached PushTimer callbacks, created once per bot (lazy ??=). The start-delay and
    // loop-advance timers fire once per replay cycle; caching the delegate here avoids
    // allocating a fresh closure + delegate on every cycle.
    public Func<TimerAction>? StartDelayCallback  { get; set; }
    public Func<TimerAction>? LoopAdvanceCallback { get; set; }

    public int GetCurrentStage()
    {
        var ticks = Header?.StageTicks;

        if (ticks == null || ticks.Count == 0)
        {
            return 0;
        }

        for (var i = 0; i < ticks.Count; i++)
        {
            if (CurrentFrame <= ticks[i])
            {
                return i + 1;
            }
        }

        return ticks.Count;
    }

    public bool IsTrackAllowed(int track)
    {
        switch (Config.PlayType)
        {
            case EReplayBotPlayType.All:
            case EReplayBotPlayType.MainOnly when track  == 0:
            case EReplayBotPlayType.BonusOnly when track > 0:
                return true;
            default:
                return false;
        }
    }
}
