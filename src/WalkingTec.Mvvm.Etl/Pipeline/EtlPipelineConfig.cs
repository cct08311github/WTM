#nullable enable
using System;
using System.Data;

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// ETL Pipeline 執行配置
/// </summary>
public record EtlPipelineConfig
{
    public Guid JobId { get; init; }
    public string JobName { get; init; } = string.Empty;
    public string SourceConnectionString { get; init; } = string.Empty;
    public string TargetConnectionString { get; init; } = string.Empty;
    public string QueryTemplate { get; init; } = string.Empty;
    public string TargetTableName { get; init; } = string.Empty;
    public string MergeKeyColumn { get; init; } = string.Empty;
    public int BatchSize { get; init; } = 50_000;
    public StagingTableSpec StagingTable { get; init; } = null!;

    /// <summary>可選的轉換函式（null = 零轉換直搬）</summary>
    public Func<DataTable, DataTable>? TransformFunc { get; init; }
}
