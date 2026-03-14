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
        base.Validate();

        if (!string.IsNullOrEmpty(Entity.CronExpression) &&
            !CronExpression.IsValidExpression(Entity.CronExpression))
        {
            MSD.AddModelError("Entity.CronExpression", "無效的 Cron 表達式");
        }

        if (!string.IsNullOrEmpty(Entity.MergeKeyColumn) && !string.IsNullOrEmpty(Entity.TargetTableName) && !string.IsNullOrEmpty(Entity.TargetConnectionString))
        {
            try
            {
                var factory = Wtm.ServiceProvider.GetService<EtlSourceFactory>();
                var loader = factory?.GetLoader(Entity.TargetDbType);
                if (loader != null)
                {
                    bool isUnique = loader.IsUniqueColumnAsync(Entity.TargetConnectionString, Entity.TargetTableName, Entity.MergeKeyColumn).GetAwaiter().GetResult();
                    if (!isUnique)
                    {
                        MSD.AddModelError("Entity.MergeKeyColumn", "選定的合併主鍵未具備唯一限制 (Primary Key 或 Unique)，可能導致資料異常。");
                    }
                }
            }
            catch (System.Exception ex)
            {
                // Warn instead of error if connection fails during validation
                // MSD.AddWarning(...) is not available in standard WTM, so we just log or ignore
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
