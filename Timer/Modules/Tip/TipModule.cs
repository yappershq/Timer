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
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Modules;

internal interface ITipModule;

internal sealed class TipModule : IModule, ITipModule
{
    private const double TipIntervalSeconds = 180.0;

    private static readonly string[] Tips =
    {
        "Set a checkpoint with !cp and teleport back with !tp. A run with 0 teleports is a PRO time.",
        "Switch movement mode with !mode. Stack styles like !abh on top.",
        "!goto <player> teleports you to someone. !measure gets the distance between two points.",
        "Set your field of view with !fov <value>, or a custom start with !setstartpos.",
        "!undo removes your last checkpoint; !prevcp / !nextcp cycle through them.",
    };

    private readonly InterfaceBridge    _bridge;
    private readonly ICommandManager    _commandManager;
    private readonly ILogger<TipModule> _logger;
    private readonly bool[]             _enabled = new bool[PlayerSlot.MaxPlayerCount];

    private int  _next;
    private Guid _timer;

    public TipModule(InterfaceBridge bridge, ICommandManager commandManager, ILogger<TipModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _logger         = logger;
        Array.Fill(_enabled, true);
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("tips", (slot, _) =>
        {
            _enabled[slot] = !_enabled[slot];
            if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } c)
                c.Print(HudPrintChannel.Chat, _enabled[slot] ? "Tips enabled." : "Tips disabled.");
            return ECommandAction.Handled;
        });

        _timer = _bridge.ModSharp.PushTimer(BroadcastNextTip, TipIntervalSeconds, GameTimerFlags.Repeatable);
        return true;
    }

    public void Shutdown()
    {
        if (_timer != Guid.Empty) _bridge.ModSharp.StopTimer(_timer);
    }

    private void BroadcastNextTip()
    {
        if (Tips.Length == 0) return;

        var tip = Tips[_next];
        _next = (_next + 1) % Tips.Length;

        foreach (var client in _bridge.ClientManager.GetGameClients(inGame: true))
            if (!client.IsFakeClient && _enabled[client.Slot])
                client.Print(HudPrintChannel.Chat, $"[Tip] {tip}");
    }
}
