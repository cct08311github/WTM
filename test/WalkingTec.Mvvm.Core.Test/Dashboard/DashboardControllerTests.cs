#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
    public class DashboardControllerTests
    {
        private Mock<IDashboardService> _service = null!;
        private _DashboardController _controller = null!;

        [TestInitialize]
        public void Init()
        {
            _service = new Mock<IDashboardService>();
            var opts = Options.Create(new DashboardOptions());
            _controller = new _DashboardController(
                _service.Object, opts,
                Enumerable.Empty<IWidgetDataSource>())
            {
                Wtm = MockWtmContext.CreateWtmContext()
            };
            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        private void SetUser(string userId, params string[] roles)
        {
            _controller.Wtm.LoginUserInfo = new LoginUserInfo { ITCode = userId };
            _controller.Wtm.LoginUserInfo.Roles = new List<SimpleRole>();
            foreach (var r in roles)
            {
                _controller.Wtm.LoginUserInfo.Roles.Add(new SimpleRole { RoleCode = r });
            }
        }

        [TestMethod]
        public async Task List_returns_200_with_empty_list()
        {
            SetUser("bob");
            _service.Setup(x => x.ListAsync("bob", It.IsAny<string[]>(), It.IsAny<string?>()))
                .ReturnsAsync(new List<DashboardSummary>());

            var result = await _controller.List() as OkObjectResult;

            result.Should().NotBeNull();
            result!.Value.Should().BeOfType<List<DashboardSummary>>();
            ((List<DashboardSummary>)result.Value!).Should().BeEmpty();
        }

        [TestMethod]
        public async Task Create_returns_id()
        {
            SetUser("bob");
            _service.Setup(x => x.CreateAsync(It.IsAny<DashboardDefinition>()))
                .ReturnsAsync("new-id");

            var result = await _controller.Create(new DashboardDefinition()) as OkObjectResult;

            result.Should().NotBeNull();
            result!.Value.Should().Be("new-id");
        }

        [TestMethod]
        public async Task Get_returns_404_for_unknown()
        {
            _service.Setup(x => x.GetAsync("unknown", It.IsAny<string?>())).ReturnsAsync((DashboardDefinition?)null);

            var result = await _controller.Get("unknown") as NotFoundResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task Get_returns_403_when_access_denied()
        {
            SetUser("alice");
            var def = new DashboardDefinition { Id = "id1", Owner = "bob" };
            _service.Setup(x => x.GetAsync("id1", It.IsAny<string?>())).ReturnsAsync(def);
            _service.Setup(x => x.CanAccess(def, "alice", It.IsAny<string[]>())).Returns(false);

            var result = await _controller.Get("id1") as ForbidResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task Update_returns_403_for_non_owner()
        {
            SetUser("alice");
            var existing = new DashboardDefinition { Id = "id1", Owner = "bob" };
            var update = new DashboardDefinition { Id = "id1" };
            _service.Setup(x => x.GetAsync("id1", It.IsAny<string?>())).ReturnsAsync(existing);
            _service.Setup(x => x.CanEdit(existing, "alice", It.IsAny<string[]>())).Returns(false);

            var result = await _controller.Update("id1", update) as ForbidResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task Delete_returns_404_for_unknown()
        {
            _service.Setup(x => x.GetAsync("unknown", It.IsAny<string?>())).ReturnsAsync((DashboardDefinition?)null);

            var result = await _controller.Delete("unknown") as NotFoundResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task Delete_returns_403_for_non_owner()
        {
            SetUser("alice");
            var existing = new DashboardDefinition { Id = "id1", Owner = "bob" };
            _service.Setup(x => x.GetAsync("id1", It.IsAny<string?>())).ReturnsAsync(existing);
            _service.Setup(x => x.CanEdit(existing, "alice", It.IsAny<string[]>())).Returns(false);

            var result = await _controller.Delete("id1") as ForbidResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task GetWidgetData_returns_404_for_unknown_dashboard()
        {
            _service.Setup(x => x.GetAsync("unknown", It.IsAny<string?>())).ReturnsAsync((DashboardDefinition?)null);

            var result = await _controller.GetWidgetData("unknown", "w1", CancellationToken.None) as NotFoundResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task GetWidgetData_returns_404_for_unknown_widget()
        {
            SetUser("bob");
            var def = new DashboardDefinition { Id = "id1", Owner = "bob" };
            _service.Setup(x => x.GetAsync("id1", It.IsAny<string?>())).ReturnsAsync(def);
            _service.Setup(x => x.CanAccess(def, "bob", It.IsAny<string[]>())).Returns(true);

            var result = await _controller.GetWidgetData("id1", "w1", CancellationToken.None) as NotFoundResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task GetWidgetData_returns_403_when_dashboard_access_denied()
        {
            SetUser("alice");
            var def = new DashboardDefinition { Id = "id1", Owner = "bob" };
            _service.Setup(x => x.GetAsync("id1", It.IsAny<string?>())).ReturnsAsync(def);
            _service.Setup(x => x.CanAccess(def, "alice", It.IsAny<string[]>())).Returns(false);

            var result = await _controller.GetWidgetData("id1", "w1", CancellationToken.None) as ForbidResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public void GetDataSources_returns_200_with_empty_list()
        {
            var result = _controller.GetDataSources() as OkObjectResult;

            result.Should().NotBeNull();
            result!.StatusCode.Should().Be(200);
        }

        [TestMethod]
        public void GetDataSources_includes_custom_sources()
        {
            var mockSource = new Mock<IWidgetDataSource>();
            mockSource.Setup(s => s.Name).Returns("test-source");
            mockSource.Setup(s => s.Kind).Returns(WidgetDataSourceKind.Custom);

            var opts = Options.Create(new DashboardOptions());
            var controller = new _DashboardController(
                _service.Object, opts,
                new[] { mockSource.Object })
            {
                Wtm = MockWtmContext.CreateWtmContext()
            };
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };

            var result = controller.GetDataSources() as OkObjectResult;

            result.Should().NotBeNull();
            var json = System.Text.Json.JsonSerializer.Serialize(result!.Value);
            json.Should().Contain("test-source");
            json.Should().Contain("custom");
        }

        // Test model for analysis datasource listing
        private class DashCtrlTestRecord : TopBasePoco
        {
            [Dimension(DisplayName = "City")] public string City { get; set; } = "";
            [Measure(AllowedFuncs = AggregateFunc.Sum, DisplayName = "Revenue")]
            public decimal Revenue { get; set; }
        }

        [EnableAnalysis]
        private class DashCtrlTestListVM : BasePagedListVM<DashCtrlTestRecord, BaseSearcher>
        {
            public override IOrderedQueryable<DashCtrlTestRecord> GetSearchQuery()
                => new List<DashCtrlTestRecord>().AsQueryable().OrderBy(x => x.ID);
        }

        // ─── Widget Type validation (#382) ────────────────────────────────────

        [TestMethod]
        public async Task Create_returns_400_when_widget_type_is_empty_string()
        {
            SetUser("bob");
            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    { "w1", new WidgetDefinition { Type = "" } }
                }
            };

            var result = await _controller.Create(dashboard) as BadRequestObjectResult;

            result.Should().NotBeNull("空字串 Widget Type 應被拒絕");
            result!.Value.Should().BeOfType<string>()
                .Which.Should().Contain("w1");
        }

        [TestMethod]
        public async Task Create_returns_400_when_widget_type_is_whitespace()
        {
            SetUser("bob");
            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    { "w1", new WidgetDefinition { Type = "   " } }
                }
            };

            var result = await _controller.Create(dashboard) as BadRequestObjectResult;

            result.Should().NotBeNull("空白字串 Widget Type 應被拒絕");
        }

        [TestMethod]
        public async Task Create_accepts_valid_widget_type()
        {
            SetUser("bob");
            _service.Setup(x => x.CreateAsync(It.IsAny<DashboardDefinition>()))
                .ReturnsAsync("new-id");

            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    { "w1", new WidgetDefinition { Type = "chart" } },
                    { "w2", new WidgetDefinition { Type = "kpi" } }
                }
            };

            var result = await _controller.Create(dashboard) as OkObjectResult;

            result.Should().NotBeNull("有效 Widget Type 應通過驗證");
        }

        [TestMethod]
        public async Task Update_returns_400_when_widget_type_is_empty_string()
        {
            SetUser("bob");
            var existing = new DashboardDefinition { Id = "d1", Owner = "bob" };
            _service.Setup(x => x.GetAsync("d1", It.IsAny<string?>())).ReturnsAsync(existing);
            _service.Setup(x => x.CanEdit(existing, "bob", It.IsAny<string[]>())).Returns(true);

            var dashboard = new DashboardDefinition
            {
                Id = "d1",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    { "w1", new WidgetDefinition { Type = "" } }
                }
            };

            var result = await _controller.Update("d1", dashboard) as BadRequestObjectResult;

            result.Should().NotBeNull("Update 時空字串 Widget Type 應被拒絕");
        }

        [TestMethod]
        public async Task Create_with_no_widgets_does_not_validate_widget_types()
        {
            // 沒有 widget 的 dashboard 應正常建立（新增空 dashboard 是合法操作）
            SetUser("bob");
            _service.Setup(x => x.CreateAsync(It.IsAny<DashboardDefinition>()))
                .ReturnsAsync("new-id");

            var result = await _controller.Create(new DashboardDefinition()) as OkObjectResult;

            result.Should().NotBeNull("無 widget 的 dashboard 應通過驗證");
        }

        [TestMethod]
        public void GetDataSources_includes_analysis_vms()
        {
            var registry = new AnalysisVmRegistry();
            registry.Build(new[] { typeof(DashboardControllerTests).Assembly });

            var opts = Options.Create(new DashboardOptions());
            var controller = new _DashboardController(
                _service.Object, opts,
                Enumerable.Empty<IWidgetDataSource>(),
                registry)
            {
                Wtm = MockWtmContext.CreateWtmContext()
            };
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };

            var result = controller.GetDataSources() as OkObjectResult;

            result.Should().NotBeNull();
            var json = System.Text.Json.JsonSerializer.Serialize(result!.Value);
            json.Should().Contain("analysis");
            json.Should().Contain("City");
            json.Should().Contain("Revenue");
        }

        // ─── 正向路徑測試 (#365, #366) ────────────────────────────────────────

        [TestMethod]
        public async Task Get_returns_200_with_dashboard_when_access_granted()
        {
            SetUser("bob");
            var def = new DashboardDefinition { Id = "id1", Owner = "bob" };
            _service.Setup(x => x.GetAsync("id1", It.IsAny<string?>())).ReturnsAsync(def);
            _service.Setup(x => x.CanAccess(def, "bob", It.IsAny<string[]>())).Returns(true);

            var result = await _controller.Get("id1") as OkObjectResult;

            result.Should().NotBeNull();
            result!.Value.Should().Be(def);
        }

        [TestMethod]
        public async Task List_returns_200_with_dashboard_summaries_when_data_exists()
        {
            SetUser("bob");
            var summaries = new List<DashboardSummary>
            {
                new DashboardSummary { Id = "id1", Title = "My Dashboard" },
                new DashboardSummary { Id = "id2", Title = "Team Dashboard" }
            };
            _service.Setup(x => x.ListAsync("bob", It.IsAny<string[]>(), It.IsAny<string?>()))
                .ReturnsAsync(summaries);

            var result = await _controller.List() as OkObjectResult;

            result.Should().NotBeNull();
            ((List<DashboardSummary>)result!.Value!).Should().HaveCount(2);
        }

        [TestMethod]
        public async Task Update_returns_404_for_unknown_dashboard()
        {
            SetUser("bob");
            _service.Setup(x => x.GetAsync("unknown", It.IsAny<string?>()))
                .ReturnsAsync((DashboardDefinition?)null);

            var update = new DashboardDefinition { Id = "unknown" };
            var result = await _controller.Update("unknown", update) as NotFoundResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task Update_returns_200_and_preserves_owner()
        {
            // 安全行為回歸保護：server 強制保留原始 owner，防止 client 偽造
            SetUser("bob");
            var existing = new DashboardDefinition { Id = "id1", Owner = "bob" };
            _service.Setup(x => x.GetAsync("id1", It.IsAny<string?>())).ReturnsAsync(existing);
            _service.Setup(x => x.CanEdit(existing, "bob", It.IsAny<string[]>())).Returns(true);
            _service.Setup(x => x.UpdateAsync(It.IsAny<DashboardDefinition>())).Returns(Task.CompletedTask);

            // Client 試圖偽造 owner
            var update = new DashboardDefinition { Id = "id1", Owner = "evil-attacker" };
            var result = await _controller.Update("id1", update);

            result.Should().BeOfType<OkResult>();
            // owner 必須被 server 覆蓋回 existing.Owner
            _service.Verify(x => x.UpdateAsync(It.Is<DashboardDefinition>(d => d.Owner == "bob")), Times.Once);
        }

        [TestMethod]
        public async Task Create_sets_owner_from_session_not_from_client_body()
        {
            // 安全行為回歸保護：owner 由 session user 決定，client 傳入的 owner 應被忽略
            SetUser("alice");
            _service.Setup(x => x.CreateAsync(It.IsAny<DashboardDefinition>()))
                .ReturnsAsync("new-id");

            // Client 試圖偽造 owner
            var dashboard = new DashboardDefinition { Owner = "evil-attacker" };
            await _controller.Create(dashboard);

            _service.Verify(x => x.CreateAsync(It.Is<DashboardDefinition>(d => d.Owner == "alice")), Times.Once);
        }

        [TestMethod]
        public async Task Delete_returns_200_when_owner_deletes()
        {
            SetUser("bob");
            var existing = new DashboardDefinition { Id = "id1", Owner = "bob" };
            _service.Setup(x => x.GetAsync("id1", It.IsAny<string?>())).ReturnsAsync(existing);
            _service.Setup(x => x.CanEdit(existing, "bob", It.IsAny<string[]>())).Returns(true);
            // BUG-FIX (Finding 3): controller now calls the tenant-aware overload.
            _service.Setup(x => x.DeleteAsync("id1", It.IsAny<string?>())).Returns(Task.CompletedTask);

            var result = await _controller.Delete("id1");

            result.Should().BeOfType<OkResult>();
            _service.Verify(x => x.DeleteAsync("id1", It.IsAny<string?>()), Times.Once);
        }

        [TestMethod]
        public async Task GetWidgetData_returns_200_with_widget_result()
        {
            SetUser("bob");
            var def = new DashboardDefinition
            {
                Id = "id1", Owner = "bob",
                Widgets = new Dictionary<string, WidgetDefinition> { { "w1", new WidgetDefinition { Type = "chart" } } }
            };
            var widgetResult = new WidgetDataResult { Value = 42 };
            _service.Setup(x => x.GetAsync("id1", It.IsAny<string?>())).ReturnsAsync(def);
            _service.Setup(x => x.CanAccess(def, "bob", It.IsAny<string[]>())).Returns(true);
            _service.Setup(x => x.GetWidgetDataAsync("id1", "w1", It.IsAny<Dictionary<string, string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(widgetResult);

            var result = await _controller.GetWidgetData("id1", "w1", CancellationToken.None) as OkObjectResult;

            result.Should().NotBeNull();
            result!.Value.Should().Be(widgetResult);
        }

        [TestMethod]
        public async Task GetWidgetData_returns_404_when_service_throws_KeyNotFoundException()
        {
            SetUser("bob");
            var def = new DashboardDefinition
            {
                Id = "id1", Owner = "bob",
                Widgets = new Dictionary<string, WidgetDefinition> { { "w1", new WidgetDefinition { Type = "chart" } } }
            };
            _service.Setup(x => x.GetAsync("id1", It.IsAny<string?>())).ReturnsAsync(def);
            _service.Setup(x => x.CanAccess(def, "bob", It.IsAny<string[]>())).Returns(true);
            _service.Setup(x => x.GetWidgetDataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new KeyNotFoundException("data source not found"));

            var result = await _controller.GetWidgetData("id1", "w1", CancellationToken.None) as NotFoundResult;

            result.Should().NotBeNull();
        }

        // ─── PostWidgetData 端點測試 (#364, #366) ─────────────────────────────

        [TestMethod]
        public async Task PostWidgetData_returns_404_for_unknown_dashboard()
        {
            _service.Setup(x => x.GetAsync("unknown", It.IsAny<string?>()))
                .ReturnsAsync((DashboardDefinition?)null);

            var result = await _controller.PostWidgetData(
                "unknown", "w1",
                new Dictionary<string, string>(),
                CancellationToken.None) as NotFoundResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task PostWidgetData_returns_403_when_access_denied()
        {
            SetUser("alice");
            var def = new DashboardDefinition { Id = "id1", Owner = "bob" };
            _service.Setup(x => x.GetAsync("id1", It.IsAny<string?>())).ReturnsAsync(def);
            _service.Setup(x => x.CanAccess(def, "alice", It.IsAny<string[]>())).Returns(false);

            var result = await _controller.PostWidgetData(
                "id1", "w1",
                new Dictionary<string, string>(),
                CancellationToken.None) as ForbidResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task PostWidgetData_returns_404_for_unknown_widget()
        {
            SetUser("bob");
            // dashboard exists but has no widgets
            var def = new DashboardDefinition { Id = "id1", Owner = "bob" };
            _service.Setup(x => x.GetAsync("id1", It.IsAny<string?>())).ReturnsAsync(def);
            _service.Setup(x => x.CanAccess(def, "bob", It.IsAny<string[]>())).Returns(true);

            var result = await _controller.PostWidgetData(
                "id1", "w1",
                new Dictionary<string, string>(),
                CancellationToken.None) as NotFoundResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task PostWidgetData_returns_200_with_widget_data()
        {
            SetUser("bob");
            var def = new DashboardDefinition
            {
                Id = "id1", Owner = "bob",
                Widgets = new Dictionary<string, WidgetDefinition> { { "w1", new WidgetDefinition { Type = "chart" } } }
            };
            var widgetResult = new WidgetDataResult { Value = 42 };
            var filters = new Dictionary<string, string> { { "year", "2026" } };
            _service.Setup(x => x.GetAsync("id1", It.IsAny<string?>())).ReturnsAsync(def);
            _service.Setup(x => x.CanAccess(def, "bob", It.IsAny<string[]>())).Returns(true);
            _service.Setup(x => x.GetWidgetDataAsync("id1", "w1", filters, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(widgetResult);

            var result = await _controller.PostWidgetData("id1", "w1", filters, CancellationToken.None) as OkObjectResult;

            result.Should().NotBeNull();
            result!.Value.Should().Be(widgetResult);
        }

        [TestMethod]
        public async Task PostWidgetData_returns_404_when_service_throws_KeyNotFoundException()
        {
            SetUser("bob");
            var def = new DashboardDefinition
            {
                Id = "id1", Owner = "bob",
                Widgets = new Dictionary<string, WidgetDefinition> { { "w1", new WidgetDefinition { Type = "chart" } } }
            };
            _service.Setup(x => x.GetAsync("id1", It.IsAny<string?>())).ReturnsAsync(def);
            _service.Setup(x => x.CanAccess(def, "bob", It.IsAny<string[]>())).Returns(true);
            _service.Setup(x => x.GetWidgetDataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new KeyNotFoundException("widget config error"));

            var result = await _controller.PostWidgetData("id1", "w1", new Dictionary<string, string>(), CancellationToken.None) as NotFoundResult;

            result.Should().NotBeNull();
        }

        // ─── null body / ID 不符守衛測試 (#371, #369) ─────────────────────────

        [TestMethod]
        public async Task Create_returns_400_when_body_is_null()
        {
            var result = await _controller.Create(null!) as BadRequestResult;

            result.Should().NotBeNull("null body 應被 Create 拒絕並回傳 400");
        }

        [TestMethod]
        public async Task Update_returns_400_when_body_is_null()
        {
            var result = await _controller.Update("id1", null!) as BadRequestResult;

            result.Should().NotBeNull("null body 應被 Update 拒絕並回傳 400");
        }

        [TestMethod]
        public async Task Update_returns_400_when_body_id_mismatches_route_id()
        {
            // 防 confused deputy 攻擊：route id 與 body id 不一致 → 400
            var mismatch = new DashboardDefinition { Id = "id-B" };
            var result = await _controller.Update("id-A", mismatch) as BadRequestResult;

            result.Should().NotBeNull("route id 與 body id 不符應被拒絕並回傳 400");
        }

        // ─── M13: GetWidgetDataAsync tenantId forwarding (#137) ──────────────────

        /// <summary>
        /// Regression test for M13: the controller must forward the authenticated tenantId
        /// to GetWidgetDataAsync so the service reads from the correct tenant directory.
        /// </summary>
        [TestMethod]
        public async Task GetWidgetData_forwards_tenantId_from_session_to_service()
        {
            // Arrange: user belongs to "tenantXYZ"
            SetUser("bob");
            _controller.Wtm.LoginUserInfo!.TenantCode = "tenantXYZ";

            var def = new DashboardDefinition
            {
                Id = "id1", Owner = "bob",
                Widgets = new Dictionary<string, WidgetDefinition> { { "w1", new WidgetDefinition { Type = "chart" } } }
            };
            var widgetResult = new WidgetDataResult { Value = 99 };
            _service.Setup(x => x.GetAsync("id1", "tenantXYZ")).ReturnsAsync(def);
            _service.Setup(x => x.CanAccess(def, "bob", It.IsAny<string[]>())).Returns(true);
            _service.Setup(x => x.GetWidgetDataAsync(
                    "id1", "w1",
                    It.IsAny<Dictionary<string, string>>(),
                    "tenantXYZ",                  // tenantId must be passed from session
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(widgetResult);

            // Act
            var result = await _controller.GetWidgetData("id1", "w1", CancellationToken.None) as OkObjectResult;

            // Assert: 200 returned and service was called with the correct tenantId
            result.Should().NotBeNull();
            result!.Value.Should().Be(widgetResult);
            _service.Verify(x => x.GetWidgetDataAsync(
                "id1", "w1",
                It.IsAny<Dictionary<string, string>>(),
                "tenantXYZ",
                It.IsAny<CancellationToken>()), Times.Once,
                "GetWidgetDataAsync must receive the authenticated tenantId from session");
        }

        [TestMethod]
        public async Task PostWidgetData_forwards_tenantId_from_session_to_service()
        {
            // Arrange: user belongs to "tenantXYZ"
            SetUser("bob");
            _controller.Wtm.LoginUserInfo!.TenantCode = "tenantXYZ";

            var def = new DashboardDefinition
            {
                Id = "id1", Owner = "bob",
                Widgets = new Dictionary<string, WidgetDefinition> { { "w1", new WidgetDefinition { Type = "chart" } } }
            };
            var widgetResult = new WidgetDataResult { Value = 88 };
            var filters = new Dictionary<string, string> { { "year", "2026" } };
            _service.Setup(x => x.GetAsync("id1", "tenantXYZ")).ReturnsAsync(def);
            _service.Setup(x => x.CanAccess(def, "bob", It.IsAny<string[]>())).Returns(true);
            _service.Setup(x => x.GetWidgetDataAsync(
                    "id1", "w1",
                    filters,
                    "tenantXYZ",
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(widgetResult);

            // Act
            var result = await _controller.PostWidgetData("id1", "w1", filters, CancellationToken.None) as OkObjectResult;

            // Assert
            result.Should().NotBeNull();
            result!.Value.Should().Be(widgetResult);
            _service.Verify(x => x.GetWidgetDataAsync(
                "id1", "w1",
                filters,
                "tenantXYZ",
                It.IsAny<CancellationToken>()), Times.Once,
                "PostWidgetData must forward tenantId to GetWidgetDataAsync");
        }
    }
}