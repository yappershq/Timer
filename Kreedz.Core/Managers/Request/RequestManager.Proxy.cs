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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Units;
using Kreedz.Shared.Interfaces;
using Kreedz.Shared.Models;
using Kreedz.Shared.Models.Zone;

namespace Kreedz.Managers.Request;

internal sealed class RequestManagerProxy : IManager, IRequestManager
{
    private readonly ISharedSystem                _shared;
    private readonly RequestManagerLiteDB         _fallback;
    private readonly ILogger<RequestManagerProxy> _logger;

    private IRequestManager _current;
    private bool            _fallbackInitialized;

    public RequestManagerProxy(ISharedSystem                shared,
                               RequestManagerLiteDB         fallback,
                               ILogger<RequestManagerProxy> logger)
    {
        _shared   = shared;
        _fallback = fallback;
        _current  = fallback;
        _logger   = logger;
    }

    private IRequestManager Current => Volatile.Read(ref _current);

    public bool Init()
    {
        try
        {
            RefreshManager();

            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to initialize request manager proxy.");

            return false;
        }
    }

    public void Shutdown()
    {
        if (_fallbackInitialized)
        {
            _fallback.Shutdown();
            _fallbackInitialized = false;
        }
    }

    public void RefreshManager()
    {
        var external = _shared.GetSharpModuleManager()
                              .GetOptionalSharpModuleInterface<IRequestManager>(IRequestManager.Identity);

        if (external?.Instance is { } instance && !ReferenceEquals(instance, this))
        {
            Use(instance, external.GetType().FullName);

            return;
        }

        UseFallback();
    }

    public void Use(IRequestManager manager, string? providerName = null)
    {
        if (ReferenceEquals(manager, _fallback))
        {
            EnsureFallbackInitialized();
        }

        if (ReferenceEquals(Current, manager))
        {
            return;
        }

        Volatile.Write(ref _current, manager);

        if (!ReferenceEquals(manager, _fallback))
        {
            if (!string.IsNullOrWhiteSpace(providerName))
            {
                _logger.LogInformation("Using external IRequestManager from {provider}.", providerName);
            }
            else
            {
                _logger.LogInformation("Using custom IRequestManager instance.");
            }
        }
        else
        {
            _logger.LogInformation("Using built-in IRequestManager: {type}",
                                   _fallback.GetType()
                                            .FullName);
        }
    }

    public void UseFallback()
        => Use(_fallback);

    private readonly object _fallbackLock = new();

    private void EnsureFallbackInitialized()
    {
        if (Volatile.Read(ref _fallbackInitialized))
        {
            return;
        }

        lock (_fallbackLock)
        {
            if (_fallbackInitialized)
            {
                return;
            }

            if (!_fallback.Init())
            {
                throw new InvalidOperationException("Failed to initialize built-in RequestManagerLiteDB.");
            }

            Volatile.Write(ref _fallbackInitialized, true);
        }
    }

    public Task<MapProfile> GetMapInfo(string map)
        => Current.GetMapInfo(map);

    public Task UpdateMapInfo(MapProfile info)
        => Current.UpdateMapInfo(info);

    public Task<IReadOnlyList<RunRecord>> GetMapRecords(string mapName, int limit = IRequestManager.DefaultRecordLimit)
        => Current.GetMapRecords(mapName, limit);

    public Task<IReadOnlyList<RunRecord>> GetMapStageRecords(string mapName, int limit = IRequestManager.DefaultRecordLimit)
        => Current.GetMapStageRecords(mapName, limit);

    public Task<IReadOnlyList<RunRecord>> GetMapRecords(string mapName,
                                                        int    style,
                                                        int    track,
                                                        int    limit = IRequestManager.DefaultRecordLimit)
        => Current.GetMapRecords(mapName, style, track, limit);

    public Task<IReadOnlyList<RunRecord>> GetMapStageRecords(string mapName,
                                                             int    style,
                                                             int    track,
                                                             int    stage,
                                                             int    limit = IRequestManager.DefaultRecordLimit)
        => Current.GetMapStageRecords(mapName, style, track, stage, limit);

