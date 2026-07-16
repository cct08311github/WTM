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
    /// <summary>
    /// #673/#700: default safety cap on the number of dead-letter entries buffered in
    /// memory for a single run, used when the caller does not supply one explicitly
    /// (e.g. direct construction outside DI). Mirrors <see cref="EtlOptions.MaxDeadLetterRowsPerRun"/>'s
    /// default so behaviour is unchanged for callers that predate the configurable cap.
    /// A run that produces more violations than this is almost certainly misconfigured
    /// (e.g. a quality rule that rejects nearly every row) — capturing unbounded rows in
    /// memory risks OOM long before the DB write. Once the cap is reached, a single
    /// truncation-marker entry is appended and further entries for this run are dropped
    /// (never silently — the marker documents that truncation happened and by how much).
    /// </summary>
    internal const int DefaultMaxDeadLetterRowsPerRun = 10_000;

    private readonly IEtlSource _source;
    private readonly IBulkLoader _loader;
    private readonly IProgress<EtlProgress>? _progress;
    private readonly ILogger? _logger;
    private readonly IEtlGovernanceStore _governance;
    private readonly int _maxDeadLetterRowsPerRun;

    public EtlPipelineExecutor(
        IEtlSource source,
        IBulkLoader loader,
        IProgress<EtlProgress>? progress = null,
        ILogger? logger = null,
        IEtlGovernanceStore? governanceStore = null,
        int? maxDeadLetterRowsPerRun = null)
    {
        _source = source;
        _loader = loader;
        _progress = progress;
        _logger = logger;
        _governance = governanceStore ?? NullEtlGovernanceStore.Instance;
        // #700: caller-supplied cap (normally EtlOptions.MaxDeadLetterRowsPerRun, wired
        // by EtlQuartzJob/EtlSchedulerService) takes precedence; falls back to the
        // pre-#700 hardcoded default for callers that don't pass one.
        _maxDeadLetterRowsPerRun = maxDeadLetterRowsPerRun ?? DefaultMaxDeadLetterRowsPerRun;
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
        // ETL-004/#673: dead-letter entries accumulated for the ENTIRE run and flushed
        // exactly once, after the run's outcome (success/abort/failure) is known — see
        // FlushDeadLetterBufferAsync. Previously this flushed per-batch, which could
        // persist rows from a run that later failed, before the caller (e.g. a rerun
        // after a transient failure) had a chance to decide whether they were still
        // relevant; buffering avoids writing partial-run diagnostics ahead of the
        // final outcome. Bounded by _maxDeadLetterRowsPerRun (#700: configurable via
        // EtlOptions.MaxDeadLetterRowsPerRun; defaults to DefaultMaxDeadLetterRowsPerRun).
        var deadLetterEntries = new List<EtlDeadLetterEntry>();
        var deadLetterTruncated = false;

        try
        {
            // 0. 快速失敗驗證
            if (config.BatchSize <= 0)
                throw new ArgumentException($"BatchSize must be greater than 0, got {config.BatchSize}.", nameof(config));
            if (config.LoadMode == EtlLoadMode.Merge && string.IsNullOrWhiteSpace(config.MergeKeyColumn))
                throw new ArgumentException(
                    "MergeKeyColumn must not be null or empty when LoadMode = Merge.",
                    nameof(config));

            // #673(d): run-scoped dead-letter dedupe — before this run writes anything,
            // clear any dead-letter rows left over from a PRIOR run of this same job that
            // did NOT complete successfully. A retry re-extracts the same
            // (watermark-unchanged) window and will produce its own up-to-date
            // diagnostics, so the previous failed attempt's rows are stale and would
            // otherwise accumulate as duplicates on every retry. Rows from a
            // successfully-completed run (RunSucceeded == true) or written before this
            // feature existed (RunSucceeded == null) are never touched — see
            // EtlDeadLetterRow.RunSucceeded. Best-effort: a failure here is logged, not
            // fatal, and never blocks the run itself from proceeding.
            if (config.EnableDeadLetter)
            {
                try
                {
                    await _governance.ClearDeadLetterFromFailedRunsAsync(config.JobId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex,
                        "ETL dead-letter failed-run cleanup failed for Job={JobId}", config.JobId);
                }
            }

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
                // #673(b): capture a batch-level dead-letter marker when TransformFunc
                // throws. Per-row attribution is not possible here — the function
                // receives the whole batch and can do arbitrary reshaping — so we record
                // one documented marker entry rather than guessing which rows were at
                // fault. The `when` filter means this adds ZERO behaviour when
                // EnableDeadLetter is false (or on cancellation): the original exception
                // propagates untouched to the existing outer catch, preserving the exact
                // rollback/rethrow semantics that existed before this change.
                DataTable transformed;
                try
                {
                    transformed = config.TransformFunc != null
                        ? config.TransformFunc(batch)
                        : batch;
                }
                catch (Exception ex) when (config.EnableDeadLetter && ex is not OperationCanceledException)
                {
                    TryCaptureBatchLevelDeadLetter(
                        deadLetterEntries, ref deadLetterTruncated, batch, ex, EtlDeadLetterSource.TransformError);
                    throw;
                }

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
                    // #673(c): Abort throws EtlQualityRuleViolationException with the
                    // offending row attached (only when captureRows/EnableDeadLetter is
                    // on) — capture it here, BEFORE the exception reaches the outer
                    // catch, then rethrow unchanged. This is additive diagnostics only:
                    // the exception type, message, and the fact that it fails the run
                    // are exactly as before.
                    try
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
                                TryAddDeadLetterEntry(deadLetterEntries, ref deadLetterTruncated,
                                    new EtlDeadLetterEntry(
                                        SerializeRow(transformed, row),
                                        reason,
                                        EtlDeadLetterSource.QualityRule));
                            }
                        }
                    }
                    catch (EtlQualityRuleViolationException ex) when (config.EnableDeadLetter && ex.OffendingRow != null)
                    {
                        TryAddDeadLetterEntry(deadLetterEntries, ref deadLetterTruncated,
                            new EtlDeadLetterEntry(
                                SerializeRow(transformed, ex.OffendingRow),
                                ex.ViolationReason ?? ex.Message,
                                EtlDeadLetterSource.QualityRuleAbort));
                        throw;
                    }
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

                // #673(a): capture a batch-level dead-letter marker when the retried
                // BulkLoad ultimately fails (type/constraint errors etc.). Per-row
                // attribution isn't available at this level — bulk-copy failures are
                // reported per-batch by ADO.NET providers — so we record one documented
                // marker for the whole batch rather than guessing. The `when` filter
                // means this is a no-op (zero behaviour change) when EnableDeadLetter is
                // false: the original exception propagates to the existing outer catch
                // exactly as before, and retry/backoff behaviour inside
                // BulkLoadWithRetryAsync is untouched.
                try
                {
                    await BulkLoadWithRetryAsync(
                        config, transformed,
                        onRetryStarted: () => retryAttempts++,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (config.EnableDeadLetter && ex is not OperationCanceledException)
                {
                    TryCaptureBatchLevelDeadLetter(
                        deadLetterEntries, ref deadLetterTruncated, transformed, ex, EtlDeadLetterSource.LoadError);
                    throw;
                }

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

            // #673(d): flush the run's buffered dead-letter entries now that the
            // outcome is known (success), then mark them RunSucceeded=true so the NEXT
            // run's start-of-run cleanup never deletes them — this is a permanent
            // Drop-path record for a window that loaded successfully, not stale retry
            // noise. See FlushDeadLetterBufferAsync for why a flush failure here is
            // logged, not propagated.
            await FlushDeadLetterBufferAsync(config, runId, deadLetterEntries, runSucceeded: true, cancellationToken)
                .ConfigureAwait(false);

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
            // #673: cancellation is an operator action, not a data-quality event —
            // any dead-letter entries buffered so far for this cancelled run are
            // discarded along with the rest of the run's progress (consistent with
            // TransformFunc/BulkLoad capture also excluding OperationCanceledException).
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
            // #673(c): flush whatever was captured for this failed run — including a
            // quality-rule Abort's offending row and/or earlier batches' Drop-path
            // captures from before the failure. This is the diagnostic payoff of
            // buffering: the flush happens AFTER the outcome (failure) is known,
            // tagged with this run's RunId, instead of racing ahead of it per-batch.
            // runSucceeded: false — left for the NEXT run's start-of-run cleanup
            // (ClearDeadLetterFromFailedRunsAsync) to remove once superseded.
            await FlushDeadLetterBufferAsync(config, runId, deadLetterEntries, runSucceeded: false, cancellationToken)
                .ConfigureAwait(false);
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
    /// #673(d): persists the run's buffered dead-letter entries in a single write,
    /// tagged with <paramref name="runId"/>. No-op when dead-letter is disabled or the
    /// buffer is empty. A failure to persist is logged but never propagated — by the
    /// time this runs, the run's real outcome (success/failure) is already decided;
    /// letting a diagnostics-write failure override that outcome would be worse than
    /// losing the diagnostics (e.g. it would report an already-merged, successful load
    /// as "Failed" and trigger an unnecessary rerun).
    /// </summary>
    private async Task FlushDeadLetterBufferAsync(
        EtlPipelineConfig config, Guid runId, List<EtlDeadLetterEntry> buffer,
        bool runSucceeded, CancellationToken cancellationToken)
    {
        if (!config.EnableDeadLetter || buffer.Count == 0) { return; }

        try
        {
            await _governance.AddDeadLetterRowsAsync(
                config.JobId, runId, buffer, config.DeadLetterTenantCode, cancellationToken)
                .ConfigureAwait(false);

            if (runSucceeded)
            {
                // #673(d): flip RunSucceeded=true for the rows just written so a future
                // run's ClearDeadLetterFromFailedRunsAsync never deletes them. A failure
                // here is folded into the same best-effort logging as the write above —
                // worst case the rows remain RunSucceeded=false and get cleaned up by the
                // next run's cleanup pass, which is a false-negative (loses a legitimate
                // historical record slightly early) rather than a false-positive
                // (deleting something it shouldn't) — the safer failure mode.
                await _governance.MarkDeadLetterRunSucceededAsync(config.JobId, runId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "ETL dead-letter flush failed for Job={JobId} Run={RunId}: {Count} buffered entries were NOT persisted",
                config.JobId, runId, buffer.Count);
        }
    }

    /// <summary>
    /// #673/#700: appends <paramref name="entry"/> to the run's dead-letter buffer,
    /// enforcing the configured <see cref="_maxDeadLetterRowsPerRun"/> cap (from
    /// <see cref="EtlOptions.MaxDeadLetterRowsPerRun"/>). Once the cap is reached a
    /// single truncation-marker entry is appended (once) and all further entries for
    /// this run are dropped — bounds memory for a pathologically-misconfigured quality
    /// rule without ever silently under-reporting (the marker documents that it
    /// happened). Instance method (not static) so it can read the per-executor cap.
    /// </summary>
    private void TryAddDeadLetterEntry(
        List<EtlDeadLetterEntry> buffer, ref bool truncated, EtlDeadLetterEntry entry)
    {
        if (truncated) { return; }
        if (buffer.Count >= _maxDeadLetterRowsPerRun)
        {
            truncated = true;
            buffer.Add(new EtlDeadLetterEntry(
                "{\"_marker\":\"dead-letter-capture-truncated\"}",
                $"Dead-letter capture capped at {_maxDeadLetterRowsPerRun} entries for this run; " +
                "further violations were not captured (bounds memory use for this run).",
                EtlDeadLetterSource.QualityRule));
            return;
        }
        buffer.Add(entry);
    }

    /// <summary>
    /// #673(a)/(b): builds a batch-level dead-letter marker for failures where per-row
    /// attribution isn't available — bulk-load and transform exceptions operate on the
    /// whole batch (a provider-level bulk-copy failure or an arbitrary
    /// <see cref="EtlPipelineConfig.TransformFunc"/> reshape doesn't identify a single
    /// offending row). One documented marker entry represents the whole batch rather
    /// than guessing. Routed through <see cref="TryAddDeadLetterEntry"/> for the same
    /// truncation cap as row-level captures. The error is sanitized via
    /// <see cref="EtlErrorSanitizer"/> — never a raw provider message.
    /// </summary>
    private void TryCaptureBatchLevelDeadLetter(
        List<EtlDeadLetterEntry> buffer, ref bool truncated, DataTable batch, Exception ex, string source)
    {
        var marker = new Dictionary<string, object?>
        {
            ["_marker"] = "batch-level-capture",
            ["batchRowCount"] = batch.Rows.Count,
            ["note"] = "Per-row attribution is not available for this failure type; " +
                       "all rows in this batch are represented by this single dead-letter entry.",
        };
        TryAddDeadLetterEntry(buffer, ref truncated, new EtlDeadLetterEntry(
            JsonSerializer.Serialize(marker),
            EtlErrorSanitizer.Sanitize(ex),
            source));
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
        // ETL-015: source connection string is intentionally omitted to
        // prevent DB passwords from reaching the log sink (Issue #376).
        _logger?.LogInformation(
            "ETL dry-run started: Job={JobId} Name={JobName} Query={Query} Watermark={Watermark}",
            config.JobId, config.JobName,
            EtlErrorSanitizer.SanitizeRaw(config.QueryTemplate),
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
                config.JobId, config.JobName, sw.ElapsedMilliseconds, EtlErrorSanitizer.SanitizeRaw(ex.Message));
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
        // Perf(#674): pre-resolve each mapping's source column ordinal ONCE here, instead
        // of a Columns.Contains(key) + string-indexed srcRow[key] lookup PER ROW PER
        // MAPPING in the loop below (O(rows * mappings) string lookups over the whole
        // batch). srcOrdinals[i] == -1 marks a mapping whose source column is absent from
        // this batch — the row loop below still emits DBNull.Value for it, identical to
        // the previous per-row Contains() check. Target-side is also indexed by position
        // (mappingCount output columns are added below in this exact loop order, so
        // output column ordinal i == array index i) rather than by name — same target
        // column, same value, just resolved once instead of via a name lookup per row.
        var mappingCount = mappings.Count;
        var srcOrdinals = new int[mappingCount];
        int idx = 0;
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
            var srcCol = source.Columns.Contains(srcName) ? source.Columns[srcName] : null;
            var srcType = srcCol?.DataType ?? typeof(object);
            output.Columns.Add(tgtName, srcType);
            srcOrdinals[idx] = srcCol?.Ordinal ?? -1;
            idx++;
        }

        foreach (DataRow srcRow in source.Rows)
        {
            var newRow = output.NewRow();
            for (int i = 0; i < mappingCount; i++)
            {
                newRow[i] = srcOrdinals[i] >= 0 ? srcRow[srcOrdinals[i]] : DBNull.Value;
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
