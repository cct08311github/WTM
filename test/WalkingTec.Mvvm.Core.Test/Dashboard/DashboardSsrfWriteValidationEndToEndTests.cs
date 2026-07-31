#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Dashboard;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    /// <summary>
    /// Issue #948: end-to-end proof, through the real HTTP-facing controllers backed by a
    /// real <see cref="JsonFileDashboardService"/> (not a mock), that a caller supplying
    /// <c>AllowPrivateNetwork = true</c> through Create, Update, and Preview never gets a
    /// persisted widget carrying that flag — i.e. does not obtain private-network egress.
    /// A mocked <see cref="IDashboardService"/> (as the rest of DashboardControllerTests.cs
    /// and DashboardDesignerControllerTests.cs use) would bypass
    /// <c>ValidateWidgetConfigs</c> entirely and prove nothing about this guard; these
    /// tests deliberately use the real service so the guard from
    /// <c>JsonFileDashboardService.ValidateWidgetConfigs</c> is actually on the call path.
    /// </summary>
    [TestClass]
    public class DashboardSsrfWriteValidationEndToEndTests
    {
        private string _tempDir = "";
        private JsonFileDashboardService _service = null!;

        [TestInitialize]
        public void Init()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var options = Options.Create(new DashboardOptions { DashboardDirectory = _tempDir });
            _service = new JsonFileDashboardService(options, Array.Empty<IWidgetDataSource>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonFileDashboardService>.Instance);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }

        private _DashboardController BuildDashboardController()
        {
            var opts = Options.Create(new DashboardOptions());
            var ctrl = new _DashboardController(_service, opts, Array.Empty<IWidgetDataSource>())
            {
                Wtm = MockWtmContext.CreateWtmContext()
            };
            ctrl.Wtm.LoginUserInfo = new LoginUserInfo { ITCode = "attacker" };
            ctrl.Wtm.LoginUserInfo.Roles = new List<SimpleRole>();
            ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return ctrl;
        }

        private _DashboardDesignerController BuildDesignerController()
        {
            var opts = Options.Create(new DashboardOptions());
            var ctrl = new _DashboardDesignerController(_service, opts)
            {
                Wtm = MockWtmContext.CreateWtmContext()
            };
            ctrl.Wtm.LoginUserInfo = new LoginUserInfo { ITCode = "attacker" };
            ctrl.Wtm.LoginUserInfo.Roles = new List<SimpleRole>();
            ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return ctrl;
        }

        private static DashboardDefinition MaliciousDashboard(string title = "attacker-dashboard") => new()
        {
            Title = title,
            Widgets = new Dictionary<string, WidgetDefinition>
            {
                ["w1"] = new WidgetDefinition
                {
                    Type = "chart",
                    Source = new WidgetSourceDefinition
                    {
                        Kind = "rest",
                        RestOptions = new RestWidgetDataSourceOptions
                        {
                            Url = "https://169.254.169.254/latest/meta-data/iam/security-credentials/",
                            AllowPrivateNetwork = true
                        }
                    }
                }
            }
        };

        // ── Create ───────────────────────────────────────────────────────────

        [TestMethod]
        public async Task Create_with_AllowPrivateNetwork_true_is_rejected()
        {
            var ctrl = BuildDashboardController();

            var result = await ctrl.Create(MaliciousDashboard()) as BadRequestObjectResult;

            result.Should().NotBeNull("a widget definition requesting AllowPrivateNetwork must be rejected, not persisted");
            var listResult = await ctrl.List() as OkObjectResult;
            ((List<DashboardSummary>)listResult!.Value!).Should().BeEmpty(
                "nothing should have been persisted after a rejected Create");
        }

        // ── Update ───────────────────────────────────────────────────────────

        [TestMethod]
        public async Task Update_with_AllowPrivateNetwork_true_is_rejected_and_original_widget_survives()
        {
            var ctrl = BuildDashboardController();

            // First, legitimately create a dashboard with a safe public REST widget.
            var safe = new DashboardDefinition
            {
                Title = "safe-dashboard",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = new WidgetDefinition
                    {
                        Type = "chart",
                        Source = new WidgetSourceDefinition
                        {
                            Kind = "rest",
                            RestOptions = new RestWidgetDataSourceOptions { Url = "https://8.8.8.8/data" }
                        }
                    }
                }
            };
            var createResult = await ctrl.Create(safe) as OkObjectResult;
            createResult.Should().NotBeNull();
            var id = (string)createResult!.Value!;

            // Attempt to escalate through Update.
            var malicious = MaliciousDashboard();
            malicious.Id = id;
            var result = await ctrl.Update(id, malicious) as BadRequestObjectResult;

            result.Should().NotBeNull("Update must reject a widget definition requesting AllowPrivateNetwork");

            var getResult = await ctrl.Get(id) as OkObjectResult;
            var stored = (DashboardDefinition)getResult!.Value!;
            stored.Widgets["w1"].Source.RestOptions!.Url.Should().Be("https://8.8.8.8/data",
                "the original safe widget must survive a rejected Update, not be overwritten");
            stored.Widgets["w1"].Source.RestOptions!.AllowPrivateNetwork.Should().BeFalse();
        }

        // ── Preview ──────────────────────────────────────────────────────────

        [TestMethod]
        public async Task Preview_with_AllowPrivateNetwork_true_is_rejected()
        {
            var ctrl = BuildDesignerController();
            var widget = new WidgetDefinition
            {
                Type = "chart",
                Source = new WidgetSourceDefinition
                {
                    Kind = "rest",
                    RestOptions = new RestWidgetDataSourceOptions
                    {
                        Url = "https://169.254.169.254/latest/meta-data/iam/security-credentials/",
                        AllowPrivateNetwork = true
                    }
                }
            };

            var result = await ctrl.Preview(widget, CancellationToken.None) as BadRequestObjectResult;

            result.Should().NotBeNull("Preview must reject a widget definition requesting AllowPrivateNetwork " +
                                       "before ever persisting the transient dashboard or fetching data");
        }

        // ── Positive control ────────────────────────────────────────────────
        // Per issue #948's test plan: a fix that blocks EVERYTHING would also pass the
        // three rejection assertions above, so a positive control is required to prove
        // the guard is scoped to the security-sensitive fields, not "rest" widgets in
        // general.

        [TestMethod]
        public async Task Create_Update_and_Preview_all_accept_a_legitimate_public_https_destination()
        {
            var ctrl = BuildDashboardController();
            var designerCtrl = BuildDesignerController();

            var legit = new DashboardDefinition
            {
                Title = "legit-dashboard",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = new WidgetDefinition
                    {
                        Type = "chart",
                        Source = new WidgetSourceDefinition
                        {
                            Kind = "rest",
                            RestOptions = new RestWidgetDataSourceOptions { Url = "https://8.8.8.8/public-api" }
                        }
                    }
                }
            };

            var createResult = await ctrl.Create(legit) as OkObjectResult;
            createResult.Should().NotBeNull("Create must still accept a legitimate public HTTPS REST widget");
            var id = (string)createResult!.Value!;

            legit.Id = id;
            var updateResult = await ctrl.Update(id, legit);
            updateResult.Should().BeOfType<OkResult>("Update must still accept a legitimate public HTTPS REST widget");

            var previewWidget = new WidgetDefinition
            {
                Type = "chart",
                Source = new WidgetSourceDefinition
                {
                    Kind = "rest",
                    RestOptions = new RestWidgetDataSourceOptions { Url = "https://8.8.8.8/public-api" }
                }
            };
            var previewResult = await designerCtrl.Preview(previewWidget, CancellationToken.None);
            // This test's service has no "rest" IWidgetDataSource registered (Init() wires
            // JsonFileDashboardService with Array.Empty<IWidgetDataSource>()), so Preview's
            // subsequent GetWidgetDataAsync call fails with "Data source not found" and
            // surfaces as 502 (InvalidOperationException catch clause) — that failure is
            // expected and NOT what this assertion checks. The point here is only that the
            // request is NOT rejected as 400 at the write-time ValidateWidgetConfigs stage,
            // i.e. it is not treated as a security-field violation. See
            // RestWidgetSsrfHardeningTests.cs / RestWidgetDataSourceTests.cs for full
            // coverage of the actual REST fetch pipeline with a mocked HttpMessageHandler.
            previewResult.Should().NotBeOfType<BadRequestObjectResult>(
                "a legitimate public HTTPS REST widget must pass write-time validation in Preview");
        }
    }
}
