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
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Models.Style;
using Source2Surf.Timer.Shared.Models.Timer;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules;

internal interface IStyleModule
{
    void RegisterListener(IStyleModuleListener listener);

    void UnregisterListener(IStyleModuleListener listener);

    StyleSetting GetStyleSetting(int style);

    int GetStyleCount();
}

internal class StyleModule : IModule, IStyleModule, IGameListener, ITimerModuleListener, IZoneModuleListener
{
    private readonly InterfaceBridge _bridge;

    private readonly string _styleConfigPath;

    private readonly ICommandManager _commandManager;

    private readonly IZoneModule          _zoneModule;
    private          ITimerModule         _timerModule = null!;
    private readonly IMapInfoModule       _mapInfoModule;
    private readonly ILogger<StyleModule> _logger;
    private readonly ListenerHub<IStyleModuleListener> _listenerHub;

    private List<StyleSetting> _styles = [];

    // ReSharper disable InconsistentNaming

    private readonly IConVar sv_autobunnyhopping;
    private readonly IConVar sv_accelerate;
    private readonly IConVar sv_friction;
    private readonly IConVar sv_enablebunnyhopping;
    private readonly IConVar sv_air_max_wishspeed;
    private readonly IConVar sv_airaccelerate;

    // ReSharper restore InconsistentNaming

    // Cached last-applied style key to skip redundant Set() calls across players.
    // Combines style index + inStartZone flag into a single value.
    // Only when the composite key changes do we call Set() on all ConVars.
    private int  _lastStyleIndex = -1;
    private bool _lastInStartZone;

    public StyleModule(InterfaceBridge      bridge,
                       ICommandManager      commandManager,
                       IZoneModule          zoneModule,
                       IMapInfoModule       mapInfoModule,
                       ILogger<StyleModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _zoneModule     = zoneModule;
        _mapInfoModule  = mapInfoModule;
        _logger      = logger;
        _listenerHub = new ListenerHub<IStyleModuleListener>(logger);

        var configDir = Path.Combine(bridge.SharpPath, "configs");
        Directory.CreateDirectory(configDir);
        _styleConfigPath = Path.Combine(configDir, "timer-styles.jsonc");

        sv_autobunnyhopping   = InitializeConVar("sv_autobunnyhopping");
        sv_accelerate         = InitializeConVar("sv_accelerate");
        sv_friction           = InitializeConVar("sv_friction");
        sv_enablebunnyhopping = InitializeConVar("sv_enablebunnyhopping");
        sv_air_max_wishspeed  = InitializeConVar("sv_air_max_wishspeed");
        sv_airaccelerate      = InitializeConVar("sv_airaccelerate");
    }

