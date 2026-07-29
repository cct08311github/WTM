#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Etl.Models;

/// <summary>
/// ETL 執行記錄 — 每次 Job 執行產生一筆
/// </summary>
/// <remarks>
/// Issue #841/#862: implements <see cref="ITenant"/> so the DataContext global query filter
/// (applied via <c>EtlDbContextExtensions.ApplyEtlModels(ModelBuilder, EmptyContext)</c>)
/// scopes run logs to the current tenant when multi-tenancy is enabled. <see cref="TenantCode"/>
/// is a NEW column -- see the #862 migration notes in CHANGELOG.md and
/// docs/production-readiness.md for the ALTER TABLE + backfill-from-EtlJobDefinitions script
/// existing deployments need to run, and the resulting visibility change for pre-existing rows.
/// </remarks>
public class EtlRunLog : BasePoco, ITenant
{
    [Required]
    public Guid JobId { get; set; }

    /// <summary>
    /// Tenant discriminator for multi-tenant isolation (#841/#862). Populated by the
    /// scheduler/executor from the owning <see cref="EtlJobDefinition.TenantCode"/> at the
    /// moment each run log is written. Null in single-tenant deployments.
    /// </summary>
    [StringLength(50)]
    public string? TenantCode { get; set; }

    /// <summary>關聯的 Job 定義</summary>
    public EtlJobDefinition? Job { get; set; }

    public EtlRunTrigger Trigger { get; set; }
    public EtlRunResult Result { get; set; }

    public int ExtractedRows { get; set; }
    public int LoadedRows { get; set; }
    public int ErrorRows { get; set; }
    public long ElapsedMs { get; set; }

    [StringLength(4000)]
    public string? ErrorMessage { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    /// <summary>執行時的 watermark 起始值快照（供重跑用）</summary>
    public string? WatermarkSnapshot { get; set; }
}
