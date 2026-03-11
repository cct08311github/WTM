#nullable enable

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// ETL Pipeline 執行結果
/// </summary>
public class EtlExecutionResult
{
    public bool Success { get; init; }
    public bool Aborted { get; init; }
    public int ExtractedRows { get; init; }
    public int LoadedRows { get; init; }
    public int ErrorRows { get; init; }
    public long ElapsedMs { get; init; }
    public string? ErrorMessage { get; init; }
    public string? NewWatermarkValue { get; init; }
}
