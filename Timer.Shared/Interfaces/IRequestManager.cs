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

using System.Collections.Generic;
using System.Threading.Tasks;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Shared.Interfaces;

/// <summary>
///     Outcome of a record-save attempt. The ordinal ORDER is part of the contract:
///     consumers use relational comparisons (e.g. <c>result &gt;= NewPersonalRecord</c>
///     for "is a new best"), so members must stay sorted from worst to best outcome.
/// </summary>
public enum EAttemptResult
{
    NoNewRecord,
    NewPersonalRecord,
    NewServerRecord,
}

public interface IRequestManager
{
    static readonly string Identity = typeof(IRequestManager).FullName!;

    const int DefaultRecordLimit = 5000;

#region MapInfo

    Task<MapProfile> GetMapInfo(string map);

    Task UpdateMapInfo(MapProfile info);

    /// <summary>
    /// Get all map names.
    /// </summary>
    Task<IReadOnlyList<string>> GetAllMapNamesAsync();

#endregion

#region Record

    Task<IReadOnlyList<RunRecord>> GetMapRecords(string mapName, int limit = DefaultRecordLimit);

    Task<IReadOnlyList<RunRecord>> GetMapStageRecords(string mapName, int limit = DefaultRecordLimit);

    Task<IReadOnlyList<RunRecord>> GetMapRecords(string mapName,
                                                 int    style,
                                                 int    track,
                                                 int    limit = DefaultRecordLimit);

    Task<IReadOnlyList<RunRecord>> GetMapStageRecords(string mapName,
                                                      int    style,
                                                      int    track,
                                                      int    stage,
                                                      int    limit = DefaultRecordLimit);

    Task<(EAttemptResult, RunRecord, int rank)> AddPlayerRecord(SteamID steamId, string mapName, RecordRequest recordRequest);

    Task<IReadOnlyList<RunRecord>> GetPlayerRecords(SteamID steamId, string mapName);

    Task<RunRecord?> GetPlayerRecord(SteamID steamId, string mapName, int style, int track);

    Task<(EAttemptResult, RunRecord, int rank)> AddPlayerStageRecord(SteamID steamId, string mapName, RecordRequest newRunRecord);

    Task<IReadOnlyList<RunRecord>> GetPlayerStageRecords(SteamID steamId, string mapName);

    Task RemoveMapRecords(string mapName);

    Task<IReadOnlyList<RunCheckpoint>> GetRecordCheckpoints(long recordId);

    Task<IReadOnlyList<RunRecord>> GetRecentRecords(string mapName, SteamID steamId, int limit = 10);

#endregion

#region Score

    /// <summary>
    /// Manually trigger score recalculation for all tracks on a given map.
    /// </summary>
    /// <param name="mapName">Map name</param>
    /// <param name="styleFactors">Style score factor dictionary (key: style index, value: ScoreFactor)</param>
    /// <returns>Number of tracks queued for recalculation</returns>
    Task<int> RecalculateMapScoresAsync(string mapName, IReadOnlyDictionary<int, double>? styleFactors = null);

#endregion

#region Zone

    /// <summary>
    /// Gets all custom zones for the specified map.
    /// </summary>
    Task<IReadOnlyList<ZoneData>> GetZonesAsync(string mapName);

    /// <summary>
    /// Replaces all custom zones for the specified map (transactional: delete then insert).
    /// </summary>
    Task SaveZonesAsync(string mapName, IReadOnlyList<ZoneData> zones);

#endregion

    Task<PlayerProfile> GetPlayerProfile(SteamID steamId, string name);

    /// <summary>
    /// Get the player's rank by points (1-based) and total ranked player count.
    /// Returns (0, 0) if the player has no points.
    /// </summary>
    Task<(int rank, int total)> GetPlayerPointsRank(SteamID steamId);

    /// <summary>
    /// Atomically increment a player's per-map play time and play count.
    /// </summary>
    Task UpdatePlayerMapStatsAsync(SteamID steamId, string mapName, float deltaSeconds);

    /// <summary>
    /// Get a player's per-map play time and play count.
    /// Returns (0, 0) if no stats exist.
    /// </summary>
    Task<(float playTime, int playCount)> GetPlayerMapStatsAsync(SteamID steamId, string mapName);
}
