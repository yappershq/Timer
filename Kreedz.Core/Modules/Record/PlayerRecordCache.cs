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
using Sharp.Shared.Units;
using Kreedz.Shared;
using Kreedz.Shared.Models;

namespace Kreedz.Modules.Record;

internal sealed class PlayerRecordCache
{
    private readonly Dictionary<int, Dictionary<(int style, int track), RunRecord>> _records = [];
    private readonly Dictionary<int, Dictionary<(int style, int track, int stage), RunRecord>> _stageRecords = [];
    private readonly ILogger _logger;

    public PlayerRecordCache(ILogger logger)
    {
        _logger = logger;
    }

    public void Populate(PlayerSlot slot, IReadOnlyList<RunRecord> records, IReadOnlyList<RunRecord> stageRecords)
    {
        var mainRecords    = GetOrAddSlotRecords(slot);
        var stageRecordsMap = GetOrAddSlotStageRecords(slot);

        foreach (var record in records)
        {
            mainRecords[(record.Style, record.Track)] = record;
        }

        foreach (var record in stageRecords)
        {
            var style = record.Style;
            var track = record.Track;
            var stage = record.Stage;

            if (!IsValidStageIndex(stage))
            {
                _logger.LogWarning("Ignore invalid player stage record while loading cache. slot={slot}, style={style}, track={track}, stage={stage}",
                                   slot,
                                   style,
                                   track,
                                   stage);

                continue;
            }

            stageRecordsMap[(style, track, stage)] = record;
        }
    }

    /// <summary>Mode-filtered PB — a VNL PB must not display as your CKZ PB.</summary>
    public RunRecord? GetRecord(PlayerSlot slot, int style, int track, int mode, int stage)
    {
        var r = GetRecord(slot, style, track, stage);
        return r is null || r.Mode == mode ? r : null;
    }

    public RunRecord? GetRecord(PlayerSlot slot, int style, int track, int stage = 0)
    {
        if (stage == 0)
        {
            return _records.TryGetValue(slot, out var slotRecords)
                && slotRecords.TryGetValue((style, track), out var rec)
                    ? rec
                    : null;
        }

        if (!IsValidStageIndex(stage))
        {
            throw
                new IndexOutOfRangeException($"Stage index is out of range [1, {TimerConstants.MAX_STAGE}), current: {stage}");
        }

        return _stageRecords.TryGetValue(slot, out var slotStageRecords)
            && slotStageRecords.TryGetValue((style, track, stage), out var stageRec)
                ? stageRec
                : null;
    }

    public void SetRecord(PlayerSlot slot, int style, int track, RunRecord record)
    {
        GetOrAddSlotRecords(slot)[(style, track)] = record;
    }

    public void SetStageRecord(PlayerSlot slot, int style, int track, int stage, RunRecord record)
    {
        GetOrAddSlotStageRecords(slot)[(style, track, stage)] = record;
    }

    public void Clear(PlayerSlot slot)
    {
        _records.Remove(slot);
        _stageRecords.Remove(slot);
    }

    public void ClearAll()
    {
        _records.Clear();
        _stageRecords.Clear();
    }

    private Dictionary<(int style, int track), RunRecord> GetOrAddSlotRecords(int slot)
    {
        if (!_records.TryGetValue(slot, out var dict))
        {
            dict = [];
            _records[slot] = dict;
        }

        return dict;
    }

    private Dictionary<(int style, int track, int stage), RunRecord> GetOrAddSlotStageRecords(int slot)
    {
        if (!_stageRecords.TryGetValue(slot, out var dict))
        {
            dict = [];
            _stageRecords[slot] = dict;
        }

        return dict;
    }

    private static bool IsValidStageIndex(int stage) =>
        stage >= 1 && stage < TimerConstants.MAX_STAGE;
}
