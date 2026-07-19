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
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Cysharp.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Modules.MapInfo;
using Source2Surf.Timer.Utilities;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Modules;

internal interface IMapInfoModule
{
    float GetEnterSpeedLimit(int track);

    int GetMaxPrejumps(int track);

    EGameMode GetCurrentGameMode();

    float? GetZoneExitSpeedOverride(int track);

    float GetGameModeExitSpeedLimit();

    float GetStageEnterSpeedLimit(int track);

    float? GetStageExitSpeedOverride(int track);

    float GetDefaultAirAccelerate();

    MapProfile GetCurrentMapProfile();
}

internal class MapInfoModule : IModule, IMapInfoModule, IGameListener
{
    private readonly InterfaceBridge _bridge;
    private readonly IRequestManager _requestManager;
    private readonly ICommandManager _commandManager;

    private readonly ILogger<MapInfoModule>          _logger;
    private readonly TaskTracker                     _taskTracker;

    // Placeholder until the async GetMapInfo load completes (never null): consumers
    // (!tier, !mi, replay StorePendingReplay) may run before/without a DB round-trip.
    private MapProfile _currentMapProfileInfo = new () { MapName = string.Empty };

    private double     _currentMapStartTime;
    private MapConfig? _currentMapConfig = null;

    private readonly string _configPath;

    private readonly string[] _baseCvars =
    [
        "bot_quota 0",
        "bot_quota_mode normal",
        "mp_limitteams 0",
        "bot_chatter off",
        "bot_flipout 1",
        "bot_zombie 1",
        "bot_stop 1",
        "mp_autoteambalance 0",
        "bot_controllable 0",
        "mp_ignore_round_win_conditions 1",
        "sv_accelerate 5",
        "sv_friction 4",
        "sv_jump_precision_enable 0",
        "sv_staminajumpcost 0",
        "sv_staminalandcost 0",
        "sv_disable_radar 1",
        "sv_subtick_movement_view_angles 0",
        "mp_solid_enemies 0",
        "mp_solid_teammates 0",
        "sv_legacy_jump 1",
        "bot_auto_vacate 0",
        "ms_override_team_limit 1",
    ];

    private static readonly GameModeConfig DefaultConfig = new(
        prefix: "", fileName: "", specificCvars: [],
        gameMode: EGameMode.None,
        enterSpeedLimit: 260.0f, exitSpeedLimit: 375.0f,
        maxPrejumps: 1, airAccelerate: 150.0f
    );

    private static readonly IReadOnlyList<GameModeConfig> GameModeConfigs =
    [
        new ("surf", "surf.cfg", ["sv_airaccelerate 150"], EGameMode.Surf,
            enterSpeedLimit: 260.0f, exitSpeedLimit: 375.0f, maxPrejumps: 1, airAccelerate: 150.0f),
        new ("bhop", "bhop.cfg", ["sv_airaccelerate 1000"], EGameMode.Bhop,
            enterSpeedLimit: 260.0f, exitSpeedLimit: 290.0f, maxPrejumps: 1, airAccelerate: 1000.0f),
    ];

    private EGameMode _currentGameMode = EGameMode.None;

    private GameModeConfig _currentGameModeConfig = DefaultConfig;

    private bool _mapStatsPersisted;
    private bool _mapProfileLoaded;

    // Late-resolved to avoid circular DI (RecordModule depends on IMapInfoModule)
    private IRecordModule _recordModule = null!;
    private IZoneModule   _zoneModule   = null!;

    public MapInfoModule(InterfaceBridge        bridge,
                         IRequestManager        requestManager,
                         ICommandManager        commandManager,
                         ILogger<MapInfoModule> logger)
    {
        _bridge         = bridge;
        _requestManager = requestManager;
        _commandManager = commandManager;
        _logger         = logger;
        _taskTracker    = new TaskTracker(logger);

        _configPath = Path.Combine(bridge.TimerDataPath, "map_configs");

        if (!Directory.Exists(_configPath))
        {
            Directory.CreateDirectory(_configPath);
        }
    }

    public bool Init()
    {
        // Prime the cache on load so it's valid after a mid-map hot-reload (OnGameInit won't re-fire).
        _bridge.RefreshMapName();
        _currentMapProfileInfo = new () { MapName = _bridge.CurrentMapName };

        _bridge.ModSharp.InstallGameListener(this);

        _commandManager.AddAdminChatCommand("set_tier", [], OnCommandSetTier);
        _commandManager.AddClientChatCommand("mi", OnCommandMapInfo);
        _commandManager.AddClientChatCommand("mapinfo", OnCommandMapInfo);
        _commandManager.AddClientChatCommand("tier", OnCommandTier);
        _commandManager.AddClientChatCommand("playtime", OnCommandPlaytime);

        return true;
    }

