#nullable enable
using System;
using System.Collections.Generic;
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
///
/// Dry-run mode（<see cref="EtlPipelineConfig.IsDryRun"/>, #834）：
///   0. 僅做快速參數驗證
///   1. Extract（只抓第一個 batch，驗證 SQL）
///   2. Transform
///   3. 驗證 MergeKeyColumn 存在於 source columns
///   4. 回傳前 N 筆預覽 + ValidationWarnings；<b>不</b>寫 staging / target / watermark
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
    /// 執行完整 ETL Pipeline（或 dry-run 預覽模式）
    /// </summary>
    public async Task<EtlExecutionResult> ExecuteAsync(
        EtlPipelineConfig config,
        WatermarkStrategy watermark,
        CancellationToken cancellationToken = default)
    {
        if (config.IsDryRun)
        {
            return await ExecuteDryRunAsync(config, watermark, cancellationToken).ConfigureAwait(false);
        }

        var sw = Stopwatch.StartNew();
        int totalExtracted = 0;
        int totalLoaded = 0;
        int retryAttempts = 0;

        try
        {
            // 0. 快速失敗驗證
            if (config.BatchSize <= 0)
                throw new ArgumentException($"BatchSize must be greater than 0, got {config.BatchSize}.", nameof(config));
            if (config.LoadMode == EtlLoadMode.Merge && string.IsNullOrWhiteSpace(config.MergeKeyColumn))
                throw new ArgumentException(
                    "MergeKeyColumn must not be null or empty when LoadMode = Merge.",
                    nameof(config));

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

                // Count source rows before transform (for audit trail)
                int extractedBatchRows = batch.Rows.Count;
                totalExtracted += extractedBatchRows;

                // Transform hook
                var transformed = config.TransformFunc != null
                    ? config.TransformFunc(batch)
                    : batch;

                // Column mapping (10.5+): rename source-column names to
                // target-column names and drop columns not in the map.
                // Runs AFTER TransformFunc so apps can use Transform to
                // synthesise columns that the mapping then renames /
                // forwards. No-op when ColumnMappings is null/empty
                // (back-compat with 10.4.x — same-name 1:1 SqlBulkCopy).
                if (config.ColumnMappings != null && config.ColumnMappings.Count > 0)
                {
                    transformed = ApplyColumnMappings(transformed, config.ColumnMappings);
                }

                await BulkLoadWithRetryAsync(
                    config, transformed,
                    onRetryStarted: () => retryAttempts++,
                    cancellationToken).ConfigureAwait(false);

                totalLoaded += transformed.Rows.Count;

                // 更新 watermark 暫存
                if (watermark.Type != EtlWatermarkType.FullLoad && !string.IsNullOrEmpty(watermark.Column))
                {
                    var maxVal = GetMaxValue(batch, watermark.Column);
                    if (maxVal != null) watermark.UpdateFromBatchMax(maxVal);
                }

                // 回報進度
                ReportProgress(config, totalLoaded, sw);
            }

            // 4. Load to target — Merge or Replace per LoadMode
            ReportProgress(config, totalLoaded, sw,
                config.LoadMode == EtlLoadMode.Replace ? "Replacing" : "Merging");

            if (config.LoadMode == EtlLoadMode.Replace)
            {
                await _loader.ReplaceAsync(
                    config.TargetConnectionString, config.StagingTable.TableName,
                    config.TargetTableName, config.ReplaceWhereClause,
                    cancellationToken);
            }
            else
            {
                await _loader.MergeAsync(
                    config.TargetConnectionString, config.StagingTable.TableName,
                    config.TargetTableName, config.MergeKeyColumn,
                    cancellationToken);
            }

            // 5. 成功 → commit watermark
            var newWatermark = watermark.CommitPendingValue();

            sw.Stop();
            return new EtlExecutionResult
            {
                Success = true,
                ExtractedRows = totalExtracted,
                LoadedRows = totalLoaded,
                ElapsedMs = sw.ElapsedMilliseconds,
                NewWatermarkValue = newWatermark,
                RetryAttemptsTotal = retryAttempts,
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
                ErrorMessage = "Job was aborted",
                RetryAttemptsTotal = retryAttempts,
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
                ErrorMessage = EtlErrorSanitizer.Sanitize(ex),
                RetryAttemptsTotal = retryAttempts,
            };
        }
    }

    /// <summary>
    /// Wrap <see cref="IBulkLoader.BulkLoadAsync"/> with exponential-
    /// backoff retry per <see cref="EtlPipelineConfig.MaxBatchRetries"/>.
    /// Returns the number of *retry* attempts (not counting the first
    /// try); 0 means "first try succeeded". When retries are exhausted
    /// the original exception is re-thrown so callers see the same
    /// failure shape as before.
    /// </summary>
    /// <remarks>
    /// Exponential backoff with full jitter:
    /// <c>delay = random(0, BaseDelay × 2^attempt)</c>,
    /// clamped to <see cref="EtlPipelineConfig.BatchRetryMaxDelayMs"/>.
    /// Cancellation is honoured — a cancellation token observed during
    /// the wait short-circuits the retry loop and surfaces as
    /// <see cref="OperationCanceledException"/>.
    /// </remarks>
    private async Task BulkLoadWithRetryAsync(
        EtlPipelineConfig config,
        DataTable transformed,
        Action onRetryStarted,
        CancellationToken cancellationToken)
    {
        var maxRetries = Math.Clamp(config.MaxBatchRetries, 0, 50);
        var baseDelay = Math.Max(0, config.BatchRetryBaseDelayMs);
        var maxDelay = Math.Max(baseDelay, config.BatchRetryMaxDelayMs);
        var attempt = 0;

        while (true)
        {
            try
            {
                await _loader.BulkLoadAsync(
                    config.TargetConnectionString, config.StagingTable.TableName,
                    transformed, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                // Don't swallow — caller's outer try/catch handles abort.
                throw;
            }
            catch when (attempt < maxRetries && !cancellationToken.IsCancellationRequested)
            {
                attempt++;
                onRetryStarted();
                // Exponential w/ full jitter; attempt is capped at 50 so
                // (long)baseDelay << attempt won't overflow Int64.
                var ceilingMs = (long)baseDelay << Math.Min(attempt, 30);
                ceilingMs = Math.Min(ceilingMs, maxDelay);
                var jittered = ceilingMs <= 0 ? 0 : Random.Shared.NextInt64(0, ceilingMs + 1);
                if (jittered > 0)
                {
                    await Task.Delay((int)jittered, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Dry-run mode: extract-and-inspect without writing anything (#834).
    /// Never calls EnsureStagingTable / TruncateStaging / BulkLoad / Merge /
    /// watermark commit. Iterates at most one batch so operator gets feedback
    /// quickly regardless of source size.
    /// </summary>
    private async Task<EtlExecutionResult> ExecuteDryRunAsync(
        EtlPipelineConfig config,
        WatermarkStrategy watermark,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        int extractedRows = 0;
        var warnings = new List<string>();
        var preview = new List<IDictionary<string, object?>>();

        try
        {
            // Parameter validation upgraded to warnings in dry-run (informational,
            // never fatal) so operator sees all issues in one pass.
            if (config.BatchSize <= 0)
                warnings.Add($"BatchSize must be greater than 0, got {config.BatchSize}.");
            if (string.IsNullOrWhiteSpace(config.MergeKeyColumn))
                warnings.Add("MergeKeyColumn is null or empty — real run will throw ArgumentException.");
            if (config.DryRunPreviewSampleSize < 0)
                warnings.Add($"DryRunPreviewSampleSize is negative ({config.DryRunPreviewSampleSize}); treated as 0.");

            var sampleCap = Math.Max(0, config.DryRunPreviewSampleSize);
            var wmParam = watermark.GetParameterValue();

            await foreach (var batch in _source.ExtractBatchesAsync(
                config.SourceConnectionString, config.QueryTemplate, wmParam,
                Math.Max(1, config.BatchSize), cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                extractedRows = batch.Rows.Count;

                var transformed = config.TransformFunc != null
                    ? config.TransformFunc(batch)
                    : batch;

                // Validate that MergeKeyColumn exists in source columns — the
                // most common misconfiguration and the one that causes the
                // worst downstream damage (merge on wrong key → dup rows).
                if (!string.IsNullOrWhiteSpace(config.MergeKeyColumn) &&
                    !transformed.Columns.Contains(config.MergeKeyColumn))
                {
                    var availableCols = string.Join(", ",
                        transformed.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
                    warnings.Add(
                        $"MergeKeyColumn '{config.MergeKeyColumn}' not found in source columns: [{availableCols}].");
                }

                // Capture first-N rows as preview.
                var takeCount = Math.Min(sampleCap, transformed.Rows.Count);
                for (int i = 0; i < takeCount; i++)
                {
                    var row = transformed.Rows[i];
                    var dict = new Dictionary<string, object?>(transformed.Columns.Count);
                    foreach (DataColumn col in transformed.Columns)
                    {
                        var value = row[col];
                        dict[col.ColumnName] = value == DBNull.Value ? null : value;
                    }
                    preview.Add(dict);
                }

                // Calculate pending watermark (but never commit).
                if (watermark.Type != EtlWatermarkType.FullLoad && !string.IsNullOrEmpty(watermark.Column))
                {
                    var maxVal = GetMaxValue(batch, watermark.Column);
                    if (maxVal != null) watermark.UpdateFromBatchMax(maxVal);
                }

                break; // Dry-run stops after first batch — bounded cost.
            }

            // What would the real run commit? (Does NOT actually commit.)
            var pendingWatermark = watermark.PeekPendingValue();
            watermark.DiscardPendingValue();

            if (extractedRows == 0)
            {
                warnings.Add("Source returned zero rows. Check QueryTemplate and watermark value.");
            }

            sw.Stop();
            return new EtlExecutionResult
            {
                Success = true,
                IsDryRun = true,
                ExtractedRows = extractedRows,
                LoadedRows = 0,
                ElapsedMs = sw.ElapsedMilliseconds,
                NewWatermarkValue = pendingWatermark,
                PreviewRows = preview,
                ValidationWarnings = warnings,
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
                IsDryRun = true,
                ExtractedRows = extractedRows,
                LoadedRows = 0,
                ElapsedMs = sw.ElapsedMilliseconds,
                ErrorMessage = "Dry-run aborted.",
                PreviewRows = preview,
                ValidationWarnings = warnings,
            };
        }
        catch (Exception ex)
        {
            watermark.DiscardPendingValue();
            sw.Stop();
            return new EtlExecutionResult
            {
                Success = false,
                IsDryRun = true,
                ExtractedRows = extractedRows,
                LoadedRows = 0,
                ElapsedMs = sw.ElapsedMilliseconds,
                ErrorMessage = EtlErrorSanitizer.Sanitize(ex),
                PreviewRows = preview,
                ValidationWarnings = warnings,
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

    /// <summary>
    /// Apply <see cref="EtlPipelineConfig.ColumnMappings"/> to a batch:
    /// build a NEW <see cref="DataTable"/> containing only the columns
    /// listed in <paramref name="mappings"/>, renamed to the target name.
    /// Source columns absent from the map are dropped (whitelist
    /// semantics). When a mapping key is not present in the source
    /// batch, the target column is created and filled with DBNull —
    /// matches operator intent of "this column should always be in
    /// the output, sometimes the source has it sometimes not". Public
    /// for unit-test determinism.
    /// </summary>
    public static DataTable ApplyColumnMappings(
        DataTable source, IDictionary<string, string> mappings)
    {
        if (mappings == null) { throw new ArgumentNullException(nameof(mappings)); }

        var output = new DataTable();
        // Build target column schema in mapping-iteration order so the
        // operator controls column order at the load step.
        foreach (var kv in mappings)
        {
            var srcName = kv.Key;
            var tgtName = kv.Value;
            if (string.IsNullOrWhiteSpace(srcName) || string.IsNullOrWhiteSpace(tgtName))
            {
                throw new ArgumentException(
                    "Column mapping entry has empty source or target name.", nameof(mappings));
            }
            var srcType = source.Columns.Contains(srcName)
                ? source.Columns[srcName]!.DataType
                : typeof(object);
            output.Columns.Add(tgtName, srcType);
        }

        foreach (DataRow srcRow in source.Rows)
        {
            var newRow = output.NewRow();
            foreach (var kv in mappings)
            {
                var tgtName = kv.Value;
                if (source.Columns.Contains(kv.Key))
                {
                    newRow[tgtName] = srcRow[kv.Key];
                }
                else
                {
                    newRow[tgtName] = DBNull.Value;
                }
            }
            output.Rows.Add(newRow);
        }
        return output;
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
