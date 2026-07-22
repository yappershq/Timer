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
using Kreedz.Shared.Interfaces.Modules;

namespace Kreedz.Modules;

// ReSharper disable once InconsistentNaming
internal static class ModuleDI
{
    public static void AddModuleService(this IServiceCollection services)
    {
        services.ImplSingleton<IZoneModule, IModule, ZoneModule>();
        services.ImplSingleton<IMapInfoModule, IModule, MapInfoModule>();

        // KZ port: Mapping API entity-keyvalue source (cs2kz kz_mappingapi) — detours CreateWorldInternal to
        // read modern kz_ maps' timer_trigger_type/course keyvalues and feed the MappingApiRegistry parser.
        services.ImplSingleton<IMapApiSource, IModule, MapApiSourceModule>();

        // KZ port: anti-bhop + modifier trigger runtime (cs2kz kz_trigger) — per-player touching sets fed
        // by ZoneModule's touch outputs; jump-block/gravity/duck applied per tick.
        services.ImplSingleton<ITriggerModifiers, IModule, TriggerModifierModule>();

        // KZ port: !hide (cs2kz quiet) — per-viewer transmit hiding of other players + their weapons.
        services.AddSingleton<IModule, HideModule>();

        // KZ port: !spec (cs2kz spec) — spectate a player by name (slay-then-switch + observer lock).
        services.AddSingleton<IModule, SpecModule>();

        // KZ port: !beam (cs2kz beam) — airborne trail via a reused env_beam segment ring.
        services.AddSingleton<IModule, BeamModule>();

        // KZ port: jumpstats option toggles (cs2kz optionService keys jsFailstats/jsAlways).
        services.AddSingleton<IModule, OptionsModule>();

        services.ImplSingleton<ITimerModule, IModule, TimerModule>();
        services.ImplSingleton<IStyleModule, IModule, StyleModule>();
        services.ImplSingleton<IRecordModule, IModule, RecordModule>();
        services.ImplSingleton<IReplayPlaybackModule, IModule, ReplayPlaybackModule>();
        services.AddSingleton<IReplayModule>(x => x.GetRequiredService<ReplayPlaybackModule>());
        services.ImplSingleton<IReplayRecorderModule, IModule, ReplayRecorderModule>();
        // KZ port: the HUD now ships as the external Kreedz.Hud plugin (reads IKzRunService + IKzModeRegistry).

        // KZ port: CKZ movement now ships as the external Kreedz.Mode.CKZ plugin (registers via IKzModeRegistry).
        services.ImplSingleton<IMessageModule, IModule, MessageModule>();

        services.ImplSingleton<IMiscModule, IModule, MiscModule>();

        // KZ port: cp/tp save-loc practice system (net-new; cs2kz src/kz/checkpoint).
        services.ImplSingleton<ICheckpointModule, IModule, CheckpointModule>();

        // KZ port: per-player preference persistence (cs2kz src/kz/option) — modes/styles/fov survive reconnect.
        services.ImplSingleton<IPreferencesModule, IModule, PreferencesModule>();

        // KZ port: mode framework + Vanilla mode (cs2kz src/kz/mode; CKZ movement at P5).
        services.ImplSingleton<IModeModule, IModule, ModeModule>();

        // KZ port: Core-owned native movement detours (installed once; dispatched to the active player's mode
        // via IKzMovementMode, so any mode plugin shares one set of trampolines — cs2kz's architecture).
        services.AddSingleton<IModule, MovementModule>();

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

        // KZ port: jumpstats now ships as the external Kreedz.Jumpstats plugin (ISharedSystem + IKzStyleRegistry).

        // KZ port: KZ run semantics (Pro/Standard from teleport count) on the timer (cs2kz src/kz/timer).
        services.ImplSingleton<IKzTimerModule, IModule, KzTimerModule>();

        // KZ port: anticheat now ships as the external Kreedz.Anticheat plugin (ISharedSystem-only, no Core deps).

        // KZ port: rotating tips (cs2kz src/kz/tip).
        services.ImplSingleton<ITipModule, IModule, TipModule>();

        // KZ port: ban management — !ban/!unban admin commands + connect-time kick (cs2kz src/kz/ban).
        services.ImplSingleton<IBanModule, IModule, BanModule>();

        // KZ port: the global-API client now ships as the external Kreedz.Global plugin
        // (reads IKzRunService.RunFinished + IKzModeRegistry; dormant without kz_global_apikey).
    }
}