    public Task<(EAttemptResult, RunRecord, int rank)> AddPlayerRecord(SteamID steamId, string mapName, RecordRequest recordRequest)
        => Current.AddPlayerRecord(steamId, mapName, recordRequest);

    public Task<IReadOnlyList<RunRecord>> GetPlayerRecords(SteamID steamId, string mapName)
        => Current.GetPlayerRecords(steamId, mapName);

    public Task<RunRecord?> GetPlayerRecord(SteamID steamId, string mapName, int style, int track)
        => Current.GetPlayerRecord(steamId, mapName, style, track);

    public Task<(EAttemptResult, RunRecord, int rank)> AddPlayerStageRecord(SteamID steamId, string mapName, RecordRequest newRunRecord)
        => Current.AddPlayerStageRecord(steamId, mapName, newRunRecord);

    public Task<IReadOnlyList<RunRecord>> GetPlayerStageRecords(SteamID steamId, string mapName)
        => Current.GetPlayerStageRecords(steamId, mapName);

    public Task RemoveMapRecords(string mapName)
        => Current.RemoveMapRecords(mapName);

    public Task<IReadOnlyList<RunCheckpoint>> GetRecordCheckpoints(long recordId)
        => Current.GetRecordCheckpoints(recordId);

    public Task<IReadOnlyList<RunRecord>> GetRecentRecords(string mapName, SteamID steamId, int limit = 10)
        => Current.GetRecentRecords(mapName, steamId, limit);

    public Task<int> RecalculateMapScoresAsync(string mapName, IReadOnlyDictionary<int, double>? styleFactors = null)
        => Current.RecalculateMapScoresAsync(mapName, styleFactors);

    public Task<IReadOnlyList<string>> GetAllMapNamesAsync()
        => Current.GetAllMapNamesAsync();

    #region Zone

    public Task<IReadOnlyList<ZoneData>> GetZonesAsync(string mapName)
        => Current.GetZonesAsync(mapName);

    public Task SaveZonesAsync(string mapName, IReadOnlyList<ZoneData> zones)
        => Current.SaveZonesAsync(mapName, zones);

    public Task<ulong> AddZoneAsync(string mapName, ZoneData zone)
        => Current.AddZoneAsync(mapName, zone);

    public Task DeleteZonesAsync(string mapName)
        => Current.DeleteZonesAsync(mapName);

    #endregion

    public Task<PlayerProfile> GetPlayerProfile(SteamID steamId, string name)
        => Current.GetPlayerProfile(steamId, name);

    public Task<(int rank, int total)> GetPlayerPointsRank(SteamID steamId)
        => Current.GetPlayerPointsRank(steamId);

    public Task UpdatePlayerMapStatsAsync(SteamID steamId, string mapName, float deltaSeconds)
        => Current.UpdatePlayerMapStatsAsync(steamId, mapName, deltaSeconds);

    public Task<(float playTime, int playCount)> GetPlayerMapStatsAsync(SteamID steamId, string mapName)
        => Current.GetPlayerMapStatsAsync(steamId, mapName);

    #region Ban

    public Task AddBanAsync(SteamID steamId, string? reason, DateTime expiresAt)
        => Current.AddBanAsync(steamId, reason, expiresAt);

    public Task<int> RemoveBansAsync(SteamID steamId)
        => Current.RemoveBansAsync(steamId);

    public Task<BanRecord?> GetActiveBanAsync(SteamID steamId)
        => Current.GetActiveBanAsync(steamId);

    public Task SaveInfractionAsync(SteamID steamId, string type, string? details)
        => Current.SaveInfractionAsync(steamId, type, details);

    public Task SaveJumpAsync(SteamID steamId, string jumpType, float distance, int strafes, float sync, float gain, float maxSpeed, float height)
        => Current.SaveJumpAsync(steamId, jumpType, distance, strafes, sync, gain, maxSpeed, height);

    #endregion

    #region Preferences

    public Task<string?> GetPreferencesAsync(SteamID steamId)
        => Current.GetPreferencesAsync(steamId);

    public Task SavePreferencesAsync(SteamID steamId, string json)
        => Current.SavePreferencesAsync(steamId, json);

    #endregion
}