    public bool Init()
    {
        if (!File.Exists(_styleConfigPath))
        {
            throw new FileNotFoundException($"File {_styleConfigPath} doesn't exist");
        }

        _bridge.ModSharp.InstallGameListener(this);

        _commandManager.AddServerCommand("reload_styles", OnCommandReloadStyles);

        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnProcessMovementPre);
        _bridge.HookManager.PlayerGetMaxSpeed.InstallHookPre(OnPlayerGetMaxSpeed);
        _bridge.HookManager.PlayerWalkMove.InstallForward(OnPlayerWalkMove);
        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawn);

        return true;
    }

    public void Shutdown()
    {
        _commandManager.ClearStyleCommands();

        sv_autobunnyhopping.Flags   |= ConVarFlags.Replicated;
        sv_accelerate.Flags         |= ConVarFlags.Replicated;
        sv_friction.Flags           |= ConVarFlags.Replicated;
        sv_enablebunnyhopping.Flags |= ConVarFlags.Replicated;
        sv_air_max_wishspeed.Flags  |= ConVarFlags.Replicated;
        sv_airaccelerate.Flags      |= ConVarFlags.Replicated;

        _bridge.ModSharp.RemoveGameListener(this);

        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovementPre);
        _bridge.HookManager.PlayerGetMaxSpeed.RemoveHookPre(OnPlayerGetMaxSpeed);
        _bridge.HookManager.PlayerWalkMove.RemoveForward(OnPlayerWalkMove);
        _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawn);

        _timerModule.UnregisterListener(this);
        _zoneModule.UnregisterListener(this);
    }

    public void OnPostInit(ServiceProvider provider)
    {
        _timerModule = provider.GetRequiredService<ITimerModule>();
        _timerModule.RegisterListener(this);
        _zoneModule.RegisterListener(this);
        LoadStyleConfig();
    }

    public void OnServerActivate()
    {
    }

    private ECommandAction OnCommandReloadStyles(StringCommand arg)
    {
        LoadStyleConfig();

        return ECommandAction.Handled;
    }

    private HookReturnValue<float> OnPlayerGetMaxSpeed(IPlayerGetMaxSpeedHookParams @params, HookReturnValue<float> ret)
    {
        var client = @params.Client;

        if (client.IsFakeClient || _timerModule.GetTimerInfo(client.Slot) is not { } timer)
        {
            return new ();
        }

        var styleSetting = _styles[timer.Style];

        return new (EHookAction.SkipCallReturnOverride, styleSetting.RunSpeed);
    }

    private static void OnPlayerWalkMove(IPlayerWalkMoveForwardParams @params)
    {
        @params.SetSpeed(291);
    }

    private unsafe void OnProcessMovementPre(IPlayerProcessMoveForwardParams @params)
    {
        var pawn = @params.Pawn;

        if (!pawn.IsAlive)
        {
            return;
        }

        var client = @params.Client;

        if (client.IsFakeClient)
        {
            return;
        }

        if (_timerModule.GetTimerInfo(client.Slot) is not { } mainTimer
            || _timerModule.GetStageTimerInfo(client.Slot) is not { } stageTimer)
        {
            return;
        }

        var service = @params.Service;
        var style   = _styles[mainTimer.Style];

        var inStartZone = mainTimer.InZone == EZoneType.Start;

        if (mainTimer.Style != _lastStyleIndex || inStartZone != _lastInStartZone)
        {
            var airAccel = style.CustomAirAccelerate ? style.AirAccelerate : _mapInfoModule.GetDefaultAirAccelerate();
            sv_airaccelerate.Set(airAccel);
            sv_autobunnyhopping.Set(!inStartZone && style.AutoBhop);
            sv_accelerate.Set(style.Accelerate);
            sv_friction.Set(style.Friction);
            sv_air_max_wishspeed.Set(style.WishSpeed);
            sv_enablebunnyhopping.Set(style.AllowBunnyhopping);

            _lastStyleIndex  = mainTimer.Style;
            _lastInStartZone = inStartZone;
        }

        var mv = @params.Info;

        if (pawn.ActualMoveType == MoveType.Walk)
        {
            if (style.BlockW && (mv->ForwardMove > 0 || (service.KeyButtons & UserCommandButtons.Forward) != 0))
            {
                mv->ForwardMove    =  0;
                service.KeyButtons &= ~UserCommandButtons.Forward;
            }

            if (style.BlockS && (mv->ForwardMove < 0 || (service.KeyButtons & UserCommandButtons.Back) != 0))
            {
                mv->ForwardMove    =  0;
                service.KeyButtons &= ~UserCommandButtons.Back;
            }

            if (style.BlockA && (mv->SideMove > 0 || (service.KeyButtons & UserCommandButtons.MoveLeft) != 0))
            {
                mv->SideMove       =  0;
                service.KeyButtons &= ~UserCommandButtons.MoveLeft;
            }

            if (style.BlockD && (mv->SideMove < 0 || (service.KeyButtons & UserCommandButtons.MoveRight) != 0))
            {
                mv->SideMove       =  0;
                service.KeyButtons &= ~UserCommandButtons.MoveRight;
            }
        }
    }

    private void OnPlayerSpawn(IPlayerSpawnForwardParams param)
    {
        var client = param.Client;

        if (client.IsFakeClient || _timerModule.GetTimerInfo(client.Slot) is not { } info)
        {
            return;
        }

        ReplicateClientCvars(client, info.Style);
    }

    public void OnPlayerTimerStart(IPlayerController controller, IPlayerPawn pawn, ITimerInfo info)
    {
        var slot = controller.PlayerSlot;

        if (_bridge.ClientManager.GetGameClient(slot) is not { } client)
        {
            return;
        }

        var style = _styles[info.Style];

        sv_autobunnyhopping.ReplicateToClient(client, style.AutoBhop.ToString());
    }

    public void OnZoneStartTouch(IZoneInfo zoneInfo, IPlayerController controller, IPlayerPawn pawn)
    {
        if (zoneInfo.ZoneType != EZoneType.Start)
        {
            return;
        }

        var slot = controller.PlayerSlot;

        if (_bridge.ClientManager.GetGameClient(slot) is not { } client || client.IsFakeClient)
        {
            return;
        }

        sv_autobunnyhopping.ReplicateToClient(client, false.ToString());
    }

    public void OnZoneEndTouch(IZoneInfo zoneInfo, IPlayerController controller, IPlayerPawn pawn)
    {
        if (zoneInfo.ZoneType != EZoneType.Start)
        {
            return;
        }

        var slot = controller.PlayerSlot;

        if (_bridge.ClientManager.GetGameClient(slot) is not { } client || client.IsFakeClient)
        {
            return;
        }

        if (_timerModule.GetTimerInfo(slot) is not { } timerInfo)
        {
            return;
        }

        var style = _styles[timerInfo.Style];

        sv_autobunnyhopping.ReplicateToClient(client, style.AutoBhop.ToString());
    }

    private IConVar InitializeConVar(string name)
    {
        var conVar = _bridge.ConVarManager.FindConVar(name)
                     ?? throw new NullReferenceException($"Failed to find {name}");

        conVar.Flags &= ~ConVarFlags.Replicated;

        return conVar;
    }

    private void LoadStyleConfig()
    {
        _commandManager.ClearStyleCommands();

        _styles = [new ()];

        if (!File.Exists(_styleConfigPath))
        {
            _logger.LogWarning("Style config file not found at {path}. Creating a new list with a default style.",
                               _styleConfigPath);

            File.WriteAllText(_styleConfigPath, JsonSerializer.Serialize(_styles, Utils.SerializerOptions));

            goto end;
        }

        try
        {
            var json = File.ReadAllText(_styleConfigPath);

            _styles = JsonSerializer.Deserialize<List<StyleSetting>>(json, Utils.DeserializerOptions) ?? [];

            if (_styles.Count == 0)
            {
                _logger.LogWarning("Style config is missing or empty, adding default style.");

                File.WriteAllText(_styleConfigPath, JsonSerializer.Serialize(_styles, Utils.SerializerOptions));
            }
            else if (_styles.Count > TimerConstants.MAX_STYLE)
            {
                var count = _styles.Count;

                _logger.LogWarning("Current style count {current} exceeds allowed count {max}, removing excess styles.",
                                   count,
                                   TimerConstants.MAX_STYLE);

                var numToRemove = count - TimerConstants.MAX_STYLE;
                _styles.RemoveRange(TimerConstants.MAX_STYLE, numToRemove);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize style config, using the default style setting");
        }

    end:
        AddStyleCommands();

        NotifyStyleConfigLoaded(_styles);
    }

    private void AddStyleCommands()
    {
        for (var i = 0; i < _styles.Count; i++)
        {
            var split = _styles[i].Command
                                  .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var styleIndex = i;

            foreach (var command in split)
            {
                _commandManager.AddStyleCommand(command, OnStyleCommand);
            }

            continue;

            ECommandAction OnStyleCommand(PlayerSlot slot, StringCommand _)
            {
                var client = _bridge.ClientManager.GetGameClient(slot);

                if (client is null)
                {
                    return ECommandAction.Handled;
                }

                var controller = client.GetPlayerController();

                if (controller is not { IsValidEntity: true }
                    || _timerModule.GetTimerInfo(slot) is not { } timerInfo
                    || _timerModule.GetStageTimerInfo(slot) is not { } stageTimer)
                {
                    return ECommandAction.Handled;
                }

                var oldStyle = timerInfo.Style;
                NotifyClientStyleChanged(slot, oldStyle, styleIndex);
                timerInfo.ChangeStyle(styleIndex);
                stageTimer.ChangeStyle(styleIndex);

                // force respawning the player after the game has processed most of the logic to
                // prevent crashes. because Respawn fundamentally calls SetPawn in the modsharp framework
                // and doing that in the middle of processing can make WriteEnterPVS, which has parallel workers,
                // fail to ge the pawn entity
                _bridge.ModSharp.InvokeFrameAction(() =>
                {
                    if (_bridge.ClientManager.GetGameClient(slot) is not { } deferredClient)
                    {
                        return;
                    }

                    if (deferredClient.GetPlayerController() is not { IsValidEntity: true } deferredController)
                    {
                        return;
                    }

                    deferredController.Respawn();

                    ReplicateClientCvars(deferredClient, styleIndex);
                });

                return ECommandAction.Handled;
            }
        }
    }

    private void ReplicateClientCvars(IGameClient client, int styleIndex)
    {
        var style = _styles[styleIndex];

        sv_accelerate.ReplicateToClient(client, style.Accelerate.ToString(CultureInfo.InvariantCulture));
        sv_autobunnyhopping.ReplicateToClient(client, style.AutoBhop.ToString(CultureInfo.InvariantCulture));
        sv_friction.ReplicateToClient(client, style.Friction.ToString(CultureInfo.InvariantCulture));

        sv_enablebunnyhopping.ReplicateToClient(client,
                                                style.AllowBunnyhopping.ToString(CultureInfo.InvariantCulture));

        sv_air_max_wishspeed.ReplicateToClient(client, style.WishSpeed.ToString(CultureInfo.InvariantCulture));

        sv_airaccelerate.ReplicateToClient(client,
                                           (style.CustomAirAccelerate
                                               ? style.AirAccelerate
                                               : _mapInfoModule.GetDefaultAirAccelerate())
                                           .ToString(CultureInfo.InvariantCulture));
    }

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    public void RegisterListener(IStyleModuleListener listener)
        => _listenerHub.Register(listener);

    public void UnregisterListener(IStyleModuleListener listener)
        => _listenerHub.Unregister(listener);

    private void NotifyStyleConfigLoaded(IReadOnlyList<StyleSetting> styles)
    {
        foreach (var listener in _listenerHub.Snapshot)
        {
            try
            {
                listener.OnStyleConfigLoaded(styles);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error when calling OnStyleConfigLoaded listener");
            }
        }
    }

    private void NotifyClientStyleChanged(PlayerSlot slot, int oldStyle, int newStyle)
    {
        foreach (var listener in _listenerHub.Snapshot)
        {
            try
            {
                listener.OnClientStyleChanged(slot, oldStyle, newStyle);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error when calling OnClientStyleChanged listener");
            }
        }
    }

    public StyleSetting GetStyleSetting(int style)
    {
        if (style < 0 || style >= _styles.Count)
        {
            throw new IndexOutOfRangeException("Style index is out of range");
        }

        return _styles[style];
    }

    public int GetStyleCount()
        => _styles.Count;
}
