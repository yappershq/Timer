/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ jumpstats foundation (cs2kz src/kz/jumpstats). Detects takeoffs/landings on the per-tick movement
 * hook, computes jump distance, classifies LongJump vs Bhop by pre-takeoff ground time, and reports the
 * distance + tier (Meh→Wrecker) when it clears the min tier — the satisfying "263.1u — Impressive!"
 * feedback. This is the core loop; the full 1:1 port (all ~20 stats: sync/strafes/badAngles/overlap/
 * airpath/edge/block, per-mode tier tables, and the strict validation that voids external/styled/water
 * jumps) fills in alongside the P5 movement engine (where takeoff/landing origin correction lives).
 */

using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Modules;

internal interface IJumpstatsModule;

internal enum JumpType { LongJump, Bhop }

internal enum DistanceTier { None, Meh, Impressive, Perfect, Godlike, Ownage, Wrecker }

internal sealed class JumpstatsModule : IModule, IJumpstatsModule
{
    private const float OffsetUnits = 32f;   // KZ block offset added to raw horizontal distance
    private const int   PerfTicks   = 2;     // ground ticks ≤ this before takeoff → treated as a bhop (perf-ish)
    private const float MinTierDist = 217f;  // below the Meh threshold → not announced

    private readonly InterfaceBridge         _bridge;
    private readonly IKzStyleModule          _styleModule;
    private readonly ILogger<JumpstatsModule> _logger;

    private readonly bool[]    _wasOnGround = new bool[PlayerSlot.MaxPlayerCount];
    private readonly int[]     _groundTicks = new int[PlayerSlot.MaxPlayerCount];
    private readonly bool[]    _tracking    = new bool[PlayerSlot.MaxPlayerCount];
    private readonly Vector[]  _takeoff     = new Vector[PlayerSlot.MaxPlayerCount];
    private readonly JumpType[] _type       = new JumpType[PlayerSlot.MaxPlayerCount];

    public JumpstatsModule(InterfaceBridge bridge, IKzStyleModule styleModule, ILogger<JumpstatsModule> logger)
    {
        _bridge      = bridge;
        _styleModule = styleModule;
        _logger      = logger;
    }

    public bool Init()
    {
        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnProcessMovePre);
        return true;
    }

    public void Shutdown() => _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovePre);

    private void OnProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;
        if (!client.IsValid || client.IsFakeClient) return;

        var pawn = arg.Pawn;
        if (!pawn.IsAlive) return;

        var slot     = client.Slot;
        var onGround = pawn.GroundEntityHandle.IsValid();
        var origin   = pawn.GetAbsOrigin();

        if (_wasOnGround[slot] && !onGround)
        {
            // Takeoff.
            _takeoff[slot]   = origin;
            _type[slot]      = _groundTicks[slot] <= PerfTicks ? JumpType.Bhop : JumpType.LongJump;
            _tracking[slot]  = pawn.ActualMoveType is MoveType.Walk; // ignore noclip/ladder starts
        }
        else if (!_wasOnGround[slot] && onGround && _tracking[slot])
        {
            // Landing.
            _tracking[slot] = false;
            var dx   = origin.X - _takeoff[slot].X;
            var dy   = origin.Y - _takeoff[slot].Y;
            var dist = System.MathF.Sqrt(dx * dx + dy * dy) + OffsetUnits;

            if (dist >= MinTierDist && !_styleModule.HasAnyStyle(slot)) // styled runs don't count (1:1)
                Report(slot, _type[slot], dist);
        }

        _groundTicks[slot] = onGround ? _groundTicks[slot] + 1 : 0;
        _wasOnGround[slot] = onGround;
    }

    private void Report(PlayerSlot slot, JumpType type, float distance)
    {
        var tier = Tier(distance);
        if (tier == DistanceTier.None) return;

        var label = type == JumpType.Bhop ? "BH" : "LJ";
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            client.Print(HudPrintChannel.Chat, $"{label}: {distance:0.0}u — {tier}!");
    }

    // VNL/CKZ LongJump tier thresholds (ascending). Full per-mode/per-type tables land with the port.
    private static DistanceTier Tier(float d) => d switch
    {
        >= 284f => DistanceTier.Wrecker,
        >= 280f => DistanceTier.Ownage,
        >= 275f => DistanceTier.Godlike,
        >= 270f => DistanceTier.Perfect,
        >= 265f => DistanceTier.Impressive,
        >= 217f => DistanceTier.Meh,
        _       => DistanceTier.None,
    };
}
