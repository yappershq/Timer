using System;
using Sharp.Shared.Units;

namespace Kreedz.Shared.Interfaces;

/// <summary>
/// Read access to Core's persisted per-player preferences (cs2kz optionService) for external plugins
/// (e.g. Jumpstats reads jsFailstats/jsAlways). Values are the raw stored strings; missing = null.
/// </summary>
public interface IKzPreferences
{
    static readonly string Identity = typeof(IKzPreferences).FullName!;

    string? Get(PlayerSlot slot, string key);

    /// <summary>Fired on the game thread once a player's preferences have loaded from the DB.</summary>
    event Action<PlayerSlot>? Loaded;
}
