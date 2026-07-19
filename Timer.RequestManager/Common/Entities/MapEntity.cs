using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

[SugarTable("surf_maps")]
[SugarIndex("idx_surf_maps_file", nameof(File), OrderByType.Asc, true)]
internal sealed class MapEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public ulong MapId { get; set; }

    public string File { get; set; } = "INVALID_FILE";

    public byte Tier { get; set; }

    public ushort Stages { get; set; }

    /// <summary>
    /// Base score pool for this map.
    /// 0 means use the global default (ScoreCalculator.DefaultBasePot).
    /// </summary>
    public int BasePot { get; set; }

    public int Bonuses { get; set; }

    public int PlayCount { get; set; }

    public float TotalPlayTime { get; set; }
}
