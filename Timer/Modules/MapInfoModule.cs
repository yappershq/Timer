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
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Modules;

internal interface IMapInfoModule
{
    float GetEnterSpeedLimit(int track);

    float GetExitSpeedLimit(int track);

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

    private MapProfile _currentMapProfileInfo = null!;

    private string _currentMapName = string.Empty;

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

    private float _currentAirAccelerate;
    private bool  _mapStatsPersisted;
    private bool  _mapProfileLoaded;

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
        _currentMapName      = _bridge.GlobalVars.MapName;
        _currentMapStartTime = _bridge.ModSharp.EngineTime();
        _mapStatsPersisted   = false;
        _mapProfileLoaded    = false;

        var loadTask = Task.Run(async () =>
        {
            try
            {
                var profile = await RetryHelper.RetryAsync(
                    () => _requestManager.GetMapInfo(_currentMapName),
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

        _currentAirAccelerate = _currentGameModeConfig.AirAccelerate;
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
            sb.Append(_currentMapName);
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
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var steamId        = client.SteamId;
        var mapName        = _currentMapName;
        var currentSession = _recordModule.GetSessionTime(slot);

        Task.Run(async () =>
        {
            try
            {
                var (dbPlayTime, playCount) = await RetryHelper.RetryAsync(
                    () => _requestManager.GetPlayerMapStatsAsync(steamId, mapName),
                    RetryHelper.IsTransient, _logger, "GetPlayerMapStatsAsync"
                ).ConfigureAwait(false);

                var totalTime = dbPlayTime + currentSession;

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    var sb = ZString.CreateStringBuilder(true);
                    try
                    {
                        sb.Append("Playtime on ");
                        sb.Append(ChatColor.LightGreen);
                        sb.Append(mapName);
                        sb.Append(ChatColor.White);
                        sb.Append(": ");
                        sb.Append(ChatColor.LightGreen);

                        var hours   = (int)(totalTime / 3600f);
                        var minutes = (int)((totalTime % 3600f) / 60f);

                        if (hours > 0)
                        {
                            sb.Append(hours);
                            sb.Append("h ");
                        }

                        sb.Append(minutes);
                        sb.Append("m");
                        sb.Append(ChatColor.White);
                        sb.Append(" | Plays: ");
                        sb.Append(ChatColor.LightGreen);
                        sb.Append(playCount + 1);
                        sb.Append(ChatColor.White);

                        controller.PrintToChat(sb.ToString());
                    }
                    finally
                    {
                        sb.Dispose();
                    }
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when fetching player playtime");
            }
        }, _bridge.CancellationToken);

        return ECommandAction.Handled;
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
            sb.Append(_currentMapName);
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

            var totalSeconds = profile.TotalPlayTime;
            var hours   = (int)(totalSeconds / 3600f);
            var minutes = (int)((totalSeconds % 3600f) / 60f);

            if (hours > 0)
            {
                sb.Append(hours);
                sb.Append("h ");
            }

            sb.Append(minutes);
            sb.Append("m");
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
            if (!_currentMapName.StartsWith(cfg.Prefix, StringComparison.OrdinalIgnoreCase))
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
        var configPath = Path.Combine(_configPath, $"{_currentMapName}.json");

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

    public float GetExitSpeedLimit(int track)
    {
        if (_currentMapConfig != null
            && _currentMapConfig.ZoneConfigs.TryGetValue(track, out var zone)
            && zone.ExitSpeedLimit is { } exitLimit)
        {
            return exitLimit;
        }
        return _currentGameModeConfig.ExitSpeedLimit;
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

        if (_currentMapStartTime <= 0 || string.IsNullOrEmpty(_currentMapName))
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
            var mapName = _currentMapName;

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
