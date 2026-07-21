/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ style system (1:1 cs2kz src/kz/style). Styles are STACKABLE movement modifiers layered on top of
 * the base mode (mode → styles, last-wins). Implemented now: **AutoBhop (ABH)** and **LegacyJump (LGJ)**
 * — pure convar styles, fully faithful. **AutoUnduck (AUD)** and the input-driven styles land with the
 * movement hooks at P5 (they need per-tick input manipulation). Style changes revert the mode base then
 * re-apply the active stack, so removing a style cleanly reverts its convars.
 *
 * Commands: !style [name] (toggle), !addstyle, !removestyle, !togglestyle, !clearstyles.
 * Ranking note: any active style makes a run a separate (styled) leaderboard category — wired when the
 * timer's record submission lands (StyleIDFlags != 0).
 */

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Modules;

internal interface IKzStyleModule
{
    /// <summary>True if the player has any style active (→ styled leaderboard category, jumpstats invalid).</summary>
    bool HasAnyStyle(PlayerSlot slot);
}

internal interface IKzStyle
{
    string Id        { get; } // "abh"
    string Name      { get; } // "Auto Bhop"
    string ShortName { get; } // "ABH"
    IReadOnlyDictionary<string, string> Convars { get; }
}

internal sealed class KzStyleModule : IModule, IKzStyleModule
{
    private readonly InterfaceBridge        _bridge;
    private readonly ICommandManager        _commandManager;
    private readonly IModeModule            _modeModule;
    private readonly ILogger<KzStyleModule> _logger;

    private readonly Dictionary<string, IKzStyle> _styles = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>[]            _active = new HashSet<string>[PlayerSlot.MaxPlayerCount];

    public KzStyleModule(InterfaceBridge bridge, ICommandManager commandManager, IModeModule modeModule, ILogger<KzStyleModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _modeModule     = modeModule;
        _logger         = logger;

        for (var i = 0; i < _active.Length; i++)
            _active[i] = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
    }

    public bool Init()
    {
        Register(new AutoBhopStyle());
        Register(new LegacyJumpStyle());

        foreach (var style in _styles.Values)
            foreach (var name in style.Convars.Keys)
                if (_bridge.ConVarManager.FindConVar(name) is { } cv)
                    cv.Flags |= ConVarFlags.Replicated;

        _commandManager.AddClientChatCommand("style",       (s, c) => { ToggleArg(s, c); return ECommandAction.Handled; });
        _commandManager.AddClientChatCommand("togglestyle", (s, c) => { ToggleArg(s, c); return ECommandAction.Handled; });
        _commandManager.AddClientChatCommand("addstyle",    (s, c) => { SetStyle(s, Arg(c), true);  return ECommandAction.Handled; });
        _commandManager.AddClientChatCommand("removestyle", (s, c) => { SetStyle(s, Arg(c), false); return ECommandAction.Handled; });
        _commandManager.AddClientChatCommand("clearstyles", (s, _) => { ClearStyles(s); return ECommandAction.Handled; });
        return true;
    }

    public bool HasAnyStyle(PlayerSlot slot) => _active[slot].Count > 0;

    private void Register(IKzStyle style)
    {
        _styles[style.Id] = style;
        _commandManager.AddClientChatCommand(style.Id, (slot, _) => { SetStyle(slot, style.Id, !_active[slot].Contains(style.Id)); return ECommandAction.Handled; });
    }

    private static string? Arg(StringCommand c) => c.ArgCount >= 1 ? c.GetArg(1) : null;

    private void ToggleArg(PlayerSlot slot, StringCommand command)
    {
        if (Arg(command) is not { } id)
        {
            var active = _active[slot].Count == 0 ? "none" : string.Join(", ", _active[slot]);
            Tell(slot, $"Active styles: {active}. Available: {string.Join(", ", _styles.Values.Select(s => s.ShortName))}. Use !style <name>.");
            return;
        }

        SetStyle(slot, id, !_active[slot].Contains(id));
    }

    private void SetStyle(PlayerSlot slot, string? id, bool enable)
    {
        if (id is null || _styles.GetValueOrDefault(id) is not { } style)
        {
            Tell(slot, $"Unknown style '{id}'.");
            return;
        }

        if (enable) _active[slot].Add(style.Id);
        else        _active[slot].Remove(style.Id);

        ReapplyAll(slot);
        Tell(slot, enable ? $"Style {style.Name} enabled." : $"Style {style.Name} disabled.");
    }

    private void ClearStyles(PlayerSlot slot)
    {
        _active[slot].Clear();
        ReapplyAll(slot);
        Tell(slot, "Styles cleared.");
    }

    /// <summary>Revert to the mode base, then re-apply the active style stack on top (last-wins).</summary>
    private void ReapplyAll(PlayerSlot slot)
    {
        _modeModule.Reapply(slot);

        if (_bridge.ClientManager.GetGameClient(slot) is not { IsFakeClient: false } client) return;
        foreach (var id in _active[slot])
            if (_styles.GetValueOrDefault(id) is { } style)
                foreach (var (name, value) in style.Convars)
                    _bridge.ConVarManager.FindConVar(name)?.ReplicateToClient(client, value);
    }

    private void Tell(PlayerSlot slot, string message)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            client.Print(HudPrintChannel.Chat, message);
    }
}

internal sealed class AutoBhopStyle : IKzStyle
{
    public string Id        => "abh";
    public string Name      => "Auto Bhop";
    public string ShortName => "ABH";
    public IReadOnlyDictionary<string, string> Convars { get; } = new Dictionary<string, string>
    {
        ["sv_autobunnyhopping"]   = "true",
        ["sv_enablebunnyhopping"] = "true",
    };
}

internal sealed class LegacyJumpStyle : IKzStyle
{
    public string Id        => "lgj";
    public string Name      => "Legacy Jump";
    public string ShortName => "LGJ";
    public IReadOnlyDictionary<string, string> Convars { get; } = new Dictionary<string, string>
    {
        ["sv_legacy_jump"] = "true",
    };
}
