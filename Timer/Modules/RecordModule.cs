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
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Listeners;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Modules.Record;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Interfaces.Modules;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Timer;

namespace Source2Surf.Timer.Modules;

internal interface IRecordModule
{
    void RegisterListener(IRecordModuleListener listener);

    void UnregisterListener(IRecordModuleListener listener);

    int GetRankForTime(int style, int track, float time);

    RunRecord? GetPlayerRecord(PlayerSlot slot, int style, int track, int stage = 0);

    RunRecord? GetWR(int style, int track, int stage = 0);

    float? GetWRTime(int style, int track);

    int GetTotalRecordCount(int style, int track);

    IReadOnlyList<RunCheckpoint>? GetWRCheckpoints(int style, int track);

    /// <summary>
    /// Get the current session elapsed time (seconds) for a player on this map.
    /// Returns 0 if the player has no active session.
    /// </summary>
    float GetSessionTime(PlayerSlot slot);
}

internal partial class RecordModule : IModule, IGameListener, IRecordModule, ITimerModuleListener, IPlayerManagerListener
{
    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    private readonly InterfaceBridge       _bridge;
    private readonly ITimerModule          _timerModule;
    private readonly IStyleModule          _styleModule;
    private readonly IPlayerManager        _playerManager;
    private readonly ICommandManager       _commandManager;
    private readonly IRequestManager       _request;
    private readonly IMapInfoModule        _mapInfo;
    private readonly ILogger<RecordModule> _logger;

    // Sub-components
    private readonly MapRecordCache                     _mapCache;
    private readonly PlayerRecordCache                  _playerCache;
    private readonly RecordSaver                        _saver;
    private readonly TaskTracker                        _taskTracker;
    private readonly ListenerHub<IRecordModuleListener> _listenerHub;

    // Per-slot session start time (engine time when player joined this map)
    private readonly double[] _sessionStartTime = new double[64];

    // Late-resolved to avoid circular DI (ReplayRecorderModule depends on IRecordModule)
    private IReplayRecorderModule _replayRecorder = null!;

    public RecordModule(InterfaceBridge       bridge,
                        ITimerModule          timerModule,
                        IStyleModule          styleModule,
                        IPlayerManager        playerManager,
                        IRequestManager       request,
                        ICommandManager       commandManager,
                        IMapInfoModule        mapInfoModule,
                        ILogger<RecordModule> logger)
    {
        _bridge         = bridge;
        _timerModule    = timerModule;
        _styleModule    = styleModule;
        _playerManager  = playerManager;
        _request        = request;
        _commandManager = commandManager;
        _mapInfo        = mapInfoModule;
        _logger         = logger;

        _listenerHub = new ListenerHub<IRecordModuleListener>(logger);
        _mapCache    = new MapRecordCache(logger);
        _playerCache = new PlayerRecordCache(logger);
        _saver       = new RecordSaver(bridge, request, styleModule, _mapCache, _playerCache, _listenerHub, logger);
        _taskTracker = new TaskTracker(logger);
    }

    public bool Init()
    {
        _bridge.ModSharp.InstallGameListener(this);

        _timerModule.RegisterListener(this);

        _playerManager.RegisterListener(this);

        _commandManager.AddServerCommand("timer_recalc_scores", OnCommandRecalcScores);

        _commandManager.AddClientChatCommand("wr",      OnCommandWR);
        _commandManager.AddClientChatCommand("pb",      OnCommandPB);
        _commandManager.AddClientChatCommand("rank",    OnCommandRank);
        _commandManager.AddClientChatCommand("top",     OnCommandTop);
        _commandManager.AddClientChatCommand("recent",  OnCommandRecent);
        _commandManager.AddClientChatCommand("cpr",      OnCommandCpr);
        _commandManager.AddClientChatCommand("profile", OnCommandProfile);
        _commandManager.AddClientChatCommand("stats",   OnCommandProfile);
        _commandManager.AddClientChatCommand("swr",      OnCommandStageWR);
        _commandManager.AddClientChatCommand("stagewr",  OnCommandStageWR);
        _commandManager.AddClientChatCommand("btop",     OnCommandBonusTop);
        _commandManager.AddClientChatCommand("bwr",      OnCommandBonusWR);
        _commandManager.AddClientChatCommand("bpb",      OnCommandBonusPB);
        _commandManager.AddClientChatCommand("spb",      OnCommandStagePB);

#if DEBUG
        {
            _commandManager.AddServerCommand("clr_rec", OnCommandClearRecords);
        }
#endif

        return true;
    }

    public void OnPostInit(ServiceProvider provider)
    {
        _replayRecorder = provider.GetRequiredService<IReplayRecorderModule>();
    }

    public void Shutdown()
    {
        _bridge.ModSharp.RemoveGameListener(this);

        _timerModule.UnregisterListener(this);

        _playerManager.UnregisterListener(this);

        _taskTracker.DrainPendingTasks();
    }

    public void OnGameActivate()
    {
    }

    public void OnGameInit()
    {
    }

