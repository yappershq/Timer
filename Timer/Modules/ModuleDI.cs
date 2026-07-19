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

using Microsoft.Extensions.DependencyInjection;
using Source2Surf.Timer.Modules.Practice;
using Source2Surf.Timer.Shared.Interfaces.Modules;

namespace Source2Surf.Timer.Modules;

// ReSharper disable once InconsistentNaming
internal static class ModuleDI
{
    public static void AddModuleService(this IServiceCollection services)
    {
        services.ImplSingleton<IZoneModule, IModule, ZoneModule>();
        services.ImplSingleton<IMapInfoModule, IModule, MapInfoModule>();

        services.ImplSingleton<ITimerModule, IModule, TimerModule>();
        services.ImplSingleton<IStyleModule, IModule, StyleModule>();

        services.ImplSingleton<IPracticeModule, IModule, PracticeManager>();
        services.ImplSingleton<IRecordModule, IModule, RecordModule>();
        services.ImplSingleton<IReplayPlaybackModule, IModule, ReplayPlaybackModule>();
        services.AddSingleton<IReplayModule>(x => x.GetRequiredService<ReplayPlaybackModule>());
        services.ImplSingleton<IReplayRecorderModule, IModule, ReplayRecorderModule>();
        services.ImplSingleton<IHudModule, IModule, HudModule>();
        services.ImplSingleton<IMessageModule, IModule, MessageModule>();

        services.ImplSingleton<IMiscModule, IModule, MiscModule>();
    }
}
