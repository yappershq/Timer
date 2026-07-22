/*
 * Mapping-API anti-bhop + modifier + teleport/bhop trigger runtime (cs2kz src/kz/trigger —
 * UpdateTriggerTouchList, TouchAntibhopTrigger, TouchModifierTrigger, TouchTeleportTrigger,
 * UpdateModifiersInternal).
 *
 * TriggerFix: the touch source is NOT the engine's touch outputs — every tick each player's live
 * collision hull is overlap-traced against trigger shapes (raw TraceShape with HitTrigger + a
 * collect-and-refuse filter, cs2kz CTraceFilterHitAllTriggers), and the diff against the tracked set
 * synthesizes enter/exit. Engine touches can be dodged with subtick movement; this can't. The per-player
 * touching sets then drive the per-tick effects:
 *   - Anti-bhop: while active (time==0, or on-ground shorter than the trigger's grace time, or airborne
 *     for prediction), jumping is blocked by stripping IN_JUMP from the usercmd (buttons + subtick jump
 *     presses) and holding OldJumpPressed — the managed equivalent of cs2kz's per-slot
 *     sv_jump_spam_penalty_time=999999.9 + m_nLastActualJumpPressTick trick (ModSharp has no per-slot
 *     server convar values).
 *   - Modifier: gravity scale per touching tick (reset to 1 when clear), force-duck via DuckOverride,
 *     and the disable-checkpoint/teleport/jumpstats/pause flags exposed for the other modules to query.
 *   - Force-unduck: m_flLastDuckTime pinned huge + ducking flag cleared per tick (via schema net-var).
 *   - NOT portable yet (needs per-slot server convars ModSharp doesn't expose): enable_slide
 *     (sv_standable_normal/sv_walkable_normal/sv_airaccelerate), jump_impulse factor (sv_jump_impulse/sv_staminajumpcost).
 *
 * State clears on spawn (round restarts respawn triggers with new handles, so stale handles never pin
 * a zone effect across rounds).
 */

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Modules.MappingApi;

namespace Kreedz.Modules;

internal interface ITriggerModifiers
{
    /// <summary>timer_modifier_disable_checkpoints — player is in an anti-cp area.</summary>
    bool CheckpointsDisabled(PlayerSlot slot);

    /// <summary>timer_modifier_disable_teleports — checkpoint teleports blocked here.</summary>
    bool TeleportsDisabled(PlayerSlot slot);

    /// <summary>timer_modifier_disable_jumpstats — jumpstats suppressed here.</summary>
    bool JumpstatsDisabled(PlayerSlot slot);

    /// <summary>Fired on entry to a ResetCheckpoints map trigger — TimerModule gates + acts (it owns the
    /// timer-running state and checkpoint access, avoiding a Checkpoint↔Trigger DI cycle).</summary>
    event Action<PlayerSlot>? ResetCheckpointsRequested;
}

internal sealed unsafe class TriggerModifierModule : IModule, ITriggerModifiers
{
    private readonly InterfaceBridge                _bridge;
    private readonly ILogger<TriggerModifierModule> _logger;

    private readonly Dictionary<uint, float>[]         _antibhops = NewDicts<float>();
    private readonly Dictionary<uint, KzMapModifier>[] _modifiers = NewDicts<KzMapModifier>();

    // Teleport-family triggers currently touched (cs2kz triggerTrackers): handle → data + touch time.
    private readonly record struct TeleportState(KzMapTeleportData Data, float StartTouchTime, Vector TriggerOrigin);

    private const int SequentialBhopMemory = 64; // cs2kz CSequentialBhopBuffer size

    private readonly Dictionary<uint, TeleportState>[] _teleports      = NewDicts<TeleportState>();
    private readonly uint[]                            _lastSingleBhop = new uint[PlayerSlot.MaxPlayerCount];
    private readonly Queue<uint>[]                     _seqBhops       = NewQueues();

    private static Queue<uint>[] NewQueues()
    {
        var a = new Queue<uint>[PlayerSlot.MaxPlayerCount];
        for (var i = 0; i < a.Length; i++) a[i] = new Queue<uint>(SequentialBhopMemory);
        return a;
    }

    private readonly float[] _landTime       = new float[PlayerSlot.MaxPlayerCount];
    private readonly bool[]  _wasGround      = new bool[PlayerSlot.MaxPlayerCount];
    private readonly bool[]  _onGround       = new bool[PlayerSlot.MaxPlayerCount];
    private readonly bool[]  _gravityApplied = new bool[PlayerSlot.MaxPlayerCount];
    private readonly bool[]  _duckApplied    = new bool[PlayerSlot.MaxPlayerCount];
    private readonly bool[]  _unduckApplied  = new bool[PlayerSlot.MaxPlayerCount];

