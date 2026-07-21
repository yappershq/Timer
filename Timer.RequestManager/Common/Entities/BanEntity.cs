using System;
using Sharp.Shared.Units;
using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

/// <summary>
/// KZ ban record. A player is "banned" (excluded from ranking + optionally kicked) while an
/// unexpired row exists — ranking queries `LEFT JOIN kz_bans … WHERE Id IS NULL`, and the KZ
/// anticheat writes rows here. Distinct from the legacy surf_* tables (KZ-new; the eventual
/// surf_→kz_ rebrand leaves this as-is). Unread until the Anticheat/Global modules land (P6).
/// </summary>
[SugarTable("kz_bans")]
[SugarIndex("idx_kz_bans_steamid", nameof(SteamId), OrderByType.Asc, false)]
[SugarIndex("idx_kz_bans_expires", nameof(ExpiresAt), OrderByType.Asc, false)]
internal sealed class BanEntity
{
    [SugarColumn(IsPrimaryKey = true, Length = 36)]
    public string Id { get; set; } = string.Empty; // UUID

    [SugarColumn(ColumnDataType = "bigint", SqlParameterDbType = typeof(SteamIdDataConvert))]
    public SteamID SteamId { get; set; }

    [SugarColumn(Length = 255, IsNullable = true)]
    public string? Reason { get; set; }

    [SugarColumn(Length = 36, IsNullable = true)]
    public string? ReplayUuid { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
