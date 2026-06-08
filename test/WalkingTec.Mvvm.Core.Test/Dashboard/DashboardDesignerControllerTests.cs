#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Dashboard;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    [TestClass]
    public class DashboardDesignerControllerTests
    {
        private Mock<IDashboardService> _service = null!;
        private _DashboardDesignerController _ctrl = null!;

        [TestInitialize]
        public void Init()
        {
            _service = new Mock<IDashboardService>();
            var opts = Options.Create(new DashboardOptions());
            _ctrl = new _DashboardDesignerController(_service.Object, opts)
            {
                Wtm = MockWtmContext.CreateWtmContext()
            };
            _ctrl.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        private void SetUser(string userId, string tenant = "", params string[] roles)
        {
            _ctrl.Wtm.LoginUserInfo = new LoginUserInfo { ITCode = userId, TenantCode = tenant };
            _ctrl.Wtm.LoginUserInfo.Roles = new List<SimpleRole>();
            foreach (var r in roles)
                _ctrl.Wtm.LoginUserInfo.Roles.Add(new SimpleRole { RoleCode = r });
        }

        // ── GetVmMeta ─────────────────────────────────────────────────────────

        [TestMethod]
        public void GetVmMeta_returns_404_when_registry_not_configured()
        {
            // Controller created without a registry
            var result = _ctrl.GetVmMeta("Foo.Bar") as NotFoundObjectResult;
            result.Should().NotBeNull();
        }

        [TestMethod]
        public void GetVmMeta_returns_400_for_empty_vmType()
        {
            var result = _ctrl.GetVmMeta("") as BadRequestObjectResult;
            result.Should().NotBeNull();
        }

        [TestMethod]
        public void GetVmMeta_returns_404_for_unknown_vm_when_registry_present()
        {
            var registry = new AnalysisVmRegistry();
            registry.Build([]); // empty — nothing registered

            var opts = Options.Create(new DashboardOptions());
            var ctrl = new _DashboardDesignerController(_service.Object, opts, registry)
            {
                Wtm = MockWtmContext.CreateWtmContext()
            };
            ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            var result = ctrl.GetVmMeta("Unknown.VmType") as NotFoundObjectResult;
            result.Should().NotBeNull();
        }

        // ── Preview ───────────────────────────────────────────────────────────

        [TestMethod]
        public async Task Preview_returns_400_for_null_body()
        {
            var result = await _ctrl.Preview(null!, CancellationToken.None) as BadRequestObjectResult;
            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task Preview_returns_400_for_missing_type()
        {
            var widget = new WidgetDefinition { Type = "" };
            var result = await _ctrl.Preview(widget, CancellationToken.None) as BadRequestObjectResult;
            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task Preview_returns_400_for_disallowed_type_when_allowlist_configured()
        {
            var opts = Options.Create(new DashboardOptions { AllowedWidgetTypes = ["kpi", "chart"] });
            var ctrl = new _DashboardDesignerController(_service.Object, opts)
            {
                Wtm = MockWtmContext.CreateWtmContext()
            };
            ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            var widget = new WidgetDefinition { Type = "unknowntype" };
            var result = await ctrl.Preview(widget, CancellationToken.None) as BadRequestObjectResult;
            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task Preview_returns_400_for_disallowed_filter_op()
        {
            SetUser("alice");
            var widget = new WidgetDefinition
            {
                Type = "chart",
                Source = new WidgetSourceDefinition
                {
                    Filters =
                    [
                        new FilterConfig { Field = "year", Op = "INJECTION--", Value = "2024" }
                    ]
                }
            };
            var result = await _ctrl.Preview(widget, CancellationToken.None) as BadRequestObjectResult;
            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task Preview_creates_transient_dashboard_and_returns_widget_data()
        {
            SetUser("alice", "t1");

            string? capturedId = null;

            // Capture what ID was created so we can verify the delete call
            _service
                .Setup(x => x.CreateAsync(It.IsAny<DashboardDefinition>()))
                .ReturnsAsync((DashboardDefinition d) =>
                {
                    d.Id = "transient-preview-id";
                    capturedId = d.Id;
                    return d.Id;
                });

            _service
                .Setup(x => x.GetWidgetDataAsync(
                    "transient-preview-id",
                    "_preview",
                    It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WidgetDataResult { Value = 42 });

            // DeleteAsync(id, tenantId) is a default interface method — mock the
            // non-tenant overload which the default delegates to
            _service
                .Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<string?>()))
                .Returns(Task.CompletedTask);

            var widget = new WidgetDefinition { Type = "kpi", Title = "Revenue" };
            var result = await _ctrl.Preview(widget, CancellationToken.None) as OkObjectResult;

            result.Should().NotBeNull();

            // The transient dashboard must be created with the correct owner + tenant
            _service.Verify(x => x.CreateAsync(
                It.Is<DashboardDefinition>(d =>
                    d.Owner == "alice" &&
                    d.TenantId == "t1" &&
                    d.Widgets.ContainsKey("_preview"))), Times.Once);

            // Data fetch must pass the authenticated tenantId
            _service.Verify(x => x.GetWidgetDataAsync(
                "transient-preview-id", "_preview",
                It.IsAny<Dictionary<string, string>?>(),
                "t1",
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task Preview_deletes_transient_dashboard_even_when_data_fetch_fails()
        {
            SetUser("alice");

            _service
                .Setup(x => x.CreateAsync(It.IsAny<DashboardDefinition>()))
                .ReturnsAsync("tmp-id");

            _service
                .Setup(x => x.GetWidgetDataAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("fetch error"));

            _service
                .Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<string?>()))
                .Returns(Task.CompletedTask);

            var widget = new WidgetDefinition { Type = "kpi" };
            var result = await _ctrl.Preview(widget, CancellationToken.None);

            // Should return 502 (not throw), and the transient dashboard must be cleaned up
            result.Should().BeOfType<ObjectResult>()
                  .Which.StatusCode.Should().Be(StatusCodes.Status502BadGateway);

            _service.Verify(x => x.DeleteAsync("tmp-id", It.IsAny<string?>()), Times.Once);
        }

        [TestMethod]
        public async Task Preview_returns_200_with_valid_kpi_widget()
        {
            SetUser("bob", "t-bob");

            _service
                .Setup(x => x.CreateAsync(It.IsAny<DashboardDefinition>()))
                .ReturnsAsync("tmp-ok");

            var expectedValue = 999;
            _service
                .Setup(x => x.GetWidgetDataAsync(
                    "tmp-ok", "_preview",
                    It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WidgetDataResult { Value = expectedValue });

            _service
                .Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<string?>()))
                .Returns(Task.CompletedTask);

            var widget = new WidgetDefinition { Type = "kpi", Title = "Units Sold" };
            var result = await _ctrl.Preview(widget, CancellationToken.None) as OkObjectResult;

            result.Should().NotBeNull();
            result!.Value.Should().NotBeNull();
        }

        // ── Tenant scoping ────────────────────────────────────────────────────

        [TestMethod]
        public async Task Preview_scopes_transient_dashboard_to_caller_tenant()
        {
            // Arrange: user belongs to tenant "acme"
            SetUser("carol", "acme");

            string? savedTenant = null;
            _service
                .Setup(x => x.CreateAsync(It.IsAny<DashboardDefinition>()))
                .ReturnsAsync((DashboardDefinition d) =>
                {
                    savedTenant = d.TenantId;
                    d.Id = "t-acme";
                    return d.Id;
                });

            _service
                .Setup(x => x.GetWidgetDataAsync(
                    It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WidgetDataResult { Value = null });

            _service
                .Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<string?>()))
                .Returns(Task.CompletedTask);

            await _ctrl.Preview(new WidgetDefinition { Type = "kpi" }, CancellationToken.None);

            savedTenant.Should().Be("acme");
        }
    }
}
