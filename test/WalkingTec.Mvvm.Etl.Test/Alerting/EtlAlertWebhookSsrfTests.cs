#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Alerting;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.ViewModels;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.Etl.Test.ViewModels;

namespace WalkingTec.Mvvm.Etl.Test.Alerting;

/// <summary>
/// Regression tests for Issue #484: SSRF hardening on the ETL legacy webhook alert path.
///
/// Covers:
///  (a) Boundary validation in EtlJobDefinitionVM.Validate() — blocked hosts/IPs rejected at save.
///  (b) Dispatch-time guard in EtlAlertService.SendLegacyWebhookAsync() — POST is skipped for
///      private/loopback/IMDS URLs even when the VM layer was bypassed.
/// </summary>
[TestClass]
public class EtlAlertWebhookSsrfTests
{
    // ── ViewModel boundary validation ──────────────────────────────────────

    private WTMContext _wtm = null!;

    [TestInitialize]
    public void Setup()
    {
        var dc = new EtlTestDataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory);
        _wtm = MockWtmContext.CreateWtmContext(dc);
    }

    private static bool HasError(IModelStateService msd, string key)
        => msd != null && msd.Keys.Any(k => k == key);

    private EtlJobDefinitionVM BaseVm()
    {
        var vm = _wtm.CreateVM<EtlJobDefinitionVM>();
        vm.Entity.Name = "SsrfTestJob";
        vm.Entity.CronExpression = "0 0 * * * ?";
        vm.Entity.SourceCsKey = "src";
        vm.Entity.SourceDbType = DBTypeEnum.SqlServer;
        vm.Entity.QueryTemplate = "SELECT 1";
        vm.Entity.TargetTableName = "T";
        vm.Entity.LoadMode = EtlLoadMode.Replace;    // skip merge-key uniqueness probe
        vm.Entity.MergeKeyColumn = "";
        return vm;
    }

    [TestMethod]
    [Description("VM: loopback URL (127.0.0.1) rejected at save — IP literal blocked")]
    public void Validate_AlertWebhookUrl_Loopback_IP_Rejected()
    {
        var vm = BaseVm();
        vm.Entity.AlertWebhookUrl = "https://127.0.0.1/alert";

        vm.Validate();

        Assert.IsTrue(HasError(vm.MSD, "Entity.AlertWebhookUrl"),
            "Loopback IP literal (127.0.0.1) must be rejected at save time.");
    }

    [TestMethod]
    [Description("VM: IMDS URL (169.254.169.254) rejected at save — link-local blocked")]
    public void Validate_AlertWebhookUrl_Imds_IP_Rejected()
    {
        var vm = BaseVm();
        vm.Entity.AlertWebhookUrl = "https://169.254.169.254/latest/meta-data/";

        vm.Validate();

        Assert.IsTrue(HasError(vm.MSD, "Entity.AlertWebhookUrl"),
            "Link-local / IMDS IP (169.254.169.254) must be rejected at save time.");
    }

    [TestMethod]
    [Description("VM: RFC1918 private IP (10.0.0.1) rejected at save")]
    public void Validate_AlertWebhookUrl_PrivateIp_Rejected()
    {
        var vm = BaseVm();
        vm.Entity.AlertWebhookUrl = "https://10.0.0.1/hook";

        vm.Validate();

        Assert.IsTrue(HasError(vm.MSD, "Entity.AlertWebhookUrl"),
            "RFC1918 private IP (10.x.x.x) must be rejected at save time.");
    }

    [TestMethod]
    [Description("VM: RFC1918 private IP 192.168.x.x rejected at save")]
    public void Validate_AlertWebhookUrl_PrivateIp192168_Rejected()
    {
        var vm = BaseVm();
        vm.Entity.AlertWebhookUrl = "https://192.168.1.100/hook";

        vm.Validate();

        Assert.IsTrue(HasError(vm.MSD, "Entity.AlertWebhookUrl"),
            "RFC1918 private IP (192.168.x.x) must be rejected at save time.");
    }

    [TestMethod]
    [Description("VM: plain http:// URL rejected at save (must be https://)")]
    public void Validate_AlertWebhookUrl_Http_Rejected()
    {
        var vm = BaseVm();
        vm.Entity.AlertWebhookUrl = "http://hooks.example.com/alert";

        vm.Validate();

        Assert.IsTrue(HasError(vm.MSD, "Entity.AlertWebhookUrl"),
            "Non-https URL must be rejected at save time.");
    }

    [TestMethod]
    [Description("VM: non-absolute URL rejected at save")]
    public void Validate_AlertWebhookUrl_Relative_Rejected()
    {
        var vm = BaseVm();
        vm.Entity.AlertWebhookUrl = "/hooks/alert";

        vm.Validate();

        Assert.IsTrue(HasError(vm.MSD, "Entity.AlertWebhookUrl"),
            "Relative URL must be rejected at save time.");
    }

    [TestMethod]
    [Description("VM: null AlertWebhookUrl is silently accepted")]
    public void Validate_AlertWebhookUrl_Null_Accepted()
    {
        var vm = BaseVm();
        vm.Entity.AlertWebhookUrl = null;

        vm.Validate();

        Assert.IsFalse(HasError(vm.MSD, "Entity.AlertWebhookUrl"),
            "Null webhook URL (alert disabled) must not produce a validation error.");
    }

    [TestMethod]
    [Description("VM: valid https:// public URL is accepted")]
    public void Validate_AlertWebhookUrl_ValidPublicHttps_Accepted()
    {
        var vm = BaseVm();
        vm.Entity.AlertWebhookUrl = "https://hooks.slack.com/services/T000/B000/xxxx";

        vm.Validate();

        Assert.IsFalse(HasError(vm.MSD, "Entity.AlertWebhookUrl"),
            "Well-formed public https:// URL must be accepted.");
    }

    // ── Dispatch-time SSRF guard in EtlAlertService ───────────────────────

    /// <summary>Tracks whether the underlying HttpClient was invoked.</summary>
    private sealed class TrackingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (EtlAlertService svc, TrackingHandler handler) MakeService()
    {
        var handler = new TrackingHandler();
        var client  = new HttpClient(handler);

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);

        var options = Options.Create(new EtlAlertOptions());
        return (new EtlAlertService(factory.Object, options, NullLogger<EtlAlertService>.Instance), handler);
    }

    private static EtlJobDefinition MakeJob(string? webhookUrl) => new()
    {
        Name = "SsrfDispatchJob",
        CronExpression = "0 0 2 * * ?",
        JobClassName = "Fake",
        SourceCsKey = "src",
        TargetCsKey = "tgt",
        TargetTableName = "tbl",
        MergeKeyColumn = "id",
        QueryTemplate = "SELECT 1",
        AlertWebhookUrl = webhookUrl,
        ConsecutiveFailureCount = 1,
    };

    private static EtlRunLog MakeRunLog() => new()
    {
        JobId = Guid.NewGuid(),
        Trigger = EtlRunTrigger.Scheduled,
        Result = EtlRunResult.Failed,
        ErrorMessage = "Timeout",
        StartedAt = DateTime.UtcNow.AddSeconds(-5),
        FinishedAt = DateTime.UtcNow,
    };

    [TestMethod]
    [Description("Dispatch: loopback IP literal (127.0.0.1) skipped at POST time — no HTTP call made")]
    public async Task SendAlertAsync_LoopbackIpUrl_NoHttpPost()
    {
        var (svc, handler) = MakeService();
        var job = MakeJob("https://127.0.0.1/hook");

        await svc.SendAlertAsync(job, MakeRunLog());

        Assert.AreEqual(0, handler.CallCount,
            "Dispatch-time SSRF guard must suppress the POST to a loopback IP.");
    }

    [TestMethod]
    [Description("Dispatch: IMDS IP literal (169.254.169.254) skipped at POST time")]
    public async Task SendAlertAsync_ImdsIpUrl_NoHttpPost()
    {
        var (svc, handler) = MakeService();
        var job = MakeJob("https://169.254.169.254/latest/meta-data/");

        await svc.SendAlertAsync(job, MakeRunLog());

        Assert.AreEqual(0, handler.CallCount,
            "Dispatch-time SSRF guard must suppress the POST to the IMDS endpoint.");
    }

    [TestMethod]
    [Description("Dispatch: http:// URL skipped at POST time (not https://)")]
    public async Task SendAlertAsync_HttpUrl_NoHttpPost()
    {
        var (svc, handler) = MakeService();
        var job = MakeJob("http://hooks.example.com/alert");

        await svc.SendAlertAsync(job, MakeRunLog());

        Assert.AreEqual(0, handler.CallCount,
            "Dispatch-time guard must suppress the POST for non-https:// URLs.");
    }

    [TestMethod]
    [Description("Dispatch: valid https:// public URL is POSTed (guard permits it)")]
    public async Task SendAlertAsync_ValidHttpsUrl_HttpPostMade()
    {
        var (svc, handler) = MakeService();
        var job = MakeJob("https://hooks.example.com/alert");

        await svc.SendAlertAsync(job, MakeRunLog());

        Assert.AreEqual(1, handler.CallCount,
            "Dispatch-time guard must NOT suppress the POST for a valid public https:// URL.");
    }
}