    public void OnServerActivate()
    {
        Task.Run(async () =>
        {
            try
            {
                var currentMapName = _bridge.GlobalVars.MapName;

                var records = await RetryHelper.RetryAsync(
                    () => _request.GetMapRecords(currentMapName),
                    RetryHelper.IsTransient, _logger, "GetMapRecords"
                ).ConfigureAwait(false);

                var stageRecords = await RetryHelper.RetryAsync(
                    () => _request.GetMapStageRecords(currentMapName),
                    RetryHelper.IsTransient, _logger, "GetMapStageRecords"
                ).ConfigureAwait(false);

                // Load WR checkpoints for each (style, track) combination
                var wrCheckpointMap = new Dictionary<(int style, int track), IReadOnlyList<RunCheckpoint>>();

                var wrByTrack = records.GroupBy(r => (r.Style, r.Track));

                foreach (var group in wrByTrack)
                {
                    var wr = group.OrderBy(r => r.Time).ThenBy(r => r.Id).FirstOrDefault();

                    if (wr is not null)
                    {
                        var checkpoints = await RetryHelper.RetryAsync(
                            () => _request.GetRecordCheckpoints(wr.Id),
                            RetryHelper.IsTransient, _logger, "GetRecordCheckpoints"
                        ).ConfigureAwait(false);

                        wrCheckpointMap[(wr.Style, wr.Track)] = checkpoints;
                    }
                }

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    _mapCache.Clear();
                    _mapCache.Populate(records, stageRecords);

                    foreach (var ((style, track), checkpoints) in wrCheckpointMap)
                    {
                        _mapCache.SetWRCheckpoints(style, track, checkpoints);
                    }

                    foreach (var listener in _listenerHub.Snapshot)
                    {
                        try
                        {
                            listener.OnMapRecordsLoaded();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error when calling OnMapRecordsLoaded listener");
                        }
                    }
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when loading map records on server activate");
            }
        }, _bridge.CancellationToken);
    }

    public void OnGameShutdown()
    {
        // Flush playtime for all connected players before map change
        for (var i = 0; i < 64; i++)
        {
            if (_sessionStartTime[i] <= 0)
            {
                continue;
            }

            var playerSlot = new PlayerSlot(i);

            if (_bridge.ClientManager.GetGameClient(playerSlot) is { IsFakeClient: false } client)
            {
                FlushPlayerMapStats(playerSlot, client.SteamId);
            }
            else
            {
                _sessionStartTime[i] = 0;
            }
        }

        _mapCache.Clear();
    }

    public void OnPlayerFinishMap(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        var slot = controller.PlayerSlot;

        var client = _bridge.ClientManager.GetGameClient(slot);

        if (client is null)
        {
            using var scope = _logger.BeginScope("OnPlayerFinishMap");

            _logger.LogError("player slot#{slot} has null IGameClient???", slot);

            return;
        }

        _taskTracker.Track(_saver.SaveMapRecordAsync(slot,
                                                     client.SteamId,
                                                     client.Name,
                                                     _bridge.GlobalVars.MapName,
                                                     timerInfo,
                                                     attemptId: _replayRecorder.GetAttemptId(slot),
                                                     _bridge.CancellationToken));
    }

    public void OnPlayerStageTimerFinish(IPlayerController controller,
                                         IPlayerPawn       pawn,
                                         IStageTimerInfo   timerInfo)
    {
        var slot = controller.PlayerSlot;

        var client = _bridge.ClientManager.GetGameClient(slot);

        if (client is null)
        {
            using var scope = _logger.BeginScope("OnPlayerStageTimerFinish");
            _logger.LogError("player slot#{slot} has null IGameClient???", slot);

            return;
        }

        _taskTracker.Track(_saver.SaveStageRecordAsync(slot,
                                                       client.SteamId,
                                                       client.Name,
                                                       _bridge.GlobalVars.MapName,
                                                       timerInfo,
                                                       attemptId: _replayRecorder.GetAttemptId(slot),
                                                       _bridge.CancellationToken));
    }

