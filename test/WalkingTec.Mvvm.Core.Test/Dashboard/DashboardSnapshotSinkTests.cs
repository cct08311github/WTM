#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core.Dashboard;
using WalkingTec.Mvvm.Core.Dashboard.Snapshot;
using WalkingTec.Mvvm.Core.Notifications;
using WalkingTec.Mvvm.Core.Support.Email;

namespace WalkingTec.Mvvm.Core.Test.Dashboard;

/// <summary>
/// Tests for Issue #438: opt-in scheduled snapshot delivery sinks (file / e-mail / webhook).
/// </summary>
[TestClass]
public class DashboardSnapshotSinkTests
{
    // ── Shared helpers ────────────────────────────────────────────────────────

    private static DashboardSnapshotResult MakeResult(
        string jobId = "job-1",
        string dashboardId = "dash-1",
        DashboardExportFormat format = DashboardExportFormat.Excel,
        byte[]? content = null) =>
        new()
        {
            JobId       = jobId,
            DashboardId = dashboardId,
            Format      = format,
            Content     = content ?? new byte[] { 1, 2, 3, 4 }
        };

    private static IOptions<DashboardSnapshotDeliveryOptions> DeliveryOpts(
        Action<DashboardSnapshotDeliveryOptions>? configure = null)
    {
        var opts = new DashboardSnapshotDeliveryOptions();
        configure?.Invoke(opts);
        return Options.Create(opts);
    }

    // ── IDashboardSnapshotSink — fake sink ────────────────────────────────────

    private sealed class CapturingSink : IDashboardSnapshotSink
    {
        public List<(DashboardSnapshotResult Snapshot, byte[] Content, string FileName, string ContentType)> Calls { get; } = new();

