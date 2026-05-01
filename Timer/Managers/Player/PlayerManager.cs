/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Managers.Player;

internal interface IPlayerManager
{
    void RegisterListener(IPlayerManagerListener listener);

    void UnregisterListener(IPlayerManagerListener listener);

    PlayerProfile? GetPlayerProfile(PlayerSlot slot);

    PlayerProfile? GetPlayerProfile(SteamID steamId);
}

internal class PlayerManager : IManager, IPlayerManager, IClientListener
{
    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 1;

    private readonly InterfaceBridge        _bridge;
    private readonly IRequestManager        _requestManager;
    private readonly ILogger<PlayerManager> _logger;

    // Core storage: PlayerProfile indexed by PlayerSlot
    private readonly PlayerProfile?[] _profiles;

    // Reverse index: SteamID -> PlayerSlot for O(1) lookup
    private readonly Dictionary<SteamID, PlayerSlot> _steamIdToSlot = new();

    // Temporary storage: pending SteamID between OnClientConnected and OnClientPostAdminCheck
    private readonly SteamID?[] _pendingSteamIds;

    // Temporary storage: whether a slot has completed authentication
    private readonly bool[] _authenticated;

    // Listener hub for notifying consumers
    private readonly ListenerHub<IPlayerManagerListener> _listenerHub;

    private readonly ISteamApi _steamApi;

    public PlayerManager(InterfaceBridge        bridge,
                         IRequestManager        requestManager,
                         ILogger<PlayerManager> logger)
    {
        _bridge         = bridge;
        _requestManager = requestManager;
        _logger         = logger;

        _steamApi = _bridge.ModSharp.GetSteamGameServer();

        _profiles        = new PlayerProfile?[PlayerSlot.MaxPlayerCount];
        _pendingSteamIds = new SteamID?[PlayerSlot.MaxPlayerCount];
        _authenticated   = new bool[PlayerSlot.MaxPlayerCount];
        _listenerHub     = new ListenerHub<IPlayerManagerListener>(logger);
    }

    public void OnClientConnected(IGameClient client)
    {
        var slot = (int) client.Slot;

        // Clean up old data if slot is occupied
        if (_profiles[slot] is { } oldProfile)
        {
            _steamIdToSlot.Remove(oldProfile.SteamId);
            _profiles[slot] = null;
        }

        if (_pendingSteamIds[slot] is { } oldPending)
        {
            _steamIdToSlot.Remove(oldPending);
        }

        _pendingSteamIds[slot] = client.SteamId;
        _authenticated[slot]   = false;
    }

    public void OnClientPutInServer(IGameClient client)
    {
        foreach (var listener in _listenerHub.Snapshot)
        {
            try
            {
                listener.OnClientPutInServer(client.Slot);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error when calling OnClientPutInServer listener");
            }
        }

        var serverLoggedInToSteam = _steamApi.BLoggedOn() && _steamApi.IsAvailable();

        if (serverLoggedInToSteam)
        {
            return;
        }
    }

    public void OnClientPostAdminCheck(IGameClient client)
    {
        if (client.IsFakeClient)
        {
            return;
        }

        var slot = client.Slot;

        // Verify SteamID consistency
        if (_pendingSteamIds[slot] is not { } pendingSteamId || pendingSteamId != client.SteamId)
        {
            using var scope = _logger.BeginScope("OnClientPostAdminCheck");

            _logger.LogError("Player {@client} SteamID mismatch: pending={pendingSteamId}, actual={actualSteamId} at slot<{slot}>",
                             client,
                             _pendingSteamIds[slot],
                             client.SteamId,
                             client.Slot);

            _bridge.ClientManager.KickClient(client, "Invalid SteamId", NetworkDisconnectionReason.SteamAuthInvalid);

            return;
        }

        // Check for duplicate authentication
        if (_authenticated[slot])
        {
            using var scope = _logger.BeginScope("OnClientPostAdminCheck");
            _logger.LogError("Player {@client} was already fully Authenticated!", client);

            _bridge.ClientManager.KickClient(client,
                                             "Invalid Steam Authenticated",
                                             NetworkDisconnectionReason.SteamAuthInvalid);

            return;
        }

        _authenticated[slot] = true;

        var steamId = client.SteamId;
        var name    = client.Name;

        Task.Run(async () =>
        {
            try
            {
                var profile = await RetryHelper.RetryAsync(() => _requestManager.GetPlayerProfile(steamId, name),
                                                           RetryHelper.IsTransient,
                                                           _logger,
                                                           "GetPlayerProfile").ConfigureAwait(false);

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    if (_pendingSteamIds[slot] is not { } pending)
                    {
                        return;
                    }

                    if (pending != steamId)
                    {
                        return;
                    }

                    if (_bridge.ClientManager.GetGameClient(steamId) is not { } c)
                    {
                        return;
                    }

                    slot = c.Slot;

                    _profiles[slot]         = profile;
                    _steamIdToSlot[steamId] = slot;
                    _pendingSteamIds[slot]  = null;

                    foreach (var listener in _listenerHub.Snapshot)
                    {
                        try
                        {
                            listener.OnClientInfoLoaded(steamId);
                        }
                        catch (Exception exception)
                        {
                            _logger.LogError(exception, "Error when calling OnClientInfoLoaded listener");
                        }
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when loading player profile for {steamId}", steamId);
            }
        }, _bridge.CancellationToken);
    }

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        var slot = (int) client.Slot;

        foreach (var listener in _listenerHub.Snapshot)
        {
            try
            {
                listener.OnClientDisconnected(client.Slot);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Error when calling OnClientDisconnected listener");
            }
        }

        if (_profiles[slot] is { } profile)
        {
            _steamIdToSlot.Remove(profile.SteamId);
            _profiles[slot] = null;
        }

        if (_pendingSteamIds[slot] is { } pending)
        {
            _steamIdToSlot.Remove(pending);
            _pendingSteamIds[slot] = null;
        }

        _authenticated[slot] = false;
    }

    public PlayerProfile? GetPlayerProfile(PlayerSlot slot)
    {
        var index = (int) slot;

        if (index < 0 || index >= _profiles.Length)
        {
            return null;
        }

        return _profiles[index];
    }

    public PlayerProfile? GetPlayerProfile(SteamID steamId)
    {
        if (_steamIdToSlot.TryGetValue(steamId, out var slot))
        {
            var profile = _profiles[(int) slot];

            if (profile is not null && profile.SteamId == steamId)
            {
                return profile;
            }
        }

        return null;
    }

    public void RegisterListener(IPlayerManagerListener listener)
        => _listenerHub.Register(listener);

    public void UnregisterListener(IPlayerManagerListener listener)
        => _listenerHub.Unregister(listener);

    public bool Init()
    {
        _bridge.ClientManager.InstallClientListener(this);

        return true;
    }

    public void OnPostInit()
    {
    }

    public void Shutdown()
    {
        _bridge.ClientManager.RemoveClientListener(this);
        _listenerHub.Clear();
        _steamIdToSlot.Clear();
    }
}
