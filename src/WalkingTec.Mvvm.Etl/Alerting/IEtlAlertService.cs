#nullable enable
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.Alerting;

/// <summary>
/// ETL 告警服務介面。
/// 在 EtlJobDefinition 連續失敗次數達到門檻時由 EtlQuartzJob 呼叫。
/// </summary>
public interface IEtlAlertService
{
    /// <summary>
    /// 依照 <paramref name="jobDef"/> 的告警設定（Email / Webhook）發送告警通知。
    /// </summary>
    Task SendAlertAsync(
        EtlJobDefinition jobDef,
        EtlRunLog runLog,
        CancellationToken ct = default);
}