    public void OnPostInit(ServiceProvider provider)
    {
        _recordModule = provider.GetRequiredService<IRecordModule>();
        _zoneModule   = provider.GetRequiredService<IZoneModule>();
    }

    public void Shutdown()
    {
        PersistCurrentMapStats();
        _taskTracker.DrainPendingTasks();

        _bridge.ModSharp.RemoveGameListener(this);
    }

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    public void OnGameInit()
    {
        _bridge.RefreshMapName();
        _currentMapStartTime = _bridge.ModSharp.EngineTime();
        _mapStatsPersisted   = false;
        _mapProfileLoaded    = false;

        // Reset immediately: keeping the previous map's profile visible during the async
        // load would let consumers key data (e.g. replays) against the WRONG MapId.
        _currentMapProfileInfo = new () { MapName = _bridge.CurrentMapName };

        var loadTask = Task.Run(async () =>
        {
            try
            {
                var profile = await RetryHelper.RetryAsync(
                    () => _requestManager.GetMapInfo(_bridge.CurrentMapName),
                    RetryHelper.IsTransient, _logger, "GetMapInfo"
                ).ConfigureAwait(false);

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    _currentMapProfileInfo = profile;
                    _mapProfileLoaded = true;
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when trying to get map profile");
            }
        }, _bridge.CancellationToken);

        _taskTracker.Track(loadTask);
    }

    public void OnGameActivate()
    {
        LoadGameModeConfig();
        LoadMapConfig();
    }

    public void OnGamePreShutdown()
    {
        _currentMapConfig = null;
        PersistCurrentMapStats();
    }

    private ECommandAction OnCommandSetTier(PlayerSlot slot, StringCommand command)
    {
        if (command.ArgCount < 1)
        {
            return ECommandAction.Handled;
        }

        if (command.TryGet<byte>(1) is { } tier and > 0)
        {
            // Persisting the placeholder would overwrite the map's real Stages/PlayCount
            // with zeros — refuse until the profile load has completed.
            if (!_mapProfileLoaded)
            {
                _logger.LogWarning("set_tier ignored: map profile not loaded yet.");

                return ECommandAction.Handled;
            }

            _currentMapProfileInfo.Tier[0] = tier;

            var profile = _currentMapProfileInfo;
            _taskTracker.Track(Task.Run(async () => await RetryHelper.RetryAsync(
                () => _requestManager.UpdateMapInfo(profile),
                RetryHelper.IsTransient, _logger, "UpdateMapInfo"
            ).ConfigureAwait(false), _bridge.CancellationToken));
        }

        return ECommandAction.Handled;
    }
    private ECommandAction OnCommandTier(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append(_bridge.CurrentMapName);
            sb.Append(" | Tier: ");
            sb.Append(ChatColor.LightGreen);
            sb.Append(_currentMapProfileInfo.Tier[0]);
            sb.Append(ChatColor.White);

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandPlaytime(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetClientController(slot, out var client, out _))
        {
            return ECommandAction.Handled;
        }

        var steamId        = client.SteamId;
        var mapName        = _bridge.CurrentMapName;
        var currentSession = _recordModule.GetSessionTime(slot);

        AsyncChatCommand.Run(_bridge, _logger, slot, "GetPlayerMapStatsAsync",
                             () => _requestManager.GetPlayerMapStatsAsync(steamId, mapName),
                             (ctrl, stats) =>
                             {
                                 var (dbPlayTime, playCount) = stats;
                                 var totalTime               = dbPlayTime + currentSession;

                                 var sb = ZString.CreateStringBuilder(true);
                                 try
                                 {
                                     sb.Append("Playtime on ");
                                     sb.Append(ChatColor.LightGreen);
                                     sb.Append(mapName);
                                     sb.Append(ChatColor.White);
                                     sb.Append(": ");
                                     sb.Append(ChatColor.LightGreen);
                                     AppendPlaytime(ref sb, totalTime);
                                     sb.Append(ChatColor.White);
                                     sb.Append(" | Plays: ");
                                     sb.Append(ChatColor.LightGreen);
                                     sb.Append(playCount + 1);
                                     sb.Append(ChatColor.White);

                                     ctrl.PrintToChat(sb.ToString());
                                 }
                                 finally
                                 {
                                     sb.Dispose();
                                 }
                             });

        return ECommandAction.Handled;
    }

    private static void AppendPlaytime(ref Utf16ValueStringBuilder sb, float totalSeconds)
    {
        var hours   = (int) (totalSeconds / 3600f);
        var minutes = (int) ((totalSeconds % 3600f) / 60f);

        if (hours > 0)
        {
            sb.Append(hours);
            sb.Append("h ");
        }

        sb.Append(minutes);
        sb.Append("m");
    }

