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

    /// <summary>
    /// 發送 SLA 違反告警（ETL-010）。
    /// 當某次執行的實際耗時超過 <see cref="EtlJobDefinition.ExpectedDurationSeconds"/> 時呼叫。
    /// 預設實作呼叫 <see cref="SendAlertAsync"/>（重用 Email/Webhook 通道）；
    /// 可 override 以發送不同訊息或使用其他通道。
    /// </summary>
    Task SendSlaBreachAlertAsync(
        EtlJobDefinition jobDef,
        EtlRunLog runLog,
        long actualElapsedMs,
        CancellationToken ct = default)
        => SendAlertAsync(jobDef, runLog, ct);
}
