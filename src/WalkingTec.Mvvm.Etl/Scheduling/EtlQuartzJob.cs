#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Support.Quartz;
using WalkingTec.Mvvm.Etl.Alerting;
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
                StartedAt = DateTime.UtcNow,
                FinishedAt = DateTime.UtcNow
            });

            await dc.SaveChangesAsync();
            return;
        }

        // 3. 檢查 Status
        if (jobDef.Status != EtlJobStatus.Enabled && jobDef.Status != EtlJobStatus.Failed)
            return;

        // 4. 更新 Status = Running
        jobDef.Status = EtlJobStatus.Running;
        dc.Set<EtlJobDefinition>().Update(jobDef);
        await dc.SaveChangesAsync();

        var startedAt = DateTime.UtcNow;

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
            var watermarkValue = jobDef.LastWatermarkValue ?? jobDef.InitialWatermarkValue;
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
                StagingTable = new StagingTableSpec(stagingTable)
            };

            // 9. 執行 Pipeline
            IProgress<EtlProgress>? progress = tracker != null
                ? new Progress<EtlProgress>(p => tracker.Update(p))
                : null;

            var executor = new EtlPipelineExecutor(source, loader, progress);
            result = await executor.ExecuteAsync(config, watermark, timeoutCts.Token);

            // 10. 成功 → 更新 watermark、重置連續失敗計數
            if (result.Success)
            {
                jobDef.LastWatermarkValue = result.NewWatermarkValue;
                jobDef.LastRunAt = DateTime.UtcNow;
                jobDef.LastError = null;
                jobDef.Status = EtlJobStatus.Enabled;
                jobDef.ConsecutiveFailureCount = 0;
            }
            else
            {
                jobDef.LastError = result.ErrorMessage?.Length > 2000
                    ? result.ErrorMessage[..2000]
                    : result.ErrorMessage;
                jobDef.Status = result.Aborted ? EtlJobStatus.Enabled : EtlJobStatus.Failed;
                // 未中止的失敗才累計連續失敗次數
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
                ElapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds
            };
            jobDef.LastError = sanitized;
            jobDef.Status = EtlJobStatus.Failed;
            jobDef.ConsecutiveFailureCount++;
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
                FinishedAt = DateTime.UtcNow,
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

            // 清除進度
            tracker?.Remove(jobDefId);
        }
    }

}
