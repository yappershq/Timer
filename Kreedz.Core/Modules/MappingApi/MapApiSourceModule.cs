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
}

internal sealed unsafe class MapApiSourceModule : IModule, IMapApiSource
{
    // The mapping-API keyvalues we read off each entity (cs2kz kz_mappingapi.cpp names). classname routes
    // course-descriptor vs zone-trigger; the rest are parsed by the registry.
    private static readonly string[] MapApiKeys =
    [
        "classname", "targetname", "origin", "hammerUniqueId", "timer_trigger_type",
        "timer_zone_course_descriptor", "timer_zone_split_number", "timer_zone_checkpoint_number",
        "timer_zone_stage_number", "timer_course_number", "timer_course_name", "timer_course_disable_checkpoint",
    ];

    private readonly InterfaceBridge             _bridge;
    private readonly ILogger<MapApiSourceModule> _logger;
    private readonly MappingApiRegistry          _registry = new();

    // Parsed KZ zone triggers keyed by their (rounded) origin — correlated to the spawned trigger_multiple
    // in ZoneModule. Rounded to the nearest unit; KZ zones are hundreds of units apart so this is unambiguous.
    private readonly Dictionary<(int X, int Y, int Z), (KzTriggerType Type, int Number)> _zonesByOrigin = new();

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

        _registry.Clear();
        _zonesByOrigin.Clear();

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
                            _zonesByOrigin[OriginKey(zoneOrigin.X, zoneOrigin.Y, zoneOrigin.Z)] = (type, ParseZoneNumber(dict, type));
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