    private static Dictionary<uint, T>[] NewDicts<T>()
    {
        var a = new Dictionary<uint, T>[PlayerSlot.MaxPlayerCount];
        for (var i = 0; i < a.Length; i++) a[i] = new Dictionary<uint, T>();
        return a;
    }

    private readonly IMapApiSource _mapApi;
    private Sharp.Shared.Objects.IConVar? _svStandableNormal;

    public TriggerModifierModule(InterfaceBridge bridge, IMapApiSource mapApiSource, ILogger<TriggerModifierModule> logger)
    {
        _bridge = bridge;
        _mapApi = mapApiSource;
        _logger = logger;
    }

    public bool Init()
    {
        _svStandableNormal = _bridge.ConVarManager.FindConVar("sv_standable_normal");
        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        _bridge.HookManager.PlayerProcessMovePost.InstallForward(OnProcessMovePost);
        _bridge.HookManager.PlayerRunCommand.InstallHookPre(OnRunCommandPre);
        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);
        return true;
    }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);
        _bridge.HookManager.PlayerProcessMovePost.RemoveForward(OnProcessMovePost);
        _bridge.HookManager.PlayerRunCommand.RemoveHookPre(OnRunCommandPre);
        _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);
    }

    public event Action<PlayerSlot>? ResetCheckpointsRequested;

    private readonly HashSet<uint>[] _simpleTouched = NewSets(); // reset-checkpoint / single-bhop-reset entry latch

    private void Exit(PlayerSlot slot, uint triggerHandle)
    {
        _antibhops[slot].Remove(triggerHandle);
        _modifiers[slot].Remove(triggerHandle);
        _teleports[slot].Remove(triggerHandle);
        _pushTouched[slot].Remove(triggerHandle);
        _simpleTouched[slot].Remove(triggerHandle);

        if (_pushData[slot].Remove(triggerHandle, out var push)
            && (push.Conditions & KzPushCondition.EndTouch) != 0)
            AddPushEvent(slot, triggerHandle, push);
    }

    // ─── Push triggers (cs2kz AddPushEvent / ApplyPushes / CleanupPushEvents) ───
    //
    // Pushes are delayed EVENTS, not instant impulses: a satisfied condition queues (source, fireTime =
    // now + delay); ApplyPushes fires events whose time falls in the current tick (per-axis setSpeed
    // overrides vs additive); cleanup drops applied events once fireTime + cooldown passes — the event's
    // presence in the queue doubles as the per-trigger cooldown/dedupe gate.

    private struct PushEvent
    {
        public uint          Source;
        public KzMapPushData Data;
        public float         FireTime;
        public bool          Applied;
    }

    private readonly Dictionary<uint, KzMapPushData>[] _pushData   = NewDicts<KzMapPushData>(); // touching push triggers
    private readonly List<PushEvent>[]                 _pushEvents = NewPushLists();
    private readonly UserCommandButtons[]              _newPressed = new UserCommandButtons[PlayerSlot.MaxPlayerCount];

    private static List<PushEvent>[] NewPushLists()
    {
        var a = new List<PushEvent>[PlayerSlot.MaxPlayerCount];
        for (var i = 0; i < a.Length; i++) a[i] = new List<PushEvent>();
        return a;
    }

    private void AddPushEvent(PlayerSlot slot, uint source, in KzMapPushData data)
    {
        foreach (var e in _pushEvents[slot])
            if (e.Source == source)
                return; // pending/cooling event from this trigger — dedupe (cs2kz Find)

        _pushEvents[slot].Add(new PushEvent
        {
            Source   = source,
            Data     = data,
            FireTime = _bridge.GlobalVars.CurTime + data.Delay,
        });
    }

    private void ApplyAndCleanupPushes(PlayerSlot slot, Sharp.Shared.GameEntities.IPlayerPawn pawn)
    {
        var events = _pushEvents[slot];
        if (events.Count == 0)
            return;

        var now       = _bridge.GlobalVars.CurTime;
        var frametime = _bridge.GlobalVars.FrameTime;

        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Applied || now < e.FireTime || now - frametime >= e.FireTime)
                continue;

            var vel = pawn.GetAbsVelocity();
            vel.X = e.Data.SetSpeedX ? e.Data.Impulse.X : vel.X + e.Data.Impulse.X;
            vel.Y = e.Data.SetSpeedY ? e.Data.Impulse.Y : vel.Y + e.Data.Impulse.Y;
            vel.Z = e.Data.SetSpeedZ ? e.Data.Impulse.Z : vel.Z + e.Data.Impulse.Z;
            pawn.SetAbsVelocity(vel);

            e.Applied = true;
            events[i] = e;
        }

        for (var i = events.Count - 1; i >= 0; i--)
            if (events[i].Applied && now - frametime >= events[i].FireTime + events[i].Data.Cooldown)
                events.RemoveAt(i);
    }

    // ─── TriggerFix: per-tick hull-overlap trigger detection (cs2kz UpdateTriggerTouchList) ───

    private readonly HashSet<uint>[] _pushTouched = NewSets();
    private readonly HashSet<uint>   _present     = new();  // scratch — hooks are main-thread
    private readonly List<uint>      _exitScratch = new();

    private static HashSet<uint>[] NewSets()
    {
        var a = new HashSet<uint>[PlayerSlot.MaxPlayerCount];
        for (var i = 0; i < a.Length; i++) a[i] = new HashSet<uint>();
        return a;
    }

    private static readonly List<nint> HitTriggers = new(16);

    [UnmanagedCallersOnly]
    private static bool CollectTriggerFilter(CTraceFilter* filter, nint entityPtr)
    {
        HitTriggers.Add(entityPtr);
        return false; // never actually hit — collect every consulted trigger (cs2kz CTraceFilterHitAllTriggers)
    }

    private void UpdateTouchList(PlayerSlot slot, Sharp.Shared.GameEntities.IPlayerPawn pawn)
    {
        // cs2kz EndTouchAll — noclip drops every touch.
        if (pawn.ActualMoveType == MoveType.NoClip)
        {
            if (_antibhops[slot].Count + _modifiers[slot].Count + _teleports[slot].Count + _pushTouched[slot].Count > 0)
            {
                _antibhops[slot].Clear();
                _modifiers[slot].Clear();
                _teleports[slot].Clear();
                _pushTouched[slot].Clear();
                _pushData[slot].Clear();
                _simpleTouched[slot].Clear();
            }

            return;
        }

        HitTriggers.Clear();

        var attr = new RnQueryShapeAttr
        {
            m_nInteractsWith  = InteractionLayers.Trigger,
            m_nObjectSetMask  = RnQueryObjectSet.All,
            m_nCollisionGroup = CollisionGroupType.Debris, // cs2kz CTraceFilterHitAllTriggers
            HitSolid          = true,
            HitTrigger        = true,
            Unknown           = true,
        };

        var col  = pawn.GetCollisionProperty();
        var hull = new TraceShapeHull
        {
            Mins = col?.Mins ?? new Vector(-16f, -16f, 0f),
            Maxs = col?.Maxs ?? new Vector(16f, 16f, 72f),
        };

        var origin = pawn.GetAbsOrigin();
        _bridge.PhysicsQueryManager.TraceShape(new TraceShapeRay(hull), origin, origin, attr,
            (nint) (delegate* unmanaged<CTraceFilter*, nint, bool>) &CollectTriggerFilter);

        // Resolve overlapped triggers → the mapping-API family; enter the new ones.
        // ponytail: resolves per tick (MakeEntity + origin lookup per overlapped trigger) — cache
        // handle→kind per map if a profile ever shows this; usually 0-3 triggers per player.
        _present.Clear();
        foreach (var ptr in HitTriggers)
        {
            if (_bridge.EntityManager.MakeEntityFromPointer<Sharp.Shared.GameEntities.IBaseEntity>(ptr)
                is not { IsValidEntity: true } trigger)
                continue;

            var handle = trigger.Handle.GetValue();

            if (_antibhops[slot].ContainsKey(handle) || _modifiers[slot].ContainsKey(handle)
                || _teleports[slot].ContainsKey(handle) || _pushTouched[slot].Contains(handle))
            {
                _present.Add(handle);
                continue;
            }

            var triggerOrigin = trigger.GetAbsOrigin();
            if (_mapApi.TryResolveTeleport(triggerOrigin, out var teleport))
                _teleports[slot][handle] = new TeleportState(teleport, _bridge.GlobalVars.CurTime, triggerOrigin);
            else if (_mapApi.TryResolveAntiBhop(triggerOrigin, out var abTime))
                _antibhops[slot][handle] = abTime;
            else if (_mapApi.TryResolveModifier(triggerOrigin, out var modifier))
                _modifiers[slot][handle] = modifier;
            else if (_mapApi.TryResolvePush(triggerOrigin, out var push))
            {
                _pushData[slot][handle] = push;
                _pushTouched[slot].Add(handle);
                if ((push.Conditions & KzPushCondition.StartTouch) != 0)
                    AddPushEvent(slot, handle, push);
            }
            else if (_mapApi.TryResolveSimpleTrigger(triggerOrigin, out var simpleType))
            {
                // Momentary keyvalue-less triggers — act once on entry (cs2kz OnMappingApiTriggerStartTouchPost).
                if (_simpleTouched[slot].Add(handle))
                {
                    if (simpleType == KzTriggerType.SingleBhopReset)
                    {
                        _lastSingleBhop[slot] = 0;
                        _seqBhops[slot].Clear(); // cs2kz ResetBhopState
                    }
                    else // ResetCheckpoints
                    {
                        ResetCheckpointsRequested?.Invoke(slot); // TimerModule gates on timer-running + prints
                    }
                }
            }
            else
            {
                continue; // not a mapping-API trigger
            }

            _present.Add(handle);
        }

        // Exit everything tracked that the hull no longer overlaps.
        _exitScratch.Clear();
        foreach (var h in _antibhops[slot].Keys)
            if (!_present.Contains(h)) _exitScratch.Add(h);
        foreach (var h in _modifiers[slot].Keys)
            if (!_present.Contains(h)) _exitScratch.Add(h);
        foreach (var h in _teleports[slot].Keys)
            if (!_present.Contains(h)) _exitScratch.Add(h);
        foreach (var h in _pushTouched[slot])
            if (!_present.Contains(h)) _exitScratch.Add(h);

        foreach (var h in _exitScratch)
            Exit(slot, h);
    }

    public bool CheckpointsDisabled(PlayerSlot slot) => AnyModifier(slot, static m => m.DisableCheckpoints);

    public bool TeleportsDisabled(PlayerSlot slot) => AnyModifier(slot, static m => m.DisableTeleports);

    public bool JumpstatsDisabled(PlayerSlot slot) => AnyModifier(slot, static m => m.DisableJumpstats);

    private bool AnyModifier(PlayerSlot slot, Func<KzMapModifier, bool> pred)
    {
        foreach (var m in _modifiers[slot].Values)
            if (pred(m))
                return true;

        return false;
    }

    // cs2kz TouchAntibhopTrigger — jump-block is active while: no grace time set, or the player hasn't
    // been grounded past the trigger's grace time, or they're airborne (for prediction).
    private bool AntiBhopActive(PlayerSlot slot)
    {
        if (_antibhops[slot].Count == 0)
            return false;

        var timeOnGround = _bridge.GlobalVars.CurTime - _landTime[slot];
        foreach (var time in _antibhops[slot].Values)
        {
            if (time == 0f || timeOnGround <= time || !_onGround[slot])
                return true;
        }

        return false;
    }

    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient || !arg.Pawn.IsAlive)
            return;

        var slot = client.Slot;

        _preOrigin[slot] = arg.Pawn.GetAbsOrigin(); // start-of-tick position for the path-swept TriggerFix

        UpdateTouchList(slot, arg.Pawn); // TriggerFix — refresh the touching sets from a live hull overlap

        var onGround = arg.Pawn.GroundEntityHandle.IsValid();
        var tookOff  = !onGround && _wasGround[slot];
        // cs2kz gates KZ_PUSH_JUMP_EVENT on player->jumped (a real jump), not any ground-leave. Managed
        // stand-in: an upward takeoff impulse (walking off a ledge has vz ~0/negative).
        var jumped   = tookOff && arg.Pawn.GetAbsVelocity().Z > 150f;

        if (onGround && !_wasGround[slot])
            _landTime[slot] = _bridge.GlobalVars.CurTime;

        _onGround[slot]  = onGround;
        _wasGround[slot] = onGround;

        // cs2kz OnStopTouchGround — leaving the ground while on bhop triggers records them for the
        // single/sequential "can't repeat" rules. Any bhop type updates lastSingleBhop (jumping between
        // a multi and a single must work); only sequential types enter the sequential memory.
        if (tookOff)
        {
            foreach (var (handle, st) in _teleports[slot])
            {
                if (!KzTrigger.IsBhop(st.Data.Type))
                    continue;

                if (st.Data.Type == KzTriggerType.SequentialBhop)
                {
                    if (_seqBhops[slot].Count >= SequentialBhopMemory)
                        _seqBhops[slot].Dequeue();
                    _seqBhops[slot].Enqueue(handle);
                }

                _lastSingleBhop[slot] = handle;
            }
        }

        // cs2kz ResetBhopState — grounded/laddered with no bhop trigger under you clears the memory.
        if ((onGround || arg.Pawn.ActualMoveType == MoveType.Ladder) && !AnyBhopTouching(slot))
        {
            _lastSingleBhop[slot] = 0;
            _seqBhops[slot].Clear();
        }

        // Push conditions that depend on this tick's inputs/movement (cs2kz TouchPushTrigger + OnStopTouchGround).
        var pressed = _newPressed[slot];
        _newPressed[slot] = 0;
        if (_pushData[slot].Count > 0)
        {
            foreach (var (handle, push) in _pushData[slot])
            {
                var c = push.Conditions;
                if ((c & KzPushCondition.Touch) != 0
                    || ((c & KzPushCondition.JumpButton) != 0 && (pressed & UserCommandButtons.Jump) != 0)
                    || ((c & KzPushCondition.Attack) != 0 && (pressed & UserCommandButtons.Attack) != 0)
                    || ((c & KzPushCondition.Attack2) != 0 && (pressed & UserCommandButtons.Attack2) != 0)
                    || ((c & KzPushCondition.Use) != 0 && (pressed & UserCommandButtons.Use) != 0)
                    || ((c & KzPushCondition.JumpEvent) != 0 && jumped))
                    AddPushEvent(slot, handle, push);
            }
        }

        ApplyAndCleanupPushes(slot, arg.Pawn);

        EvaluateTeleports(slot, arg.Pawn, onGround);

        // Anti-bhop: hold the legacy jump latch every active tick (cs2kz ApplyAntiBhop).
        if (AntiBhopActive(slot))
            arg.Service.OldJumpPressed = true;

        // Modifier gravity (cs2kz TouchModifierTrigger): applied per touching tick, last trigger wins;
        // reset to 1 once no gravity modifier is touching.
        var gravity = 1f;
        foreach (var m in _modifiers[slot].Values)
            if (m.Gravity != 1f)
                gravity = m.Gravity;

        if (gravity != 1f)
        {
            arg.Pawn.GravityScale = gravity;
            _gravityApplied[slot] = true;
        }
        else if (_gravityApplied[slot])
        {
            arg.Pawn.GravityScale = 1f;
            _gravityApplied[slot] = false;
        }

        // Forced duck via the movement service's duck override (cs2kz ApplyForcedDuck).
        var forceDuck = AnyModifier(slot, static m => m.ForceDuck);
        if (forceDuck)
        {
            arg.Service.DuckOverride = true;
            _duckApplied[slot]       = true;
        }
        else if (_duckApplied[slot])
        {
            arg.Service.DuckOverride = false;
            _duckApplied[slot]       = false;
        }

        // Forced unduck (cs2kz ApplyForcedUnduck): pin the last-duck time huge so the player can't re-duck,
        // and clear the ducking flag each tick (they can be in a spot that isn't unduckable, e.g. a tunnel).
        // m_flLastDuckTime is reached via the schema net-var API — the typed field isn't on the interface.
        var forceUnduck = AnyModifier(slot, static m => m.ForceUnduck);
        if (forceUnduck)
        {
            arg.Service.SetNetVar("m_flLastDuckTime", 100000f);
            arg.Pawn.Flags &= ~EntityFlags.Ducking;
            _unduckApplied[slot] = true;
        }
        else if (_unduckApplied[slot])
        {
            arg.Service.SetNetVar("m_flLastDuckTime", 0f); // cs2kz CancelForcedUnduck
            _unduckApplied[slot] = false;
        }
    }

    // ─── Path-swept TriggerFix (cs2kz VNL/CKZ OnTryPlayerMovePost → TouchTriggersAlongPath) ───
    //
    // A fast player can cross a thin trigger entirely within one tick — the per-tick overlap scan
    // samples only the end position and would miss it. Sweep the (slightly XY-shrunk, per cs2kz)
    // hull from the tick's start to end position and fire the discrete trigger effects for anything
    // crossed but not currently overlapped: plain teleports (delay <= 0) and pushes. Momentary
    // crossings of anti-bhop/modifier zones have no lasting effect, and bhop teleports require
    // standing on them — nothing to do for those.
    // ponytail: sweeps start→end in one segment; cs2kz sweeps each TryPlayerMove bump segment, so a
    // path that bends around a corner mid-tick can still cut it. Upgrade = replay the bump points.

    private readonly Vector[] _preOrigin = new Vector[PlayerSlot.MaxPlayerCount];

    private static readonly List<Vector> PathScratch = new(8);

    private void OnProcessMovePost(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient || !arg.Pawn.IsAlive)
            return;

        var slot = client.Slot;
        var pawn = arg.Pawn;

        if (pawn.ActualMoveType == MoveType.NoClip)
            return;

        // Bump-segment path replay (cs2kz VNL OnTryPlayerMove): when the engine ran TryPlayerMove this
        // tick for an airborne move, re-predict its bump path from the exact captured inputs and sweep
        // every bump-to-bump pair — a path that bends around a corner mid-tick can't cut triggers.
        if (MovementModule.TpmTick[slot] == _bridge.GlobalVars.TickCount && !_onGround[slot])
        {
            PredictBumpPath(pawn,
                MovementModule.TpmOrigin[slot],
                MovementModule.TpmVelocity[slot],
                MovementModule.TpmTime[slot],
                PathScratch);

            if (PathScratch.Count > 1)
            {
                for (var i = 0; i < PathScratch.Count - 1; i++)
                    SweepSegment(slot, pawn, PathScratch[i], PathScratch[i + 1]);

                return; // path replay covered the tick — single-segment fallback not needed
            }
        }

        SweepSegment(slot, pawn, _preOrigin[slot], pawn.GetAbsOrigin());
    }

    private void SweepSegment(PlayerSlot slot, Sharp.Shared.GameEntities.IPlayerPawn pawn, Vector from, Vector to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dz = to.Z - from.Z;
        if (dx * dx + dy * dy + dz * dz < 4f) // < 2u moved — the overlap scan already covers it
            return;

        HitTriggers.Clear();

        var attr = new RnQueryShapeAttr
        {
            m_nInteractsWith  = InteractionLayers.Trigger,
            m_nObjectSetMask  = RnQueryObjectSet.All,
            m_nCollisionGroup = CollisionGroupType.Debris,
            HitSolid          = true,
            HitTrigger        = true,
            Unknown           = true,
        };

        var col  = pawn.GetCollisionProperty();
        var mins = col?.Mins ?? new Vector(-16f, -16f, 0f);
        var maxs = col?.Maxs ?? new Vector(16f, 16f, 72f);
        var hull = new TraceShapeHull
        {
            Mins = new Vector(mins.X + 0.03125f, mins.Y + 0.03125f, mins.Z), // cs2kz shrinks XY for the sweep
            Maxs = new Vector(maxs.X - 0.03125f, maxs.Y - 0.03125f, maxs.Z),
        };

        _bridge.PhysicsQueryManager.TraceShape(new TraceShapeRay(hull), from, to, attr,
            (nint) (delegate* unmanaged<CTraceFilter*, nint, bool>) &CollectTriggerFilter);

        foreach (var ptr in HitTriggers)
        {
            if (_bridge.EntityManager.MakeEntityFromPointer<Sharp.Shared.GameEntities.IBaseEntity>(ptr)
                is not { IsValidEntity: true } trigger)
                continue;

            var handle = trigger.Handle.GetValue();
            if (_teleports[slot].ContainsKey(handle) || _pushTouched[slot].Contains(handle)
                || _antibhops[slot].ContainsKey(handle) || _modifiers[slot].ContainsKey(handle))
                continue; // currently overlapped — the per-tick engine owns it

            var triggerOrigin = trigger.GetAbsOrigin();
            if (_mapApi.TryResolveTeleport(triggerOrigin, out var teleport))
            {
                if (!KzTrigger.IsBhop(teleport.Type) && teleport.Delay <= 0f)
                {
                    ExecuteTeleport(slot, pawn, new TeleportState(teleport, _bridge.GlobalVars.CurTime, triggerOrigin));
                    break; // teleported — the rest of the path no longer applies
                }
            }
            else if (_mapApi.TryResolvePush(triggerOrigin, out var push))
            {
                // Crossed within one tick = a start-touch + touch that the overlap scan never saw.
                if ((push.Conditions & (KzPushCondition.StartTouch | KzPushCondition.Touch)) != 0)
                    AddPushEvent(slot, handle, push);
            }
        }
    }

    // ─── cs2kz VNL OnTryPlayerMove — CS2 TryPlayerMove re-prediction (bump/clip loop) ───
    //
    // Reproduces the engine's TryPlayerMove from the captured entry inputs purely to collect the
    // position at each bump (wall clip) — the sweep pairs. Faithful except: the jump-precision error
    // term (m_flAccumulatedJumpError, unexposed; sub-0.03u effect) and the sv_bounce/surface-friction
    // overbounce factor (both mode tables set sv_bounce 0 → factor is exactly 1).

    private static void ClipVelocity(in Vector inVel, in Vector normal, out Vector outVel, float overbounce)
    {
        var backoff = -((normal.X * inVel.X) + (normal.Y * inVel.Y) + (normal.Z * inVel.Z)) * overbounce;
        backoff = MathF.Max(backoff, 0f) + 0.03125f;
        outVel = new Vector(normal.X * backoff + inVel.X, normal.Y * backoff + inVel.Y, normal.Z * backoff + inVel.Z);
    }

    private void PredictBumpPath(Sharp.Shared.GameEntities.IPlayerPawn pawn, Vector origin, Vector velocity, float timeLeft, List<Vector> outOrigins)
    {
        outOrigins.Clear();

        var col = pawn.GetCollisionProperty();
        if (col is null || timeLeft <= 0f)
            return;

        var hull = new TraceShapeRay(new TraceShapeHull { Mins = col.Mins, Maxs = col.Maxs });
        var standableZ = _svStandableNormal?.GetFloat() ?? 0.7f;

        Span<Vector> planes = stackalloc Vector[5];
        var numplanes        = 0;
        var originalVelocity = velocity;
        var primalVelocity   = velocity;
        var allFraction      = 0f;

        for (var bump = 0; bump < 4; bump++)
        {
            var speed = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y + velocity.Z * velocity.Z);
            if (speed == 0f)
                break;

            var end = new Vector(origin.X + velocity.X * timeLeft, origin.Y + velocity.Y * timeLeft, origin.Z + velocity.Z * timeLeft);
            if (numplanes == 1)
                end += planes[0] * 0.03125f;

            var attr = RnQueryShapeAttr.PlayerMovement(col.CollisionAttribute.InteractsWith);
            attr.SetEntityToIgnore(pawn, 0);
            var pm = _bridge.PhysicsQueryManager.TraceShape(hull, origin, end, attr);

            var fraction = pm.Fraction;
            if (allFraction == 0f && fraction < 1f && speed * timeLeft >= 0.03125f && fraction * speed * timeLeft < 0.03125f)
                fraction = 0f;

            if (fraction * MathF.Max(1f, speed) > 0.03125f)
            {
                origin           = pm.EndPosition;
                originalVelocity = velocity;
                numplanes        = 0;
            }

            allFraction += fraction;
            outOrigins.Add(pm.EndPosition);

            if (fraction == 1f)
                break;

            timeLeft -= fraction * timeLeft;

            if (numplanes >= 5)
                break;

            var normal = pm.PlaneNormal;

            // Standable-landing stop (cs2kz 2024-11-07): descending onto standable ground with ~no 2D speed.
            if (velocity.Z < 0f && normal.Z >= standableZ
                && MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y) < 1f)
                break;

            planes[numplanes++] = normal;

            if (numplanes == 1)
            {
                ClipVelocity(originalVelocity, planes[0], out var clipped, 1f);
                velocity         = clipped;
                originalVelocity = clipped;
            }
            else
            {
                int i, j;
                for (i = 0; i < numplanes; i++)
                {
                    ClipVelocity(originalVelocity, planes[i], out velocity, 1f);
                    for (j = 0; j < numplanes; j++)
                        if (j != i && velocity.X * planes[j].X + velocity.Y * planes[j].Y + velocity.Z * planes[j].Z < 0f)
                            break;
                    if (j == numplanes)
                        break;
                }

                if (i == numplanes)
                {
                    if (numplanes != 2)
                        break;

                    // Slide along the crease of the two planes.
                    var dir = new Vector(
                        planes[0].Y * planes[1].Z - planes[0].Z * planes[1].Y,
                        planes[0].Z * planes[1].X - planes[0].X * planes[1].Z,
                        planes[0].X * planes[1].Y - planes[0].Y * planes[1].X);
                    var len = MathF.Sqrt(dir.X * dir.X + dir.Y * dir.Y + dir.Z * dir.Z);
                    if (len < 1e-6f)
                        break;
                    dir *= 1f / len;
                    var d = dir.X * velocity.X + dir.Y * velocity.Y + dir.Z * velocity.Z;
                    velocity = dir * d;
                }

                if (velocity.X * primalVelocity.X + velocity.Y * primalVelocity.Y + velocity.Z * primalVelocity.Z <= 0f)
                    break;
            }
        }
    }

    private bool AnyBhopTouching(PlayerSlot slot)
    {
        foreach (var st in _teleports[slot].Values)
            if (KzTrigger.IsBhop(st.Data.Type))
                return true;

        return false;
    }

    // cs2kz TouchTeleportTrigger — per-tick teleport decision for the teleport family. Bhop triggers only
    // fire on the ground after outstaying the grace delay (or on single/sequential repeat); plain teleports
    // fire immediately (delay <= 0) or after the delay.
    private void EvaluateTeleports(PlayerSlot slot, Sharp.Shared.GameEntities.IPlayerPawn pawn, bool onGround)
    {
        if (_teleports[slot].Count == 0)
            return;

        var now = _bridge.GlobalVars.CurTime;

        foreach (var (handle, st) in _teleports[slot])
        {
            var isBhop         = KzTrigger.IsBhop(st.Data.Type);
            var shouldTeleport = false;

            if (isBhop)
            {
                if (!onGround)
                    continue;

                var effectiveStart = MathF.Max(_landTime[slot], st.StartTouchTime);
                if (now - effectiveStart > st.Data.Delay)
                    shouldTeleport = true;
                else if (st.Data.Type == KzTriggerType.SingleBhop)
                    shouldTeleport = _lastSingleBhop[slot] == handle;
                else if (st.Data.Type == KzTriggerType.SequentialBhop)
                    shouldTeleport = _seqBhops[slot].Contains(handle);
            }
            else
            {
                shouldTeleport = st.Data.Delay <= 0f || now - st.StartTouchTime > st.Data.Delay;
            }

            if (shouldTeleport && ExecuteTeleport(slot, pawn, st))
                break; // one teleport per tick; the rest re-evaluate next tick
        }
    }

    private bool ExecuteTeleport(PlayerSlot slot, Sharp.Shared.GameEntities.IPlayerPawn pawn, in TeleportState st)
    {
        if (!_mapApi.TryResolveDestination(st.Data.Destination, out var destOrigin, out var destAngles))
        {
            _logger.LogWarning("[KZ.Trigger] invalid teleport destination \"{dest}\"", st.Data.Destination);
            return false;
        }

        var reorient    = st.Data.Reorient && destAngles.Y != 0f;
        var finalOrigin = destOrigin;

        if (st.Data.Relative)
        {
            var offset = pawn.GetAbsOrigin() - st.TriggerOrigin;
            if (reorient)
                offset = RotateYaw(offset, destAngles.Y);
            finalOrigin = destOrigin + offset;
        }

        var angles   = pawn.GetEyeAngles();
        var velocity = pawn.GetAbsVelocity();

        if (reorient)
        {
            velocity  = RotateYaw(velocity, destAngles.Y);
            angles.Y -= destAngles.Y; // cs2kz does exactly this (known quirk noted upstream)
        }
        else if (st.Data.UseDestAngles)
        {
            angles = destAngles;
        }

        pawn.Teleport(finalOrigin, angles, st.Data.ResetSpeed ? new Vector() : velocity);

        // cs2kz OnTeleport — drop pending push events flagged cancel_on_teleport.
        _pushEvents[slot].RemoveAll(static e => e.Data.CancelOnTeleport); // cs2kz drops ALL cancel-on-tp events
        return true;
    }

    private static Vector RotateYaw(Vector v, float yawDeg)
    {
        var (sin, cos) = MathF.SinCos(yawDeg * MathF.PI / 180f);
        return new Vector(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos, v.Z);
    }

    // Strip jump input at the usercmd level while anti-bhop is active — kills both the button path and
    // the subtick jump-press path before movement processing sees them.
    private HookReturnValue<EmptyHookReturn> OnRunCommandPre(IPlayerRunCommandHookParams param, HookReturnValue<EmptyHookReturn> ret)
    {
        var client = param.Client;
        if (!client.IsValid || client.IsFakeClient)
            return ret;

        _newPressed[client.Slot] |= param.ChangedButtons & param.KeyButtons; // push button conditions

        if (AntiBhopActive(client.Slot))
        {
            param.KeyButtons     &= ~UserCommandButtons.Jump;
            param.ChangedButtons &= ~UserCommandButtons.Jump;

            for (var i = 0; i < param.SubtickMoveSize; i++)
            {
                var step = param.GetSubtickMove(i);
                if (step != null && step->Buttons == UserCommandButtons.Jump && step->Pressed)
                    step->Pressed = false; // turn the press into a harmless release
            }
        }

        return ret;
    }

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var slot = @params.Client.Slot;
        _antibhops[slot].Clear();
        _modifiers[slot].Clear();
        _teleports[slot].Clear();
        _pushTouched[slot].Clear();
        _pushData[slot].Clear();
        _simpleTouched[slot].Clear();
        _pushEvents[slot].Clear();
        _seqBhops[slot].Clear();
        _lastSingleBhop[slot] = 0;
        _newPressed[slot]     = 0;
        _gravityApplied[slot] = false;
        _duckApplied[slot]    = false;
    }
}
