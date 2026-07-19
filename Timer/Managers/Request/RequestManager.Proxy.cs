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
using Sharp.Shared;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Managers.Request;

internal sealed class RequestManagerProxy : ExternalModuleProxy<IRequestManager>, IRequestManager
{
    private readonly RequestManagerLiteDB _fallbackManager;

    public RequestManagerProxy(ISharedSystem                shared,
                               RequestManagerLiteDB         fallback,
                               ILogger<RequestManagerProxy> logger)
        : base(shared, fallback, logger)
    {
        _fallbackManager = fallback;
    }

    protected override string Identity     => IRequestManager.Identity;
    protected override string ContractName => "IRequestManager";

    protected override bool InitFallback()
        => _fallbackManager.Init();

    protected override void ShutdownFallback()
        => _fallbackManager.Shutdown();

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

    #endregion

    public Task<PlayerProfile> GetPlayerProfile(SteamID steamId, string name)
        => Current.GetPlayerProfile(steamId, name);

    public Task<(int rank, int total)> GetPlayerPointsRank(SteamID steamId)
        => Current.GetPlayerPointsRank(steamId);

    public Task UpdatePlayerMapStatsAsync(SteamID steamId, string mapName, float deltaSeconds)
        => Current.UpdatePlayerMapStatsAsync(steamId, mapName, deltaSeconds);

    public Task<(float playTime, int playCount)> GetPlayerMapStatsAsync(SteamID steamId, string mapName)
        => Current.GetPlayerMapStatsAsync(steamId, mapName);
}
