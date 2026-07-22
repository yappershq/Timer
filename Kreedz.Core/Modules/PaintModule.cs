/*
 * !paint — cs2kz paint. Marks every takeoff spot so you can read your bhop lines: green = perf,
 * red = non-perf. Preference-persisted ("paint").
 *
 * Primary path = REAL engine decals, exactly like cs2kz: UTIL_DecalTrace (sig via kreedz-paint
 * gamedata) fires the decal at a downward ground trace, with the symbol from tier0's exported
 * _MakeGlobalSymbol("paint"); the resulting GE_PlaceDecalEvent is recolored in a PostEventAbstract
 * hook while our pendingPaint flag is set (cs2kz's exact recolor mechanism, ABGR). If either native
 * fails to resolve on a new build, the module falls back automatically to the env_beam X-mark ring.
 */

using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using System.Runtime.CompilerServices;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal sealed unsafe class PaintModule : IModule
{
    // ─── Real-decal path (cs2kz UTIL_DecalTrace + PlaceDecalEvent recolor) ───

    private static delegate* unmanaged<void*, ulong*, float, void> _fnDecalTrace;
    private static ulong _paintSymbol;
    private bool _decalsAvailable;
    private int  _pendingPaintSlot = -1;
    private bool _pendingPaintPerf;

    private void InitDecalPath()
    {
        try
        {
            var gameData = _bridge.ModSharp.GetGameData();
            gameData.Register("kreedz-paint.games");

            _fnDecalTrace = (delegate* unmanaged<void*, ulong*, float, void>) gameData.GetAddress("DecalTrace");
            _logger.LogInformation("[KZ.Paint] DecalTrace resolved: {Ok}", _fnDecalTrace != null);

            var makeSymbol = (delegate* unmanaged<byte*, ulong>) gameData.GetAddress("_MakeGlobalSymbol");
            _logger.LogInformation("[KZ.Paint] _MakeGlobalSymbol resolved: {Ok}", makeSymbol != null);

            if (makeSymbol != null)
            {
                var name = "paint\0"u8;
                fixed (byte* p = name)
                {
                    _paintSymbol = makeSymbol(p);
                }
            }

            _decalsAvailable = _fnDecalTrace != null && _paintSymbol != 0;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[KZ.Paint] decal natives unavailable — falling back to beam marks");
            _decalsAvailable = false;
        }

        if (_decalsAvailable)
        {
            _bridge.ModSharp.HookNetMessage(ProtobufNetMessageType.GE_PlaceDecalEvent);
            _bridge.HookManager.PostEventAbstract.InstallHookPre(OnPlaceDecal);
            _logger.LogInformation("[KZ.Paint] real-decal path active (DecalTrace + _MakeGlobalSymbol resolved)");
        }
        else
        {
            _logger.LogWarning("[KZ.Paint] real-decal path unavailable — using beam X-marks");
        }
    }

    // cs2kz OnPostEvent GE_PlaceDecalEvent: while our own DecalTrace call is on the stack, restyle
    // the event (game expects ABGR; green = perf, red = non-perf). Foreign decals pass untouched.
    private HookReturnValue<NetworkReceiver> OnPlaceDecal(IPostEventAbstractHookParams param, HookReturnValue<NetworkReceiver> ret)
    {
        if (param.MsgId != ProtobufNetMessageType.GE_PlaceDecalEvent || _pendingPaintSlot < 0)
            return ret;

        var abgr = _pendingPaintPerf
            ? 0xFF00FF00u  // green
            : 0xFF2828FFu; // red
        param.Data.SetUInt32("color", abgr);

        return ret;
    }

    private bool TryPlaceDecal(Sharp.Shared.GameEntities.IPlayerPawn pawn, Vector origin, bool perf, PlayerSlot slot)
    {
        // Ground trace under the takeoff spot — DecalTrace needs a real hit trace to project onto.
        var col  = pawn.GetCollisionProperty();
        var attr = RnQueryShapeAttr.PlayerMovement(col?.CollisionAttribute.InteractsWith ?? default);
        attr.SetEntityToIgnore(pawn, 0);

        var start = new Vector(origin.X, origin.Y, origin.Z + 4f);
        var end   = new Vector(origin.X, origin.Y, origin.Z - 24f);
        var line  = new TraceShapeRay(new TraceShapeLine());
        var tr    = _bridge.PhysicsQueryManager.TraceShape(line, start, end, attr);

        if (!tr.DidHit())
            return false;

        // The managed GameTrace mirrors the native CGameTrace layout (sizeof == 192 per the ModSharp
        // engine header); copy it into a zeroed native-sized buffer for the engine call.
        var buf = stackalloc byte[192];
        Unsafe.InitBlock(buf, 0, 192);
        Unsafe.CopyBlock(buf, &tr, 185);

        var symbol = _paintSymbol;
        _pendingPaintSlot = slot.AsPrimitive();
        _pendingPaintPerf = perf;
        try
        {
            _fnDecalTrace(buf, &symbol, 0f);
        }
        finally
        {
            _pendingPaintSlot = -1;
        }

        return true;
    }

    private const int   MarkCount = 48; // marks per player; 2 beams each
    private const float MarkSize  = 6f;
    private const int   PerfTicks = 2;  // ground ticks <= this at takeoff = perf (jumpstats convention)

    private readonly InterfaceBridge      _bridge;
    private readonly ICommandManager      _commandManager;
    private readonly IPreferencesModule   _prefs;
    private readonly ILogger<PaintModule> _logger;

    private readonly bool[]           _enabled     = new bool[PlayerSlot.MaxPlayerCount];
    private readonly IBaseModelEntity?[][] _marks  = new IBaseModelEntity?[PlayerSlot.MaxPlayerCount][];
    private readonly int[]            _head        = new int[PlayerSlot.MaxPlayerCount];
    private readonly bool[]           _wasGround   = new bool[PlayerSlot.MaxPlayerCount];
    private readonly int[]            _groundTicks = new int[PlayerSlot.MaxPlayerCount];

    public PaintModule(InterfaceBridge bridge, ICommandManager commandManager, IPreferencesModule prefs, ILogger<PaintModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _prefs          = prefs;
        _logger         = logger;
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("paint", (slot, _) =>
        {
            _enabled[slot] = !_enabled[slot];
            _prefs.Set(slot, "paint", _enabled[slot] ? "1" : "0");
            if (!_enabled[slot])
                KillMarks(slot);
            if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
                Loc.Chat(_bridge.LocalizerManager, client, _enabled[slot] ? "Kreedz_Opt_On" : "Kreedz_Opt_Off", "paint");
            return ECommandAction.Handled;
        });

        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        _prefs.Loaded += slot => _enabled[slot] = _prefs.Get(slot, "paint") == "1";
        InitDecalPath();
        return true;
    }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);
        if (_decalsAvailable)
            _bridge.HookManager.PostEventAbstract.RemoveHookPre(OnPlaceDecal);
    }

    private void OnProcessMovePre(Sharp.Shared.HookParams.IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient || !arg.Pawn.IsAlive)
            return;

        var slot     = client.Slot;
        var onGround = arg.Pawn.GroundEntityHandle.IsValid();

        if (_enabled[slot] && !onGround && _wasGround[slot] && arg.Pawn.ActualMoveType == MoveType.Walk)
        {
            var perf = _groundTicks[slot] <= PerfTicks;
            if (!_decalsAvailable || !TryPlaceDecal(arg.Pawn, arg.Pawn.GetAbsOrigin(), perf, slot))
                PlaceMark(slot, arg.Pawn.GetAbsOrigin(), perf);
        }

        _groundTicks[slot] = onGround ? _groundTicks[slot] + 1 : 0;
        _wasGround[slot]   = onGround;
    }

    private void PlaceMark(PlayerSlot slot, Vector origin, bool perf)
    {
        var ring = _marks[slot] ??= new IBaseModelEntity?[MarkCount * 2];
        var kv = new Dictionary<string, KeyValuesVariantValueItem>
        {
            { "rendercolor", perf ? "0 255 0" : "255 40 40" },
            { "BoltWidth", "1" },
        };

        var z = origin.Z + 1f;
        Span<(Vector A, Vector B)> legs =
        [
            (new Vector(origin.X - MarkSize, origin.Y - MarkSize, z), new Vector(origin.X + MarkSize, origin.Y + MarkSize, z)),
            (new Vector(origin.X - MarkSize, origin.Y + MarkSize, z), new Vector(origin.X + MarkSize, origin.Y - MarkSize, z)),
        ];

        foreach (var (a, b) in legs)
        {
            var i = _head[slot];
            _head[slot] = (i + 1) % ring.Length;

            var beam = ring[i];
            if (beam is not { IsValidEntity: true })
            {
                beam = _bridge.EntityManager.SpawnEntitySync<IBaseModelEntity>("env_beam", kv);
                if (beam is not { IsValidEntity: true })
                    continue;
                ring[i] = beam;
            }
            else
            {
                beam.RenderColor = perf ? new Color32(0, 255, 0, 255) : new Color32(255, 40, 40, 255);
            }

            beam.SetAbsOrigin(a);
            beam.SetNetVar("m_vecEndPos", b);
        }
    }

    private void KillMarks(PlayerSlot slot)
    {
        if (_marks[slot] is not { } ring)
            return;

        foreach (var m in ring)
            if (m is { IsValidEntity: true })
                m.Kill();

        Array.Clear(ring);
        _head[slot] = 0;
    }
}
