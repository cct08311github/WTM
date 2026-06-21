#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;
using WalkingTec.Mvvm.Etl.Pipeline.Sources;

namespace WalkingTec.Mvvm.Etl.ViewModels;

public class EtlJobDefinitionVM : BaseCRUDVM<EtlJobDefinition>
{
    public override void Validate()
    {
        if (!string.IsNullOrEmpty(Entity.CronExpression))
        {
            var parts = Entity.CronExpression.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 5)
            {
                // Auto-convert Unix Cron (5 fields) to Quartz Cron (6 fields)
                Entity.CronExpression = "0 " + Entity.CronExpression;
            }
        }

        base.Validate();

        if (!string.IsNullOrEmpty(Entity.CronExpression) &&
            !CronExpression.IsValidExpression(Entity.CronExpression))
        {
            MSD.AddModelError("Entity.CronExpression", "無效的 Cron 表達式");
        }

        // ── LoadMode coherence (10.5+) ──────────────────────────────────
        // Merge mode requires MergeKeyColumn; Replace mode requires either
        // a WHERE clause or explicit acknowledgement that the whole target
        // table will be wiped (we just warn-by-omission via empty clause).
        if (Entity.LoadMode == EtlLoadMode.Merge
            && string.IsNullOrWhiteSpace(Entity.MergeKeyColumn))
        {
            MSD.AddModelError("Entity.MergeKeyColumn",
                "Merge 模式必須提供合併主鍵欄位 (MergeKeyColumn)。");
        }

        // Replace whereClause SQL-injection guard — same rule the loader
        // applies at runtime, but surfaced at save time so the operator
        // gets feedback in the form rather than at next scheduled run.
        if (Entity.LoadMode == EtlLoadMode.Replace
            && !string.IsNullOrWhiteSpace(Entity.ReplaceWhereClause)
            && !MssqlBulkLoader.IsSafeWhereClause(Entity.ReplaceWhereClause))
        {
            MSD.AddModelError("Entity.ReplaceWhereClause",
                "WHERE 條件含有不允許的字元或 token (';' / '--' / '/*' / xp_ / sp_)。");
        }

