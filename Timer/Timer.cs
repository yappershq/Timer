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
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Source2Surf.Timer.Managers;
using Source2Surf.Timer.Managers.Command;
using Source2Surf.Timer.Managers.Replay;
using Source2Surf.Timer.Managers.Request;
using Source2Surf.Timer.Modules;
using Source2Surf.Timer.Shared.Interfaces;

[assembly: DisableRuntimeMarshalling]

namespace Source2Surf.Timer;

public class Timer : IModSharpModule
{
    private readonly InterfaceBridge         _bridge;
    private readonly ILogger<Timer>          _logger;
    private readonly ServiceProvider         _serviceProvider;
    private readonly CancellationTokenSource _token;
    private int                              _shutdownState;

    public Timer(ISharedSystem   shared,
                 string?         dllPath,
                 string?         sharpPath,
                 Version?        version,
                 IConfiguration? coreConfiguration,
                 bool            hotReload)
    {
        ArgumentNullException.ThrowIfNull(dllPath);
        ArgumentNullException.ThrowIfNull(sharpPath);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(coreConfiguration);

        var token = new CancellationTokenSource();

        /*var configuration = new ConfigurationBuilder()
                            .AddJsonFile(Path.Combine(dllPath, "appsettings.json"), false, false)
                            .Build();*/

        var bridge = new InterfaceBridge(sharpPath, shared, token.Token);

        var factory = shared.GetLoggerFactory();
        var logger  = factory.CreateLogger<Timer>();

        var gameData = shared.GetModSharp()
                             .GetGameData();

        gameData.Register("timer.games");

        /*if (File.Exists(Path.Combine(sharpPath, "gamedata", "test.games.kv")))
        {
            gameData.Register("test.games");
            _testGameData = true;
        }*/

        var services = new ServiceCollection();

        services.AddSingleton(bridge);
        services.AddSingleton(factory);
        services.AddSingleton(shared);
        services.AddSingleton(gameData);
        /*services.AddSingleton<IConfiguration>(configuration);*/
        /*ConfigureDebugServices(services, bridge);*/
        ConfigureServices(services);

        _serviceProvider = services.BuildServiceProvider();

        _token  = token;
        _bridge = bridge;
        _logger = logger;
    }

    public string DisplayName   => "SurfTimer";
    public string DisplayAuthor => "github.com/Nukoooo";

    public bool Init()
    {
        foreach (var service in _serviceProvider.GetServices<IManager>())
        {
            if (service.Init())
            {
#if DEBUG
                _logger.LogInformation("Init service {service}!",
                                       service.GetType()
                                              .FullName);
#endif
                continue;
            }

            _logger.LogError("Failed to init {service}!",
                             service.GetType()
                                    .FullName);

            return false;
        }

        foreach (var service in _serviceProvider.GetServices<IModule>())
        {
            if (service.Init())
            {
#if DEBUG
                _logger.LogInformation("Init module {service}!",
                                       service.GetType()
                                              .FullName);
#endif
                continue;
            }

            _logger.LogError("Failed to init {service}!",
                             service.GetType()
                                    .FullName);

            return false;
        }

        foreach (var service in _serviceProvider.GetServices<IManager>())
        {
            try
            {
                service.OnPostInit();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "An error occurred while calling PostInit for {type}", service.GetType().FullName);
            }
        }

        foreach (var service in _serviceProvider.GetServices<IModule>())
        {
            try
            {
                service.OnPostInit(_serviceProvider);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "An error occurred while calling PostInit for {type}", service.GetType().FullName);
            }
        }

        return true;
    }

    public void PostInit()
    {
        RefreshRequestManager();
        RefreshCommandManager();
        RefreshReplayProvider();
    }

    public void OnLibraryConnected(string moduleIdentity)
    {
        if (moduleIdentity.Equals(IRequestManager.Identity, StringComparison.Ordinal))
        {
            RefreshRequestManager();
        }
        else if (moduleIdentity.Equals(IReplayProvider.Identity, StringComparison.Ordinal))
        {
            RefreshReplayProvider();
        }
        else if (moduleIdentity.Equals(ICommandManager.Identity, StringComparison.Ordinal))
        {
            RefreshCommandManager();
        }
    }

    public void OnLibraryDisconnect(string moduleIdentity)
    {
        if (moduleIdentity.Equals(IRequestManager.Identity, StringComparison.Ordinal))
        {
            SwitchRequestManagerToLiteDb();
        }
        else if (moduleIdentity.Equals(IReplayProvider.Identity, StringComparison.Ordinal))
        {
            // RefreshProvider re-resolves; with the module gone it clears the provider
            // instead of holding a dead reference.
            RefreshReplayProvider();
        }
        else if (moduleIdentity.Equals(ICommandManager.Identity, StringComparison.Ordinal))
        {
            SwitchCommandManagerToFallback();
        }
    }

    public void OnAllModulesLoaded()
    {
        RefreshRequestManager();
        RefreshCommandManager();
        RefreshReplayProvider();
    }

    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdownState, 1) != 0)
        {
            return;
        }

        try
        {
            _serviceProvider.GetRequiredService<IGameData>()
                            .Unregister("timer.games");

            foreach (var service in _serviceProvider.GetServices<IManager>())
            {
                try
                {
                    service.Shutdown();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "An error occurred while calling Shutdown for {type}", service.GetType().FullName);
                }
            }

            foreach (var service in _serviceProvider.GetServices<IModule>())
            {
                try
                {
                    service.Shutdown();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "An error occurred while calling Shutdown for {type}", service.GetType().FullName);
                }
            }

            _token.Cancel();

            _logger.LogInformation("Shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error when shutting down");

            // ignored
        }
        finally
        {
            try
            {
                _serviceProvider.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error when disposing ServiceProvider");
            }

            _token.Dispose();
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging();

        services.AddManagerService();
        services.AddModuleService();
    }

    private void RefreshRequestManager()
    {
        if (_serviceProvider.GetService<IRequestManager>() is RequestManagerProxy proxy)
        {
            proxy.RefreshManager();

            return;
        }

        _logger.LogWarning("IRequestManager is not RequestManagerProxy, skip refresh.");
    }

    private void SwitchRequestManagerToLiteDb()
    {
        if (_serviceProvider.GetService<IRequestManager>() is RequestManagerProxy proxy)
        {
            proxy.UseFallback();

            return;
        }

        _logger.LogWarning("IRequestManager is not RequestManagerProxy, cannot force LiteDB fallback.");
    }

    private void RefreshCommandManager()
    {
        if (_serviceProvider.GetService<ICommandManager>() is CommandManagerProxy proxy)
        {
            proxy.RefreshManager();

            return;
        }

        _logger.LogWarning("ICommandManager is not CommandManagerProxy, skip refresh.");
    }

    private void SwitchCommandManagerToFallback()
    {
        if (_serviceProvider.GetService<ICommandManager>() is CommandManagerProxy proxy)
        {
            proxy.UseFallback();

            return;
        }

        _logger.LogWarning("ICommandManager is not CommandManagerProxy, cannot force fallback.");
    }

    private void RefreshReplayProvider()
    {
        _serviceProvider.GetService<ReplayProviderProxy>()?.RefreshProvider();
    }
}
