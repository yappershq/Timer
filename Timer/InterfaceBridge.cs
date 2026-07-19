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

using System.IO;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Sharp.Shared;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace Source2Surf.Timer;

internal interface IModule
{
    bool Init();

    void OnPostInit(ServiceProvider provider)
    {
    }

    void Shutdown()
    {
    }
}

internal interface IManager
{
    bool Init();

    void OnPostInit()
    {
    }

    void Shutdown();
}

internal class InterfaceBridge
{
    public InterfaceBridge(string sharpPath, ISharedSystem sharedSystem, CancellationToken token)
    {
        SharpPath         = sharpPath;
        CancellationToken = token;

        TimerDataPath = Path.Combine(sharpPath, "data", "surftimer");

        if (!Directory.Exists(TimerDataPath))
        {
            Directory.CreateDirectory(TimerDataPath);
        }

        ModSharp            = sharedSystem.GetModSharp();
        ConVarManager       = sharedSystem.GetConVarManager();
        EventManager        = sharedSystem.GetEventManager();
        ClientManager       = sharedSystem.GetClientManager();
        EntityManager       = sharedSystem.GetEntityManager();
        HookManager         = sharedSystem.GetHookManager();
        SchemaManager       = sharedSystem.GetSchemaManager();
        PhysicsQueryManager = sharedSystem.GetPhysicsQueryManager();
        Modules             = sharedSystem.GetLibraryModuleManager();
    }

    public string SharpPath { get; }

    public string TimerDataPath { get; }

    public CancellationToken CancellationToken { get; }

    public IModSharp             ModSharp { get; }
    public ILibraryModuleManager Modules  { get; }

    public IConVarManager       ConVarManager       { get; }
    public IEventManager        EventManager        { get; }
    public IClientManager       ClientManager       { get; }
    public IEntityManager       EntityManager       { get; }
    public IHookManager         HookManager         { get; }
    public ISchemaManager       SchemaManager       { get; }
    public IPhysicsQueryManager PhysicsQueryManager { get; }

    public IGameRules  GameRules  => ModSharp.GetGameRules();
    public IGlobalVars GlobalVars => ModSharp.GetGlobals();

    // Cached map name; safe to read off-thread (immutable string). Refresh on the main thread.
    public string CurrentMapName { get; private set; } = string.Empty;

    public string RefreshMapName()
        => CurrentMapName = ModSharp.GetGlobals().MapName;
}