        // ── Column mapping JSON validation ──────────────────────────────
        // Parse-once at save time so a typo in the dashboard's mapping
        // textarea is caught here rather than at first run. Empty / null
        // mapping is the back-compat default and silently OK.
        if (!string.IsNullOrWhiteSpace(Entity.ColumnMappingJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    Entity.ColumnMappingJson);
                if (parsed != null)
                {
                    foreach (var kv in parsed)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                        {
                            MSD.AddModelError("Entity.ColumnMappingJson",
                                "欄位對應 JSON 中存在空白的 key 或 value。");
                            break;
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                MSD.AddModelError("Entity.ColumnMappingJson",
                    $"欄位對應 JSON 格式錯誤：{ex.Message}");
            }
        }

        // ── AlertWebhookUrl SSRF guard (#484) ───────────────────────────
        // Require an absolute https:// URI and reject private/loopback/link-local/
        // IMDS hosts. Uses the same IsBlockedIp helper as RestEtlSource so the
        // block-list is defined in exactly one place.
        if (!string.IsNullOrWhiteSpace(Entity.AlertWebhookUrl))
        {
            if (!Uri.TryCreate(Entity.AlertWebhookUrl, UriKind.Absolute, out var webhookUri)
                || webhookUri.Scheme != Uri.UriSchemeHttps)
            {
                MSD.AddModelError("Entity.AlertWebhookUrl",
                    "告警 Webhook URL 必須是絕對 https:// 網址。");
            }
            else
            {
                // Block private/loopback/link-local/IMDS IP literals.
                if (IPAddress.TryParse(webhookUri.Host, out var literalIp)
                    && RestEtlSource.IsBlockedIp(literalIp))
                {
                    MSD.AddModelError("Entity.AlertWebhookUrl",
                        "告警 Webhook URL 目標 IP 屬於受限範圍（私有 / loopback / IMDS），不允許存取。");
                }
                else if (!IPAddress.TryParse(webhookUri.Host, out _))
                {
                    // Hostname: do a best-effort DNS pre-check at save time.
                    // The authoritative TOCTOU-safe block runs in PinnedConnectAsync at
                    // actual POST time; this catches obvious misconfiguration early.
                    try
                    {
                        var ips = Dns.GetHostAddresses(webhookUri.Host);
                        foreach (var ip in ips)
                        {
                            if (RestEtlSource.IsBlockedIp(ip))
                            {
                                MSD.AddModelError("Entity.AlertWebhookUrl",
                                    "告警 Webhook URL 主機名稱解析後屬於受限 IP 範圍（私有 / loopback / IMDS），不允許存取。");
                                break;
                            }
                        }
                    }
                    catch (SocketException)
                    {
                        // DNS lookup failed (host unreachable at save time) — allow save;
                        // the ConnectCallback will enforce the block at actual POST time.
                    }
                }
            }
        }

        // ── Merge-key uniqueness probe (Merge mode only) ────────────────
        // Replace mode doesn't use MergeKey so don't probe.
        if (Entity.LoadMode == EtlLoadMode.Merge
            && !string.IsNullOrEmpty(Entity.MergeKeyColumn)
            && !string.IsNullOrEmpty(Entity.TargetTableName)
            && !string.IsNullOrEmpty(Entity.TargetCsKey))
        {
            try
            {
                var targetCs = Wtm.ConfigInfo.Connections?.FirstOrDefault(c => c.Key == Entity.TargetCsKey)?.Value;
                if (!string.IsNullOrEmpty(targetCs))
                {
                    var loader = EtlSourceFactory.CreateLoader(Entity.TargetDbType);
                    if (loader != null)
                    {
                        bool isUnique = loader.IsUniqueColumnAsync(targetCs, Entity.TargetTableName, Entity.MergeKeyColumn).GetAwaiter().GetResult();
                        if (!isUnique)
                        {
                            MSD.AddModelError("Entity.MergeKeyColumn", "選定的合併主鍵未具備唯一限制 (Primary Key 或 Unique)，可能導致資料異常。");
                        }
                    }
                }
            }
            catch (System.NotSupportedException ex)
            {
                MSD.AddModelError("Entity.TargetDbType", ex.Message);
            }
            catch (System.ArgumentException ex)
            {
                MSD.AddModelError("Entity.MergeKeyColumn", ex.Message);
            }
            catch (System.Exception)
            {
                // Connectivity failures (SqlException, TimeoutException, etc.) are
                // silently skipped — a DB connection may not be available during save.
            }
        }
    }

    public override void DoAdd()
    {
        base.DoAdd();

        if (Entity.Status == EtlJobStatus.Enabled)
        {
            var scheduler = Wtm.ServiceProvider.GetService<EtlSchedulerService>();
            scheduler?.EnableAsync(Entity.ID).GetAwaiter().GetResult();
        }
    }

    public override void DoEdit(bool updateAllFields = false)
    {
        var original = DC.Set<EtlJobDefinition>().AsNoTracking().FirstOrDefault(x => x.ID == Entity.ID);
        var oldCron = original?.CronExpression;
        var oldStatus = original?.Status;

        base.DoEdit(updateAllFields);

        var scheduler = Wtm.ServiceProvider.GetService<EtlSchedulerService>();
        if (scheduler == null) return;

        // Handle status change
        if (oldStatus != Entity.Status)
        {
            if (Entity.Status == EtlJobStatus.Enabled)
                scheduler.EnableAsync(Entity.ID).GetAwaiter().GetResult();
            else if (Entity.Status == EtlJobStatus.Disabled)
                scheduler.DisableAsync(Entity.ID).GetAwaiter().GetResult();
        }
        // Handle cron change (only if still enabled)
        else if (oldCron != Entity.CronExpression && Entity.Status == EtlJobStatus.Enabled)
        {
            scheduler.RescheduleAsync(Entity.ID, Entity.CronExpression).GetAwaiter().GetResult();
        }
    }

    public override void DoDelete()
    {
        if (Entity.Status == EtlJobStatus.Running)
        {
            MSD.AddModelError("", "無法刪除正在執行中的 Job，請先中止再刪除");
            return;
        }

        var scheduler = Wtm.ServiceProvider.GetService<EtlSchedulerService>();
        scheduler?.DisableAsync(Entity.ID).GetAwaiter().GetResult();

        base.DoDelete();
    }
}