    private ECommandAction OnCommandMapInfo(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var profile  = _currentMapProfileInfo;
        var gameMode = _currentGameMode;

        // Line 1: Map name, tier, mode
        var sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append(ChatColor.LightGreen);
            sb.Append(_bridge.CurrentMapName);
            sb.Append(ChatColor.White);
            sb.Append(" | Tier: ");
            sb.Append(ChatColor.LightGreen);
            sb.Append(profile.Tier[0]);
            sb.Append(ChatColor.White);
            sb.Append(" | Mode: ");
            sb.Append(ChatColor.LightGreen);
            sb.Append(gameMode);
            sb.Append(ChatColor.White);

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        // Line 2: Track layout — stages, checkpoints, bonuses, linear
        sb = ZString.CreateStringBuilder(true);
        try
        {
            var totalStages      = _zoneModule.GetTotalStages(0);
            var totalCheckpoints = _zoneModule.GetCurrentTrackCheckpointCount(0);
            var isLinear         = _zoneModule.IsCurrentTrackLinear(0);

            sb.Append("Type: ");
            sb.Append(ChatColor.LightGreen);
            sb.Append(isLinear ? "Linear" : "Staged");
            sb.Append(ChatColor.White);

            if (totalStages > 0)
            {
                sb.Append(" | Stages: ");
                sb.Append(ChatColor.LightGreen);
                sb.Append(totalStages);
                sb.Append(ChatColor.White);
            }

            if (totalCheckpoints > 0)
            {
                sb.Append(" | Checkpoints: ");
                sb.Append(ChatColor.LightGreen);
                sb.Append(totalCheckpoints);
                sb.Append(ChatColor.White);
            }

            if (profile.Bonuses > 0)
            {
                sb.Append(" | Bonuses: ");
                sb.Append(ChatColor.LightGreen);
                sb.Append(profile.Bonuses);
                sb.Append(ChatColor.White);
            }

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        // Line 3: WR and completions
        sb = ZString.CreateStringBuilder(true);
        try
        {
            var wr    = _recordModule.GetWR(0, 0);
            var total = _recordModule.GetTotalRecordCount(0, 0);

            sb.Append("WR: ");

            if (wr is not null)
            {
                sb.Append(ChatColor.LightGreen);
                Utils.FormatTime(ref sb, wr.Time, true);
                sb.Append(ChatColor.White);
            }
            else
            {
                sb.Append(ChatColor.Red);
                sb.Append("None");
                sb.Append(ChatColor.White);
            }

            sb.Append(" | Completions: ");
            sb.Append(ChatColor.LightGreen);
            sb.Append(total);
            sb.Append(ChatColor.White);

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        // Line 4: Play count and total play time
        sb = ZString.CreateStringBuilder(true);
        try
        {
            sb.Append("Played: ");
            sb.Append(ChatColor.LightGreen);
            sb.Append(profile.PlayCount);
            sb.Append(ChatColor.White);
            sb.Append(" times | Total: ");
            sb.Append(ChatColor.LightGreen);
            AppendPlaytime(ref sb, profile.TotalPlayTime);
            sb.Append(ChatColor.White);

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        return ECommandAction.Handled;
    }

    private void LoadGameModeConfig()
    {
        var             configPath = "";
        GameModeConfig? config     = null;

        foreach (var cfg in GameModeConfigs)
        {
            if (!_bridge.CurrentMapName.StartsWith(cfg.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            config           = cfg;
            configPath       = Path.Combine(_configPath, cfg.FileName);
            _currentGameMode = cfg.GameMode;

            break;
        }

        if (config == null)
        {
            _currentGameModeConfig = DefaultConfig;
            _currentGameMode       = EGameMode.None;

            foreach (var cvar in _baseCvars)
            {
                _bridge.ModSharp.ServerCommand(cvar);
            }

            return;
        }

        _currentGameModeConfig = config;

        EnsureConfigExists(configPath, config);
        ExecuteGameModeConfig(configPath);
    }

    private void EnsureConfigExists(string path, GameModeConfig config)
    {
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            File.WriteAllLines(path, _baseCvars);
            File.AppendAllLines(path, config.SpecificCvars);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when trying to create config at {p}", path);
        }
    }

    private void ExecuteGameModeConfig(string path)
    {
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();

                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//"))
                {
                    continue;
                }

                var commentIndex = trimmed.IndexOf("//", StringComparison.Ordinal);

                var commandToExecute = commentIndex > 0
                    ? trimmed[..commentIndex].Trim()
                    : trimmed;

                if (!string.IsNullOrWhiteSpace(commandToExecute))
                {
                    _bridge.ModSharp.ServerCommand(commandToExecute);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when trying to execute config {p}", path);
        }
    }

    private void LoadMapConfig()
    {
        var configPath = Path.Combine(_configPath, $"{_bridge.CurrentMapName}.json");

        if (!File.Exists(configPath))
        {
            return;
        }

        try
        {
            var content = File.ReadAllText(configPath);

            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            var mapConfig = JsonSerializer.Deserialize<MapConfig>(content);

            if (mapConfig == null)
            {
                return;
            }

            _currentMapConfig = mapConfig;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when reading map config {p}", configPath);

            return;
        }

        foreach (var command in _currentMapConfig.Commands)
        {
            _bridge.ModSharp.ServerCommand(command);
        }
    }

    private record GameModeConfig
    {
        public string    Prefix          { get; }
        public string    FileName        { get; }
        public string[]  SpecificCvars   { get; }
        public EGameMode GameMode        { get; }
        public float     EnterSpeedLimit { get; }
        public float     ExitSpeedLimit  { get; }
        public int       MaxPrejumps     { get; }
        public float     AirAccelerate   { get; }

        public GameModeConfig(string prefix, string fileName, string[] specificCvars, EGameMode gameMode,
                              float enterSpeedLimit, float exitSpeedLimit, int maxPrejumps, float airAccelerate)
        {
            Prefix          = prefix;
            FileName        = fileName;
            SpecificCvars   = specificCvars;
            GameMode        = gameMode;
            EnterSpeedLimit = enterSpeedLimit;
            ExitSpeedLimit  = exitSpeedLimit;
            MaxPrejumps     = maxPrejumps;
            AirAccelerate   = airAccelerate;
        }
    }

    public float GetEnterSpeedLimit(int track)
    {
        if (_currentMapConfig != null
            && _currentMapConfig.ZoneConfigs.TryGetValue(track, out var zone)
            && zone.EnterSpeedLimit is { } enterLimit)
        {
            return enterLimit;
        }
        return _currentGameModeConfig.EnterSpeedLimit;
    }

    public int GetMaxPrejumps(int track)
    {
        if (_currentMapConfig != null
            && _currentMapConfig.ZoneConfigs.TryGetValue(track, out var zone)
            && zone.MaxJumps is { } maxJumps)
        {
            return maxJumps;
        }
        return _currentGameModeConfig.MaxPrejumps;
    }

    public EGameMode GetCurrentGameMode()
        => _currentGameMode;

    public float? GetZoneExitSpeedOverride(int track)
    {
        if (_currentMapConfig != null
            && _currentMapConfig.ZoneConfigs.TryGetValue(track, out var zone))
        {
            return zone.ExitSpeedLimit;
        }
        return null;
    }

    public float GetGameModeExitSpeedLimit()
        => _currentGameModeConfig.ExitSpeedLimit;

    public float GetStageEnterSpeedLimit(int track)
    {
        if (_currentMapConfig != null
            && _currentMapConfig.ZoneConfigs.TryGetValue(track, out var zone)
            && zone.StageZone is { EnterSpeedLimit: { } stageEnterLimit })
        {
            return stageEnterLimit;
        }
        return GetEnterSpeedLimit(track);
    }

    public float? GetStageExitSpeedOverride(int track)
    {
        if (_currentMapConfig != null
            && _currentMapConfig.ZoneConfigs.TryGetValue(track, out var zone)
            && zone.StageZone is { } stageZone)
        {
            return stageZone.ExitSpeedLimit;
        }
        return GetZoneExitSpeedOverride(track);
    }

    public float GetDefaultAirAccelerate()
        => _currentGameModeConfig.AirAccelerate;

    public MapProfile GetCurrentMapProfile()
        => _currentMapProfileInfo;

    private void PersistCurrentMapStats()
    {
        if (_mapStatsPersisted)
        {
            return;
        }

        if (_currentMapStartTime <= 0 || string.IsNullOrEmpty(_bridge.CurrentMapName))
        {
            return;
        }

        _mapStatsPersisted = true;

        var delta = (float)(_bridge.ModSharp.EngineTime() - _currentMapStartTime);

        if (delta < 0f)
        {
            delta = 0f;
        }

        if (!_mapProfileLoaded)
        {
            var mapName = _bridge.CurrentMapName;

            _taskTracker.Track(Task.Run(async () =>
            {
                try
                {
                    var profile = await _requestManager.GetMapInfo(mapName).ConfigureAwait(false);
                    profile.PlayCount++;
                    profile.TotalPlayTime += delta;

                    await _requestManager.UpdateMapInfo(profile).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error when updating map info on shutdown");
                }
            }, _bridge.CancellationToken));

            return;
        }

        var profile = _currentMapProfileInfo;
        profile.PlayCount++;
        profile.TotalPlayTime += delta;

        _taskTracker.Track(Task.Run(async () =>
        {
            try
            {
                await _requestManager.UpdateMapInfo(profile).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when updating map info on shutdown");
            }
        }, _bridge.CancellationToken));
    }
}
