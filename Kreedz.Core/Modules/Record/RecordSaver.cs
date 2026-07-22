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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Units;
using Kreedz.Extensions;
using Kreedz.Shared;
using Kreedz.Shared.Events;
using Kreedz.Shared.Interfaces;
using Kreedz.Shared.Interfaces.Listeners;
using Kreedz.Shared.Models;
using Kreedz.Shared.Models.Timer;
using Kreedz.Modules;

namespace Kreedz.Modules.Record;

internal sealed class RecordSaver
{
    private readonly InterfaceBridge                    _bridge;
    private readonly IRequestManager                    _request;
    private readonly IStyleModule                       _styleModule;
    private readonly IModeModule                        _modes;
    private readonly ICheckpointModule                  _checkpoint;
    private readonly MapRecordCache                     _mapCache;
    private readonly PlayerRecordCache                  _playerCache;
    private readonly ListenerHub<IRecordModuleListener> _listenerHub;
    private readonly ILogger                            _logger;

    public RecordSaver(InterfaceBridge                    bridge,
                       IRequestManager                    request,
                       IStyleModule                       styleModule,
                       IModeModule                        modeModule,
                       ICheckpointModule                  checkpoint,
                       MapRecordCache                     mapCache,
                       PlayerRecordCache                  playerCache,
                       ListenerHub<IRecordModuleListener> listenerHub,
                       ILogger                            logger)
    {
        _bridge      = bridge;
        _request     = request;
        _modes       = modeModule;
        _styleModule = styleModule;
        _checkpoint  = checkpoint;
        _mapCache    = mapCache;
        _playerCache = playerCache;
        _listenerHub = listenerHub;
        _logger      = logger;
    }

    public static RecordRequest CreateRecordRequest(ITimerInfo timerInfo, IStyleModule styleModule, int teleports = 0)
    {
        var styleSetting = styleModule.GetStyleSetting(timerInfo.Style);

        var recordRequest = new RecordRequest
        {
            Style       = timerInfo.Style,
            Track       = timerInfo.Track,
            Stage       = 0,
            Time        = timerInfo.Time,
            Jumps       = timerInfo.Jumps,
            Strafes     = timerInfo.Strafes,
            Sync        = timerInfo.Sync,
            Teleports   = teleports,
            StyleFactor = styleSetting.ScoreFactor,
        };

        for (var i = 0; i < timerInfo.Checkpoints.Count; i++)
        {
            var cp = timerInfo.Checkpoints[i];

            var request = new RecordRequest.CheckpointRecord
            {
                CheckpointIndex = i + 1, Time = cp.Time, Sync = cp.Sync,
            };

            request.SetAverageVelocity(cp.AverageVelocity);
            request.SetMaxVelocity(cp.MaxVelocity);
            request.SetStartVelocity(cp.StartVelocity);
            request.SetEndVelocity(cp.EndVelocity);

            recordRequest.Checkpoints.Add(request);
        }

        return recordRequest;
    }

