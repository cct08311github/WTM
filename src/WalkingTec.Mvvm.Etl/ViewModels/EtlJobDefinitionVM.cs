#nullable enable
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Etl.Pipeline;

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

        if (!string.IsNullOrEmpty(Entity.MergeKeyColumn) && !string.IsNullOrEmpty(Entity.TargetTableName) && !string.IsNullOrEmpty(Entity.TargetCsKey))
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
