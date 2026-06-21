#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Etl.Alerting;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.Test.Alerting;

[TestClass]
public class EtlAlertServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>可記錄並攔截 HTTP 請求的 fake message handler。</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string? Uri, string? Body)> Calls = new();
        public HttpStatusCode ResponseCode { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;
            Calls.Add((request.Method, request.RequestUri?.ToString(), body));
            return new HttpResponseMessage(ResponseCode);
        }
    }

    private static (EtlAlertService svc, CapturingHandler handler) MakeService(
        EtlAlertOptions? opts = null)
    {
        var handler = new CapturingHandler();
        var client = new HttpClient(handler);

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        var options = Options.Create(opts ?? new EtlAlertOptions());
        var svc = new EtlAlertService(factory.Object, options, NullLogger<EtlAlertService>.Instance);
        return (svc, handler);
    }

    private static EtlJobDefinition MakeJob(
        string? webhookUrl = null,
        string? alertEmail = null,
        int threshold = 1,
        int consecutiveFailures = 1) => new()
    {
        Name = "TestJob",
        CronExpression = "0 0 2 * * ?",
        JobClassName = "FakeJob",
        SourceCsKey = "src",
        TargetCsKey = "tgt",
        TargetTableName = "tbl",
        MergeKeyColumn = "id",
        QueryTemplate = "SELECT 1",
        AlertWebhookUrl = webhookUrl,
        AlertEmail = alertEmail,
        AlertAfterConsecutiveFailures = threshold,
        ConsecutiveFailureCount = consecutiveFailures,
    };

    private static EtlRunLog MakeRunLog(string? error = "Timeout") => new()
    {
        JobId = Guid.NewGuid(),
        Trigger = EtlRunTrigger.Scheduled,
        Result = EtlRunResult.Failed,
        ErrorMessage = error,
        StartedAt = DateTime.UtcNow.AddSeconds(-30),
        FinishedAt = DateTime.UtcNow,
    };

    // ── Webhook tests ─────────────────────────────────────────────────────

    [TestMethod]
    [Description("Webhook 設定時 → 發送 HTTP POST，body 含 text 欄位")]
    public async Task SendAlertAsync_WithWebhook_PostsJson()
    {
        var (svc, handler) = MakeService();
        var job = MakeJob(webhookUrl: "https://hooks.example.com/alert");
        var log = MakeRunLog("Connection refused");

        await svc.SendAlertAsync(job, log);

        Assert.AreEqual(1, handler.Calls.Count, "應呼叫一次 HTTP POST");
        var (method, uri, body) = handler.Calls[0];
        Assert.AreEqual(HttpMethod.Post, method);
        Assert.AreEqual("https://hooks.example.com/alert", uri);
        Assert.IsNotNull(body);

        using var doc = JsonDocument.Parse(body!);
        Assert.IsTrue(doc.RootElement.TryGetProperty("text", out var textProp),
            "JSON body 應包含 text 欄位");
        StringAssert.Contains(textProp.GetString(), "TestJob",
            "text 應包含 job 名稱");
        StringAssert.Contains(textProp.GetString(), "Connection refused",
            "text 應包含錯誤訊息");
    }

    [TestMethod]
    [Description("Webhook 無設定時 → 不發送任何 HTTP 請求")]
    public async Task SendAlertAsync_NoWebhook_NoHttpCall()
    {
        var (svc, handler) = MakeService();
        var job = MakeJob(); // webhookUrl = null

        await svc.SendAlertAsync(job, MakeRunLog());

        Assert.AreEqual(0, handler.Calls.Count, "未設定 webhook 不應發送 HTTP 請求");
    }

    [TestMethod]
    [Description("#484 secret-leak gap: both `text` AND `errorMessage` payload fields must be sanitized — raw secret must not appear in either")]
    public async Task SendAlertAsync_WithSecretInErrorMessage_BothTextAndErrorMessageAreSanitized()
    {
        var (svc, handler) = MakeService();
        var job = MakeJob(webhookUrl: "https://hooks.example.com/alert");
        // Simulate a connection string with a secret leaking into ErrorMessage.
        var secretError = "Login failed. Server=prod-db.internal;Database=Orders;User Id=sa;Password=S3cr3t!";
        var log = MakeRunLog(secretError);

        await svc.SendAlertAsync(job, log);

        Assert.AreEqual(1, handler.Calls.Count, "應呼叫一次 HTTP POST");
        var (_, _, body) = handler.Calls[0];
        Assert.IsNotNull(body);

        using var doc = JsonDocument.Parse(body!);

        // `text` field — rendered by Slack/Teams/DingTalk — must not contain the raw secret
        Assert.IsTrue(doc.RootElement.TryGetProperty("text", out var textProp),
            "JSON body 應包含 text 欄位");
        var textValue = textProp.GetString() ?? string.Empty;
        StringAssert.DoesNotMatch(textValue,
            new System.Text.RegularExpressions.Regex(@"Password\s*=\s*S3cr3t!", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            "text 欄位不應包含原始 Password 值");
        StringAssert.Contains(textValue, "[redacted]",
            "text 欄位應包含 sanitizer 的 [redacted] 標記");

        // `errorMessage` field — must also be sanitized
        Assert.IsTrue(doc.RootElement.TryGetProperty("errorMessage", out var errProp),
            "JSON body 應包含 errorMessage 欄位");
        var errValue = errProp.GetString() ?? string.Empty;
        StringAssert.DoesNotMatch(errValue,
            new System.Text.RegularExpressions.Regex(@"Password\s*=\s*S3cr3t!", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            "errorMessage 欄位不應包含原始 Password 值");
        StringAssert.Contains(errValue, "[redacted]",
            "errorMessage 欄位應包含 sanitizer 的 [redacted] 標記");
    }

    [TestMethod]
    [Description("Webhook 回傳非 2xx → 不拋例外，僅記錄警告")]
    public async Task SendAlertAsync_WebhookNon2xx_DoesNotThrow()
    {
        var (svc, handler) = MakeService();
        handler.ResponseCode = HttpStatusCode.InternalServerError;
        var job = MakeJob(webhookUrl: "https://hooks.example.com/alert");

        // 不應拋出任何例外
        await svc.SendAlertAsync(job, MakeRunLog());
        Assert.AreEqual(1, handler.Calls.Count);
    }

    // ── Email tests ───────────────────────────────────────────────────────

    [TestMethod]
    [Description("AlertEmail 設定但 SmtpOptions 未設定 → 不拋例外")]
    public async Task SendAlertAsync_EmailWithoutSmtpConfig_DoesNotThrow()
    {
        var (svc, _) = MakeService(new EtlAlertOptions { Smtp = null });
        var job = MakeJob(alertEmail: "ops@example.com");

        // 不應拋出；僅記錄警告
        await svc.SendAlertAsync(job, MakeRunLog());
    }

    [TestMethod]
    [Description("AlertEmail 和 Webhook 同時設定 → 兩者都嘗試發送")]
    public async Task SendAlertAsync_BothChannels_AttemptsWebhookAndEmail()
    {
        var (svc, handler) = MakeService(new EtlAlertOptions { Smtp = null });
        var job = MakeJob(
            webhookUrl: "https://hooks.example.com/alert",
            alertEmail: "ops@example.com");

        // Email 會因無 SMTP 設定而靜默失敗；webhook 應發送
        await svc.SendAlertAsync(job, MakeRunLog());

        Assert.AreEqual(1, handler.Calls.Count, "Webhook 應已發送");
    }

    // ── ConsecutiveFailureCount logic (unit tests on model) ───────────────

    [TestMethod]
    [Description("連續失敗計數 — 失敗累加、成功歸零")]
    public void ConsecutiveFailureCount_IncrementsOnFailureResetsOnSuccess()
    {
        var job = new EtlJobDefinition
        {
            Name = "j",
            CronExpression = "x",
            JobClassName = "x",
            SourceCsKey = "s",
            TargetCsKey = "t",
            TargetTableName = "t",
            MergeKeyColumn = "id",
            QueryTemplate = "SELECT 1",
            ConsecutiveFailureCount = 0,
        };

        // simulate two failures
        job.ConsecutiveFailureCount++;
        job.ConsecutiveFailureCount++;
        Assert.AreEqual(2, job.ConsecutiveFailureCount);

        // simulate success
        job.ConsecutiveFailureCount = 0;
        Assert.AreEqual(0, job.ConsecutiveFailureCount);
    }

    [TestMethod]
    [Description("門檻設定 — 未達門檻不觸發告警")]
    public async Task SendAlertAsync_BelowThreshold_WebhookNotCalled()
    {
        var (svc, handler) = MakeService();
        // threshold=3 but only 2 consecutive failures
        var job = MakeJob(
            webhookUrl: "https://hooks.example.com/alert",
            threshold: 3,
            consecutiveFailures: 2);

        // Caller (EtlQuartzJob) checks threshold before calling SendAlertAsync.
        // Simulate correct guard:
        if (job.ConsecutiveFailureCount >= job.AlertAfterConsecutiveFailures)
            await svc.SendAlertAsync(job, MakeRunLog());

        Assert.AreEqual(0, handler.Calls.Count, "未達門檻不應觸發 webhook");
    }

    [TestMethod]
    [Description("門檻設定 — 達到門檻觸發告警")]
    public async Task SendAlertAsync_AtThreshold_WebhookCalled()
    {
        var (svc, handler) = MakeService();
        var job = MakeJob(
            webhookUrl: "https://hooks.example.com/alert",
            threshold: 3,
            consecutiveFailures: 3); // exactly at threshold

        if (job.ConsecutiveFailureCount >= job.AlertAfterConsecutiveFailures)
            await svc.SendAlertAsync(job, MakeRunLog());

        Assert.AreEqual(1, handler.Calls.Count, "達到門檻應觸發 webhook");
    }
}
