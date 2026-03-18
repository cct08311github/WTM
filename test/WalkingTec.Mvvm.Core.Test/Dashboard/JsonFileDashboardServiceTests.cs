using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core.Dashboard;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    [TestClass]
    public class JsonFileDashboardServiceTests
    {
        private string _tempDir = "";
        private JsonFileDashboardService _service = null!;

        [TestInitialize]
        public void Init()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var options = Options.Create(new DashboardOptions { DashboardDirectory = _tempDir });
            
            // To make sure base path logic points to _tempDir for testing we actually have to be careful
            // Wait, the constructor uses AppDomain.CurrentDomain.BaseDirectory. So we should pass a relative path
            // or we need to ensure it uses an absolute path if provided.
            // Oh, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, tempPath) if tempPath is absolute will return tempPath in Windows but not always what we expect. Let's fix DashboardDirectory to be an absolute path here, and see if Path.Combine handles it correctly. Path.Combine(baseDir, absPath) returns absPath in .NET Core.
            
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

        [TestMethod]
        public async Task Create_and_get_round_trip()
        {
            var def = new DashboardDefinition { Title = "Test 1" };
            var id = await _service.CreateAsync(def);
            
            id.Should().NotBeNullOrEmpty();
            def.Id.Should().Be(id);

            var retrieved = await _service.GetAsync(id);
            retrieved.Should().NotBeNull();
            retrieved!.Id.Should().Be(id);
            retrieved.Title.Should().Be("Test 1");
        }

        [TestMethod]
        public async Task Create_new_dashboard_has_empty_widgets_and_layout()
        {
            // #439: new dashboard must start as a clean empty canvas
            var def = new DashboardDefinition { Title = "Empty Canvas" };
            var id = await _service.CreateAsync(def);

            var retrieved = await _service.GetAsync(id);
            retrieved.Should().NotBeNull();

            retrieved!.Widgets.Should().NotBeNull("Widgets should be an empty dict, not null");
            retrieved.Widgets.Should().BeEmpty("New dashboard should have no widgets");
            retrieved.Layout.Should().NotBeNull("Layout should be an empty list, not null");
            retrieved.Layout.Should().BeEmpty("New dashboard should have no layout items");
            retrieved.RefreshInterval.Should().Be(60, "Default refresh interval should be 60 seconds");
        }

        [TestMethod]
        public async Task Get_returns_null_for_unknown_id()
        {
            var retrieved = await _service.GetAsync("unknown_id");
            retrieved.Should().BeNull();
        }

        [TestMethod]
        public async Task Update_modifies_existing_dashboard()
        {
            var def = new DashboardDefinition { Title = "Test 1" };
            var id = await _service.CreateAsync(def);

            def.Title = "Updated 1";
            await _service.UpdateAsync(def);

            var retrieved = await _service.GetAsync(id);
            retrieved.Should().NotBeNull();
            retrieved!.Title.Should().Be("Updated 1");
        }

        [TestMethod]
        public async Task Update_throws_for_unknown_id()
        {
            var def = new DashboardDefinition { Id = "unknown", Title = "Test 1" };
            Func<Task> act = async () => await _service.UpdateAsync(def);
            await act.Should().ThrowAsync<KeyNotFoundException>();
        }

        [TestMethod]
        public async Task Delete_removes_dashboard()
        {
            var def = new DashboardDefinition { Title = "Test 1" };
            var id = await _service.CreateAsync(def);

            await _service.DeleteAsync(id);

            var retrieved = await _service.GetAsync(id);
            retrieved.Should().BeNull();
        }

        [TestMethod]
        public async Task List_filters_by_owner()
        {
            await _service.CreateAsync(new DashboardDefinition { Owner = "user1", Title = "D1" });
            await _service.CreateAsync(new DashboardDefinition { Owner = "user2", Title = "D2" });

            var list = await _service.ListAsync("user1", Array.Empty<string>());
            list.Should().HaveCount(1);
            list[0].Title.Should().Be("D1");
        }

        [TestMethod]
        public async Task List_includes_public_dashboards()
        {
            await _service.CreateAsync(new DashboardDefinition { Owner = "user1", Title = "D1", Sharing = new SharingDefinition { Mode = "public" } });
            await _service.CreateAsync(new DashboardDefinition { Owner = "user2", Title = "D2" });

            var list = await _service.ListAsync("user3", Array.Empty<string>());
            list.Should().HaveCount(1);
            list[0].Title.Should().Be("D1");
        }

        [TestMethod]
        public async Task List_filters_by_role()
        {
            await _service.CreateAsync(new DashboardDefinition { Owner = "user1", Title = "D1", Sharing = new SharingDefinition { Roles = new List<string> { "Manager" } } });
            await _service.CreateAsync(new DashboardDefinition { Owner = "user2", Title = "D2" });

            var list = await _service.ListAsync("user3", new[] { "Manager" });
            list.Should().HaveCount(1);
            list[0].Title.Should().Be("D1");
        }

        [TestMethod]
        public async Task Admin_can_access_all_dashboards()
        {
            await _service.CreateAsync(new DashboardDefinition { Owner = "user1", Title = "D1" });
            await _service.CreateAsync(new DashboardDefinition { Owner = "user2", Title = "D2" });

            var list = await _service.ListAsync("user3", new[] { "Admin" });
            list.Should().HaveCount(2);
        }

        [TestMethod]
        public void CanEdit_only_owner_and_admin()
        {
            var def = new DashboardDefinition { Owner = "user1" };

            _service.CanEdit(def, "user1", Array.Empty<string>()).Should().BeTrue();
            _service.CanEdit(def, "user2", new[] { "Admin" }).Should().BeTrue();
            _service.CanEdit(def, "user2", Array.Empty<string>()).Should().BeFalse();
            _service.CanEdit(def, "user2", new[] { "Manager" }).Should().BeFalse();
        }

        [TestMethod]
        public async Task Tenant_isolation_separates_dashboards()
        {
            await _service.CreateAsync(new DashboardDefinition { Owner = "user1", Title = "Tenant A", TenantId = "tenantA" });
            await _service.CreateAsync(new DashboardDefinition { Owner = "user1", Title = "Tenant B", TenantId = "tenantB" });

            var listA = await _service.ListAsync("user1", Array.Empty<string>(), "tenantA");
            listA.Should().HaveCount(1);
            listA[0].Title.Should().Be("Tenant A");

            var listB = await _service.ListAsync("user1", Array.Empty<string>(), "tenantB");
            listB.Should().HaveCount(1);
            listB[0].Title.Should().Be("Tenant B");
        }

        [TestMethod]
        public async Task Default_tenant_dashboards_visible_without_tenant_filter()
        {
            await _service.CreateAsync(new DashboardDefinition { Owner = "user1", Title = "Default" });

            var list = await _service.ListAsync("user1", Array.Empty<string>());
            list.Should().HaveCount(1);
            list[0].Title.Should().Be("Default");
        }

        [TestMethod]
        public async Task Path_traversal_in_id_throws()
        {
            Func<Task> act = async () => await _service.CreateAsync(
                new DashboardDefinition { Id = "../../../etc/evil", Title = "hack" });
            await act.Should().ThrowAsync<ArgumentException>();
        }

        [TestMethod]
        public async Task Path_traversal_in_tenantId_throws()
        {
            Func<Task> act = async () => await _service.GetAsync("valid-id", "../other_tenant");
            await act.Should().ThrowAsync<ArgumentException>();
        }

        // ─── Widget data parameter bridging tests ─────────────────────────

        /// <summary>
        /// Captures the WidgetDataRequest passed to GetDataAsync for assertion.
        /// </summary>
        private class CapturingDataSource : IWidgetDataSource
        {
            public string Name => "test-capture";
            public WidgetDataSourceKind Kind => WidgetDataSourceKind.Custom;
            public WidgetDataRequest? CapturedRequest { get; private set; }

            public Task<WidgetDataResult> GetDataAsync(WidgetDataRequest request, CancellationToken ct = default)
            {
                CapturedRequest = request;
                return Task.FromResult(new WidgetDataResult { Value = 42 });
            }
        }

        private JsonFileDashboardService CreateServiceWithDataSource(IWidgetDataSource ds)
        {
            var options = Options.Create(new DashboardOptions { DashboardDirectory = _tempDir });
            return new JsonFileDashboardService(options, new[] { ds },
                Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonFileDashboardService>.Instance);
        }

        [TestMethod]
        public async Task GetWidgetData_bridges_source_ListVmType_into_parameters()
        {
            var capture = new CapturingDataSource();
            var svc = CreateServiceWithDataSource(capture);

            var def = new DashboardDefinition
            {
                Title = "Bridge Test",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = new WidgetDefinition
                    {
                        Type = "chart",
                        Title = "Test",
                        Source = new WidgetSourceDefinition
                        {
                            Kind = "custom",
                            Name = "test-capture",
                            ListVmType = "MyApp.ViewModels.OrderListVM",
                            Dimensions = new List<DimensionConfig>
                            {
                                new() { Field = "Region" },
                                new() { Field = "Date", Hierarchy = "month" }
                            },
                            Measures = new List<MeasureConfig>
                            {
                                new() { Field = "Amount", Func = "Sum" }
                            }
                        }
                    }
                }
            };

            await svc.CreateAsync(def);
            var result = await svc.GetWidgetDataAsync(def.Id, "w1");

            result.Value.Should().Be(42);
            capture.CapturedRequest.Should().NotBeNull();

            var p = capture.CapturedRequest!.Parameters;
            p.Should().ContainKey("listVmType");
            p["listVmType"].Should().Be("MyApp.ViewModels.OrderListVM");
            p.Should().ContainKey("dimensions");
            p.Should().ContainKey("measures");

            var dims = JsonSerializer.Deserialize<List<string>>(p["dimensions"]);
            dims.Should().Contain("Region");
            dims.Should().Contain("Date");
        }

        [TestMethod]
        public async Task GetWidgetData_caller_filters_not_overwritten_by_source()
        {
            var capture = new CapturingDataSource();
            var svc = CreateServiceWithDataSource(capture);

            var def = new DashboardDefinition
            {
                Title = "Filter Priority Test",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = new WidgetDefinition
                    {
                        Type = "kpi",
                        Title = "Test",
                        Source = new WidgetSourceDefinition
                        {
                            Name = "test-capture",
                            ListVmType = "Default.VM"
                        }
                    }
                }
            };

            await svc.CreateAsync(def);

            // Caller explicitly passes listVmType — should NOT be overwritten
            var callerFilters = new Dictionary<string, string> { ["listVmType"] = "Caller.VM" };
            await svc.GetWidgetDataAsync(def.Id, "w1", callerFilters);

            capture.CapturedRequest!.Parameters["listVmType"].Should().Be("Caller.VM",
                "caller-supplied filters should take priority over widget source defaults");
        }

        [TestMethod]
        public async Task GetWidgetData_falls_back_to_Kind_when_Name_is_null()
        {
            var ds = new CapturingDataSource();
            // Create a data source whose Name matches the Kind value
            var kindDs = new Mock<IWidgetDataSource>();
            kindDs.Setup(x => x.Name).Returns("custom");
            kindDs.Setup(x => x.Kind).Returns(WidgetDataSourceKind.Custom);
            kindDs.Setup(x => x.GetDataAsync(It.IsAny<WidgetDataRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WidgetDataResult { Value = 99 });

            var options = Options.Create(new DashboardOptions { DashboardDirectory = _tempDir });
            var svc = new JsonFileDashboardService(options, new IWidgetDataSource[] { kindDs.Object },
                Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonFileDashboardService>.Instance);

            var def = new DashboardDefinition
            {
                Title = "Kind Fallback Test",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = new WidgetDefinition
                    {
                        Type = "kpi",
                        Title = "Test",
                        Source = new WidgetSourceDefinition
                        {
                            Kind = "custom",
                            Name = null  // No explicit Name — should fall back to Kind
                        }
                    }
                }
            };

            await svc.CreateAsync(def);
            var result = await svc.GetWidgetDataAsync(def.Id, "w1");

            result.Value.Should().Be(99);
            kindDs.Verify(x => x.GetDataAsync(It.IsAny<WidgetDataRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        // ─── CanAccess 權限邏輯（#430） ───────────────────────────────────────

        [TestMethod]
        public void CanAccess_Admin_bypasses_all_checks()
        {
            var def = new DashboardDefinition { Owner = "other" };
            _service.CanAccess(def, "any_user", new[] { "Admin" }).Should().BeTrue();
        }

        [TestMethod]
        public void CanAccess_Admin_check_is_case_insensitive()
        {
            var def = new DashboardDefinition { Owner = "other" };
            _service.CanAccess(def, "any_user", new[] { "admin" }).Should().BeTrue();
            _service.CanAccess(def, "any_user", new[] { "ADMIN" }).Should().BeTrue();
        }

        [TestMethod]
        public void CanAccess_Owner_can_access_own_dashboard()
        {
            var def = new DashboardDefinition { Owner = "user1" };
            _service.CanAccess(def, "user1", Array.Empty<string>()).Should().BeTrue();
        }

        [TestMethod]
        public void CanAccess_NonOwner_NonAdmin_without_sharing_is_denied()
        {
            var def = new DashboardDefinition { Owner = "user1" };
            _service.CanAccess(def, "user2", Array.Empty<string>()).Should().BeFalse();
        }

        [TestMethod]
        public void CanAccess_public_dashboard_accessible_to_anyone()
        {
            var def = new DashboardDefinition
            {
                Owner = "user1",
                Sharing = new SharingDefinition { Mode = "public" }
            };
            _service.CanAccess(def, "user2", Array.Empty<string>()).Should().BeTrue();
        }

        [TestMethod]
        public void CanAccess_role_sharing_allows_matching_user()
        {
            var def = new DashboardDefinition
            {
                Owner = "user1",
                Sharing = new SharingDefinition
                {
                    Mode = "role",
                    Roles = new List<string> { "Manager", "Analyst" }
                }
            };
            _service.CanAccess(def, "user2", new[] { "Analyst" }).Should().BeTrue();
        }

        [TestMethod]
        public void CanAccess_role_sharing_denies_non_matching_user()
        {
            var def = new DashboardDefinition
            {
                Owner = "user1",
                Sharing = new SharingDefinition
                {
                    Mode = "role",
                    Roles = new List<string> { "Manager" }
                }
            };
            _service.CanAccess(def, "user2", new[] { "Analyst" }).Should().BeFalse();
        }

        [TestMethod]
        public void CanAccess_null_roles_does_not_throw_and_returns_false()
        {
            var def = new DashboardDefinition { Owner = "user1" };
            var act = () => _service.CanAccess(def, "user2", null!);
            act.Should().NotThrow();
            _service.CanAccess(def, "user2", null!).Should().BeFalse();
        }

        [TestMethod]
        public void CanAccess_empty_sharing_roles_list_returns_false()
        {
            var def = new DashboardDefinition
            {
                Owner = "user1",
                Sharing = new SharingDefinition
                {
                    Mode = "role",
                    Roles = new List<string>()
                }
            };
            _service.CanAccess(def, "user2", new[] { "Analyst" }).Should().BeFalse();
        }

        // ─── AdminRoles 配置化測試（#555） ─────────────────────────────────────

        private JsonFileDashboardService CreateServiceWithAdminRoles(params string[] adminRoles)
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var options = Options.Create(new DashboardOptions
            {
                DashboardDirectory = dir,
                AdminRoles = adminRoles
            });
            return new JsonFileDashboardService(options, Array.Empty<IWidgetDataSource>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonFileDashboardService>.Instance);
        }

        [TestMethod]
        public void CanAccess_custom_admin_role_grants_access()
        {
            var svc = CreateServiceWithAdminRoles("SystemAdmin");
            var def = new DashboardDefinition { Owner = "user1" };

            svc.CanAccess(def, "user2", new[] { "SystemAdmin" }).Should().BeTrue();
        }

        [TestMethod]
        public void CanAccess_default_Admin_role_no_longer_grants_when_not_in_AdminRoles()
        {
            var svc = CreateServiceWithAdminRoles("SystemAdmin");
            var def = new DashboardDefinition { Owner = "user1" };

            // "Admin" is not in AdminRoles anymore — should be denied
            svc.CanAccess(def, "user2", new[] { "Admin" }).Should().BeFalse();
        }

        [TestMethod]
        public void CanEdit_custom_admin_role_grants_edit()
        {
            var svc = CreateServiceWithAdminRoles("SuperAdmin");
            var def = new DashboardDefinition { Owner = "user1" };

            svc.CanEdit(def, "user2", new[] { "SuperAdmin" }).Should().BeTrue();
            svc.CanEdit(def, "user2", new[] { "Admin" }).Should().BeFalse();
        }

        [TestMethod]
        public void CanAccess_multiple_admin_roles_all_grant_access()
        {
            var svc = CreateServiceWithAdminRoles("Admin", "SystemAdmin", "超級管理員");
            var def = new DashboardDefinition { Owner = "user1" };

            svc.CanAccess(def, "user2", new[] { "Admin" }).Should().BeTrue();
            svc.CanAccess(def, "user2", new[] { "SystemAdmin" }).Should().BeTrue();
            svc.CanAccess(def, "user2", new[] { "超級管理員" }).Should().BeTrue();
            svc.CanAccess(def, "user2", new[] { "Viewer" }).Should().BeFalse();
        }

        [TestMethod]
        public async Task ListAsync_custom_admin_role_sees_all_dashboards()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            try
            {
                var options = Options.Create(new DashboardOptions
                {
                    DashboardDirectory = dir,
                    AdminRoles = ["SuperAdmin"]
                });
                var svc = new JsonFileDashboardService(options, Array.Empty<IWidgetDataSource>(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonFileDashboardService>.Instance);

                await svc.CreateAsync(new DashboardDefinition { Owner = "alice", Title = "Private" });
                await svc.CreateAsync(new DashboardDefinition { Owner = "bob", Title = "Also Private" });

                var list = await svc.ListAsync("carol", new[] { "SuperAdmin" });
                list.Should().HaveCount(2);

                // "Admin" role should NOT see all dashboards when not in AdminRoles
                var listAdmin = await svc.ListAsync("carol", new[] { "Admin" });
                listAdmin.Should().BeEmpty();
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}