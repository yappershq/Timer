/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * KZ `!pistol <name>` — force-equip a pistol (cs2kz src/kz/pistol). Stores the preference per-player
 * and re-gives it on spawn (KZ/surf servers strip weapons on spawn). Name→classname with common
 * aliases. Persisted in memory for now (→ OptionModule preference store later).
 */

using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal interface IPistolModule;

internal sealed class PistolModule : IModule, IPistolModule
{
    private static readonly Dictionary<string, string> Pistols = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["usp"] = "weapon_usp_silencer", ["usps"] = "weapon_usp_silencer", ["hkp2000"] = "weapon_hkp2000", ["p2000"] = "weapon_hkp2000",
        ["glock"] = "weapon_glock", ["deagle"] = "weapon_deagle", ["p250"] = "weapon_p250",
        ["fiveseven"] = "weapon_fiveseven", ["57"] = "weapon_fiveseven", ["tec9"] = "weapon_tec9",
        ["cz"] = "weapon_cz75a", ["cz75"] = "weapon_cz75a", ["dualies"] = "weapon_elite", ["elite"] = "weapon_elite",
        ["revolver"] = "weapon_revolver", ["r8"] = "weapon_revolver",
    };

    // Team-locked pistols (cs2kz kz_pistol.cpp UpdatePistol) — giving these to the other team needs a
    // transient m_iTeamNum switch so the engine spawns the correct team-variant model. Everything else
    // (deagle/p250/cz/revolver/elite) has no team lock and is never switched.
    private static readonly Dictionary<string, CStrikeTeam> PistolTeams = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["weapon_glock"]        = CStrikeTeam.TE,
        ["weapon_tec9"]         = CStrikeTeam.TE,
        ["weapon_fiveseven"]    = CStrikeTeam.CT,
        ["weapon_hkp2000"]      = CStrikeTeam.CT,
        ["weapon_usp_silencer"] = CStrikeTeam.CT,
    };

    private readonly InterfaceBridge       _bridge;
    private readonly ICommandManager       _commandManager;
    private readonly ILogger<PistolModule> _logger;

    private readonly string?[] _preferred = new string?[PlayerSlot.MaxPlayerCount];

    public PistolModule(InterfaceBridge bridge, ICommandManager commandManager, ILogger<PistolModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _logger         = logger;
    }

    public bool Init()
    {
        _commandManager.AddClientChatCommand("pistol", OnCommandPistol);
        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);
        return true;
    }

    public void Shutdown() => _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);

    private ECommandAction OnCommandPistol(PlayerSlot slot, StringCommand command)
    {
        if (command.ArgCount < 1)
        {
            Msg(slot, "Kreedz_Pistol_Usage", string.Join("/", new[] { "usp", "glock", "deagle", "p250", "tec9", "cz", "revolver" }));
            return ECommandAction.Handled;
        }

        if (!Pistols.TryGetValue(command.GetArg(1), out var classname))
        {
            Msg(slot, "Kreedz_Pistol_Unknown", command.GetArg(1));
            return ECommandAction.Handled;
        }

        _preferred[slot] = classname;
        Give(slot, classname);
        Msg(slot, "Kreedz_Pistol_Set", classname["weapon_".Length..]);
        return ECommandAction.Handled;
    }

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var slot = @params.Client.Slot;
        if (!@params.Client.IsFakeClient && _preferred[slot] is { } classname)
            // Give after the frame so it survives any spawn-time weapon stripping.
            _bridge.ModSharp.InvokeFrameAction(() => Give(slot, classname));
    }

    private void Give(PlayerSlot slot, string classname)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { IsFakeClient: false } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller
            || controller.GetPlayerPawn() is not { IsValidEntity: true, IsAlive: true } pawn)
            return;

        // cs2kz strips the existing pistol before giving the new one (no additive stacking of secondaries).
        if (pawn.GetWeaponService() is { } weapons)
        {
            foreach (var handle in weapons.GetMyWeapons())
            {
                if (_bridge.EntityManager.FindEntityByHandle(handle) is { IsValidEntity: true } w
                    && Pistols.Values.Contains(w.Classname))
                    w.Kill();
            }
        }

        // cs2kz UpdatePistol: team-locked pistols need a transient m_iTeamNum switch so the engine gives
        // the correct team-variant model when handing it to the other team, then the team is restored.
        // (cs2kz also re-checks the player's econ inventory for a no-team-lock skin match here — skipped,
        // needs econ inventory access we don't have wired up. // ponytail: no inventory-skin check)
        var originalTeam = pawn.Team;

        if (PistolTeams.TryGetValue(classname, out var pistolTeam)
            && originalTeam is CStrikeTeam.TE or CStrikeTeam.CT
            && originalTeam != pistolTeam)
        {
            pawn.TransientChangeTeam(pistolTeam);
            pawn.GiveNamedItem(classname);
            pawn.TransientChangeTeam(originalTeam);
        }
        else
        {
            pawn.GiveNamedItem(classname);
        }
    }

    private void Msg(PlayerSlot slot, string key, params object?[] args)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client)
            Loc.Chat(_bridge.LocalizerManager, client, key, args);
    }
}
