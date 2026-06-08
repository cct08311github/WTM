#nullable enable
namespace WalkingTec.Mvvm.Etl.Alerting;

/// <summary>
/// ETL 告警服務全域設定（透過 AddWtmEtlAlerts() 配置）。
/// </summary>
public class EtlAlertOptions
{
    /// <summary>SMTP 郵件設定。未設定時 Email 告警功能停用。</summary>
    public SmtpAlertOptions? Smtp { get; set; }

    /// <summary>
    /// 啟用 shared webhook sink 整合（DingTalk / WeCom / Feishu / Slack / Teams）。
    /// <para>
    /// 設為 <c>true</c> 且已在 DI 容器中登記 <c>IWtmWebhookSink</c>（透過
    /// <c>AddWtmWebhookSink()</c> / <c>AddWtmWebhookSinks()</c>）時，
    /// ETL failure 與 SLA breach 告警將格式化為 <see cref="WalkingTec.Mvvm.Core.Notifications.WebhookMessage"/>
    /// 發送至已設定的 webhook 端點。
    /// </para>
    /// <para>
    /// 預設值 <c>false</c>，不影響現有 Email / per-job webhook 行為。
    /// </para>
    /// </summary>
    public bool EnableWebhookAlerts { get; set; }
}

/// <summary>SMTP 連線與寄件設定。</summary>
public class SmtpAlertOptions
{
    /// <summary>SMTP 主機（如 "smtp.example.com"）</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>SMTP 連接埠（預設 25；TLS 通常用 587）</summary>
    public int Port { get; set; } = 25;

    /// <summary>SMTP 登入帳號（可選）</summary>
    public string? UserName { get; set; }

    /// <summary>SMTP 登入密碼（可選）</summary>
    public string? Password { get; set; }

    /// <summary>是否啟用 SSL/TLS</summary>
    public bool EnableSsl { get; set; }

    /// <summary>寄件人地址</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>寄件人顯示名稱（可選）</summary>
    public string? FromName { get; set; }
}
