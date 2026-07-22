/*
 * yappershq/Timer (KZ) — CS2KZ port
 *
 * Per-player preference persistence (cs2kz src/kz/option). A simple string→string store per player,
 * loaded from the DB on connect and saved on disconnect, backed by the PlayerEntity.Preferences JSON
 * column (SQL) / a kz_preferences collection (LiteDB fallback). Feature modules (mode, fov, styles, …)
 * read/write their setting through Get/Set and apply it when the Loaded event fires for a player — so a
 * player's chosen mode/fov/etc. survives a reconnect.
 */

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;

namespace Kreedz.Modules;

internal interface IPreferencesModule
{
    /// <summary>A stored preference value, or null if unset / not yet loaded.</summary>
    string? Get(PlayerSlot slot, string key);

    /// <summary>Set a preference (persisted on disconnect).</summary>
    void Set(PlayerSlot slot, string key, string value);

    /// <summary>Fired on the game thread once a player's preferences have loaded from the DB.</summary>
    event Action<PlayerSlot>? Loaded;
}

internal sealed class PreferencesModule : IModule, IPreferencesModule, IClientListener, Shared.Interfaces.IKzPreferences
{
    private readonly InterfaceBridge            _bridge;
    private readonly IRequestManager            _request;
    private readonly ILogger<PreferencesModule> _logger;

    private readonly Dictionary<string, string>?[] _cache = new Dictionary<string, string>?[PlayerSlot.MaxPlayerCount];
    private readonly bool[]                         _dirty = new bool[PlayerSlot.MaxPlayerCount];

    public event Action<PlayerSlot>? Loaded;

    public PreferencesModule(InterfaceBridge bridge, IRequestManager request, ILogger<PreferencesModule> logger)
    {
        _bridge  = bridge;
        _request = request;
        _logger  = logger;
    }

    public int ListenerVersion  => IClientListener.ApiVersion;
    public int ListenerPriority => 10;

    // Publish read access for external plugins (Jumpstats reads jsFailstats/jsAlways).
    public void OnPostInit(Microsoft.Extensions.DependencyInjection.ServiceProvider provider)
        => _bridge.SharpModuleManager.RegisterSharpModuleInterface<Shared.Interfaces.IKzPreferences>(
            _bridge.Entrypoint, Shared.Interfaces.IKzPreferences.Identity, this);

    public bool Init()
    {
        _bridge.ClientManager.InstallClientListener(this);
        return true;
    }

    public void Shutdown() => _bridge.ClientManager.RemoveClientListener(this);

    public string? Get(PlayerSlot slot, string key)
        => _cache[slot] is { } dict && dict.TryGetValue(key, out var value) ? value : null;

    public void Set(PlayerSlot slot, string key, string value)
    {
        (_cache[slot] ??= new())[key] = value;
        _dirty[slot] = true;
    }

    public void OnClientPutInServer(IGameClient client)
    {
        if (client.IsFakeClient) return;
        _ = LoadAsync(client.Slot, client.SteamId);
    }

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        var slot = client.Slot;
        if (_cache[slot] is { } dict && _dirty[slot])
        {
            var json = JsonSerializer.Serialize(dict);
            _ = SaveAsync(client.SteamId, json);
        }

        _cache[slot] = null;
        _dirty[slot] = false;
    }

    private async Task LoadAsync(PlayerSlot slot, SteamID steamId)
    {
        Dictionary<string, string>? dict = null;
        try
        {
            if (await _request.GetPreferencesAsync(steamId) is { Length: > 0 } json)
                dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[KZ.Prefs] load failed for {Sid}", steamId);
        }

        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            // Player may have left during the DB round-trip; only apply if still the same connection.
            if (_bridge.ClientManager.GetGameClient(slot) is not { IsValid: true } client || client.SteamId != steamId)
                return;

            _cache[slot] = dict ?? new();
            _dirty[slot] = false;
            Loaded?.Invoke(slot);
        });
    }

    private async Task SaveAsync(SteamID steamId, string json)
    {
        try
        {
            await _request.SavePreferencesAsync(steamId, json);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[KZ.Prefs] save failed for {Sid}", steamId);
        }
    }
}
