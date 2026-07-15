#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Support.Quartz;
using WalkingTec.Mvvm.Etl.Alerting;
using WalkingTec.Mvvm.Etl.Governance;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;

namespace WalkingTec.Mvvm.Etl.Scheduling;

/// <summary>
/// Quartz Job 橋接器 — 每次排程觸發時：
/// 1. 從 DB 讀 EtlJobDefinition
/// 2. 檢查 SkipCount、Status
/// 3. 建立 EtlPipelineExecutor 並執行
/// 4. 寫入 EtlRunLog
/// 5. 更新 watermark（僅成功時）
/// </summary>
[DisallowConcurrentExecution]
public class EtlQuartzJob : WtmJob
{
    public override async Task Execute(IJobExecutionContext context)
    {
        var jobDefIdStr = context.MergedJobDataMap.GetString("EtlJobDefinitionId");
        if (string.IsNullOrEmpty(jobDefIdStr) || !Guid.TryParse(jobDefIdStr, out var jobDefId))
            return;

        var trigger = context.MergedJobDataMap.ContainsKey("EtlTriggerType")
            ? Enum.Parse<EtlRunTrigger>(context.MergedJobDataMap.GetString("EtlTriggerType")!)
            : EtlRunTrigger.Scheduled;

        var dc = Wtm.DC;
        var tracker = Sp.GetService<EtlProgressTracker>();

        // 1. 讀 EtlJobDefinition
        var jobDef = await dc.Set<EtlJobDefinition>().FindAsync(jobDefId);
        if (jobDef == null) return;

        // 2. 檢查 SkipCount
        if (jobDef.SkipCount > 0)
        {
            jobDef.SkipCount--;
            dc.Set<EtlJobDefinition>().Update(jobDef);

            dc.Set<EtlRunLog>().Add(new EtlRunLog
            {
                JobId = jobDefId,
                Trigger = trigger,
                Result = EtlRunResult.Skipped,
                StartedAt = Wtm.TimeProvider.GetUtcNow().UtcDateTime,
                FinishedAt = Wtm.TimeProvider.GetUtcNow().UtcDateTime
            });

            await dc.SaveChangesAsync();
            return;
        }

        // 3. 檢查 Status
        if (jobDef.Status != EtlJobStatus.Enabled && jobDef.Status != EtlJobStatus.Failed)
            return;

        // 4. 更新 Status = Running — targeted update avoids full entity round-trip.
        // Also stamp UpdateTime to preserve ApplyAuditFields parity (TimeProvider.GetLocalNow().DateTime).
        // Background scheduler has no HTTP user context so UpdateBy is left unchanged (null parity with old code).
        // Keep jobDef.Status in memory so the finally-block Update picks up the correct value.
        jobDef.Status = EtlJobStatus.Running;
        var runningUpdateTime = Wtm.TimeProvider.GetLocalNow().DateTime;
        await dc.Set<EtlJobDefinition>()
            .Where(j => j.ID == jobDefId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, EtlJobStatus.Running)
                .SetProperty(j => j.UpdateTime, runningUpdateTime));

        var startedAt = Wtm.TimeProvider.GetUtcNow().UtcDateTime;

