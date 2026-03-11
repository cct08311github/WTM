using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            
            _service = new JsonFileDashboardService(options, Array.Empty<IWidgetDataSource>());
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
    }
}