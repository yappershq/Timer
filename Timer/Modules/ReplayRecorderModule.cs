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
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Managers.Replay;
using Source2Surf.Timer.Modules.Practice;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Events;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Interfaces.Modules;
using Source2Surf.Timer.Shared.Models.Replay;
using Source2Surf.Timer.Shared.Models.Timer;

namespace Source2Surf.Timer.Modules;

internal class ReplayRecorderModule : IReplayRecorderModule,
                                      IModule,
                                      IGameListener,
                                      IRecordModuleListener,
                                      ITimerModuleListener,
                                      IPlayerManagerListener
{
    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    private const int MinValidFrames = (int) (TimerConstants.Tickrate * 0.7f);

    private readonly InterfaceBridge               _bridge;
    private readonly ITimerModule                  _timerModule;
    private readonly IRecordModule                 _recordModule;
    private readonly IReplayPlaybackModule         _playbackModule;
    private readonly IPlayerManager                _playerManager;
    private readonly ReplayProviderProxy           _replayProviderProxy;
    private readonly IMapInfoModule                _mapInfoModule;
    private readonly IPracticeModule               _practiceModule;
    private readonly ILogger<ReplayRecorderModule> _logger;

    // Player frame data array indexed by PlayerSlot
    private readonly PlayerFrameData?[] _playerFrameData;

    private readonly PendingReplayStore _pendingReplayStore = new ();

    private readonly Dictionary<ReplayMatchKey, FallbackReplayRecord> _fallbackRecords = [];

    // Auto-increment AttemptId counter; overflow wraps harmlessly.
    private int _nextAttemptId;

    private readonly string _replayDirectory;

    // ConVars (recording-related)
    // ReSharper disable InconsistentNaming
    private readonly IConVar timer_replay_prerun_time;
    private readonly IConVar timer_replay_postrun_time;
    private readonly IConVar timer_replay_stage_prerun_time;
    private readonly IConVar timer_replay_stage_postrun_time;
    private readonly IConVar timer_replay_file_compression_level;
    private readonly IConVar timer_replay_file_compression_workers;
    private readonly IConVar timer_replay_pending_timeout;
    private readonly IConVar timer_replay_fallback_ttl;

    // ReSharper restore InconsistentNaming

    public ReplayRecorderModule(InterfaceBridge               bridge,
                                ITimerModule                  timerModule,
                                IRecordModule                 recordModule,
                                IReplayPlaybackModule         playbackModule,
                                IPlayerManager                playerManager,
                                ReplayProviderProxy           replayProviderProxy,
                                IMapInfoModule                mapInfoModule,
                                IPracticeModule               practiceModule,
                                ILogger<ReplayRecorderModule> logger)
    {
        _bridge              = bridge;
        _timerModule         = timerModule;
        _recordModule        = recordModule;
        _playbackModule      = playbackModule;
        _playerManager       = playerManager;
        _replayProviderProxy = replayProviderProxy;
        _mapInfoModule       = mapInfoModule;
        _practiceModule      = practiceModule;
        _logger              = logger;

        _playerFrameData = new PlayerFrameData?[PlayerSlot.MaxPlayerCount];

        timer_replay_prerun_time
            = bridge.ConVarManager.CreateConVar("timer_replay_prerun_time", 2.0f, 2.0f, 10.0f, "Seconds of player data to record before leaving the start zone")!;

        timer_replay_postrun_time
            = bridge.ConVarManager.CreateConVar("timer_replay_postrun_time", 2.0f, 2.0f, 10.0f, "Seconds of player data to record after finishing a run")!;

        timer_replay_stage_prerun_time
            = bridge.ConVarManager.CreateConVar("timer_replay_stage_prerun_time", 2.0f, 0.0f, 10.0f, "Seconds of player data to record before leaving a stage start zone")!;

        timer_replay_stage_postrun_time
            = bridge.ConVarManager.CreateConVar("timer_replay_stage_postrun_time", 2.0f, 0.0f, 10.0f, "Seconds of player data to record after finishing a stage")!;

        timer_replay_file_compression_level
            = bridge.ConVarManager.CreateConVar("timer_replay_file_compression_level", 3, 0, 19, "Replay file compression level, 0 to disable compression")!;

        timer_replay_file_compression_workers
            = bridge.ConVarManager.CreateConVar("timer_replay_file_compression_workers", 4, 0, 256, "Number of threads for replay file compression, 0 to disable")!;

        timer_replay_pending_timeout
            = bridge.ConVarManager.CreateConVar("timer_replay_pending_timeout",
                                                30.0f,
                                                5.0f,
                                                300.0f,
                                                "Timeout in seconds for pending replay before fallback save")!;

        timer_replay_fallback_ttl
            = bridge.ConVarManager.CreateConVar("timer_replay_fallback_ttl",
                                                15.0f,
                                                1.0f,
                                                1440.0f,
                                                "Minutes a fallback replay record waits for OnRecordSaved before being discarded")
            !;

        _replayDirectory = Path.Combine(bridge.TimerDataPath, "replays");
    }

    public bool Init()
    {
        _bridge.HookManager.PlayerRunCommand.InstallHookPost(OnPlayerRunCommandPost);

        _bridge.ModSharp.InstallGameListener(this);

        _playerManager.RegisterListener(this);

        _timerModule.RegisterListener(this);

        _recordModule.RegisterListener(this);

        return true;
    }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerRunCommand.RemoveHookPost(OnPlayerRunCommandPost);

        _bridge.ModSharp.RemoveGameListener(this);

        _playerManager.UnregisterListener(this);

        _timerModule.UnregisterListener(this);

        _recordModule.UnregisterListener(this);
    }

    public void OnServerActivate()
    {
        // Restore any fallback records persisted from a previous session before doing TTL cleanup,
        // so a late OnRecordSaved after a crash/restart can still find its .tmp.
        LoadFallbackRecordsFromDisk();

        // TTL cleanup: drop entries older than timer_replay_fallback_ttl.
        ExpireFallbackRecords("map activate");

        // Orphaned temp file cleanup (>24h, no matching sidecar in-flight)
        try
        {
            if (Directory.Exists(_replayDirectory))
            {
                var cutoff = DateTime.UtcNow.AddHours(-24);

                foreach (var tmpFile in Directory.GetFiles(_replayDirectory, "*.tmp", SearchOption.AllDirectories))
                {
                    try
                    {
                        var creationTime = File.GetCreationTimeUtc(tmpFile);

                        if (creationTime < cutoff)
                        {
                            File.Delete(tmpFile);
                            DeleteFallbackSidecar(tmpFile);
                            _logger.LogInformation("Deleted orphaned temp replay file: {Path}", tmpFile);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete orphaned temp file: {Path}", tmpFile);
                    }
                }

                // Orphaned sidecar cleanup (.idx without a corresponding .tmp)
                foreach (var sidecarFile in Directory.GetFiles(_replayDirectory,
                                                               "*.tmp" + FallbackSidecarSuffix,
                                                               SearchOption.AllDirectories))
                {
                    try
                    {
                        var tmpPath = sidecarFile[..^FallbackSidecarSuffix.Length];

                        if (!File.Exists(tmpPath))
                        {
                            File.Delete(sidecarFile);
                            _logger.LogInformation("Deleted orphaned fallback sidecar: {Path}", sidecarFile);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete orphaned sidecar: {Path}", sidecarFile);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to scan for orphaned temp replay files in {Dir}", _replayDirectory);
        }
    }

    public void OnGameShutdown()
    {
        var allPending = _pendingReplayStore.TakeAll();

        foreach (var (key, pending) in allPending)
        {
            SavePendingReplayAsFallback(key, pending);
        }
    }

    public int GetAttemptId(PlayerSlot slot) =>
        _playerFrameData[slot]?.AttemptId ?? 0;

    public void OnClientPutInServer(PlayerSlot slot)
    {
        var client = _bridge.ClientManager.GetGameClient(slot);

        if (client is null)
        {
            return;
        }

        if (client.IsFakeClient)
        {
            return;
        }

        var data = new PlayerFrameData
        {
            Frames  = new List<ReplayFrameData>(TimerConstants.Tickrate * 60 * 5),
            SteamId = client.SteamId,
            Name    = client.Name,
        };

        _playerFrameData[slot] = data;
    }

    public void OnClientDisconnected(PlayerSlot slot)
    {
        var client = _bridge.ClientManager.GetGameClient(slot);

        if (client is not null && client.IsFakeClient)
        {
            return;
        }

        if (_playerFrameData[slot] is not { } frame)
        {
            return;
        }

        if (frame.StagePostFrameTimer is { } stagePostFrameTimer)
        {
            _bridge.ModSharp.StopTimer(stagePostFrameTimer);
            frame.StagePostFrameTimer = null;
        }

        if (frame.PostFrameTimer is { } postFrameTimer)
        {
            _bridge.ModSharp.StopTimer(postFrameTimer);
            frame.PostFrameTimer = null;
        }

        _playerFrameData[slot] = null;
    }

    private bool TryGetFrameData(PlayerSlot slot, [NotNullWhen(true)] out PlayerFrameData? frameData)
    {
        frameData = _playerFrameData[slot];

        return frameData is not null;
    }

    private bool TryGetStageStartTick(PlayerFrameData frameData, int stageIndex, out int startTick)
    {
        if (stageIndex < frameData.StageTimerStartTicks.Count)
        {
            startTick = frameData.StageTimerStartTicks[stageIndex];

            return true;
        }

        startTick = 0;

        _logger.LogWarning("Stage start tick missing for stage index {StageIndex}. Current count: {Count}",
                           stageIndex,
                           frameData.StageTimerStartTicks.Count);

        return false;
    }

    private void SetStageTimerStart(PlayerFrameData frameData, int stageIndex, int currentFrame, int stageNumber)
    {
        var ticksList = frameData.StageTimerStartTicks;
        var count     = ticksList.Count;

        if (count == stageIndex)
        {
            ticksList.Add(currentFrame);

            return;
        }

        if (stageIndex < count)
        {
            ticksList[stageIndex] = currentFrame;

            return;
        }

        _logger.LogError("Attempted to add CurrentFrame to StageTimerStartTick for stage {Stage} (index {Index}) "
                         + "when current stage count is {Count}. Probable logic error elsewhere.",
                         stageNumber,
                         stageIndex,
                         count);
    }

    public void OnPlayerTimerStart(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        var slot = controller.PlayerSlot;

        if (!TryGetFrameData(slot, out var frameData))
        {
            return;
        }

        // StopTimer triggers ForceCallOnStop → snapshot creation.
        // Flush BOTH pending post-frame timers before bumping AttemptId and trimming frames:
        // their forced callbacks build their snapshots from the current frame state under the
        // still-correct (finish-time) AttemptId. If we bumped first, a stage snapshot flushed
        // afterwards would key on the new AttemptId and never match its OnRecordSaved event.
        if (frameData.StagePostFrameTimer is { } stagePostFrameTimer)
        {
            _bridge.ModSharp.StopTimer(stagePostFrameTimer);
            frameData.StagePostFrameTimer = null;
        }

        if (frameData.PostFrameTimer is { } postFrameTimer)
        {
            _bridge.ModSharp.StopTimer(postFrameTimer);
            frameData.PostFrameTimer = null;
        }

        frameData.AttemptId = _nextAttemptId++;

        frameData.PendingMainRecordResult = null;
        frameData.PendingStageRecordResults.Clear();

        var maxPreFrame = (int) (timer_replay_prerun_time.GetFloat() * TimerConstants.Tickrate);
        ReplayShared.TrimPreRunFrames(frameData, maxPreFrame);

        frameData.NewStageTicks.Clear();
        frameData.StageTimerStartTicks.Clear();

        frameData.TimerStartFrame = frameData.Frames.Count;
    }

    public void OnPlayerStageTimerStart(IPlayerController controller,
                                        IPlayerPawn       pawn,
                                        IStageTimerInfo   stageTimerInfo)
    {
        var slot = controller.PlayerSlot;

        if (!TryGetFrameData(slot, out var frameData))
        {
            return;
        }

        var stage = stageTimerInfo.Stage;
        var idx   = stage - 1;

        SetStageTimerStart(frameData, idx, frameData.Frames.Count, stage);
    }

    public void OnPlayerStageTimerFinish(IPlayerController controller,
                                         IPlayerPawn       pawn,
                                         IStageTimerInfo   stageTimerInfo)
    {
        var slot = controller.PlayerSlot;

        if (!TryGetFrameData(slot, out var frame))
        {
            return;
        }

        // Practice run — RecordModule won't persist anything, so a stage replay
        // would never get matched and would eventually fall back to disk. Drop
        // it on the floor.
        if (_practiceModule.IsInPractice(slot))
        {
            return;
        }

        frame.NewStageTicks.Add(frame.Frames.Count);

        frame.Name = controller.PlayerName;
        var finishedStage = stageTimerInfo.Stage;

        // Capture the AttemptId NOW, at finish time — this is the value RecordModule.GetAttemptId
        // reads synchronously and embeds in the eventual record event. The post-frame timer that
        // builds the snapshot fires later, by which point a new run may have bumped frame.AttemptId.
        var attemptId = frame.AttemptId;

        // Same reasoning for style/track — the replay must be keyed/pathed with the stage
        // run's own style/track, matching what RecordModule embeds in the record event.
        var style = stageTimerInfo.Style;
        var track = stageTimerInfo.Track;

        var lastStage = finishedStage - 1;

        if (!TryGetStageStartTick(frame, lastStage, out var timerStartTick))
        {
            return;
        }

        var time = stageTimerInfo.Time;

        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            var newStageTicks = frame.NewStageTicks[lastStage];

            var delay              = timer_replay_stage_postrun_time.GetFloat();
            var postRunFrameLength = (int) (TimerConstants.Tickrate * delay);
            var preRunFrameLength  = (int) (TimerConstants.Tickrate * timer_replay_stage_prerun_time.GetFloat());

            if (frame.StagePostFrameTimer is { } stageReplayTimer)
            {
                // we have ForceCallOnStop flag which forces firing the callback
                _bridge.ModSharp.StopTimer(stageReplayTimer);
            }

            frame.StagePostFrameTimer = _bridge.ModSharp.PushTimer(() =>
                                                                   {
                                                                       frame.StagePostFrameTimer = null;

                                                                       var startTick = Math.Max(0,
                                                                           timerStartTick - preRunFrameLength);

                                                                       var snapshot = ReplayShared.CreateStageReplaySnapshot(frame,
                                                                           startTick,
                                                                           timerStartTick,
                                                                           newStageTicks,
                                                                           postRunFrameLength,
                                                                           time);

                                                                       StorePendingReplay(frame, snapshot, finishedStage, attemptId,
                                                                           style, track);

                                                                       return TimerAction.Stop;
                                                                   },
                                                                   delay,
                                                                   GameTimerFlags.StopOnMapEnd
                                                                   | GameTimerFlags.ForceCallOnStop);
        });
    }

    public void OnPlayerFinishMap(IPlayerController controller,
                                  IPlayerPawn       pawn,
                                  ITimerInfo        timerInfo)
    {
        var slot = controller.PlayerSlot;

        if (!TryGetFrameData(slot, out var frame))
        {
            return;
        }

        // Practice run — RecordModule will short-circuit too. Skip the post-run
        // timer entirely so we never create a snapshot that has no record to
        // pair with (which would land in PendingReplayStore → fallback file).
        if (_practiceModule.IsInPractice(slot))
        {
            return;
        }

        frame.Name             = controller.PlayerName;
        frame.TimerFinishFrame = frame.Frames.Count;
        frame.FinishTime       = timerInfo.Time;

        // Capture the finish-time AttemptId/style/track for the snapshot's match key
        // (see OnPlayerStageTimerFinish) — the post-frame timer fires later.
        var attemptId = frame.AttemptId;
        var style     = timerInfo.Style;
        var track     = timerInfo.Track;

        frame.PostFrameTimer = _bridge.ModSharp.PushTimer(() =>
                                                          {
                                                              frame.PostFrameTimer = null;

                                                              if (frame.StagePostFrameTimer is { } stagePostFrameTimer)
                                                              {
                                                                  _bridge.ModSharp.StopTimer(stagePostFrameTimer);
                                                              }

                                                              frame.StagePostFrameTimer = null;

                                                              var snapshot = ReplayShared.CreateMainReplaySnapshot(frame);

                                                              StorePendingReplay(frame, snapshot, stage: 0, attemptId,
                                                                                 style, track);

                                                              return TimerAction.Stop;
                                                          },
                                                          timer_replay_postrun_time.GetFloat(),
                                                          GameTimerFlags.StopOnMapEnd | GameTimerFlags.ForceCallOnStop);
    }

    public void OnRecordSaved(PlayerRecordSavedEvent recordEvent)
    {
        var key = new ReplayMatchKey(recordEvent.MapId,
                                     recordEvent.SteamId.AsPrimitive(),
                                     recordEvent.Style,
                                     recordEvent.Track,
                                     recordEvent.Stage,
                                     recordEvent.AttemptId);

        var runId = recordEvent.SavedRecord.Id;

        // 1. Try PendingReplayStore first.
        var pending = _pendingReplayStore.TakeMatch(key);

        if (pending is not null)
        {
            ProcessPendingReplay(pending, key, runId, recordEvent);

            return;
        }

        // 2. Try _fallbackRecords.
        if (_fallbackRecords.Remove(key, out var fallback))
        {
            ProcessFallbackRecord(fallback, key, runId, recordEvent);

            return;
        }

        // 3. Record arrived before post-frame ended — store into PlayerFrameData.
        var client = _bridge.ClientManager.GetGameClient(recordEvent.SteamId);

        if (client is null)
        {
            return;
        }

        var slot = client.Slot;

        if (!TryGetFrameData(slot, out var frameData))
        {
            return;
        }

        if (frameData.AttemptId != recordEvent.AttemptId)
        {
            return;
        }

        var result = new PendingRecordResult { RunId = runId, RecordEvent = recordEvent };

        if (recordEvent.IsStageRecord)
        {
            frameData.PendingStageRecordResults[recordEvent.Stage] = result;
        }
        else
        {
            frameData.PendingMainRecordResult = result;
        }
    }

    /// <summary>
    ///     Validates and stores a replay snapshot. If the matching record save already
    ///     arrived (PendingMainRecordResult / PendingStageRecordResults), the replay is
    ///     processed directly. Otherwise it lands in PendingReplayStore with a timeout
    ///     timer that falls back to disk if the record save never arrives.
    /// </summary>
    private void StorePendingReplay(PlayerFrameData frame, ReplaySaveSnapshot snapshot, int stage, int attemptId,
                                    int             style, int                track)
    {
        var isStage = stage > 0;

        if (snapshot.Frames.Count < MinValidFrames)
        {
            if (isStage)
            {
                _logger.LogDebug("Discarding stage replay snapshot for {SteamId} style={Style} track={Track} stage={Stage}: "
                                 + "only {FrameCount} frames (min {MinFrames})",
                                 frame.SteamId, style, track, stage, snapshot.Frames.Count, MinValidFrames);
            }
            else
            {
                _logger.LogDebug("Discarding main replay snapshot for {SteamId} style={Style} track={Track}: "
                                 + "only {FrameCount} frames (min {MinFrames})",
                                 frame.SteamId, style, track, snapshot.Frames.Count, MinValidFrames);
            }

            return;
        }

        // Temp file in same directory as final → atomic same-partition File.Move
        var mapName = _bridge.CurrentMapName;

        var tempPath = ReplayShared.BuildReplayPath(_replayDirectory, mapName, style, track, stage, null);

        tempPath = Path.ChangeExtension(tempPath, ".tmp");

        var mapId = _mapInfoModule.GetCurrentMapProfile().MapId;

        var key = new ReplayMatchKey(mapId,
                                     frame.SteamId.AsPrimitive(),
                                     style,
                                     track,
                                     stage,
                                     attemptId);

        // Record arrived before post-frame ended — process directly.
        PendingRecordResult? pendingResult;

        if (isStage)
        {
            frame.PendingStageRecordResults.Remove(stage, out pendingResult);
        }
        else
        {
            pendingResult = frame.PendingMainRecordResult;

            if (pendingResult is not null)
            {
                frame.PendingMainRecordResult = null;
            }
        }

        if (pendingResult is not null)
        {
            var pending = new PendingReplay { Snapshot = snapshot, TempFilePath = tempPath, MapName = mapName };

            ProcessPendingReplay(pending, key, pendingResult.RunId, pendingResult.RecordEvent);

            return;
        }

        var pendingReplay = new PendingReplay { Snapshot = snapshot, TempFilePath = tempPath, MapName = mapName };

        var replaced = _pendingReplayStore.Add(key, pendingReplay);

        if (replaced is not null)
        {
            _logger.LogWarning(isStage
                                   ? "Replaced existing stage PendingReplay for key {Key}"
                                   : "Replaced existing PendingReplay for key {Key}",
                               key);

            SavePendingReplayAsFallback(key, replaced);
        }

        var timeoutSeconds = timer_replay_pending_timeout.GetFloat();
        var capturedKey    = key;

        var timerId = _bridge.ModSharp.PushTimer(() =>
                                                 {
                                                     var timedOut = _pendingReplayStore.TakeMatch(capturedKey);

                                                     if (timedOut is null)
                                                         return TimerAction.Stop;

                                                     timedOut.TimeoutTimerId = null;
#if DEBUG

                                                     if (isStage)
                                                     {
                                                         _logger
                                                             .LogWarning("Pending stage replay timed out for {SteamId} style={Style} track={Track} stage={Stage} attemptId={AttemptId}",
                                                                         capturedKey.SteamId,
                                                                         capturedKey.Style,
                                                                         capturedKey.Track,
                                                                         capturedKey.Stage,
                                                                         capturedKey.AttemptId);
                                                     }
                                                     else
                                                     {
                                                         _logger
                                                             .LogWarning("Pending replay timed out for {SteamId} style={Style} track={Track} attemptId={AttemptId}",
                                                                         capturedKey.SteamId,
                                                                         capturedKey.Style,
                                                                         capturedKey.Track,
                                                                         capturedKey.AttemptId);
                                                     }

#endif
                                                     SavePendingReplayAsFallback(capturedKey, timedOut);

                                                     return TimerAction.Stop;
                                                 },
                                                 timeoutSeconds,
                                                 GameTimerFlags.StopOnMapEnd);

        pendingReplay.TimeoutTimerId = timerId;
    }

    /// <summary>
    ///     Consumes a PendingReplay: cancels timeout timer, builds final path, delegates to WriteReplayToDiskAndNotify.
    /// </summary>
    private void ProcessPendingReplay(PendingReplay pending, ReplayMatchKey key, long runId, PlayerRecordSavedEvent recordEvent)
    {
        if (pending.TimeoutTimerId is { } timerId && _bridge.ModSharp.IsValidTimer(timerId))
        {
            _bridge.ModSharp.StopTimer(timerId);
        }

        pending.TimeoutTimerId = null;

        // Use the map name captured at record time, NOT the live GlobalVars.MapName: a record-saved
        // event can arrive after the map has changed, and the final path must match the recorded map.
        var filePath = ReplayShared.BuildReplayPath(_replayDirectory, pending.MapName, key.Style, key.Track, key.Stage, runId);

        var context = new ReplaySaveContext
        {
            SteamId       = recordEvent.SteamId.AsPrimitive(),
            FinishTime    = pending.Snapshot.Header.Time,
            AttemptResult = recordEvent.RecordType,
        };

        // Pass the captured map name (same one used to build filePath) so the upload targets the map
        // the run belongs to, not the live map — a record-saved event can land after a map change.
        WriteReplayToDiskAndNotify(pending.Snapshot, filePath, pending.MapName, context, key.Style, key.Track, key.Stage, runId);
    }

    /// <summary>
    ///     Serializes snapshot, writes to disk, notifies Playback, and handles upload.
    ///     Runs I/O in Task.Run. On failure, fallback-notifies with NoNewRecord.
    /// </summary>
    private void WriteReplayToDiskAndNotify(ReplaySaveSnapshot snapshot,
                                            string             filePath,
                                            string             mapName,
                                            ReplaySaveContext  context,
                                            int                style,
                                            int                track,
                                            int                stage,
                                            long?              runId)
    {
        var header = snapshot.Header;
        var frames = snapshot.Frames;

        var compressionLevel   = timer_replay_file_compression_level.GetInt32();
        var compressionWorkers = timer_replay_file_compression_workers.GetInt32();

        Task.Run(async () =>
        {
            try
            {
                if (!await ReplayShared.WriteReplayToFileAsync(header,
                                                               filePath,
                                                               frames,
                                                               compressionLevel,
                                                               compressionWorkers,
                                                               _logger)
                                       .ConfigureAwait(false))
                {
                    throw new IOException($"Failed to write replay to {filePath}");
                }

                var replayContent = new ReplayContent { Header = header, Frames = frames };

                var providerReady = _replayProviderProxy.IsAvailable;
                var uploadNonPB   = _replayProviderProxy.UploadNonPersonalBest;

                var isNewBest    = false;
                var shouldUpload = false;

                // Evaluate the playback notify on the game main thread (it touches bot state).
                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    isNewBest    = NotifyPlaybackSaved(_playbackModule, style, track, stage, replayContent, context);
                    shouldUpload = providerReady && (uploadNonPB || isNewBest);
                }).ConfigureAwait(false);

#if DEBUG
                _logger.LogInformation(
                    "Replay upload gate: steamId={SteamId} style={Style} track={Track} stage={Stage} time={Time:F3} "
                  + "hasRunId={HasRunId} providerAvailable={ProviderReady} uploadNonPB={UploadNonPB} "
                  + "isNewBest={IsNewBest} shouldUpload={ShouldUpload}",
                    header.SteamId, style, track, stage, header.Time,
                    runId.HasValue, providerReady, uploadNonPB, isNewBest, shouldUpload);
#endif

                if (runId is { } savedRunId
                    && providerReady
                    && shouldUpload)
                {
                    try
                    {
                        var replayBytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);

#if DEBUG
                        _logger.LogInformation(
                            "Uploading replay: map={Map} style={Style} track={Track} stage={Stage} steamId={SteamId} runId={RunId} bytes={Bytes}",
                            mapName, style, track, stage, header.SteamId, savedRunId, replayBytes.Length);
#endif

                        await UploadReplayAsync(_replayProviderProxy, mapName, style, track, stage,
                                                header.SteamId, (ulong) savedRunId, replayBytes).ConfigureAwait(false);

#if DEBUG
                        _logger.LogInformation(
                            "Uploaded replay: map={Map} style={Style} track={Track} stage={Stage} runId={RunId}",
                            mapName, style, track, stage, savedRunId);
#endif
                    }
                    catch (Exception uploadEx)
                    {
                        _logger.LogError(uploadEx,
                                         "Failed to upload replay remotely for style={Style} track={Track} stage={Stage}",
                                         style,
                                         track,
                                         stage);
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                                 "Failed to write replay to {Path} for style={Style} track={Track} stage={Stage}",
                                 filePath,
                                 style,
                                 track,
                                 stage);

                var fallbackContext = context with { AttemptResult = EAttemptResult.NoNewRecord };

                var fallbackContent = new ReplayContent { Header = header, Frames = frames };

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    NotifyPlaybackSaved(_playbackModule, style, track, stage, fallbackContent, fallbackContext);
                }).ConfigureAwait(false);
            }
        });
    }

    private void SavePendingReplayAsFallback(ReplayMatchKey key, PendingReplay pending)
    {
        if (pending.TimeoutTimerId is { } timerId)
        {
            try
            {
                _bridge.ModSharp.StopTimer(timerId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "StopTimer for timeout timer {TimerId} failed (likely already cleaned)", timerId);
            }
        }

        pending.TimeoutTimerId = null;

        var snapshot           = pending.Snapshot;
        var header             = snapshot.Header;
        var frames             = snapshot.Frames;
        var tempPath           = pending.TempFilePath;
        var mapName            = pending.MapName;
        var style              = key.Style;
        var track              = key.Track;
        var stage              = key.Stage;
        var steamId            = key.SteamId;
        var compressionLevel   = timer_replay_file_compression_level.GetInt32();
        var compressionWorkers = timer_replay_file_compression_workers.GetInt32();

        var createdAt = DateTime.UtcNow;

        // Write the sidecar synchronously so a late OnRecordSaved after a process restart
        // can reassociate the .tmp file with this ReplayMatchKey.
        WriteFallbackSidecar(tempPath, key, mapName, createdAt);

        var writeTask = Task.Run(async () =>
        {
            try
            {
                if (!await ReplayShared.WriteReplayToFileAsync(header,
                                                               tempPath,
                                                               frames,
                                                               compressionLevel,
                                                               compressionWorkers,
                                                               _logger)
                                       .ConfigureAwait(false))
                {
                    throw new IOException($"Failed to write fallback replay to {tempPath}");
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                                 "Failed to write fallback replay to {Path} for {SteamId} style={Style} track={Track} stage={Stage}",
                                 tempPath,
                                 steamId,
                                 style,
                                 track,
                                 stage);
            }
        });

        var fallbackContext = new ReplaySaveContext
        {
            SteamId       = steamId,
            FinishTime    = header.Time,
            AttemptResult = EAttemptResult.NoNewRecord,
        };

        var fallbackContent = new ReplayContent
        {
            Header = header,
            Frames = frames,
        };

        _ = _bridge.ModSharp.InvokeFrameActionAsync(() =>
        {
            NotifyPlaybackSaved(_playbackModule, style, track, stage, fallbackContent, fallbackContext);
        });

        // If a fallback record already exists for this exact key, its temp file has a different
        // Guid name (fallback paths are Guid-suffixed), so the blind overwrite below would orphan
        // the prior .tmp/.idx. Clean it up first (guarding against the same path).
        if (_fallbackRecords.TryGetValue(key, out var priorRecord)
            && !string.Equals(priorRecord.TempFilePath, tempPath, StringComparison.Ordinal))
        {
            DeleteFallbackTempAndSidecar(priorRecord.TempFilePath);
        }

        _fallbackRecords[key] = new FallbackReplayRecord
        {
            TempFilePath = tempPath,
            MapName      = mapName,
            WriteTask    = writeTask,
            CreatedAt    = createdAt,
        };

#if DEBUG
        _logger.LogWarning("Fallback-saved pending replay for {SteamId} style={Style} track={Track} stage={Stage} attemptId={AttemptId} to {Path}",
                           steamId,
                           style,
                           track,
                           stage,
                           key.AttemptId,
                           tempPath);
#endif

        // Expire old entries (> timer_replay_fallback_ttl)
        ExpireFallbackRecords("fallback save");
    }

    /// <summary>
    ///     Processes a late OnRecordSaved for a fallback-saved replay:
    ///     await WriteTask → File.Move → re-notify Playback → upload.
    /// </summary>
    private void ProcessFallbackRecord(FallbackReplayRecord   fallback,
                                       ReplayMatchKey         key,
                                       long                   runId,
                                       PlayerRecordSavedEvent recordEvent)
    {
        var tempPath  = fallback.TempFilePath;
        var writeTask = fallback.WriteTask;
        var style     = key.Style;
        var track     = key.Track;
        var stage     = key.Stage;
        var steamId   = key.SteamId;
        var attemptId = key.AttemptId;
        var mapId     = key.MapId;
        var createdAt = fallback.CreatedAt;

        // Use the map name captured at record time, NOT the live GlobalVars.MapName: a late
        // record-saved event can arrive after a map change, and the file must be written/uploaded
        // under the map the run actually belongs to.
        var mapName = fallback.MapName;

        var finalPath = ReplayShared.BuildReplayPath(_replayDirectory, mapName, style, track, stage, runId);

        var replayProviderProxy = _replayProviderProxy;
        var playbackModule      = _playbackModule;
        var bridge              = _bridge;
        var logger              = _logger;
        var attemptResult       = recordEvent.RecordType;
        var recordSteamId       = recordEvent.SteamId.AsPrimitive();

        Task.Run(async () =>
        {
            try
            {
                await writeTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Fallback WriteTask timed out (10s) for {SteamId} style={Style} track={Track} stage={Stage} attemptId={AttemptId}. Re-queuing for later retry",
                                  steamId,
                                  style,
                                  track,
                                  stage,
                                  attemptId);

                var reinsertKey = new ReplayMatchKey(mapId, steamId, style, track, stage, attemptId);

                var reinsertRecord = new FallbackReplayRecord
                {
                    TempFilePath = tempPath,
                    MapName      = mapName,
                    WriteTask    = writeTask,
                    CreatedAt    = createdAt,
                };

                _ = bridge.ModSharp.InvokeFrameActionAsync(() => { _fallbackRecords[reinsertKey] = reinsertRecord; });

                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                                "Fallback WriteTask faulted for {SteamId} style={Style} track={Track} stage={Stage} attemptId={AttemptId}. Aborting rename and upload",
                                steamId,
                                style,
                                track,
                                stage,
                                attemptId);

                return;
            }

            // Read and validate the .tmp BEFORE promoting it. A process crash mid-write — or an
            // interrupted write restored from disk with WriteTask=CompletedTask — can leave a
            // truncated/corrupt .tmp. Deserializing here lets us discard garbage instead of
            // File.Move-ing it onto the canonical replay path (and uploading it).
            byte[] replayBytes;

            try
            {
                replayBytes = await RetryOnIOException(() => File.ReadAllBytesAsync(tempPath),
                                                       logger,
                                                       "File.ReadAllBytes",
                                                       tempPath).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                logger.LogError(ex,
                                "Failed to read fallback temp after retries for {SteamId} style={Style} track={Track} stage={Stage}: {TempPath}",
                                steamId,
                                style,
                                track,
                                stage,
                                tempPath);

                return;
            }

            if (ReplayShared.DeserializeReplay(replayBytes, style, track, stage, logger) is not { } loaded)
            {
                logger.LogError("Fallback temp replay is corrupt/truncated; discarding instead of promoting for {SteamId} style={Style} track={Track} stage={Stage}: {TempPath}",
                                steamId,
                                style,
                                track,
                                stage,
                                tempPath);

                DeleteFallbackTempAndSidecar(tempPath);

                return;
            }

            var deserialized = loaded.Content;

            try
            {
                await RetryOnIOException(() =>
                                         {
                                             File.Move(tempPath, finalPath, true);

                                             return Task.CompletedTask;
                                         },
                                         logger,
                                         "File.Move",
                                         tempPath).ConfigureAwait(false);

                DeleteFallbackSidecar(tempPath);
            }
            catch (IOException ex)
            {
                logger.LogError(ex,
                                "File.Move failed after retries for {SteamId} style={Style} track={Track} stage={Stage}: {TempPath} → {FinalPath}",
                                steamId,
                                style,
                                track,
                                stage,
                                tempPath,
                                finalPath);

                return;
            }

            var context = new ReplaySaveContext
            {
                SteamId       = recordSteamId,
                FinishTime    = recordEvent.Time,
                AttemptResult = attemptResult,
            };

            // Derive isNewBest from the record event's authoritative RecordType — NOT from the
            // OnNew*ReplaySaved return value. SavePendingReplayAsFallback already eagerly notified
            // playback for this run, so the cache holds this run's time and the second notify here
            // returns false (existing.Time <= this run's time), which would wrongly suppress the
            // upload of a genuine new PB/WR that happened to take the fallback path.
            var isNewBest = attemptResult is EAttemptResult.NewPersonalRecord or EAttemptResult.NewServerRecord;

            if (deserialized is { } content)
            {
                await bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    NotifyPlaybackSaved(playbackModule, style, track, stage, content, context);
                }).ConfigureAwait(false);
            }

            var providerReady = replayProviderProxy.IsAvailable;
            var uploadNonPB   = replayProviderProxy.UploadNonPersonalBest;