        // 建立超時 CancellationToken（連結 Quartz 的 CancellationToken 以支援中止）
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        if (jobDef.TimeoutMinutes > 0)
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(jobDef.TimeoutMinutes));

        EtlExecutionResult? result = null;
        try
        {
            // 5. 取得來源連線字串
            var sourceCs = Wtm.ConfigInfo.Connections?
                .FirstOrDefault(c => c.Key == jobDef.SourceCsKey);
            if (sourceCs == null)
                throw new InvalidOperationException($"Connection key '{jobDef.SourceCsKey}' not found in Configs.Connections");

            // 6. 建立 source + loader
            using var source = EtlSourceFactory.CreateSource(jobDef.SourceDbType);
            var loader = EtlSourceFactory.CreateLoader(jobDef.TargetDbType);

            // 7. 建立 WatermarkStrategy
            // 優先使用 JobDataMap 中的覆蓋值（由 RerunFromSnapshotAsync 傳入）：
            // 防 TOCTOU 競態 — 即使 DB 中 LastWatermarkValue 在本次觸發和執行之間
            // 被另一個 job finally 覆蓋，重跑仍會從快照的起點開始。
            // 正常排程觸發不傳此 key，行為完全不變。
            var watermarkOverride = context.MergedJobDataMap.ContainsKey("EtlWatermarkOverride")
                ? context.MergedJobDataMap.GetString("EtlWatermarkOverride")
                : null;
            var watermarkValue = watermarkOverride
                ?? jobDef.LastWatermarkValue
                ?? jobDef.InitialWatermarkValue;
            var watermark = new WatermarkStrategy(
                jobDef.WatermarkType,
                jobDef.WatermarkColumn,
                watermarkValue,
                jobDef.WatermarkTimeZone);

            // 8. 建立 Pipeline Config
            var queryTemplate = jobDef.QueryTemplate;
            var targetTable = jobDef.TargetTableName;
            var mergeKey = jobDef.MergeKeyColumn;
            var stagingTable = context.MergedJobDataMap.GetString("StagingTableName") ?? $"STG_{targetTable}";
            var batchSize = context.MergedJobDataMap.ContainsKey("BatchSize")
                ? context.MergedJobDataMap.GetInt("BatchSize")
                : 50_000;

            // 取 target DB 連線字串
            var targetCsEntry = Wtm.ConfigInfo.Connections?
                .FirstOrDefault(c => c.Key == jobDef.TargetCsKey);
            if (targetCsEntry == null)
                throw new InvalidOperationException($"Target connection key '{jobDef.TargetCsKey}' not found in Configs.Connections");
            var targetCs = targetCsEntry.Value ?? "";

            // ETL-006: propagate tenant context to dead-letter rows
            var tenantCode = dc.TenantCode;

            var config = new EtlPipelineConfig
            {
                JobId = jobDefId,
                JobName = jobDef.Name,
                SourceConnectionString = sourceCs.Value ?? "",
                TargetConnectionString = targetCs,
                QueryTemplate = queryTemplate,
                TargetTableName = targetTable,
                MergeKeyColumn = mergeKey,
                BatchSize = batchSize,
                StagingTable = new StagingTableSpec(stagingTable),
                // ETL-004/005: governance opt-in values; default false preserves pre-10.6 behaviour.
                // Callers can override via EtlJobDataMap keys or by building config manually.
                EnableDeadLetter    = context.MergedJobDataMap.ContainsKey("EnableDeadLetter")
                                       && context.MergedJobDataMap.GetBoolean("EnableDeadLetter"),
                DeadLetterTenantCode = tenantCode,
                EnableLineage       = context.MergedJobDataMap.ContainsKey("EnableLineage")
                                       && context.MergedJobDataMap.GetBoolean("EnableLineage"),
                LineageSourceKind   = jobDef.SourceDbType.ToString(),
            };

            // 9. 執行 Pipeline
            IProgress<EtlProgress>? progress = tracker != null
                ? new Progress<EtlProgress>(p => tracker.Update(p))
                : null;

            // ETL-004/005: wire governance store when dead-letter or lineage is enabled.
            // Uses the existing scoped DataContext (dc) — no separate connection needed.
            IEtlGovernanceStore governance = (config.EnableDeadLetter || config.EnableLineage)
                ? new DbEtlGovernanceStore(dc)
                : NullEtlGovernanceStore.Instance;

            // #700: cap comes from EtlOptions.MaxDeadLetterRowsPerRun (registered by
            // AddWtmEtl for every app that uses the ETL module) — mirrors how
            // EtlSchedulerService.PruneDeadLetterAsync resolves DeadLetterRetentionDays.
            // Falls back to EtlPipelineExecutor's own default when EtlOptions somehow
            // isn't registered.
            var maxDeadLetterRowsPerRun = Sp.GetService<IOptions<EtlOptions>>()?.Value?.MaxDeadLetterRowsPerRun;

            var executor = new EtlPipelineExecutor(source, loader, progress,
                Sp.GetService<ILogger<EtlPipelineExecutor>>(),
                governance,
                maxDeadLetterRowsPerRun);
            result = await executor.ExecuteAsync(config, watermark, timeoutCts.Token);

            // 10. 成功 → 更新 watermark、重置連續失敗計數
            if (result.Success)
            {
                jobDef.LastWatermarkValue = result.NewWatermarkValue;
                jobDef.LastRunAt = Wtm.TimeProvider.GetUtcNow().UtcDateTime;
                jobDef.LastError = null;
                jobDef.Status = EtlJobStatus.Enabled;
                jobDef.ConsecutiveFailureCount = 0;  // reset on success
            }
            else
            {
                jobDef.LastError = result.ErrorMessage?.Length > 2000
                    ? result.ErrorMessage[..2000]
                    : result.ErrorMessage;
                jobDef.Status = result.Aborted ? EtlJobStatus.Enabled : EtlJobStatus.Failed;
                if (!result.Aborted)
                    jobDef.ConsecutiveFailureCount++;
            }
        }
        catch (Exception ex)
        {
            Sp.GetService<ILogger<EtlQuartzJob>>()
                ?.LogError(ex, "ETL job {JobId} failed with unhandled exception", jobDefId);
            var sanitized = EtlErrorSanitizer.Sanitize(ex);
            result = new EtlExecutionResult
            {
                Success = false,
                ErrorMessage = sanitized,
                ElapsedMs = (long)(Wtm.TimeProvider.GetUtcNow().UtcDateTime - startedAt).TotalMilliseconds
            };

            var currentAttempt = context.MergedJobDataMap.ContainsKey("_retryAttempt")
                ? context.MergedJobDataMap.GetInt("_retryAttempt")
                : 0;

            if (EtlSchedulerService.ShouldRetry(jobDef, currentAttempt))
            {
                var retryData = new JobDataMap();
                retryData["_retryAttempt"] = currentAttempt + 1;
                await context.Scheduler.TriggerJob(context.JobDetail.Key, retryData);

                jobDef.LastError = $"[自動重試 {currentAttempt + 1}/{jobDef.RetryCount}] {sanitized}";
                jobDef.Status = EtlJobStatus.Enabled;
                trigger = EtlRunTrigger.Retry;
            }
            else
            {
                // Terminal failure — no retry will be scheduled.
                // Do NOT overwrite trigger here; preserve the original trigger value
                // (e.g. Scheduled, Manual) so the RunLog accurately reflects how this
                // execution was initiated rather than mislabeling it as Retry (M24).
                jobDef.LastError = sanitized;
                jobDef.Status = EtlJobStatus.Failed;
                jobDef.ConsecutiveFailureCount++;
            }
        }
        finally
        {
            bool isFailed = result?.Success != true && result?.Aborted != true;

            // 寫 RunLog（先建立物件，稍後用於告警）
            var runLog = new EtlRunLog
            {
                JobId = jobDefId,
                Trigger = trigger,
                Result = result?.Aborted == true ? EtlRunResult.Aborted
                       : result?.Success == true ? EtlRunResult.Success
                       : EtlRunResult.Failed,
                ExtractedRows = result?.ExtractedRows ?? 0,
                LoadedRows = result?.LoadedRows ?? 0,
                ErrorRows = result?.ErrorRows ?? 0,
                ElapsedMs = result?.ElapsedMs ?? 0,
                ErrorMessage = result?.ErrorMessage,
                StartedAt = startedAt,
                FinishedAt = Wtm.TimeProvider.GetUtcNow().UtcDateTime,
                WatermarkSnapshot = jobDef.LastWatermarkValue
            };
            dc.Set<EtlRunLog>().Add(runLog);

            dc.Set<EtlJobDefinition>().Update(jobDef);
            await dc.SaveChangesAsync();

            // 告警（在 SaveChanges 後發送，不影響 DB 事務）
            if (isFailed
                && jobDef.AlertAfterConsecutiveFailures > 0
                && jobDef.ConsecutiveFailureCount >= jobDef.AlertAfterConsecutiveFailures)
            {
                var alertService = Sp.GetService<IEtlAlertService>();
                if (alertService != null)
                {
                    try
                    {
                        await alertService.SendAlertAsync(jobDef, runLog).ConfigureAwait(false);
                    }
                    catch (Exception alertEx)
                    {
                        // 告警失敗不影響 job 狀態，僅記錄日誌
                        Sp.GetService<ILogger<EtlQuartzJob>>()
                            ?.LogError(alertEx, "ETL alert failed for job '{JobName}'", jobDef.Name);
                    }
                }
            }

            // ETL-010: SLA / duration breach alert (opt-in; default ExpectedDurationSeconds == 0 → skip)
            var expectedSec = jobDef.ExpectedDurationSeconds;
            if (expectedSec > 0 && runLog.ElapsedMs > expectedSec * 1000L)
            {
                var alertService = Sp.GetService<IEtlAlertService>();
                if (alertService != null)
                {
                    try
                    {
                        await alertService.SendSlaBreachAlertAsync(
                            jobDef, runLog, runLog.ElapsedMs).ConfigureAwait(false);
                        Sp.GetService<ILogger<EtlQuartzJob>>()
                            ?.LogWarning(
                                "ETL SLA breach for job '{JobName}': expected ≤ {ExpectedSec}s, " +
                                "actual {ActualMs}ms",
                                jobDef.Name, expectedSec, runLog.ElapsedMs);
                    }
                    catch (Exception slaEx)
                    {
                        Sp.GetService<ILogger<EtlQuartzJob>>()
                            ?.LogError(slaEx, "ETL SLA breach alert failed for job '{JobName}'", jobDef.Name);
                    }
                }
            }

            // 清除進度
            tracker?.Remove(jobDefId);
        }
    }

}