    public void OnClientPutInServer(PlayerSlot slot)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: true })
        {
            return;
        }

        _playerCache.Clear(slot);
        _sessionStartTime[(int)slot] = _bridge.ModSharp.EngineTime();
    }

    public void OnClientDisconnected(PlayerSlot slot)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { IsFakeClient: false } client)
        {
            return;
        }

        FlushPlayerMapStats(slot, client.SteamId);
        _sessionStartTime[(int)slot] = 0; // player left, clear session

        _playerCache.Clear(slot);
    }

    public void OnClientInfoLoaded(SteamID steamId)
    {
        var client = _bridge.ClientManager.GetGameClient(steamId);

        if (client is null || client.IsFakeClient)
        {
            return;
        }

        var mapName = _bridge.GlobalVars.MapName;

        _taskTracker.Track(Task.Run(async () =>
                                    {
                                        try
                                        {
                                            var records = await RetryHelper
                                                                .RetryAsync(() => _request.GetPlayerRecords(steamId, mapName),
                                                                            RetryHelper.IsTransient,
                                                                            _logger,
                                                                            "GetPlayerRecords").ConfigureAwait(false);

                                            var stageRecords = await RetryHelper.RetryAsync(
                                                () => _request.GetPlayerStageRecords(steamId, mapName),
                                                RetryHelper.IsTransient, _logger, "GetPlayerStageRecords"
                                            ).ConfigureAwait(false);

                                            await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                                            {
                                                if (_bridge.ClientManager.GetGameClient(steamId)
                                                    is not { } currentClient)
                                                {
                                                    return;
                                                }

                                                _playerCache.Populate(currentClient.Slot, records, stageRecords);
                                            });
                                        }
                                        catch (Exception e)
                                        {
                                            _logger.LogError(e, "Error when loading player time for {steamId}", steamId);
                                        }
                                    },
                                    _bridge.CancellationToken));
    }

    public void RegisterListener(IRecordModuleListener listener)
        => _listenerHub.Register(listener);

    public void UnregisterListener(IRecordModuleListener listener)
        => _listenerHub.Unregister(listener);

    // IRecordModule delegation to sub-components

    public int GetRankForTime(int style, int track, float time) =>
        _mapCache.GetRankForTime(style, track, time);

    public RunRecord? GetPlayerRecord(PlayerSlot slot, int style, int track, int stage = 0) =>
        _playerCache.GetRecord(slot, style, track, stage);

    public RunRecord? GetWR(int style, int track, int stage = 0) =>
        _mapCache.GetWR(style, track, stage);

    public float? GetWRTime(int style, int track) =>
        _mapCache.GetWRTime(style, track);

    public int GetTotalRecordCount(int style, int track) =>
        _mapCache.GetRecords(style, track).Count;

    public IReadOnlyList<RunCheckpoint>? GetWRCheckpoints(int style, int track) =>
        _mapCache.GetWRCheckpoints(style, track);

    public float GetSessionTime(PlayerSlot slot)
    {
        var start = _sessionStartTime[(int)slot];
        return start > 0 ? (float)(_bridge.ModSharp.EngineTime() - start) : 0f;
    }

    private void FlushPlayerMapStats(PlayerSlot slot, SteamID steamId)
    {
        var index = (int)slot;
        var start = _sessionStartTime[index];

        if (start <= 0)
        {
            return;
        }

        var delta   = (float)(_bridge.ModSharp.EngineTime() - start);
        var mapName = _bridge.GlobalVars.MapName;

        _sessionStartTime[index] = _bridge.ModSharp.EngineTime(); // reset for next session segment

        if (delta <= 0f)
        {
            return;
        }

        _taskTracker.Track(Task.Run(async () =>
        {
            try
            {
                await _request.UpdatePlayerMapStatsAsync(steamId, mapName, delta).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when flushing player map stats for {steamId}", steamId);
            }
        }, _bridge.CancellationToken));
    }

    /// <summary>
    ///     ServerCommand: timer_recalc_scores [mapname|all]
    ///     Manually trigger score recalculation. No args = current map, with arg = specified map, "all" = every map.
    /// </summary>
    private ECommandAction OnCommandRecalcScores(StringCommand arg)
    {
        // Build the style factor dictionary
        var styleCount   = _styleModule.GetStyleCount();
        var styleFactors = new Dictionary<int, double>(styleCount);

        for (var i = 0; i < styleCount; i++)
        {
            styleFactors[i] = _styleModule.GetStyleSetting(i).ScoreFactor;
        }

        var target = arg.ArgCount > 1 ? arg.GetArg(1) : _bridge.GlobalVars.MapName;
        var isAll  = string.Equals(target, "all", StringComparison.OrdinalIgnoreCase);

        Task.Run(async () =>
                 {
                     try
                     {
                         if (isAll)
                         {
                             var mapNames = await RetryHelper.RetryAsync(
                                 () => _request.GetAllMapNamesAsync(),
                                 RetryHelper.IsTransient, _logger, "GetAllMapNamesAsync"
                             ).ConfigureAwait(false);

                             var totalTracks = 0;

                             foreach (var mapName in mapNames)
                             {
                                 totalTracks += await RetryHelper.RetryAsync(
                                     () => _request.RecalculateMapScoresAsync(mapName, styleFactors),
                                     RetryHelper.IsTransient, _logger, "RecalculateMapScoresAsync"
                                 ).ConfigureAwait(false);
                             }

                             _logger
                                 .LogInformation("Triggered score recalculation for ALL maps ({mapCount} maps, {trackCount} tracks queued)",
                                                 mapNames.Count,
                                                 totalTracks);
                         }
                         else
                         {
                             var count = await RetryHelper.RetryAsync(
                                 () => _request.RecalculateMapScoresAsync(target, styleFactors),
                                 RetryHelper.IsTransient, _logger, "RecalculateMapScoresAsync"
                             ).ConfigureAwait(false);

                             _logger.LogInformation("Triggered score recalculation for map '{map}', {count} track(s) queued",
                                                    target,
                                                    count);
                         }
                     }
                     catch (Exception e)
                     {
                         _logger.LogError(e, "Error when recalculating scores");
                     }
                 },
                 _bridge.CancellationToken);

        return ECommandAction.Handled;
    }
}