    public Task SaveMapRecordAsync(PlayerSlot        slot,
                                   SteamID           steamId,
                                   string            playerName,
                                   string            mapName,
                                   ITimerInfo        timerInfo,
                                   int               attemptId,
                                   CancellationToken ct)
    {
        var style = timerInfo.Style;
        var track = timerInfo.Track;

        var mode     = KzModes.ToIndex(_modes.GetMode(slot));
        var records  = _mapCache.GetRecords(style, track, mode);
        var wrRecord = records.Count > 0 ? records[0] : null;
        var pbRecord = _playerCache.GetRecord(slot, style, track, mode, 0);

        var recordRequest = CreateRecordRequest(timerInfo, _styleModule, _checkpoint.GetTeleportCount(slot));
        recordRequest.Mode = mode;

        return Task.Run(async () =>
                        {
                            try
                            {
                                var (recordType, savedRecord, rank) = await _request.AddPlayerRecord(steamId,
                                                                                        mapName,
                                                                                        recordRequest)
                                                                                    .ConfigureAwait(false);

                                _ = rank;

                                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                                                                              {
                                                                                  var recordEvent
                                                                                      = new PlayerRecordSavedEvent(steamId,
                                                                                          playerName,
                                                                                          recordType,
                                                                                          savedRecord,
                                                                                          wrRecord,
                                                                                          pbRecord,
                                                                                          attemptId);

                                                                                  NotifyRecordSavedListeners(recordEvent);

                                                                                  if (recordType
                                                                                   < EAttemptResult.NewPersonalRecord)
                                                                                  {
                                                                                      return;
                                                                                  }

                                                                                  if (_bridge.ClientManager
                                                                                       .GetGameClient(steamId)
                                                                                   is { } currentClient)
                                                                                  {
                                                                                      var currentSlot = currentClient.Slot;

                                                                                      _logger
                                                                                          .LogInformation("Found player {steamId} at slot {slot}, setting record cache",
                                                                                              steamId,
                                                                                              currentSlot);

                                                                                      _playerCache.SetRecord(currentSlot,
                                                                                          style,
                                                                                          track,
                                                                                          savedRecord);
                                                                                  }
                                                                              },
                                                                              ct)
                                             .ConfigureAwait(false);

                                await RefreshMapRecord(style, track).ConfigureAwait(false);
                            }
                            catch (Exception e)
                            {
                                _logger.LogError(e, "Error when saving record");
                            }
                        },
                        ct);
    }

    public Task SaveStageRecordAsync(PlayerSlot        slot,
                                     SteamID           steamId,
                                     string            playerName,
                                     string            mapName,
                                     IStageTimerInfo   timerInfo,
                                     int               attemptId,
                                     CancellationToken ct)
    {
        var style = timerInfo.Style;
        var track = timerInfo.Track;
        var stage = timerInfo.Stage;

        if (!IsValidStageIndex(stage))
        {
            _logger.LogWarning("Ignore stage-finish with invalid stage index. style={style}, track={track}, stage={stage}",
                               style,
                               track,
                               stage);

            return Task.CompletedTask;
        }

        var stageRecords = _mapCache.GetStageRecords(style, track, stage);
        var wrRecord     = stageRecords is { Count: > 0 } ? stageRecords[0] : null;
        var pbRecord     = _playerCache.GetRecord(slot, style, track, stage);

        var recordRequest = CreateRecordRequest(timerInfo, _styleModule, _checkpoint.GetTeleportCount(slot));
        recordRequest.Stage = timerInfo.Stage;
        recordRequest.Mode  = KzModes.ToIndex(_modes.GetMode(slot));

        return Task.Run(async () =>
                        {
                            try
                            {
                                var (recordType, savedRecord, rank) = await _request.AddPlayerStageRecord(steamId,
                                                                                        mapName,
                                                                                        recordRequest)
                                                                                    .ConfigureAwait(false);

                                _ = rank;

                                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                                             {
                                                 var recordEvent = new PlayerRecordSavedEvent(steamId,
                                                     playerName,
                                                     recordType,
                                                     savedRecord,
                                                     wrRecord,
                                                     pbRecord,
                                                     attemptId);

                                                 NotifyRecordSavedListeners(recordEvent);

                                                 if (_bridge.ClientManager.GetGameClient(steamId) is { } currentClient)
                                                 {
                                                     _playerCache.SetStageRecord(currentClient.Slot, style, track, stage, savedRecord);
                                                 }
                                             })
                                             .ConfigureAwait(false);

                                await RefreshMapStageRecord(style, track, stage).ConfigureAwait(false);
                            }
                            catch (Exception e)
                            {
                                _logger.LogError(e, "Error when saving stage record");
                            }
                        },
                        ct);
    }

    private async Task RefreshMapRecord(int style, int track)
    {
        try
        {
            var records = await RetryHelper.RetryAsync(
                () => _request.GetMapRecords(_bridge.GlobalVars.MapName, style, track),
                RetryHelper.IsTransient, _logger, "GetMapRecords"
            ).ConfigureAwait(false);

            IReadOnlyList<RunCheckpoint>? wrCheckpoints = null;

            if (records.Count > 0)
            {
                wrCheckpoints = await RetryHelper.RetryAsync(
                    () => _request.GetRecordCheckpoints(records[0].Id),
                    RetryHelper.IsTransient, _logger, "GetRecordCheckpoints"
                ).ConfigureAwait(false);
            }

            await _bridge.ModSharp.InvokeFrameActionAsync(() =>
            {
                _mapCache.RefreshTrack(style, track, records);

                if (wrCheckpoints is not null)
                {
                    _mapCache.SetWRCheckpoints(style, track, wrCheckpoints);
                }
                else
                {
                    _mapCache.SetWRCheckpoints(style, track, []);
                }
            });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when trying to update map record with style {s}, track: {t}", style, track);
        }
    }

    private async Task RefreshMapStageRecord(int style, int track, int stage)
    {
        if (!IsValidStageIndex(stage))
        {
            _logger.LogWarning("Skip RefreshMapStageRecord with invalid stage index. style={style}, track={track}, stage={stage}",
                               style,
                               track,
                               stage);

            return;
        }

        try
        {
            var records = await RetryHelper.RetryAsync(
                () => _request.GetMapStageRecords(_bridge.GlobalVars.MapName, style, track, stage),
                RetryHelper.IsTransient, _logger, "GetMapStageRecords"
            ).ConfigureAwait(false);

            await _bridge.ModSharp.InvokeFrameActionAsync(() => { _mapCache.RefreshStage(style, track, stage, records); });
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                             "Error when trying to update map stage record with style {s}, track: {t}, stage: {st}",
                             style,
                             track,
                             stage);
        }
    }

    private void NotifyRecordSavedListeners(PlayerRecordSavedEvent recordEvent)
    {
        foreach (var listener in _listenerHub.Snapshot)
        {
            try
            {
                listener.OnRecordSaved(recordEvent);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error when calling RecordSave listener");
            }
        }
    }

    private static bool IsValidStageIndex(int stage) =>
        stage is >= 1 and < TimerConstants.MAX_STAGE;
}
