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
        services.ImplSingleton<IRecordModule, IModule, RecordModule>();
        services.ImplSingleton<IReplayPlaybackModule, IModule, ReplayPlaybackModule>();
        services.AddSingleton<IReplayModule>(x => x.GetRequiredService<ReplayPlaybackModule>());
        services.ImplSingleton<IReplayRecorderModule, IModule, ReplayRecorderModule>();
        // KZ port: KZ HUD (speed/keys/mode/tp) replaces the surf HUD (cs2kz src/kz/hud).
        services.ImplSingleton<IHudModule, IModule, KzHudModule>();

        // KZ port: CKZ prestrafe movement foundation (cs2kz movement/kz_mode_ckz).
        services.ImplSingleton<ICkzMovementModule, IModule, CkzMovementModule>();
        services.ImplSingleton<IMessageModule, IModule, MessageModule>();

        services.ImplSingleton<IMiscModule, IModule, MiscModule>();

        // KZ port: cp/tp save-loc practice system (net-new; cs2kz src/kz/checkpoint).
        services.ImplSingleton<ICheckpointModule, IModule, CheckpointModule>();

        // KZ port: mode framework + Vanilla mode (cs2kz src/kz/mode; CKZ movement at P5).
        services.ImplSingleton<IModeModule, IModule, ModeModule>();

        // KZ port: !goto <player> (cs2kz src/kz/goto).
        services.ImplSingleton<IGotoModule, IModule, GotoModule>();

        // KZ port: !fov <value> (cs2kz src/kz/fov).
        services.ImplSingleton<IFovModule, IModule, FovModule>();

        // KZ port: !measure 2-point distance tool (cs2kz src/kz/measure).
        services.ImplSingleton<IMeasureModule, IModule, MeasureModule>();

        // KZ port: !pistol <name> (cs2kz src/kz/pistol).
        services.ImplSingleton<IPistolModule, IModule, PistolModule>();

        // KZ port: stackable styles (ABH/LGJ; cs2kz src/kz/style). Layers on top of the mode.
        services.ImplSingleton<IKzStyleModule, IModule, KzStyleModule>();

        // KZ port: jumpstats foundation — LJ/BH detection + distance tiers (cs2kz src/kz/jumpstats).
        services.ImplSingleton<IJumpstatsModule, IModule, JumpstatsModule>();

        // KZ port: KZ run semantics (Pro/Standard from teleport count) on the timer (cs2kz src/kz/timer).
        services.ImplSingleton<IKzTimerModule, IModule, KzTimerModule>();

        // KZ port: anticheat — invalid client-cvar detector (cs2kz src/kz/anticheat).
        services.ImplSingleton<IAnticheatModule, IModule, AnticheatModule>();

        // KZ port: rotating tips (cs2kz src/kz/tip).
        services.ImplSingleton<ITipModule, IModule, TipModule>();
    }
}
