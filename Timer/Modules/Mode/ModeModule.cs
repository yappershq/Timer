/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ mode framework (1:1 cs2kz src/kz/mode + movement modes). A "mode" is the base movement ruleset;
 * each supplies a set of movement convar values that are replicated per-player, and (for CKZ) a custom
 * movement model. This module owns the mode registry, per-player current mode, `!mode`/`kz_mode`
 * switching, and per-player convar application on switch + spawn.
 *
 * Modes registered now: **Vanilla (VNL)** — faithful CS2 movement (real CS2 convar defaults), which is
 * decision-independent and correct via stock movement. **Classic (CKZ)** — the gameplay heart — needs
 * the bit-faithful custom movement port (prestrafe/perf/rampbug/slopefix); that lands at P5 and plugs
 * into this same framework (a CKZ IKzMode whose OnProcessMovement runs the ported physics).
 */

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Modules;

internal interface IModeModule
{
    /// <summary>The player's current mode id (default "vnl").</summary>
    string GetMode(PlayerSlot slot);

    /// <summary>Re-apply the player's current mode convars (base layer; styles stack on top after).</summary>
    void Reapply(PlayerSlot slot);
}

/// <summary>A KZ movement mode: an id/name + the movement convar values it replicates per-player.</summary>
internal interface IKzMode
{
    string Id        { get; } // "vnl"
    string Name      { get; } // "Vanilla"
    string ShortName { get; } // "VNL"
    IReadOnlyDictionary<string, string> Convars { get; }
}

internal sealed class ModeModule : IModule, IModeModule
{
    private const string DefaultMode = "vnl";

    private readonly InterfaceBridge     _bridge;
    private readonly ICommandManager     _commandManager;
    private readonly ILogger<ModeModule> _logger;

    private readonly Dictionary<string, IKzMode> _modes = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly string[] _current = new string[PlayerSlot.MaxPlayerCount];

    public ModeModule(InterfaceBridge bridge, ICommandManager commandManager, ILogger<ModeModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _logger         = logger;

        for (var i = 0; i < _current.Length; i++)
            _current[i] = DefaultMode;
    }

    public bool Init()
    {
        Register(new VanillaMode());

        // Ensure every mode convar can be per-client replicated.
        foreach (var mode in _modes.Values)
            foreach (var name in mode.Convars.Keys)
                if (_bridge.ConVarManager.FindConVar(name) is { } cv)
                    cv.Flags |= ConVarFlags.Replicated;

        _commandManager.AddClientChatCommand("mode", OnCommandMode);

        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);
        return true;
    }

    public void Shutdown() => _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);

    public string GetMode(PlayerSlot slot) => _current[slot];

    public void Reapply(PlayerSlot slot)
    {
        if (_modes.GetValueOrDefault(_current[slot]) is { } mode
            && _bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            Apply(client, mode);
    }

    private void Register(IKzMode mode)
    {
        _modes[mode.Id] = mode;
        // Each mode also gets a short switch command, e.g. !vnl.
        _commandManager.AddClientChatCommand(mode.Id, (slot, _) => { SwitchMode(slot, mode.Id); return ECommandAction.Handled; });
    }

    private ECommandAction OnCommandMode(PlayerSlot slot, Sharp.Shared.Types.StringCommand command)
    {
        if (command.ArgCount >= 1) // ArgCount excludes the command itself; GetArg is 1-indexed
        {
            SwitchMode(slot, command.GetArg(1));
            return ECommandAction.Handled;
        }

        // No arg: report current + list available.
        var names = string.Join(", ", _modes.Values.Select(m => m.ShortName));
        Tell(slot, $"Current mode: {ModeName(_current[slot])}. Available: {names}. Use !mode <name>.");
        return ECommandAction.Handled;
    }

    private void SwitchMode(PlayerSlot slot, string id)
    {
        if (_modes.GetValueOrDefault(id) is not { } mode)
        {
            Tell(slot, $"Unknown mode '{id}'.");
            return;
        }

        _current[slot] = mode.Id;
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
        {
            Apply(client, mode);
            Tell(slot, $"Mode set to {mode.Name}.");
        }
    }

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var slot = @params.Client.Slot;
        if (_modes.GetValueOrDefault(_current[slot]) is { } mode && !@params.Client.IsFakeClient)
            Apply(@params.Client, mode);
    }

    private void Apply(IGameClient client, IKzMode mode)
    {
        foreach (var (name, value) in mode.Convars)
            _bridge.ConVarManager.FindConVar(name)?.ReplicateToClient(client, value);
    }

    private string ModeName(string id) => _modes.GetValueOrDefault(id)?.Name ?? id;

    private void Tell(PlayerSlot slot, string message)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            client.Print(HudPrintChannel.Chat, message);
    }
}

/// <summary>Vanilla (VNL) — faithful CS2 movement: real CS2 convar defaults. The full mode-convar set
/// (33 values) is completed alongside the P5 movement port; these are the core movement ones.</summary>
internal sealed class VanillaMode : IKzMode
{
    public string Id        => "vnl";
    public string Name      => "Vanilla";
    public string ShortName => "VNL";

    public IReadOnlyDictionary<string, string> Convars { get; } = new Dictionary<string, string>
    {
        ["sv_accelerate"]         = "5.5",
        ["sv_airaccelerate"]      = "12",
        ["sv_air_max_wishspeed"]  = "30",
        ["sv_friction"]           = "5.2",
        ["sv_gravity"]            = "800",
        ["sv_jump_impulse"]       = "301.993377",
        ["sv_maxspeed"]           = "320",
        ["sv_autobunnyhopping"]   = "false",
        ["sv_enablebunnyhopping"] = "false",
        ["sv_staminamax"]         = "80",
        ["sv_staminajumpcost"]    = "0.08",
        ["sv_staminalandcost"]    = "0.05",
        ["sv_staminarecoveryrate"] = "60",
    };
}