#if DEBUG
            logger.LogInformation(
                "Fallback replay upload gate: steamId={SteamId} style={Style} track={Track} stage={Stage} time={Time:F3} "
              + "hasBytes={HasBytes} providerAvailable={ProviderReady} uploadNonPB={UploadNonPB} isNewBest={IsNewBest}",
                recordSteamId, style, track, stage, recordEvent.Time,
                replayBytes is not null, providerReady, uploadNonPB, isNewBest);
#endif

            if (replayBytes is not null
                && providerReady)
            {
                var shouldUpload = uploadNonPB || isNewBest;

#if DEBUG
                logger.LogInformation(
                    "Fallback replay shouldUpload={ShouldUpload} for steamId={SteamId} style={Style} track={Track} stage={Stage}",
                    shouldUpload, recordSteamId, style, track, stage);
#endif

                if (shouldUpload)
                {
                    try
                    {
#if DEBUG
                        logger.LogInformation(
                            "Uploading fallback replay: map={Map} style={Style} track={Track} stage={Stage} steamId={SteamId} runId={RunId} bytes={Bytes}",
                            mapName, style, track, stage, recordSteamId, runId, replayBytes.Length);
#endif

                        await UploadReplayAsync(replayProviderProxy, mapName, style, track, stage,
                                                recordSteamId, (ulong) runId, replayBytes).ConfigureAwait(false);

#if DEBUG
                        logger.LogInformation(
                            "Uploaded fallback replay: map={Map} style={Style} track={Track} stage={Stage} runId={RunId}",
                            mapName, style, track, stage, runId);
#endif
                    }
                    catch (Exception uploadEx)
                    {
                        logger.LogError(uploadEx,
                                        "Failed to upload fallback replay remotely for style={Style} track={Track} stage={Stage}",
                                        style,
                                        track,
                                        stage);
                    }
                }
            }