        public Task DeliverAsync(
            DashboardSnapshotResult snapshot,
            byte[] content,
            string fileName,
            string contentType,
            CancellationToken ct = default)
        {
            Calls.Add((snapshot, content, fileName, contentType));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSink : IDashboardSnapshotSink
    {
        public int CallCount { get; private set; }

        public Task DeliverAsync(
            DashboardSnapshotResult snapshot,
            byte[] content,
            string fileName,
            string contentType,
            CancellationToken ct = default)
        {
            CallCount++;
            throw new InvalidOperationException("Simulated sink failure.");
        }
    }

    // ── DashboardSnapshotHostedService: sink invocation ───────────────────────

    private static DashboardSnapshotHostedService BuildHostedService(
        IEnumerable<IDashboardSnapshotSink> sinks,
        DashboardSnapshotResult? jobResult = null)
    {
        var mockJob = new Mock<IScheduledDashboardJob>();
        var result  = jobResult ?? MakeResult();
        mockJob
            .Setup(j => j.RunAsync(It.IsAny<ScheduledDashboardJobConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DashboardSnapshotResult)result);

        var opts = Options.Create(new DashboardSnapshotOptions
        {
            Jobs = new List<ScheduledDashboardJobConfig>
            {
                new()
                {
                    JobId          = result.JobId,
                    DashboardId    = result.DashboardId,
                    Format         = result.Format,
                    CronExpression = "0 8 * * 1" // arbitrary valid cron
                }
            }
        });

        return new DashboardSnapshotHostedService(
            mockJob.Object,
            sinks,
            opts,
            NullLogger<DashboardSnapshotHostedService>.Instance);
    }

    [TestMethod]
    public async Task Registered_sink_receives_snapshot_bytes_after_job_run()
    {
        var expectedBytes = new byte[] { 10, 20, 30 };
        var result        = MakeResult(content: expectedBytes);
        var sink          = new CapturingSink();

        var svc = BuildHostedService(new[] { sink }, result);
        await svc.RunSingleJobForTestAsync(result.JobId, result.DashboardId, result.Format, CancellationToken.None);

        sink.Calls.Should().HaveCount(1);
        sink.Calls[0].Content.Should().BeEquivalentTo(expectedBytes);
        sink.Calls[0].Snapshot.JobId.Should().Be(result.JobId);
        sink.Calls[0].Snapshot.DashboardId.Should().Be(result.DashboardId);
        sink.Calls[0].FileName.Should().NotBeNullOrWhiteSpace();
        sink.Calls[0].ContentType.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task No_sink_registered_does_not_throw_log_only_backcompat()
    {
        var result = MakeResult();
        var svc    = BuildHostedService(Array.Empty<IDashboardSnapshotSink>(), result);

        // Must not throw — backward-compatible log-only path.
        await svc.RunSingleJobForTestAsync(result.JobId, result.DashboardId, result.Format, CancellationToken.None);
    }

    [TestMethod]
    public async Task Multi_sink_both_invoked_one_throwing_does_not_prevent_other()
    {
        var expectedBytes = new byte[] { 5, 6, 7 };
        var result        = MakeResult(content: expectedBytes);

        var throwing  = new ThrowingSink();
        var capturing = new CapturingSink();

        // Order: throwing first, then capturing — capturing must still be called.
        var svc = BuildHostedService(new IDashboardSnapshotSink[] { throwing, capturing }, result);
        await svc.RunSingleJobForTestAsync(result.JobId, result.DashboardId, result.Format, CancellationToken.None);

        throwing.CallCount.Should().Be(1, "the throwing sink must have been called");
        capturing.Calls.Should().HaveCount(1, "the capturing sink must still be invoked despite the previous sink throwing");
    }

    // ── FileSystemSnapshotSink ────────────────────────────────────────────────

    [TestMethod]
    public async Task FileSystemSnapshotSink_writes_file_with_matching_content()
    {
        var tmpDir  = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var content = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var result  = MakeResult(content: content);

        try
        {
            var sink = new FileSystemSnapshotSink(
                DeliveryOpts(o => o.OutputDirectory = tmpDir),
                NullLogger<FileSystemSnapshotSink>.Instance);

            await sink.DeliverAsync(result, content, "test-snapshot.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

            var files = Directory.GetFiles(tmpDir);
            files.Should().HaveCount(1, "exactly one file should have been written");

            var writtenBytes = await File.ReadAllBytesAsync(files[0]);
            writtenBytes.Should().BeEquivalentTo(content, "file bytes must match content exactly");
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileSystemSnapshotSink_creates_directory_if_missing()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "nested", "output");
        var result = MakeResult();

        try
        {
            var sink = new FileSystemSnapshotSink(
                DeliveryOpts(o => o.OutputDirectory = tmpDir),
                NullLogger<FileSystemSnapshotSink>.Instance);

            await sink.DeliverAsync(result, result.Content, "snap.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

            Directory.Exists(tmpDir).Should().BeTrue("directory must have been created automatically");
        }
        finally
        {
            var root = Path.Combine(Path.GetTempPath(), tmpDir.Split(Path.DirectorySeparatorChar)[^3]);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileSystemSnapshotSink_noop_when_output_directory_not_configured()
    {
        var result = MakeResult();
        var sink   = new FileSystemSnapshotSink(
            DeliveryOpts(), // OutputDirectory == null
            NullLogger<FileSystemSnapshotSink>.Instance);

        // Must not throw.
        await sink.DeliverAsync(result, result.Content, "snap.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    // ── EmailSnapshotSink ─────────────────────────────────────────────────────

    private sealed class CapturingEmailService : IWtmEmailService
    {
        public List<EmailMessage> Sent { get; } = new();

        public Task SendAsync(EmailMessage msg, CancellationToken ct = default)
        {
            Sent.Add(msg);
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    public async Task EmailSnapshotSink_calls_email_service_with_attachment()
    {
        var content   = new byte[] { 1, 2, 3, 4, 5 };
        var result    = MakeResult(content: content);
        var emailSvc  = new CapturingEmailService();

        var sink = new EmailSnapshotSink(
            emailSvc,
            DeliveryOpts(o =>
            {
                o.EmailRecipients = new[] { "alice@example.com", "bob@example.com" };
            }),
            NullLogger<EmailSnapshotSink>.Instance);

        await sink.DeliverAsync(result, content, "report.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        emailSvc.Sent.Should().HaveCount(1);
        var msg = emailSvc.Sent[0];
        msg.To.Should().BeEquivalentTo(new[] { "alice@example.com", "bob@example.com" });
        msg.Attachments.Should().HaveCount(1);
        msg.Attachments[0].FileName.Should().Be("report.xlsx");
        msg.Attachments[0].Data.Should().BeEquivalentTo(content);
        msg.Attachments[0].ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    [TestMethod]
    public async Task EmailSnapshotSink_uses_default_subject_when_template_not_configured()
    {
        var result   = MakeResult(dashboardId: "sales");
        var emailSvc = new CapturingEmailService();

        var sink = new EmailSnapshotSink(
            emailSvc,
            DeliveryOpts(o => o.EmailRecipients = new[] { "ops@example.com" }),
            NullLogger<EmailSnapshotSink>.Instance);

        await sink.DeliverAsync(result, result.Content, "snap.xlsx", "application/octet-stream");

        emailSvc.Sent[0].Subject.Should().Contain("sales");
    }

    [TestMethod]
    public async Task EmailSnapshotSink_applies_subject_template_placeholders()
    {
        var result   = MakeResult(jobId: "jid-1", dashboardId: "dash-x");
        var emailSvc = new CapturingEmailService();

        var sink = new EmailSnapshotSink(
            emailSvc,
            DeliveryOpts(o =>
            {
                o.EmailRecipients      = new[] { "ops@example.com" };
                o.EmailSubjectTemplate = "Snapshot [{JobId}] for {DashboardId} — {FileName}";
            }),
            NullLogger<EmailSnapshotSink>.Instance);

        await sink.DeliverAsync(result, result.Content, "my-file.xlsx", "application/octet-stream");

        emailSvc.Sent[0].Subject.Should().Be("Snapshot [jid-1] for dash-x — my-file.xlsx");
    }

    [TestMethod]
    public async Task EmailSnapshotSink_noop_when_no_recipients_configured()
    {
        var result   = MakeResult();
        var emailSvc = new CapturingEmailService();

        var sink = new EmailSnapshotSink(
            emailSvc,
            DeliveryOpts(), // EmailRecipients == empty
            NullLogger<EmailSnapshotSink>.Instance);

        await sink.DeliverAsync(result, result.Content, "snap.xlsx", "application/octet-stream");

        emailSvc.Sent.Should().BeEmpty("no recipients → sink must silently skip");
    }

    [TestMethod]
    public async Task EmailSnapshotSink_noop_when_email_service_not_registered()
    {
        var result = MakeResult();

        // Pass null for IWtmEmailService — simulates "not registered" scenario.
        var sink = new EmailSnapshotSink(
            null,
            DeliveryOpts(o => o.EmailRecipients = new[] { "ops@example.com" }),
            NullLogger<EmailSnapshotSink>.Instance);

        // Must not throw.
        await sink.DeliverAsync(result, result.Content, "snap.xlsx", "application/octet-stream");
    }

    // ── WebhookNotificationSnapshotSink ───────────────────────────────────────

    [TestMethod]
    public async Task WebhookNotificationSnapshotSink_dispatches_info_card_with_fields()
    {
        var result     = MakeResult(jobId: "j1", dashboardId: "d1");
        var capturedMessage = (WebhookMessage?)null;

        var webhookSink = new Mock<IWtmWebhookSink>();
        webhookSink
            .Setup(s => s.SendAsync(It.IsAny<WebhookMessage>(), It.IsAny<CancellationToken>()))
            .Callback<WebhookMessage, CancellationToken>((m, _) => capturedMessage = m)
            .Returns(Task.CompletedTask);

        var sink = new WebhookNotificationSnapshotSink(
            webhookSink.Object,
            NullLogger<WebhookNotificationSnapshotSink>.Instance);

        await sink.DeliverAsync(result, result.Content, "report.xlsx", "application/octet-stream");

        webhookSink.Verify(s => s.SendAsync(It.IsAny<WebhookMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        capturedMessage.Should().NotBeNull();
        capturedMessage!.Level.Should().Be(WebhookLevel.Info);
        capturedMessage.Title.Should().Contain("d1");

        var fieldKeys = new List<string>();
        foreach (var kv in capturedMessage.Fields)
            fieldKeys.Add(kv.Key);
        fieldKeys.Should().Contain("Job");
        fieldKeys.Should().Contain("Dashboard");
        fieldKeys.Should().Contain("File");
        fieldKeys.Should().Contain("Size");
    }

    [TestMethod]
    public async Task WebhookNotificationSnapshotSink_noop_when_webhook_sink_not_registered()
    {
        var result = MakeResult();
        var sink   = new WebhookNotificationSnapshotSink(
            null,
            NullLogger<WebhookNotificationSnapshotSink>.Instance);

        // Must not throw.
        await sink.DeliverAsync(result, result.Content, "snap.xlsx", "application/octet-stream");
    }

    // ── DI / registration helpers ─────────────────────────────────────────────

    [TestMethod]
    public void AddDashboardFileSnapshotSink_registers_FileSystemSnapshotSink()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWtmDashboard();
        services.AddWtmDashboardSnapshots();
        services.AddDashboardFileSnapshotSink("/tmp/snapshots");

        using var sp   = services.BuildServiceProvider();
        var sinks      = sp.GetServices<IDashboardSnapshotSink>();

        sinks.Should().ContainSingle(s => s is FileSystemSnapshotSink);
    }

    [TestMethod]
    public void AddDashboardEmailSnapshotSink_registers_EmailSnapshotSink()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWtmDashboard();
        services.AddWtmDashboardSnapshots();
        services.AddDashboardEmailSnapshotSink(new[] { "ops@example.com" });

        using var sp = services.BuildServiceProvider();
        var sinks    = sp.GetServices<IDashboardSnapshotSink>();

        sinks.Should().ContainSingle(s => s is EmailSnapshotSink);
    }

    [TestMethod]
    public void AddDashboardWebhookNotificationSnapshotSink_registers_WebhookSink()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWtmDashboard();
        services.AddWtmDashboardSnapshots();
        services.AddDashboardWebhookNotificationSnapshotSink();

        using var sp = services.BuildServiceProvider();
        var sinks    = sp.GetServices<IDashboardSnapshotSink>();

        sinks.Should().ContainSingle(s => s is WebhookNotificationSnapshotSink);
    }

    [TestMethod]
    public void Multiple_sinks_can_be_registered_simultaneously()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWtmDashboard();
        services.AddWtmDashboardSnapshots();
        services.AddDashboardFileSnapshotSink("/tmp/snapshots");
        services.AddDashboardEmailSnapshotSink(new[] { "ops@example.com" });
        services.AddDashboardWebhookNotificationSnapshotSink();

        using var sp = services.BuildServiceProvider();
        var sinks    = sp.GetServices<IDashboardSnapshotSink>();

        sinks.Should().HaveCount(3, "all three sinks should be resolvable from DI");
    }

    [TestMethod]
    public void AddDashboardFileSnapshotSink_throws_when_directory_is_null_or_empty()
    {
        var services = new ServiceCollection();
        services.AddWtmDashboard();
        services.AddWtmDashboardSnapshots();

        Assert.ThrowsException<ArgumentException>(
            () => services.AddDashboardFileSnapshotSink(string.Empty));
        Assert.ThrowsException<ArgumentException>(
            () => services.AddDashboardFileSnapshotSink("   "));
    }
}

// ── Test helper: internal test hook ──────────────────────────────────────────

/// <summary>
/// Exposes <see cref="DashboardSnapshotHostedService"/> internals for testing
/// without running the full timing loop.
/// </summary>
internal static class DashboardSnapshotHostedServiceTestExtensions
{
    /// <summary>
    /// Runs a single job by directly invoking <see cref="IScheduledDashboardJob.RunAsync"/>
    /// and then dispatching to registered sinks — bypassing the cron scheduler.
    /// This is the test seam used to drive the service in unit tests.
    /// </summary>
    public static async Task RunSingleJobForTestAsync(
        this DashboardSnapshotHostedService svc,
        string jobId,
        string dashboardId,
        DashboardExportFormat format,
        CancellationToken ct)
    {
        // Invoke the private RunJobSafeAsync via reflection so the service
        // does not need a real cron trigger.
        var method = typeof(DashboardSnapshotHostedService)
            .GetMethod("RunJobSafeAsync",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);

        method.Should().NotBeNull("RunJobSafeAsync must exist on DashboardSnapshotHostedService");

        var cfg = new ScheduledDashboardJobConfig
        {
            JobId          = jobId,
            DashboardId    = dashboardId,
            Format         = format,
            CronExpression = "0 8 * * 1"
        };

        var task = (Task)method!.Invoke(svc, new object[] { cfg, ct })!;
        await task;
    }
}
