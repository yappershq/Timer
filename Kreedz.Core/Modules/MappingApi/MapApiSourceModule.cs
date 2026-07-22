/*
 * yappershq/Kreedz (KZ) — Mapping API entity-keyvalue source
 *
 * Modern kz_ maps describe their timer via the cs2kz Mapping API: trigger_multiple entities carry a
 * timer_trigger_type keyvalue (+ per-type keyvalues) and info_target descriptors declare courses. ModSharp
 * exposes no managed spawn-keyvalue read, so — exactly like Kxnrl/StripperSharp does — we detour
 * IWorldRendererMgr::CreateWorldInternal, walk the world's entity lumps, and read each entity's native
 * CEntityKeyValues (via the ported Kreedz.Natives layer). Those keyvalues feed the MappingApiRegistry
 * parser (previously dead code) to build the course + zone-trigger tables.
 *
 * This is the source; correlating the parsed zone triggers with their spawned entity geometry and
 * registering them in ZoneModule is the next step. For now it parses + logs what it finds so the read
 * path is verifiable on a live map.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Hooks;
using Sharp.Shared.Types;
using Kreedz.Natives;
using Kreedz.Modules.MappingApi;

namespace Kreedz.Modules;

internal interface IMapApiSource
{
    /// <summary>The parsed mapping-API registry for the current map (courses + zone triggers).</summary>
    MappingApiRegistry Registry { get; }

    /// <summary>
    /// Resolve a spawned trigger_multiple to its parsed KZ zone by matching origin (modern kz_ maps leave
    /// zone targetnames empty and describe them purely via keyvalues, so origin is the correlation key).
    /// Returns false for non-KZ triggers / legacy maps → the caller falls back to targetname matching.
    /// </summary>
    bool TryResolveZone(Vector origin, out KzTriggerType type, out int number);

    /// <summary>Resolve a spawned trigger_multiple to its mapping-API teleport data (Teleport/MultiBhop/
    /// SingleBhop/SequentialBhop family) by origin.</summary>
    bool TryResolveTeleport(Vector origin, out KzMapTeleportData data);

    /// <summary>Resolve a named teleport destination entity to its origin + angles.</summary>
    bool TryResolveDestination(string name, out Vector origin, out Vector angles);

    /// <summary>Resolve a spawned trigger_multiple to its mapping-API push data by origin.</summary>
    bool TryResolvePush(Vector origin, out KzMapPushData push);

    /// <summary>Resolve an anti-bhop trigger by origin. time = timer_anti_bhop_time (0 = always block jumps).</summary>
    bool TryResolveAntiBhop(Vector origin, out float time);

    /// <summary>Resolve a modifier trigger (gravity/duck/disable-*) by origin.</summary>
    bool TryResolveModifier(Vector origin, out KzMapModifier modifier);

    /// <summary>Resolve a keyvalue-less momentary trigger (ResetCheckpoints / SingleBhopReset) by origin.</summary>
    bool TryResolveSimpleTrigger(Vector origin, out KzTriggerType type);
}

// cs2kz KzMapTeleport (kz_mappingapi.h) — the timer_teleport_* keyvalue set, shared by the plain Teleport
// trigger and the three bhop variants (which type it is drives the per-tick teleport rules).
internal readonly record struct KzMapTeleportData(
    KzTriggerType Type,
    string Destination,
    float Delay,
    bool UseDestAngles,
    bool ResetSpeed,
    bool Reorient,
    bool Relative);

// cs2kz KzMapPush conditions (kz_mappingapi.h).
[Flags]
internal enum KzPushCondition : uint
{
    StartTouch = 1,
    Touch      = 2,
    EndTouch   = 4,
    JumpEvent  = 8,
    JumpButton = 16,
    Attack     = 32,
    Attack2    = 64,
    Use        = 128,
}

// cs2kz KzMapPush — the timer_push_* keyvalue set.
internal readonly record struct KzMapPushData(
    Vector Impulse,
    KzPushCondition Conditions,
    bool SetSpeedX,
    bool SetSpeedY,
    bool SetSpeedZ,
    bool CancelOnTeleport,
    float Cooldown,
    float Delay);

// cs2kz KzMapModifier (kz_mappingapi.h) — the timer_modifier_* keyvalue set.
internal readonly record struct KzMapModifier(
    bool DisablePausing,
    bool DisableCheckpoints,
    bool DisableTeleports,
    bool DisableJumpstats,
    bool EnableSlide,
    float Gravity,
    float JumpFactor,
    bool ForceDuck,
    bool ForceUnduck);

internal sealed unsafe class MapApiSourceModule : IModule, IMapApiSource
{
    // The mapping-API keyvalues we read off each entity (cs2kz kz_mappingapi.cpp names). classname routes
    // course-descriptor vs zone-trigger; the rest are parsed by the registry.
    private static readonly string[] MapApiKeys =
    [
        "classname", "targetname", "origin", "hammerUniqueId", "timer_trigger_type",
        "timer_zone_course_descriptor", "timer_zone_split_number", "timer_zone_checkpoint_number",
        "timer_zone_stage_number", "timer_course_number", "timer_course_name", "timer_course_disable_checkpoint",
        "timer_teleport_destination", "timer_teleport_reset_speed", "timer_push_amount",
        "timer_teleport_relative", "timer_teleport_reorient_player", "timer_teleport_use_dest_angles",
        "timer_teleport_delay", "angles",
        "timer_push_abs_speed_x", "timer_push_abs_speed_y", "timer_push_abs_speed_z",
        "timer_push_cancel_on_teleport", "timer_push_cooldown", "timer_push_delay",
        "timer_push_condition_start_touch", "timer_push_condition_touch", "timer_push_condition_end_touch",
        "timer_push_condition_jump_event", "timer_push_condition_jump_button", "timer_push_condition_attack",
        "timer_push_condition_attack2", "timer_push_condition_use",
        "timer_anti_bhop_time", "timer_modifier_gravity", "timer_modifier_jump_impulse",
        "timer_modifier_enable_slide", "timer_modifier_disable_jumpstats", "timer_modifier_disable_teleports",
        "timer_modifier_disable_checkpoints", "timer_modifier_disable_pause", "timer_modifier_force_duck",
        "timer_modifier_force_unduck",
    ];

    private readonly InterfaceBridge             _bridge;
    private readonly ILogger<MapApiSourceModule> _logger;
    private readonly MappingApiRegistry          _registry = new();

    // Parsed KZ zone triggers keyed by their (rounded) origin — correlated to the spawned trigger_multiple
    // in ZoneModule. Rounded to the nearest unit; KZ zones are hundreds of units apart so this is unambiguous.
    private readonly Dictionary<(int X, int Y, int Z), (KzTriggerType Type, int Number)> _zonesByOrigin = new();

    // Teleport-family triggers (cs2kz KZTRIGGER_TELEPORT/_MULTI_BHOP/_SINGLE_BHOP/_SEQUENTIAL_BHOP):
    // trigger origin → full timer_teleport_* keyvalue set. Destinations are any named entity's
    // origin+angles (info_target / info_teleport_destination), resolved at teleport time.
    private readonly Dictionary<(int X, int Y, int Z), KzMapTeleportData> _teleportsByOrigin = new();
    private readonly Dictionary<string, (Vector Origin, Vector Angles)> _destinations = new(StringComparer.OrdinalIgnoreCase);

    // Push triggers (cs2kz KZTRIGGER_PUSH): trigger origin → impulse (timer_push_amount). Basic add-to-velocity
    // on enter; the per-axis set-speed / condition / cooldown flags are refinements.
    private readonly Dictionary<(int X, int Y, int Z), KzMapPushData> _pushesByOrigin = new();

    private readonly Dictionary<(int X, int Y, int Z), float>         _antibhopsByOrigin = new();
    private readonly Dictionary<(int X, int Y, int Z), KzMapModifier> _modifiersByOrigin = new();

    // Keyvalue-less momentary triggers (cs2kz KZTRIGGER_RESET_CHECKPOINTS / _SINGLE_BHOP_RESET): just a type.
    private readonly Dictionary<(int X, int Y, int Z), KzTriggerType> _simpleByOrigin = new();

    // A map loads through MULTIPLE CreateWorldInternal calls (main world + sub-worlds). Clear the accumulated
    // zones only when the map actually changes, else a later sub-world wipes the main world's zones before the
    // triggers spawn (that was the bug: the 24-entity sub-world cleared the 2 zones the main world stored).
    private string _lastMap = "";

    private IRuntimeNativeHook? _hook;

    private static MapApiSourceModule? _self;
    private static nint               _trampoline;

    public MapApiSourceModule(InterfaceBridge bridge, ILogger<MapApiSourceModule> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public MappingApiRegistry Registry => _registry;

    public bool Init()
    {
        // The CreateWorldInternal sig may already be registered by another module (StripperSharp hooks the
        // same function) — that's fine, it resolves from the global gamedata table either way. Tolerate the
        // "already exists" collision here so it doesn't abort the hook install below.
        try { _bridge.ModSharp.GetGameData().Register("kreedz-mapapi.games"); }
        catch (Exception e) { _logger.LogInformation("[KZ.MapApi] CreateWorldInternal gamedata already present ({m}) — reusing it.", e.Message); }

        try
        {
            CEntityKeyValues.Init(_bridge.ModSharp);
            CKeyValues3.Init(_bridge.ModSharp);

            var hook = _bridge.HookManager.CreateDetourHook();
            hook.Prepare("IWorldRendererMgr::CreateWorldInternal",
                (nint) (delegate* unmanaged<nint, CSingleWorldRep*, nint>) &Hk_CreateWorldInternal);

            if (!hook.Install())
            {
                _logger.LogError("[KZ.MapApi] failed to install CreateWorldInternal detour (bad sig?) — modern kz_ map zones unavailable.");
                return true;
            }

            _hook       = hook;
            _trampoline = hook.Trampoline;
            _self       = this;
            _logger.LogInformation("[KZ.MapApi] entity-keyvalue source installed.");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[KZ.MapApi] mapping-API source unavailable (native failure) — modern kz_ maps won't register zones.");
        }

        return true;
    }

    public void Shutdown()
    {
        _hook?.Uninstall();
        _hook = null;
        _self = null;
    }

    [UnmanagedCallersOnly]
    private static nint Hk_CreateWorldInternal(nint pWorldRendererMgr, CSingleWorldRep* pSingleWorld)
    {
        // Let the engine build the world/lump first, then read the populated keyvalues (StripperSharp order).
        var ret = ((delegate* unmanaged<nint, CSingleWorldRep*, nint>) _trampoline)(pWorldRendererMgr, pSingleWorld);

        try { _self?.ReadWorld(pSingleWorld); }
        catch (Exception e) { _self?._logger.LogError(e, "[KZ.MapApi] error reading world entity keyvalues"); }

        return ret;
    }

    private void ReadWorld(CSingleWorldRep* pSingleWorld)
    {
        if (pSingleWorld == null || pSingleWorld->pWorld == null)
            return;

        var mapName = _bridge.GlobalVars.MapName;
        if (!string.Equals(mapName, _lastMap, StringComparison.Ordinal))
        {
            _lastMap = mapName;
            _registry.Clear();
            _zonesByOrigin.Clear();
            _teleportsByOrigin.Clear();
            _destinations.Clear();
            _pushesByOrigin.Clear();
            _antibhopsByOrigin.Clear();
            _modifiersByOrigin.Clear();
            _simpleByOrigin.Clear();
        }

        ref var lumpHandles = ref pSingleWorld->pWorld->EntityLumps;

        int entities = 0, courses = 0, zones = 0;

        for (var i = 0; i < lumpHandles.Count; i++)
        {
            ref var handle   = ref lumpHandles.Element(i);
            var     lumpData = handle.AsRef().m_pLumpData;
            if (lumpData == null)
                continue;

            for (var j = 0; j < lumpData->EntityKeyValues.Size; j++)
            {
                var kv = lumpData->EntityKeyValues.Element(j).Value;
                if (kv == null)
                    continue;

                entities++;
                var dict = ReadKeyValues(kv);

                // Any named entity with an origin is a potential teleport destination (info_target /
                // info_teleport_destination) — teleport triggers reference these by targetname.
                if (dict.GetValueOrDefault("targetname", "") is { Length: > 0 } destTarget
                    && TryParseOrigin(dict.GetValueOrDefault("origin", ""), out var destOrigin))
                {
                    TryParseOrigin(dict.GetValueOrDefault("angles", ""), out var destAngles); // default 0,0,0
                    _destinations[destTarget] = (destOrigin, destAngles);
                }

                // Route by the keys present (robust to classname variants): a course descriptor carries the
                // timer_course_* keys; a KZ zone/modifier trigger carries timer_trigger_type.
                if (dict.ContainsKey("timer_course_number") || dict.ContainsKey("timer_course_name"))
                {
                    if (_registry.TryAddCourse(dict))
                        courses++;
                }
                else if (dict.ContainsKey("timer_trigger_type"))
                {
                    var type = _registry.TryAddTrigger(dict);
                    if (type != KzTriggerType.Disabled)
                    {
                        zones++;

                        // Store timer zones (Start/End/Split/Checkpoint/Stage) by origin for correlation with
                        // the spawned trigger_multiple in ZoneModule.
                        if (KzTrigger.IsTimerZone(type)
                            && TryParseOrigin(dict.GetValueOrDefault("origin", ""), out var zoneOrigin))
                        {
                            var key = OriginKey(zoneOrigin.X, zoneOrigin.Y, zoneOrigin.Z);
                            _zonesByOrigin[key] = (type, ParseZoneNumber(dict, type));
                            _logger.LogInformation("[KZ.MapApi] stored zone {type} originKey=({x},{y},{z})", type, key.X, key.Y, key.Z);
                        }
                        else if ((KzTrigger.IsTeleport(type) || KzTrigger.IsBhop(type))
                                 && TryParseOrigin(dict.GetValueOrDefault("origin", ""), out var tpOrigin)
                                 && dict.GetValueOrDefault("timer_teleport_destination", "") is { Length: > 0 } tpDest)
                        {
                            var delay = MathF.Max(ParseFloat(dict.GetValueOrDefault("timer_teleport_delay", ""), 0f), 0f);
                            if (KzTrigger.IsBhop(type))
                                delay = MathF.Max(delay, 0.1f); // cs2kz: bhop triggers get a minimum grace

                            _teleportsByOrigin[OriginKey(tpOrigin.X, tpOrigin.Y, tpOrigin.Z)] = new KzMapTeleportData(
                                Type:          type,
                                Destination:   tpDest,
                                Delay:         delay,
                                UseDestAngles: ParseBool(dict.GetValueOrDefault("timer_teleport_use_dest_angles", "")),
                                ResetSpeed:    ParseBool(dict.GetValueOrDefault("timer_teleport_reset_speed", "")),
                                Reorient:      ParseBool(dict.GetValueOrDefault("timer_teleport_reorient_player", "")),
                                Relative:      ParseBool(dict.GetValueOrDefault("timer_teleport_relative", "")));
                        }
                        else if (type == KzTriggerType.Push
                                 && TryParseOrigin(dict.GetValueOrDefault("origin", ""), out var pushOrigin)
                                 && TryParseOrigin(dict.GetValueOrDefault("timer_push_amount", ""), out var impulse))
                        {
                            var conditions = (KzPushCondition) 0;
                            if (ParseBool(dict.GetValueOrDefault("timer_push_condition_start_touch", ""))) conditions |= KzPushCondition.StartTouch;
                            if (ParseBool(dict.GetValueOrDefault("timer_push_condition_touch", "")))       conditions |= KzPushCondition.Touch;
                            if (ParseBool(dict.GetValueOrDefault("timer_push_condition_end_touch", "")))   conditions |= KzPushCondition.EndTouch;
                            if (ParseBool(dict.GetValueOrDefault("timer_push_condition_jump_event", "")))  conditions |= KzPushCondition.JumpEvent;
                            if (ParseBool(dict.GetValueOrDefault("timer_push_condition_jump_button", ""))) conditions |= KzPushCondition.JumpButton;
                            if (ParseBool(dict.GetValueOrDefault("timer_push_condition_attack", "")))      conditions |= KzPushCondition.Attack;
                            if (ParseBool(dict.GetValueOrDefault("timer_push_condition_attack2", "")))     conditions |= KzPushCondition.Attack2;
                            if (ParseBool(dict.GetValueOrDefault("timer_push_condition_use", "")))         conditions |= KzPushCondition.Use;

                            // Legacy/simple maps set no condition — treat as push-on-start-touch (matches
                            // the previous instant-impulse behavior).
                            if (conditions == 0)
                                conditions = KzPushCondition.StartTouch;

                            _pushesByOrigin[OriginKey(pushOrigin.X, pushOrigin.Y, pushOrigin.Z)] = new KzMapPushData(
                                Impulse:          impulse,
                                Conditions:       conditions,
                                SetSpeedX:        ParseBool(dict.GetValueOrDefault("timer_push_abs_speed_x", "")),
                                SetSpeedY:        ParseBool(dict.GetValueOrDefault("timer_push_abs_speed_y", "")),
                                SetSpeedZ:        ParseBool(dict.GetValueOrDefault("timer_push_abs_speed_z", "")),
                                CancelOnTeleport: ParseBool(dict.GetValueOrDefault("timer_push_cancel_on_teleport", "")),
                                Cooldown:         ParseFloat(dict.GetValueOrDefault("timer_push_cooldown", ""), 0.1f),
                                Delay:            ParseFloat(dict.GetValueOrDefault("timer_push_delay", ""), 0f));
                        }
                        else if (type == KzTriggerType.AntiBhop
                                 && TryParseOrigin(dict.GetValueOrDefault("origin", ""), out var abOrigin))
                        {
                            _antibhopsByOrigin[OriginKey(abOrigin.X, abOrigin.Y, abOrigin.Z)] =
                                MathF.Max(ParseFloat(dict.GetValueOrDefault("timer_anti_bhop_time", ""), 0f), 0f); // cs2kz MAX(time,0)
                        }
                        else if (type == KzTriggerType.Modifier
                                 && TryParseOrigin(dict.GetValueOrDefault("origin", ""), out var modOrigin))
                        {
                            _modifiersByOrigin[OriginKey(modOrigin.X, modOrigin.Y, modOrigin.Z)] = new KzMapModifier(
                                DisablePausing:     ParseBool(dict.GetValueOrDefault("timer_modifier_disable_pause", "")),
                                DisableCheckpoints: ParseBool(dict.GetValueOrDefault("timer_modifier_disable_checkpoints", "")),
                                DisableTeleports:   ParseBool(dict.GetValueOrDefault("timer_modifier_disable_teleports", "")),
                                DisableJumpstats:   ParseBool(dict.GetValueOrDefault("timer_modifier_disable_jumpstats", "")),
                                EnableSlide:        ParseBool(dict.GetValueOrDefault("timer_modifier_enable_slide", "")),
                                Gravity:            ParseFloat(dict.GetValueOrDefault("timer_modifier_gravity", ""), 1f),
                                JumpFactor:         ParseFloat(dict.GetValueOrDefault("timer_modifier_jump_impulse", ""), 1f),
                                ForceDuck:          ParseBool(dict.GetValueOrDefault("timer_modifier_force_duck", "")),
                                ForceUnduck:        ParseBool(dict.GetValueOrDefault("timer_modifier_force_unduck", "")));
                        }
                        else if (type is KzTriggerType.ResetCheckpoints or KzTriggerType.SingleBhopReset
                                 && TryParseOrigin(dict.GetValueOrDefault("origin", ""), out var simpleOrigin))
                        {
                            _simpleByOrigin[OriginKey(simpleOrigin.X, simpleOrigin.Y, simpleOrigin.Z)] = type;
                        }
                    }
                }
            }
        }

        _logger.LogInformation("[KZ.MapApi] {map}: scanned {ents} entities → {courses} course(s), {zones} KZ zone trigger(s).",
            _bridge.GlobalVars.MapName, entities, courses, zones);

        foreach (var err in _registry.Errors)
            _logger.LogWarning("[KZ.MapApi] parse: {err}", err);
    }

    private static Dictionary<string, string> ReadKeyValues(CEntityKeyValues* kv)
    {
        var dict = new Dictionary<string, string>(MapApiKeys.Length, StringComparer.OrdinalIgnoreCase);

        foreach (var key in MapApiKeys)
        {
            var member = kv->FindKeyValuesMember(key);
            if (member != null)
                dict[key] = member->GetStringAuto();
        }

        return dict;
    }

    public bool TryResolveZone(Vector origin, out KzTriggerType type, out int number)
    {
        if (_zonesByOrigin.TryGetValue(OriginKey(origin.X, origin.Y, origin.Z), out var z))
        {
            type   = z.Type;
            number = z.Number;
            return true;
        }

        type   = KzTriggerType.Disabled;
        number = 0;
        return false;
    }

    public bool TryResolveTeleport(Vector origin, out KzMapTeleportData data)
        => _teleportsByOrigin.TryGetValue(OriginKey(origin.X, origin.Y, origin.Z), out data);

    public bool TryResolveDestination(string name, out Vector origin, out Vector angles)
    {
        if (_destinations.TryGetValue(name, out var d))
        {
            origin = d.Origin;
            angles = d.Angles;
            return true;
        }

        origin = default;
        angles = default;
        return false;
    }

    public bool TryResolvePush(Vector origin, out KzMapPushData push)
        => _pushesByOrigin.TryGetValue(OriginKey(origin.X, origin.Y, origin.Z), out push);

    public bool TryResolveAntiBhop(Vector origin, out float time)
        => _antibhopsByOrigin.TryGetValue(OriginKey(origin.X, origin.Y, origin.Z), out time);

    public bool TryResolveModifier(Vector origin, out KzMapModifier modifier)
        => _modifiersByOrigin.TryGetValue(OriginKey(origin.X, origin.Y, origin.Z), out modifier);

    public bool TryResolveSimpleTrigger(Vector origin, out KzTriggerType type)
        => _simpleByOrigin.TryGetValue(OriginKey(origin.X, origin.Y, origin.Z), out type);

    private static bool ParseBool(string s) => s is "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static float ParseFloat(string s, float fallback)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static (int X, int Y, int Z) OriginKey(float x, float y, float z)
        => ((int) MathF.Round(x), (int) MathF.Round(y), (int) MathF.Round(z));

    private static bool TryParseOrigin(string s, out Vector origin)
    {
        origin = default;
        if (string.IsNullOrEmpty(s))
            return false;

        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return false;

        if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            origin = new Vector(x, y, z);
            return true;
        }

        return false;
    }

    private static int ParseZoneNumber(Dictionary<string, string> dict, KzTriggerType type)
    {
        var key = type switch
        {
            KzTriggerType.ZoneSplit      => "timer_zone_split_number",
            KzTriggerType.ZoneCheckpoint => "timer_zone_checkpoint_number",
            KzTriggerType.ZoneStage      => "timer_zone_stage_number",
            _                            => null,
        };

        return key is not null && dict.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : 0;
    }
}
