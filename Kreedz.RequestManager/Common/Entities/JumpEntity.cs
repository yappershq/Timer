using System;
using Sharp.Shared.Units;
using SqlSugar;

namespace Kreedz.Common.Entities;

/// <summary>
/// KZ jumpstats record (cs2kz db/queries/jumpstats). One row per recorded jump — the jumpstats plugin
/// writes here so jumps persist for `!jstop` / personal-best queries.
/// </summary>
[SugarTable("kz_jumpstats")]
[SugarIndex("idx_kz_jumpstats_steamid", nameof(SteamId), OrderByType.Asc, false)]
internal sealed class JumpEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 36)]
    public string Id { get; set; } = string.Empty; // UUID

    [SugarColumn(ColumnDataType = "bigint", SqlParameterDbType = typeof(SteamIdDataConvert))]
    public SteamID SteamId { get; set; }

    [SugarColumn(Length = 16)]
    public string JumpType { get; set; } = string.Empty;

    public float Distance  { get; set; }
    public int   Strafes   { get; set; }
    public float Sync      { get; set; }
    public float Gain      { get; set; }
    public float MaxSpeed  { get; set; }
    public float Height    { get; set; }
    public DateTime CreatedAt { get; set; }
}
