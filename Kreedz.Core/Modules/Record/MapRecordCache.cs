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
using Kreedz.Shared;
using Kreedz.Shared.Models;

namespace Kreedz.Modules.Record;

internal sealed class MapRecordCache
{
    private readonly List<RunRecord>[,] _mapRecords = new List<RunRecord>[TimerConstants.MAX_STYLE, TimerConstants.MAX_TRACK];
    private readonly Dictionary<(int style, int track, int stage), List<RunRecord>> _stageRecords = [];
    private readonly IReadOnlyList<RunCheckpoint>?[,] _wrCheckpoints
        = new IReadOnlyList<RunCheckpoint>?[TimerConstants.MAX_STYLE, TimerConstants.MAX_TRACK];
    private readonly ILogger _logger;

    public MapRecordCache(ILogger logger)
    {
        _logger = logger;

        for (var s = 0; s < TimerConstants.MAX_STYLE; s++)
        {
            for (var t = 0; t < TimerConstants.MAX_TRACK; t++)
            {
                _mapRecords[s, t] = [];
            }
        }
    }

    public void Populate(IReadOnlyList<RunRecord> records, IReadOnlyList<RunRecord> stageRecords)
    {
        foreach (var record in records)
        {
            _mapRecords[record.Style, record.Track].Add(record);
        }

        foreach (var record in stageRecords)
        {
            var stage = record.Stage;

            if (!IsValidStageIndex(stage))
            {
                _logger.LogWarning("Ignore invalid stage record during map cache warm-up. style={style}, track={track}, stage={stage}",
                                   record.Style,
                                   record.Track,
                                   stage);

                continue;
            }

            var key = (record.Style, record.Track, stage);

            if (!_stageRecords.TryGetValue(key, out var list))
            {
                list = [];
                _stageRecords[key] = list;
            }

            list.Add(record);
        }

        for (var s = 0; s < TimerConstants.MAX_STYLE; s++)
        {
            for (var t = 0; t < TimerConstants.MAX_TRACK; t++)
            {
                _mapRecords[s, t].Sort();
            }
        }

        foreach (var list in _stageRecords.Values)
        {
            list.Sort();
        }
    }

    public void RefreshTrack(int style, int track, IReadOnlyList<RunRecord> records)
    {
        var list = _mapRecords[style, track];
        list.Clear();
        list.AddRange(records);
        list.Sort();
    }

    public void RefreshStage(int style, int track, int stage, IReadOnlyList<RunRecord> records)
    {
        var key = (style, track, stage);

        if (_stageRecords.TryGetValue(key, out var existing))
        {
            existing.Clear();
            existing.AddRange(records);
            existing.Sort();
        }
        else
        {
            var list = new List<RunRecord>(records);
            list.Sort();
            _stageRecords[key] = list;
        }
    }

    public int GetRankForTime(int style, int track, float time)
    {
        var records = _mapRecords[style, track];

        var low  = 0;
        var high = records.Count;

        while (low < high)
        {
            var mid = (int)(((uint) low + (uint) high) >> 1);

            if (records[mid].Time < time)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low + 1;
    }

    public RunRecord? GetWR(int style, int track, int stage = 0)
    {
        if (stage == 0)
        {
            var records = _mapRecords[style, track];

            return records.Count > 0 ? records[0] : null;
        }

        if (!IsValidStageIndex(stage))
        {
            throw
                new IndexOutOfRangeException($"Stage index is out of range [1, {TimerConstants.MAX_STAGE}), current: {stage}");
        }

        if (_stageRecords.TryGetValue((style, track, stage), out var stageRecords) && stageRecords.Count > 0)
        {
            return stageRecords[0];
        }

        return null;
    }

    public float? GetWRTime(int style, int track)
    {
        var rec = _mapRecords[style, track];

        if (rec.Count > 0)
        {
            return rec[0].Time;
        }

        return null;
    }

    public IReadOnlyList<RunRecord> GetRecords(int style, int track)
    {
        return _mapRecords[style, track];
    }

    /// <summary>Mode-filtered view — PB/WR display must never mix movement modes (record key includes mode).</summary>
    public IReadOnlyList<RunRecord> GetRecords(int style, int track, int mode)
    {
        var all = _mapRecords[style, track];
        var mixed = false;
        foreach (var r in all)
        {
            if (r.Mode != mode)
            {
                mixed = true;
                break;
            }
        }

        if (!mixed)
        {
            return all;
        }

        var filtered = new List<RunRecord>(all.Count);
        foreach (var r in all)
        {
            if (r.Mode == mode)
            {
                filtered.Add(r);
            }
        }
        return filtered;
    }

    public IReadOnlyList<RunRecord>? GetStageRecords(int style, int track, int stage) =>
        _stageRecords.GetValueOrDefault((style, track, stage));

    public void Clear()
    {
        _stageRecords.Clear();

        for (var style = 0; style < TimerConstants.MAX_STYLE; style++)
        {
            for (var track = 0; track < TimerConstants.MAX_TRACK; track++)
            {
                _mapRecords[style, track].Clear();
                _wrCheckpoints[style, track] = null;
            }
        }
    }

    public void SetWRCheckpoints(int style, int track, IReadOnlyList<RunCheckpoint> checkpoints)
    {
        _wrCheckpoints[style, track] = checkpoints;
    }

    public IReadOnlyList<RunCheckpoint>? GetWRCheckpoints(int style, int track)
    {
        return _wrCheckpoints[style, track];
    }

    private static bool IsValidStageIndex(int stage) =>
        stage is >= 1 and < TimerConstants.MAX_STAGE;
}
