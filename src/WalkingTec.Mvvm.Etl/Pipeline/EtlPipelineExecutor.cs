#nullable enable
using System;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// ETL Pipeline 執行引擎
///
/// 流程：
/// 1. EnsureStagingTable
/// 2. TruncateStaging
/// 3. foreach batch in Extract:
///    a. CancellationToken check
///    b. Transform（如果有 mapper）
///    c. BulkLoad to staging
///    d. 更新進度
/// 4. Merge staging → target
/// 5. 成功 → commit watermark；失敗 → discard watermark
/// </summary>
public class EtlPipelineExecutor
{
    private readonly IEtlSource _source;
    private readonly IBulkLoader _loader;
    private readonly IProgress<EtlProgress>? _progress;

    public EtlPipelineExecutor(IEtlSource source, IBulkLoader loader, IProgress<EtlProgress>? progress = null)
    {
        _source = source;
        _loader = loader;
        _progress = progress;
    }

    /// <summary>
    /// 執行完整 ETL Pipeline
    /// </summary>
    public async Task<EtlExecutionResult> ExecuteAsync(
        EtlPipelineConfig config,
        WatermarkStrategy watermark,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        int totalExtracted = 0;
        int totalLoaded = 0;

        try
        {
            // 1. 確認 staging table
            await _loader.EnsureStagingTableAsync(
                config.TargetConnectionString, config.StagingTable.TableName,
                config.StagingTable, cancellationToken);

            // 2. 清空 staging
            await _loader.TruncateStagingAsync(
                config.TargetConnectionString, config.StagingTable.TableName,
                cancellationToken);

            // 3. Extract + Load to staging
            var wmParam = watermark.GetParameterValue();

            await foreach (var batch in _source.ExtractBatchesAsync(
                config.SourceConnectionString, config.QueryTemplate, wmParam,
                config.BatchSize, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Transform hook
                var transformed = config.TransformFunc != null
                    ? config.TransformFunc(batch)
                    : batch;

                int batchRows = transformed.Rows.Count;
                totalExtracted += batchRows;

                await _loader.BulkLoadAsync(
                    config.TargetConnectionString, config.StagingTable.TableName,
                    transformed, cancellationToken);

                totalLoaded += batchRows;

                // 更新 watermark 暫存
                if (watermark.Type != EtlWatermarkType.FullLoad && !string.IsNullOrEmpty(watermark.Column))
                {
                    var maxVal = GetMaxValue(batch, watermark.Column);
                    if (maxVal != null) watermark.UpdateFromBatchMax(maxVal);
                }

                // 回報進度
                ReportProgress(config, totalLoaded, sw);
            }

            // 4. Merge staging → target
            ReportProgress(config, totalLoaded, sw, "Merging");

            await _loader.MergeAsync(
                config.TargetConnectionString, config.StagingTable.TableName,
                config.TargetTableName, config.MergeKeyColumn,
                cancellationToken);

            // 5. 成功 → commit watermark
            var newWatermark = watermark.CommitPendingValue();

            sw.Stop();
            return new EtlExecutionResult
            {
                Success = true,
                ExtractedRows = totalExtracted,
                LoadedRows = totalLoaded,
                ElapsedMs = sw.ElapsedMilliseconds,
                NewWatermarkValue = newWatermark
            };
        }
        catch (OperationCanceledException)
        {
            watermark.DiscardPendingValue();
            sw.Stop();
            return new EtlExecutionResult
            {
                Success = false,
                Aborted = true,
                ExtractedRows = totalExtracted,
                LoadedRows = totalLoaded,
                ElapsedMs = sw.ElapsedMilliseconds,
                ErrorMessage = "Job was aborted"
            };
        }
        catch (Exception ex)
        {
            watermark.DiscardPendingValue();
            sw.Stop();
            return new EtlExecutionResult
            {
                Success = false,
                ExtractedRows = totalExtracted,
                LoadedRows = totalLoaded,
                ElapsedMs = sw.ElapsedMilliseconds,
                ErrorMessage = ex.ToString()
            };
        }
    }

    private void ReportProgress(EtlPipelineConfig config, int totalLoaded, Stopwatch sw, string phase = "Loading")
    {
        _progress?.Report(new EtlProgress
        {
            JobId = config.JobId,
            JobName = config.JobName,
            ProcessedRows = totalLoaded,
            TotalRows = null,
            Phase = phase,
            RowsPerSecond = totalLoaded / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
            StartedAt = DateTime.UtcNow - sw.Elapsed
        });
    }

    private static object? GetMaxValue(DataTable batch, string columnName)
    {
        if (!batch.Columns.Contains(columnName) || batch.Rows.Count == 0)
            return null;

        var values = batch.AsEnumerable()
            .Select(r => r[columnName])
            .Where(v => v != null && v != DBNull.Value)
            .ToList();

        return values.Count == 0 ? null : values.Max();
    }
}
