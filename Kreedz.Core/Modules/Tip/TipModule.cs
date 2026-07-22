/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ tips (1:1 cs2kz src/kz/tip): rotating help messages broadcast to everyone every `TipInterval`.
 * `!tips` toggles them per-player (default on). Content is placeholder English → localized string table
 * when i18n lands.
 */

using System;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Units;
using Sharp.Shared.Objects;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal interface ITipModule;

internal sealed class TipModule : IModule, ITipModule
{
    // Localized tip keys (content in kreedz.json) — cycled through a shuffled order (cs2kz shuffles its tips).
    private static readonly string[] Tips =
    {
        "Kreedz_Tip_1", "Kreedz_Tip_2", "Kreedz_Tip_3", "Kreedz_Tip_4", "Kreedz_Tip_5",
    };

    private readonly InterfaceBridge    _bridge;
    private readonly ICommandManager    _commandManager;
    private readonly ILogger<TipModule> _logger;
    private readonly bool[]             _enabled = new bool[PlayerSlot.MaxPlayerCount];

    private readonly int[]  _order = new int[Tips.Length]; // shuffled index sequence
    private readonly Random _rng   = new();
    private int      _next;
    private Guid     _timer;
    private IConVar? _intervalCvar;

    public TipModule(InterfaceBridge bridge, ICommandManager commandManager, ILogger<TipModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _logger         = logger;
        Array.Fill(_enabled, true);
        for (var i = 0; i < _order.Length; i++) _order[i] = i;
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("tips", (slot, _) =>
        {
            _enabled[slot] = !_enabled[slot];
            if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } c)
                Loc.Chat(_bridge.LocalizerManager, c, _enabled[slot] ? "Kreedz_Tips_On" : "Kreedz_Tips_Off");
            return ECommandAction.Handled;
        });

        // cs2kz-style configurable broadcast interval (seconds); default 180.
        _intervalCvar = _bridge.ConVarManager.CreateConVar("kz_tip_interval", 180f, "Seconds between broadcast tips.");
        Shuffle();
        _timer = _bridge.ModSharp.PushTimer(BroadcastNextTip, _intervalCvar?.GetFloat() ?? 180f, GameTimerFlags.Repeatable);
        return true;
    }

    private void Shuffle() // Fisher–Yates
    {
        for (var i = _order.Length - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (_order[i], _order[j]) = (_order[j], _order[i]);
        }
        _next = 0;
    }

    public void Shutdown()
    {
        if (_timer != Guid.Empty) _bridge.ModSharp.StopTimer(_timer);
    }

    private void BroadcastNextTip()
    {
        if (Tips.Length == 0) return;

        var tipKey = Tips[_order[_next]];
        if (++_next >= _order.Length) Shuffle(); // reshuffle after a full pass (no repeats within a cycle)

        foreach (var client in _bridge.ClientManager.GetGameClients(inGame: true))
            if (!client.IsFakeClient && _enabled[client.Slot])
                Loc.Chat(_bridge.LocalizerManager, client, tipKey);
    }
}
