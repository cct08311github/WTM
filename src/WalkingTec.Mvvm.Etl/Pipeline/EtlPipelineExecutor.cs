#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Etl.Governance;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;

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
    private readonly ILogger? _logger;
    private readonly IEtlGovernanceStore _governance;

    public EtlPipelineExecutor(
        IEtlSource source,
        IBulkLoader loader,
        IProgress<EtlProgress>? progress = null,
        ILogger? logger = null,
        IEtlGovernanceStore? governanceStore = null)
    {
        _source = source;
        _loader = loader;
        _progress = progress;
        _logger = logger;
        _governance = governanceStore ?? NullEtlGovernanceStore.Instance;
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

        var runId = Guid.NewGuid();
        var sw = Stopwatch.StartNew();
        int totalExtracted = 0;
        int totalLoaded = 0;
        int retryAttempts = 0;
        int qualityFailedRows = 0;
        var qualityFailureSamples = new List<string>();
        var warnings = new List<string>();
        // ETL-004: accumulated dead-letter entries (per-batch, flushed to store after each batch)
        var deadLetterBatch = new List<EtlDeadLetterEntry>();

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

            // Opt-in: propagate WatermarkSqlType to MssqlSource when config specifies it.
            // This is a set property (not init) so it can be applied here after construction.
            // Cast is safe: no-op when source is not MssqlSource (e.g. OracleSource).
            if (config.WatermarkSqlType.HasValue && _source is WalkingTec.Mvvm.Etl.Pipeline.Sources.MssqlSource mssqlSrc)
            {
                mssqlSrc.WatermarkSqlType = config.WatermarkSqlType;
            }

            var wmParam = watermark.GetParameterValue();
            // Capture staging column names from the first transformed batch so we can
            // pass them to the concrete-type MergeAsync overload and skip the
            // INFORMATION_SCHEMA/USER_TAB_COLUMNS round-trip at merge time.
            // Null until the first batch is processed.
            IReadOnlyList<string>? stagingColumnNames = null;

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

                // Quality rules (10.5.1+) — Drop / Continue / Abort 違規列。
                // Abort 會 throw 並沿用既有 catch 走 watermark discard 路徑。
                if (config.QualityRules != null && config.QualityRules.Count > 0)
                {
                    transformed = EtlQualityRuleEvaluator.Apply(
                        transformed, config.QualityRules, config.QualityRuleAction,
                        out int batchFailed, out var batchSamples,
                        captureRows: config.EnableDeadLetter,
                        out var droppedRows);
                    qualityFailedRows += batchFailed;
                    foreach (var s in batchSamples)
                    {
                        if (qualityFailureSamples.Count >= EtlQualityRuleEvaluator.MaxFailureSamples) { break; }
                        qualityFailureSamples.Add(s);
                    }

                    // ETL-004: capture failed rows for dead-letter store
                    if (config.EnableDeadLetter && droppedRows != null && droppedRows.Count > 0)
                    {
                        foreach (var (row, reason) in droppedRows)
                        {
                            deadLetterBatch.Add(new EtlDeadLetterEntry(
                                SerializeRow(transformed, row),
                                reason,
                                EtlDeadLetterSource.QualityRule));
                        }
                    }
                }

                // ETL-004: flush dead-letter entries for this batch to persistent store
                if (config.EnableDeadLetter && deadLetterBatch.Count > 0)
                {
                    await _governance.AddDeadLetterRowsAsync(
                        config.JobId, runId, deadLetterBatch,
                        config.DeadLetterTenantCode, cancellationToken)
                        .ConfigureAwait(false);
                    deadLetterBatch.Clear();
                }

                // Capture column names from the first transformed batch (all batches share
                // the same schema). Used to skip the INFORMATION_SCHEMA/USER_TAB_COLUMNS
                // round-trip when the concrete loader exposes the internal MergeAsync
                // overload accepting pre-resolved column names.
                if (stagingColumnNames == null)
                {
                    stagingColumnNames = transformed.Columns
                        .Cast<DataColumn>()
                        .Select(c => c.ColumnName)
                        .ToList();
                }

                await BulkLoadWithRetryAsync(
                    config, transformed,
                    onRetryStarted: () => retryAttempts++,
                    cancellationToken).ConfigureAwait(false);

                totalLoaded += transformed.Rows.Count;

                // 更新 watermark 暫存
                if (watermark.Type != EtlWatermarkType.FullLoad && !string.IsNullOrEmpty(watermark.Column))
                {
                    // L16: surface a warning when the watermark column is absent from the batch
                    // schema so operators discover misconfiguration immediately rather than
                    // silently re-processing already-ingested rows on every subsequent run.
                    if (!batch.Columns.Contains(watermark.Column))
                    {
                        var msg = $"WatermarkColumn '{watermark.Column}' is not present in the batch schema. " +
                                  "Watermark will not advance; the next run will re-process this batch. " +
                                  "Verify EtlPipelineConfig.WatermarkColumn matches a column returned by the source query.";
                        _logger?.LogWarning(msg);
                        if (!warnings.Contains(msg))
                            warnings.Add(msg);
                    }
                    else
                    {
                        var maxVal = GetMaxValue(batch, watermark.Column);
                        if (maxVal != null) watermark.UpdateFromBatchMax(maxVal);
                    }
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
                await MergeWithColumnHintAsync(
                    config.TargetConnectionString, config.StagingTable.TableName,
                    config.TargetTableName, config.MergeKeyColumn,
                    stagingColumnNames, cancellationToken);
            }

            // 5. 成功 → commit watermark
            var newWatermark = watermark.CommitPendingValue();

            // ETL-005: write lineage record on success
            if (config.EnableLineage)
            {
                await _governance.AddLineageRecordAsync(new Models.EtlLineageRecord
                {
                    JobId            = config.JobId,
                    RunId            = runId,
                    SourceKind       = config.LineageSourceKind ?? string.Empty,
                    TargetTable      = config.TargetTableName,
                    ColumnMappingsJson = config.ColumnMappings != null
                        ? JsonSerializer.Serialize(config.ColumnMappings)
                        : null,
                    ExtractedRows    = totalExtracted,
                    LoadedRows       = totalLoaded,
                    QualityFailedRows = qualityFailedRows,
                    RecordedAt       = DateTime.UtcNow,
                }, cancellationToken).ConfigureAwait(false);
            }

            sw.Stop();
            return new EtlExecutionResult
            {
                Success = true,
                RunId = runId,
                ExtractedRows = totalExtracted,
                LoadedRows = totalLoaded,
                ElapsedMs = sw.ElapsedMilliseconds,
                NewWatermarkValue = newWatermark,
                RetryAttemptsTotal = retryAttempts,
                QualityFailedRows = qualityFailedRows,
                QualityFailureSamples = qualityFailureSamples,
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
                RunId = runId,
                ExtractedRows = totalExtracted,
                LoadedRows = totalLoaded,
                ElapsedMs = sw.ElapsedMilliseconds,
                ErrorMessage = "Job was aborted",
                RetryAttemptsTotal = retryAttempts,
                QualityFailedRows = qualityFailedRows,
                QualityFailureSamples = qualityFailureSamples,
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
                RunId = runId,
                ExtractedRows = totalExtracted,
                LoadedRows = totalLoaded,
                ElapsedMs = sw.ElapsedMilliseconds,
                ErrorMessage = EtlErrorSanitizer.Sanitize(ex),
                RetryAttemptsTotal = retryAttempts,
                QualityFailedRows = qualityFailedRows,
                QualityFailureSamples = qualityFailureSamples,
                ValidationWarnings = warnings,
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
    /// Dispatches to the concrete-loader internal MergeAsync overload when
    /// <paramref name="columnNames"/> is available (batch schema captured earlier),
    /// skipping the INFORMATION_SCHEMA/USER_TAB_COLUMNS round-trip. Falls back to
    /// the public <see cref="IBulkLoader.MergeAsync"/> for third-party loaders or
    /// when no batch was processed (columnNames is null).
    /// This wiring does NOT change the <see cref="IBulkLoader"/> interface.
    /// </summary>
    private async Task MergeWithColumnHintAsync(
        string connectionString, string stagingTableName,
        string targetTableName, string mergeKeyColumn,
        IReadOnlyList<string>? columnNames,
        CancellationToken cancellationToken)
    {
        if (columnNames != null)
        {
            // Concrete-type fast path: skip metadata round-trip.
            if (_loader is MssqlBulkLoader mssql)
            {
                await mssql.MergeAsync(connectionString, stagingTableName,
                    targetTableName, mergeKeyColumn, columnNames, cancellationToken);
                return;
            }
            if (_loader is OracleBulkLoader oracle)
            {
                await oracle.MergeAsync(connectionString, stagingTableName,
                    targetTableName, mergeKeyColumn, columnNames, cancellationToken);
                return;
            }
        }
        // Fallback: public IBulkLoader path (third-party loaders, or no batches processed).
        await _loader.MergeAsync(connectionString, stagingTableName,
            targetTableName, mergeKeyColumn, cancellationToken);
    }

    /// <summary>
    /// Dry-run mode: extract-and-inspect without writing anything (#834).
    /// Never calls EnsureStagingTable / TruncateStaging / BulkLoad / Merge /
    /// watermark commit. Iterates at most one batch so operator gets feedback
    /// quickly regardless of source size.
    /// ETL-015: logs a structured audit entry for the source-data access so
    /// dry-run reads are traceable like real runs.
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

        // ETL-015: audit log — record that a dry-run read of source data is starting.
        _logger?.LogInformation(
            "ETL dry-run started: Job={JobId} Name={JobName} Source={SourceCs} Query={Query} Watermark={Watermark}",
            config.JobId, config.JobName,
            config.SourceConnectionString,
            config.QueryTemplate,
            watermark.GetParameterValue()?.ToString() ?? "(none)");

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

            // ETL-015: audit log — dry-run completed (source data access is now auditable).
            _logger?.LogInformation(
                "ETL dry-run completed: Job={JobId} Name={JobName} ExtractedRows={ExtractedRows} " +
                "ElapsedMs={ElapsedMs} Warnings={WarningCount}",
                config.JobId, config.JobName, extractedRows,
                sw.ElapsedMilliseconds, warnings.Count);

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
            // ETL-015: audit the cancellation of the dry-run source read
            _logger?.LogWarning(
                "ETL dry-run aborted: Job={JobId} Name={JobName} ElapsedMs={ElapsedMs}",
                config.JobId, config.JobName, sw.ElapsedMilliseconds);
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
            // ETL-015: audit the failure of the dry-run source read
            _logger?.LogError(
                "ETL dry-run failed: Job={JobId} Name={JobName} ElapsedMs={ElapsedMs} Error={Error}",
                config.JobId, config.JobName, sw.ElapsedMilliseconds, ex.Message);
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

    /// <summary>
    /// Serializes a <see cref="DataRow"/> to a compact JSON string (column→value dictionary).
    /// DBNull is serialized as JSON null. Called only when dead-letter is enabled
    /// to avoid per-row serialization overhead on the hot path.
    /// </summary>
    private static string SerializeRow(DataTable tableSchema, DataRow row)
    {
        // Serialize from the *original* table schema since `row` still belongs to
        // the source DataTable (dropped rows are excluded from the Clone but the
        // DataRow objects themselves retain their original table reference).
        var cols = row.Table.Columns;
        var dict = new Dictionary<string, object?>(cols.Count);
        for (int i = 0; i < cols.Count; i++)
        {
            var v = row[i];
            dict[cols[i].ColumnName] = v == DBNull.Value ? null : v?.ToString();
        }
        return JsonSerializer.Serialize(dict);
    }

    private static object? GetMaxValue(DataTable batch, string columnName)
    {
        if (!batch.Columns.Contains(columnName) || batch.Rows.Count == 0)
            return null;

        // Single-pass: pre-resolve ordinal once; use Comparer<object>.Default to
        // preserve null-semantics identical to the previous LINQ .Max() — no
        // IComparable cast that would throw on atypical DataTable column types.
        int ord = batch.Columns[columnName]!.Ordinal;
        var comparer = Comparer<object>.Default;
        object? max = null;
        foreach (DataRow row in batch.Rows)
        {
            var v = row[ord];
            if (v == null || v == DBNull.Value) continue;
            if (max == null || comparer.Compare(v, max) > 0)
                max = v;
        }
        return max;
    }
}
