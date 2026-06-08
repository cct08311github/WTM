#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core.Notifications;
using WalkingTec.Mvvm.Etl.Alerting;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.Test.Alerting;

/// <summary>
/// Tests for the shared webhook sink card integration (issue #226).
/// Verifies ETL failure / SLA-breach alert formatting, opt-in gate,
/// and error sanitization.
/// </summary>
[TestClass]
public class EtlAlertWebhookCardTests
{
    // ── Fake sink ─────────────────────────────────────────────────────────

    private sealed class CapturingSink : IWtmWebhookSink
    {
        public readonly List<WebhookMessage> Messages = new();

        public Task SendAsync(WebhookMessage message, CancellationToken ct = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static (EtlAlertService svc, CapturingSink sink) MakeServiceWithSink(
        bool enableWebhook = true)
    {
        var sink = new CapturingSink();

        var httpFactory = new Mock<IHttpClientFactory>();
        // No real HTTP calls expected in these tests; return a dummy client.
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
                   .Returns(new System.Net.Http.HttpClient());

        var opts = new EtlAlertOptions { EnableWebhookAlerts = enableWebhook };
        var options = Options.Create(opts);

        var svc = new EtlAlertService(
            httpFactory.Object,
            options,
            NullLogger<EtlAlertService>.Instance,
            sink);

        return (svc, sink);
    }

    private static EtlJobDefinition MakeJob(
        string name = "SalesETL",
        int consecutiveFailures = 3,
        int expectedDurationSeconds = 60) => new()
    {
        Name = name,
        CronExpression = "0 0 2 * * ?",
        JobClassName  = "FakeJob",
        SourceCsKey   = "src",
        TargetCsKey   = "tgt",
        TargetTableName = "tbl",
        MergeKeyColumn  = "id",
        QueryTemplate   = "SELECT 1",
        ConsecutiveFailureCount = consecutiveFailures,
        AlertAfterConsecutiveFailures = 1,
        ExpectedDurationSeconds = expectedDurationSeconds,
    };

    private static EtlRunLog MakeRunLog(
        string? errorMessage = "Connection timed out",
        int extractedRows = 1000,
        int loadedRows = 950,
        long elapsedMs = 45_000) => new()
    {
        JobId = Guid.NewGuid(),
        Trigger  = EtlRunTrigger.Scheduled,
        Result   = EtlRunResult.Failed,
        ErrorMessage   = errorMessage,
        ExtractedRows  = extractedRows,
        LoadedRows     = loadedRows,
        ElapsedMs      = elapsedMs,
        StartedAt      = DateTime.UtcNow.AddMilliseconds(-elapsedMs),
        FinishedAt     = DateTime.UtcNow,
    };

    // ── Tests — failure alert ─────────────────────────────────────────────

    [TestMethod]
    [Description("Failure alert → sink receives one Error-level WebhookMessage")]
    public async Task SendAlertAsync_WithSinkEnabled_SendsErrorLevelCard()
    {
        var (svc, sink) = MakeServiceWithSink();
        var job = MakeJob();
        var log = MakeRunLog();

        await svc.SendAlertAsync(job, log);

        Assert.AreEqual(1, sink.Messages.Count, "Exactly one card should be sent");
        var msg = sink.Messages[0];
        Assert.AreEqual(WebhookLevel.Error, msg.Level, "Failure alert must be Error level");
    }

    [TestMethod]
    [Description("Failure alert card title contains job name")]
    public async Task SendAlertAsync_CardTitle_ContainsJobName()
    {
        var (svc, sink) = MakeServiceWithSink();
        var job = MakeJob(name: "InventorySync");
        var log = MakeRunLog();

        await svc.SendAlertAsync(job, log);

        StringAssert.Contains(sink.Messages[0].Title, "InventorySync");
    }

    [TestMethod]
    [Description("Failure alert card Fields contain Job, Status, Rows, Duration")]
    public async Task SendAlertAsync_CardFields_ContainExpectedKeys()
    {
        var (svc, sink) = MakeServiceWithSink();
        var job = MakeJob();
        var log = MakeRunLog(extractedRows: 5000, loadedRows: 4950, elapsedMs: 30_000);

        await svc.SendAlertAsync(job, log);

        var fields = sink.Messages[0].Fields;
        var keys = new HashSet<string>();
        foreach (var f in fields) keys.Add(f.Key);

        Assert.IsTrue(keys.Contains("Job"),             "Fields must include 'Job'");
        Assert.IsTrue(keys.Contains("Status"),          "Fields must include 'Status'");
        Assert.IsTrue(keys.Contains("Rows Extracted"),  "Fields must include 'Rows Extracted'");
        Assert.IsTrue(keys.Contains("Rows Loaded"),     "Fields must include 'Rows Loaded'");
        Assert.IsTrue(keys.Contains("Duration"),        "Fields must include 'Duration'");
    }

    [TestMethod]
    [Description("Failure alert card Fields contain sanitized error excerpt")]
    public async Task SendAlertAsync_CardFields_ContainsSanitizedError()
    {
        var (svc, sink) = MakeServiceWithSink();
        var job = MakeJob();
        var log = MakeRunLog(errorMessage: "Connection timed out");

        await svc.SendAlertAsync(job, log);

        var fields = sink.Messages[0].Fields;
        string? errorValue = null;
        foreach (var f in fields)
            if (f.Key == "Error") { errorValue = f.Value; break; }

        Assert.IsNotNull(errorValue, "Error field should be present when there is an error message");
        StringAssert.Contains(errorValue, "timed out", "Error excerpt should appear in the card");
    }

    // ── Tests — error sanitization ────────────────────────────────────────

    [TestMethod]
    [Description("Error field in card is sanitized — connection string secrets are redacted")]
    public async Task SendAlertAsync_ErrorField_SecretIsRedacted()
    {
        var (svc, sink) = MakeServiceWithSink();
        var job = MakeJob();
        // Simulate an error message that contains a connection string secret fragment.
        var log = MakeRunLog(
            errorMessage: "Failed: Server=prod-db;Database=sales;Password=SuperSecret;User Id=admin;");

        await svc.SendAlertAsync(job, log);

        var fields = sink.Messages[0].Fields;
        string? errorValue = null;
        foreach (var f in fields)
            if (f.Key == "Error") { errorValue = f.Value; break; }

        Assert.IsNotNull(errorValue);
        Assert.IsFalse(errorValue.Contains("SuperSecret"),
            "Password value must be redacted — never sent to external webhook");
        Assert.IsFalse(errorValue.Contains("admin"),
            "User Id value must be redacted — never sent to external webhook");
        StringAssert.Contains(errorValue, "[redacted]",
            "Redacted placeholder should appear in sanitized field");
    }

    // ── Tests — SLA breach alert ──────────────────────────────────────────

    [TestMethod]
    [Description("SLA breach alert → sink receives one Warning-level WebhookMessage")]
    public async Task SendSlaBreachAlertAsync_WithSinkEnabled_SendsWarningLevelCard()
    {
        var (svc, sink) = MakeServiceWithSink();
        var job = MakeJob(expectedDurationSeconds: 60);
        var log = MakeRunLog(elapsedMs: 120_000); // 2 min, over 60s SLA

        await svc.SendSlaBreachAlertAsync(job, log, actualElapsedMs: 120_000);

        Assert.AreEqual(1, sink.Messages.Count, "Exactly one card should be sent");
        var msg = sink.Messages[0];
        Assert.AreEqual(WebhookLevel.Warning, msg.Level, "SLA breach alert must be Warning level");
    }

    [TestMethod]
    [Description("SLA breach card title mentions SLA Breach")]
    public async Task SendSlaBreachAlertAsync_CardTitle_MentionsSlaBreachAndJobName()
    {
        var (svc, sink) = MakeServiceWithSink();
        var job = MakeJob(name: "OrdersETL", expectedDurationSeconds: 30);
        var log = MakeRunLog(elapsedMs: 90_000);

        await svc.SendSlaBreachAlertAsync(job, log, actualElapsedMs: 90_000);

        var title = sink.Messages[0].Title;
        StringAssert.Contains(title, "SLA");
        StringAssert.Contains(title, "OrdersETL");
    }

    [TestMethod]
    [Description("SLA breach card Fields contain Status=SLA Breach and Duration")]
    public async Task SendSlaBreachAlertAsync_CardFields_ContainExpectedKeys()
    {
        var (svc, sink) = MakeServiceWithSink();
        var job = MakeJob();
        var log = MakeRunLog();

        await svc.SendSlaBreachAlertAsync(job, log, actualElapsedMs: 90_000);

        var fields = sink.Messages[0].Fields;
        bool hasStatus   = false;
        bool hasDuration = false;
        foreach (var f in fields)
        {
            if (f.Key == "Status")   { Assert.AreEqual("SLA Breach", f.Value); hasStatus = true; }
            if (f.Key == "Duration") hasDuration = true;
        }
        Assert.IsTrue(hasStatus,   "Fields must include Status=SLA Breach");
        Assert.IsTrue(hasDuration, "Fields must include Duration");
    }

    // ── Tests — opt-in gate ───────────────────────────────────────────────

    [TestMethod]
    [Description("When EnableWebhookAlerts=false, the sink is NOT called even if registered")]
    public async Task SendAlertAsync_WebhookAlertsDisabled_SinkNotCalled()
    {
        var (svc, sink) = MakeServiceWithSink(enableWebhook: false);
        var job = MakeJob();
        var log = MakeRunLog();

        await svc.SendAlertAsync(job, log);

        Assert.AreEqual(0, sink.Messages.Count,
            "Sink must not be invoked when EnableWebhookAlerts=false");
    }

    [TestMethod]
    [Description("When no IWtmWebhookSink is injected (null), the service does not throw")]
    public async Task SendAlertAsync_NoSinkInjected_DoesNotThrow()
    {
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
                   .Returns(new System.Net.Http.HttpClient());

        var opts = new EtlAlertOptions { EnableWebhookAlerts = true }; // enabled but no sink
        var svc = new EtlAlertService(
            httpFactory.Object,
            Options.Create(opts),
            NullLogger<EtlAlertService>.Instance,
            webhookSink: null);   // explicit null — no sink registered

        var job = MakeJob();
        var log = MakeRunLog();

        // Must not throw.
        await svc.SendAlertAsync(job, log);
    }

    [TestMethod]
    [Description("SLA breach: when EnableWebhookAlerts=false, the sink is NOT called")]
    public async Task SendSlaBreachAlertAsync_WebhookAlertsDisabled_SinkNotCalled()
    {
        var (svc, sink) = MakeServiceWithSink(enableWebhook: false);
        var job = MakeJob();
        var log = MakeRunLog();

        await svc.SendSlaBreachAlertAsync(job, log, actualElapsedMs: 90_000);

        Assert.AreEqual(0, sink.Messages.Count,
            "Sink must not be invoked for SLA breach when EnableWebhookAlerts=false");
    }
}
