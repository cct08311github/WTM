#nullable enable
using System.Collections.Generic;

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

    /// <summary>
    /// 若為 true，表示這是乾跑（dry-run）結果。<see cref="LoadedRows"/> 恆為 0，目標表
    /// 沒有任何寫入，watermark 沒有 commit。由 <see cref="EtlPipelineConfig.IsDryRun"/> 觸發（#834）。
    /// </summary>
    public bool IsDryRun { get; init; }

    /// <summary>
    /// 乾跑模式下的預覽列：第一批 extract + transform 後，轉成 Dictionary 的前
    /// <see cref="EtlPipelineConfig.DryRunPreviewSampleSize"/> 筆（#834）。
    /// 非乾跑 / 無資料時為空 list。
    /// </summary>
    public IList<IDictionary<string, object?>> PreviewRows { get; init; } =
        new List<IDictionary<string, object?>>();

    /// <summary>
    /// 乾跑模式的結構化警告，例如「<c>MergeKeyColumn 'id' 不存在於 source columns</c>」。
    /// 實際執行時若遇到同樣情況會 throw；乾跑只警告以便 operator 修正。非乾跑為空。(#834)
    /// </summary>
    public IList<string> ValidationWarnings { get; init; } = new List<string>();
}