#if DEBUG
            logger.LogInformation("Successfully processed fallback record for {SteamId} style={Style} track={Track} stage={Stage} attemptId={AttemptId}: renamed {TempPath} → {FinalPath}",
                                  steamId,
                                  style,
                                  track,
                                  stage,
                                  attemptId,
                                  tempPath,
                                  finalPath);
#endif
        });
    }

    private static Task RetryOnIOException(Func<Task> action, ILogger logger, string operationName, string path)
        => RetryOnIOException<object?>(async () =>
                                       {
                                           await action().ConfigureAwait(false);

                                           return null;
                                       },
                                       logger,
                                       operationName,
                                       path);

    private static async Task<T> RetryOnIOException<T>(Func<Task<T>> action, ILogger logger, string operationName, string path)
    {
        const int maxRetries = 3;
        const int delayMs    = 100;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (IOException) when (attempt < maxRetries)
            {
                logger.LogWarning("{Operation} failed on attempt {Attempt}/{MaxRetries} for {Path}, retrying in {Delay}ms",
                                  operationName,
                                  attempt + 1,
                                  maxRetries,
                                  path,
                                  delayMs);

                await Task.Delay(delayMs).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Unreachable");
    }

    private const string FallbackSidecarSuffix = ".idx";

    private sealed class FallbackSidecarDto
    {
        public ulong    MapId     { get; init; }
        public string   MapName   { get; init; } = string.Empty;
        public ulong    SteamId   { get; init; }
        public int      Style     { get; init; }
        public int      Track     { get; init; }
        public int      Stage     { get; init; }
        public int      AttemptId { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private void WriteFallbackSidecar(string tempPath, ReplayMatchKey key, string mapName, DateTime createdAt)
    {
        var dto = new FallbackSidecarDto
        {
            MapId     = key.MapId,
            MapName   = mapName,
            SteamId   = key.SteamId,
            Style     = key.Style,
            Track     = key.Track,
            Stage     = key.Stage,
            AttemptId = key.AttemptId,
            CreatedAt = createdAt,
        };

        try
        {
            File.WriteAllText(tempPath + FallbackSidecarSuffix, JsonSerializer.Serialize(dto));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to write fallback sidecar at {Path}", tempPath + FallbackSidecarSuffix);
        }
    }

    private void DeleteFallbackSidecar(string tempPath)
    {
        var sidecarPath = tempPath + FallbackSidecarSuffix;

        try
        {
            if (File.Exists(sidecarPath))
            {
                File.Delete(sidecarPath);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to delete fallback sidecar at {Path}", sidecarPath);
        }
    }

    /// <summary>
    ///     Delete both the fallback temp replay file and its sidecar.
    /// </summary>
    /// <summary>
    ///     Fans a saved replay out to the playback module — main (stage == 0) or stage
    ///     variant. Must run on the game main thread (it touches bot state).
    /// </summary>
    private static bool NotifyPlaybackSaved(IReplayPlaybackModule playbackModule,
                                            int                   style,
                                            int                   track,
                                            int                   stage,
                                            ReplayContent         content,
                                            ReplaySaveContext     context)
        => stage == 0
            ? playbackModule.OnNewMainReplaySaved(style, track, content, context)
            : playbackModule.OnNewStageReplaySaved(style, track, stage, content, context);

    private static Task UploadReplayAsync(ReplayProviderProxy proxy,
                                          string              mapName,
                                          int                 style,
                                          int                 track,
                                          int                 stage,
                                          ulong               steamId,
                                          ulong               runId,
                                          byte[]              replayBytes)
        => stage == 0
            ? proxy.UploadReplayAsync(mapName, style, track, steamId, runId, replayBytes)
            : proxy.UploadStageReplayAsync(mapName, style, track, stage, steamId, runId, replayBytes);

    /// <summary>
    ///     Removes fallback records older than timer_replay_fallback_ttl, deleting their
    ///     temp replay + sidecar files (leaving the .tmp would leak multi-MB files until
    ///     the 24h orphan sweep).
    /// </summary>
    private void ExpireFallbackRecords(string reason)
    {
        var ttlMinutes   = timer_replay_fallback_ttl.GetFloat();
        var expiryCutoff = DateTime.UtcNow.AddMinutes(-ttlMinutes);

        List<ReplayMatchKey>? expiredKeys = null;

        foreach (var (key, record) in _fallbackRecords)
        {
            if (record.CreatedAt < expiryCutoff)
            {
                expiredKeys ??= [];
                expiredKeys.Add(key);
            }
        }

        if (expiredKeys is null)
        {
            return;
        }

        foreach (var key in expiredKeys)
        {
            if (_fallbackRecords.Remove(key, out var record))
            {
                DeleteFallbackTempAndSidecar(record.TempFilePath);
            }

            _logger.LogWarning("Removed expired fallback record ({Reason}) for {SteamId} style={Style} track={Track} stage={Stage} attemptId={AttemptId} (TTL {Ttl}m)",
                               reason,
                               key.SteamId,
                               key.Style,
                               key.Track,
                               key.Stage,
                               key.AttemptId,
                               ttlMinutes);
        }
    }

    private void DeleteFallbackTempAndSidecar(string tempPath)
    {
        DeleteFallbackSidecar(tempPath);

        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to delete fallback temp file at {Path}", tempPath);
        }
    }

    private void LoadFallbackRecordsFromDisk()
    {
        if (!Directory.Exists(_replayDirectory))
        {
            return;
        }

        var ttlMinutes = timer_replay_fallback_ttl.GetFloat();
        var cutoff     = DateTime.UtcNow.AddMinutes(-ttlMinutes);
        var loaded     = 0;

        foreach (var sidecarPath in Directory.GetFiles(_replayDirectory,
                                                       "*.tmp" + FallbackSidecarSuffix,
                                                       SearchOption.AllDirectories))
        {
            var tempPath = sidecarPath[..^FallbackSidecarSuffix.Length];

            try
            {
                var json = File.ReadAllText(sidecarPath);
                var dto  = JsonSerializer.Deserialize<FallbackSidecarDto>(json);

                if (dto is null || !File.Exists(tempPath) || dto.CreatedAt < cutoff)
                {
                    TryDelete(sidecarPath);
                    TryDelete(tempPath);

                    continue;
                }

                var key = new ReplayMatchKey(dto.MapId, dto.SteamId, dto.Style, dto.Track, dto.Stage, dto.AttemptId);

                // Older sidecars predating the MapName field deserialize to empty; fall back to the
                // current map name (the sidecar usually belongs to the same map it's restored on).
                var mapName = string.IsNullOrEmpty(dto.MapName) ? _bridge.CurrentMapName : dto.MapName;

                _fallbackRecords[key] = new FallbackReplayRecord
                {
                    TempFilePath = tempPath,
                    MapName      = mapName,
                    WriteTask    = Task.CompletedTask,
                    CreatedAt    = dto.CreatedAt,
                };

                loaded++;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to load fallback sidecar at {Path}; deleting", sidecarPath);
                TryDelete(sidecarPath);
                TryDelete(tempPath);
            }
        }

        if (loaded > 0)
        {
            _logger.LogInformation("Restored {Count} fallback replay records from disk", loaded);
        }

        return;

        void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to delete stale file at {Path}", path);
            }
        }
    }

    private void OnPlayerRunCommandPost(IPlayerRunCommandHookParams arg, HookReturnValue<EmptyHookReturn> hook)
    {
        var client = arg.Client;

        if (client.IsFakeClient)
        {
            return;
        }

        var pawn = arg.Pawn;

        if (!pawn.IsAlive)
        {
            return;
        }

        var slot = client.Slot;

        if (!TryGetFrameData(slot, out var frameData))
        {
            return;
        }

        // Skip recording frames while the timer is paused
        if (_timerModule.GetTimerInfo(slot) is { Status: ETimerStatus.Paused })
        {
            return;
        }

        var angles  = pawn.GetEyeAngles();
        var service = arg.Service;

        frameData.Frames.Add(new ()
        {
            Origin         = pawn.GetAbsOrigin(),
            Angles         = new (angles.X, angles.Y),
            PressedButtons = service.KeyButtons,
            ChangedButtons = service.KeyChangedButtons,
            ScrollButtons  = service.ScrollButtons,
            MoveType       = pawn.MoveType,
            Velocity       = pawn.GetAbsVelocity(),
        });
    }
}
