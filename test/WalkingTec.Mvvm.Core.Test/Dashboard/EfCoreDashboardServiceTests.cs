#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Dashboard;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    /// <summary>
    /// EfCoreDashboardService round-trip tests using SQLite shared in-memory.
    /// SQLite is preferred over EF InMemory so that cascade deletes, unique
    /// constraints, and LINQ-translatable queries behave like a real DB.
    /// </summary>
    [TestClass]
    public class EfCoreDashboardServiceTests : IDisposable
    {
        private SqliteConnection _keepAlive = null!;
        private IDbContextFactory<DashboardDbContext> _dbFactory = null!;

        [TestInitialize]
        public void Setup()
        {
            var dbName = $"DashboardEfTest_{Guid.NewGuid():N}";
            var cs = $"DataSource={dbName}?mode=memory&cache=shared";

            // Keep-alive connection holds the shared in-memory DB alive across factory calls.
            _keepAlive = new SqliteConnection(cs);
            _keepAlive.Open();

            var optionsBuilder = new DbContextOptionsBuilder<DashboardDbContext>();
            optionsBuilder.UseSqlite(cs);

            // Create schema once via EnsureCreated.
            using (var db = new DashboardDbContext(optionsBuilder.Options))
            {
                db.Database.EnsureCreated();
            }

            _dbFactory = new TestDbContextFactory(optionsBuilder.Options);
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive?.Dispose();
        }

        public void Dispose() => Cleanup();

        private EfCoreDashboardService BuildService(
            DashboardOptions? opts = null,
            IEnumerable<IWidgetDataSource>? dataSources = null)
        {
            var options = Options.Create(opts ?? new DashboardOptions());
            return new EfCoreDashboardService(
                _dbFactory,
                options,
                dataSources ?? Array.Empty<IWidgetDataSource>(),
                NullLogger<EfCoreDashboardService>.Instance);
        }

        // ── CRUD round-trips ──────────────────────────────────────────────────

        [TestMethod]
        public async Task Create_and_get_round_trip()
        {
            var svc = BuildService();
            var def = new DashboardDefinition
            {
                Title = "Sales Overview",
                Owner = "alice"
            };

            var id = await svc.CreateAsync(def);

            id.Should().NotBeNullOrEmpty();
            def.Id.Should().Be(id);

            var retrieved = await svc.GetAsync(id);
            retrieved.Should().NotBeNull();
            retrieved!.Id.Should().Be(id);
            retrieved.Title.Should().Be("Sales Overview");
            retrieved.Owner.Should().Be("alice");
        }

        [TestMethod]
        public async Task Create_assigns_guid_when_id_empty()
        {
            var svc = BuildService();
            var def = new DashboardDefinition { Title = "Auto ID", Owner = "bob" };
            var id = await svc.CreateAsync(def);

            id.Should().HaveLength(32, "Guid.NewGuid().ToString(\"N\") is 32 hex chars");
        }

        [TestMethod]
        public async Task Get_returns_null_for_unknown_id()
        {
            var svc = BuildService();
            var result = await svc.GetAsync("nonexistent-id");
            result.Should().BeNull();
        }

        [TestMethod]
        public async Task Create_duplicate_id_throws()
        {
            var svc = BuildService();
            var def = new DashboardDefinition { Id = "dup-id", Title = "D1", Owner = "alice" };
            await svc.CreateAsync(def);

            var def2 = new DashboardDefinition { Id = "dup-id", Title = "D2", Owner = "alice" };
            Func<Task> act = () => svc.CreateAsync(def2);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [TestMethod]
        public async Task Update_roundtrip()
        {
            var svc = BuildService();
            var def = new DashboardDefinition { Title = "Original", Owner = "alice" };
            var id = await svc.CreateAsync(def);

            def.Title = "Updated";
            await svc.UpdateAsync(def);

            var retrieved = await svc.GetAsync(id);
            retrieved!.Title.Should().Be("Updated");
        }

        [TestMethod]
        public async Task Update_notfound_throws()
        {
            var svc = BuildService();
            var def = new DashboardDefinition { Id = "missing", Title = "X", Owner = "alice" };
            Func<Task> act = () => svc.UpdateAsync(def);
            await act.Should().ThrowAsync<KeyNotFoundException>();
        }

        [TestMethod]
        public async Task Update_missing_id_throws()
        {
            var svc = BuildService();
            var def = new DashboardDefinition { Id = "", Title = "X", Owner = "alice" };
            Func<Task> act = () => svc.UpdateAsync(def);
            await act.Should().ThrowAsync<ArgumentException>();
        }

        [TestMethod]
        public async Task Delete_removes_dashboard_and_is_idempotent()
        {
            var svc = BuildService();
            var def = new DashboardDefinition { Title = "ToDelete", Owner = "alice" };
            var id = await svc.CreateAsync(def);

            await svc.DeleteAsync(id);
            var result = await svc.GetAsync(id);
            result.Should().BeNull();

            // Second delete must not throw.
            Func<Task> act = () => svc.DeleteAsync(id);
            await act.Should().NotThrowAsync();
        }

        // ── Widget persistence ────────────────────────────────────────────────

        [TestMethod]
        public async Task Widgets_are_persisted_and_retrieved()
        {
            var svc = BuildService();
            var def = new DashboardDefinition
            {
                Title = "With Widgets",
                Owner = "alice",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = new WidgetDefinition
                    {
                        Type = "chart",
                        Title = "Revenue",
                        Source = new WidgetSourceDefinition { Kind = "custom" }
                    }
                }
            };

            var id = await svc.CreateAsync(def);
            var retrieved = await svc.GetAsync(id);

            retrieved!.Widgets.Should().ContainKey("w1");
            retrieved.Widgets["w1"].Type.Should().Be("chart");
            retrieved.Widgets["w1"].Title.Should().Be("Revenue");
        }

        [TestMethod]
        public async Task Update_replaces_widgets_atomically()
        {
            var svc = BuildService();
            var def = new DashboardDefinition
            {
                Title = "Widget Replace Test",
                Owner = "alice",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = new WidgetDefinition { Type = "kpi", Title = "Old KPI", Source = new WidgetSourceDefinition() }
                }
            };
            var id = await svc.CreateAsync(def);

            // Replace widgets entirely.
            def.Widgets = new Dictionary<string, WidgetDefinition>
            {
                ["w2"] = new WidgetDefinition { Type = "table", Title = "New Table", Source = new WidgetSourceDefinition() }
            };
            await svc.UpdateAsync(def);

            var retrieved = await svc.GetAsync(id);
            retrieved!.Widgets.Should().NotContainKey("w1", "old widget should be gone");
            retrieved.Widgets.Should().ContainKey("w2");
        }

        // ── Delete cascades to widgets ────────────────────────────────────────

        [TestMethod]
        public async Task Delete_removes_associated_widgets()
        {
            var svc = BuildService();
            var def = new DashboardDefinition
            {
                Title = "Cascade Test",
                Owner = "alice",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = new WidgetDefinition { Type = "kpi", Title = "K", Source = new WidgetSourceDefinition() }
                }
            };
            var id = await svc.CreateAsync(def);
            await svc.DeleteAsync(id);

            // Dashboard gone.
            var retrieved = await svc.GetAsync(id);
            retrieved.Should().BeNull();

            // Widget rows should also be gone (cascade).
            await using var db = await _dbFactory.CreateDbContextAsync();
            var widgetCount = await db.WidgetRecords.CountAsync(w => w.DashboardId == id);
            widgetCount.Should().Be(0);
        }

        // ── Validation at write time ──────────────────────────────────────────

        [TestMethod]
        public async Task Create_rejects_widget_with_empty_type()
        {
            var svc = BuildService();
            var def = new DashboardDefinition
            {
                Title = "Validation Test",
                Owner = "alice",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = new WidgetDefinition { Type = "" } // empty type
                }
            };

            Func<Task> act = () => svc.CreateAsync(def);
            await act.Should().ThrowAsync<ArgumentException>();
        }

        [TestMethod]
        public async Task Create_rejects_analysis_widget_without_listVmType()
        {
            var svc = BuildService();
            var def = new DashboardDefinition
            {
                Title = "Validation Test 2",
                Owner = "alice",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = new WidgetDefinition
                    {
                        Type = "chart",
                        Source = new WidgetSourceDefinition { Kind = "analysis" } // missing ListVmType
                    }
                }
            };

            Func<Task> act = () => svc.CreateAsync(def);
            await act.Should().ThrowAsync<ArgumentException>();
        }

        // ── #948: caller-supplied AllowPrivateNetwork/AllowHttp is rejected at write time ──
        // Mirrors DashboardWidgetConfigValidationTests in DashboardReliabilityTests.cs (the
        // JsonFileDashboardService equivalent) — kept in sync deliberately, same as the
        // production ValidateWidgetConfigs copies themselves.

        private static DashboardDefinition RestWidgetDashboard(RestWidgetDataSourceOptions restOptions, string title = "SSRF Test") =>
            new()
            {
                Title = title,
                Owner = "alice",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = new WidgetDefinition
                    {
                        Type = "chart",
                        Source = new WidgetSourceDefinition { Kind = "rest", RestOptions = restOptions }
                    }
                }
            };

        [TestMethod]
        public async Task Create_rejects_rest_widget_with_AllowPrivateNetwork_true()
        {
            var svc = BuildService();
            var def = RestWidgetDashboard(new RestWidgetDataSourceOptions
            {
                Url = "https://10.0.0.1/internal-api",
                AllowPrivateNetwork = true
            });

            Func<Task> act = () => svc.CreateAsync(def);
            (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("AllowPrivateNetwork");
        }

        [TestMethod]
        public async Task Create_rejects_rest_widget_with_AllowHttp_true()
        {
            var svc = BuildService();
            var def = RestWidgetDashboard(new RestWidgetDataSourceOptions
            {
                Url = "http://8.8.8.8/data",
                AllowHttp = true
            });

            Func<Task> act = () => svc.CreateAsync(def);
            (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("AllowHttp");
        }

        [TestMethod]
        public async Task Update_rejects_rest_widget_with_AllowPrivateNetwork_true()
        {
            var svc = BuildService();
            var def = RestWidgetDashboard(new RestWidgetDataSourceOptions { Url = "https://8.8.8.8/data" });
            var id = await svc.CreateAsync(def);

            def.Id = id;
            def.Widgets["w1"].Source.RestOptions = new RestWidgetDataSourceOptions
            {
                Url = "https://169.254.169.254/latest/meta-data/",
                AllowPrivateNetwork = true
            };

            Func<Task> act = () => svc.UpdateAsync(def);
            (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("AllowPrivateNetwork");
        }

        /// <summary>
        /// Positive control (per issue #948's test plan): a legitimately configured public
        /// HTTPS destination, with neither security-sensitive flag set, must still be
        /// accepted.
        /// </summary>
        [TestMethod]
        public async Task Create_accepts_rest_widget_with_public_https_destination_and_no_security_flags()
        {
            var svc = BuildService();
            var def = RestWidgetDashboard(new RestWidgetDataSourceOptions { Url = "https://8.8.8.8/public-api" });

            var id = await svc.CreateAsync(def);
            id.Should().NotBeNullOrEmpty("a legitimately public HTTPS REST widget must still be accepted");
        }

        // ── #955 review finding F5: AllowedPorts null/empty is rejected at write time ──
        // AllowedPorts lives on the same caller-controlled RestOptions object as
        // AllowPrivateNetwork/AllowHttp; sending "allowedPorts": null turns off
        // RestWidgetDataSource's port allowlist entirely without touching either boolean.

        [TestMethod]
        public async Task Create_rejects_rest_widget_with_null_AllowedPorts()
        {
            var svc = BuildService();
            var def = RestWidgetDashboard(new RestWidgetDataSourceOptions
            {
                Url = "https://8.8.8.8/data",
                AllowedPorts = null
            });

            Func<Task> act = () => svc.CreateAsync(def);
            (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("AllowedPorts");
        }

        [TestMethod]
        public async Task Create_rejects_rest_widget_with_empty_AllowedPorts()
        {
            var svc = BuildService();
            var def = RestWidgetDashboard(new RestWidgetDataSourceOptions
            {
                Url = "https://8.8.8.8/data",
                AllowedPorts = Array.Empty<int>()
            });

            Func<Task> act = () => svc.CreateAsync(def);
            (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("AllowedPorts");
        }

        [TestMethod]
        public async Task Create_accepts_rest_widget_with_default_AllowedPorts()
        {
            // Omitting AllowedPorts entirely leaves RestWidgetDataSourceOptions' own
            // non-null default ({80,443,8080,8443}) in place — must not be rejected.
            var svc = BuildService();
            var def = RestWidgetDashboard(new RestWidgetDataSourceOptions { Url = "https://8.8.8.8/data" });

            var id = await svc.CreateAsync(def);
            id.Should().NotBeNullOrEmpty();
        }

        // ── #956: hard-rejected header names / count / size are rejected at WRITE time ──
        // Mirrors JsonFileDashboardService's DashboardWidgetConfigValidationTests (see
        // DashboardReliabilityTests.cs) against EfCoreDashboardService.

        [TestMethod]
        [DataRow("Host")]
        [DataRow("Transfer-Encoding")]
        [DataRow("Content-Length")]
        [DataRow("Connection")]
        [DataRow("Upgrade")]
        [DataRow("TE")]
        [DataRow("Trailer")]
        [DataRow("Expect")]
        [DataRow("Proxy-Authorization")]
        public async Task Create_rejects_rest_widget_with_hard_rejected_header_name(string headerName)
        {
            var svc = BuildService();
            var def = RestWidgetDashboard(new RestWidgetDataSourceOptions
            {
                Url = "https://8.8.8.8/data",
                Headers = new Dictionary<string, string> { [headerName] = "x" }
            });

            Func<Task> act = () => svc.CreateAsync(def);
            (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain(headerName);
        }

        [TestMethod]
        public async Task Create_rejects_rest_widget_with_too_many_headers()
        {
            var svc = BuildService();
            var headers = new Dictionary<string, string>();
            for (int i = 0; i < 21; i++) { headers[$"X-Custom-{i}"] = "v"; }
            var def = RestWidgetDashboard(new RestWidgetDataSourceOptions
            {
                Url = "https://8.8.8.8/data",
                Headers = headers
            });

            Func<Task> act = () => svc.CreateAsync(def);
            (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("header count");
        }

        [TestMethod]
        public async Task Create_rejects_rest_widget_with_headers_exceeding_total_size()
        {
            var svc = BuildService();
            var def = RestWidgetDashboard(new RestWidgetDataSourceOptions
            {
                Url = "https://8.8.8.8/data",
                Headers = new Dictionary<string, string> { ["X-Big"] = new string('v', 9 * 1024) }
            });

            Func<Task> act = () => svc.CreateAsync(def);
            (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("total header");
        }

        /// <summary>Positive control (issue #956) — mirrors JsonFileDashboardService's.</summary>
        [TestMethod]
        public async Task Create_accepts_rest_widget_with_Authorization_and_custom_header()
        {
            var svc = BuildService();
            var def = RestWidgetDashboard(new RestWidgetDataSourceOptions
            {
                Url = "https://8.8.8.8/data",
                Headers = new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer real-token",
                    ["X-Api-Key"] = "real-key"
                }
            });

            var id = await svc.CreateAsync(def);
            id.Should().NotBeNullOrEmpty();
        }

        // ── #955 review finding F6: legacy request-supplied "options" channel ────────
        // Mirrors JsonFileDashboardServiceTests — a "rest" widget with no persisted
        // RestOptions must no longer honour a caller-supplied "options" parameter at all.

        private class UnreachableRestDataSource : IWidgetDataSource
        {
            public string Name => "rest";
            public WidgetDataSourceKind Kind => WidgetDataSourceKind.Rest;
            public bool WasCalled { get; private set; }

            public Task<WidgetDataResult> GetDataAsync(WidgetDataRequest request, CancellationToken ct = default)
            {
                WasCalled = true;
                return Task.FromResult(new WidgetDataResult { Value = "should never be reached" });
            }
        }

        [TestMethod]
        public async Task GetWidgetDataAsync_rejects_request_supplied_options_for_rest_widget_without_persisted_RestOptions()
        {
            var stubRest = new UnreachableRestDataSource();
            var svc = BuildService(dataSources: new IWidgetDataSource[] { stubRest });

            var def = new DashboardDefinition
            {
                Title = "Legacy REST Widget",
                Owner = "alice",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = new WidgetDefinition
                    {
                        Type = "chart",
                        Source = new WidgetSourceDefinition { Kind = "rest" } // no RestOptions persisted
                    }
                }
            };
            await svc.CreateAsync(def);

            var requestFilters = new Dictionary<string, string>
            {
                ["options"] = JsonSerializer.Serialize(new RestWidgetDataSourceOptions { Url = "https://8.8.8.8/data" })
            };

            Func<Task> act = () => svc.GetWidgetDataAsync(def.Id, "w1", requestFilters);
            await act.Should().ThrowAsync<InvalidOperationException>();
            stubRest.WasCalled.Should().BeFalse(
                "the request must be rejected before ever reaching the underlying data source");
        }

        // ── Listing + access control ──────────────────────────────────────────

        [TestMethod]
        public async Task List_shows_own_and_public_dashboards()
        {
            var svc = BuildService();

            var priv = new DashboardDefinition
            {
                Title = "Private", Owner = "alice",
                Sharing = new SharingDefinition { Mode = "private" }
            };
            var pub = new DashboardDefinition
            {
                Title = "Public", Owner = "bob",
                Sharing = new SharingDefinition { Mode = "public" }
            };
            await svc.CreateAsync(priv);
            await svc.CreateAsync(pub);

            var list = await svc.ListAsync("alice", Array.Empty<string>());

            var ids = list.Select(s => s.Title).ToList();
            ids.Should().Contain("Private", "alice owns it");
            ids.Should().Contain("Public", "public dashboards are visible to everyone");
        }

        [TestMethod]
        public async Task List_admin_sees_all()
        {
            var opts = new DashboardOptions { AdminRoles = new[] { "Admin" } };
            var svc = BuildService(opts);

            await svc.CreateAsync(new DashboardDefinition
            {
                Title = "Secret", Owner = "alice",
                Sharing = new SharingDefinition { Mode = "private" }
            });

            var list = await svc.ListAsync("bob", new[] { "Admin" });
            list.Select(s => s.Title).Should().Contain("Secret");
        }

        // ── CanAccess / CanEdit ───────────────────────────────────────────────

        [TestMethod]
        public void CanAccess_owner_returns_true()
        {
            var svc = BuildService();
            var def = new DashboardDefinition { Owner = "alice", Sharing = new SharingDefinition { Mode = "private" } };
            svc.CanAccess(def, "alice", Array.Empty<string>()).Should().BeTrue();
        }

        [TestMethod]
        public void CanAccess_public_dashboard_returns_true()
        {
            var svc = BuildService();
            var def = new DashboardDefinition { Owner = "alice", Sharing = new SharingDefinition { Mode = "public" } };
            svc.CanAccess(def, "stranger", Array.Empty<string>()).Should().BeTrue();
        }

        [TestMethod]
        public void CanEdit_non_owner_returns_false()
        {
            var svc = BuildService();
            var def = new DashboardDefinition { Owner = "alice", Sharing = new SharingDefinition { Mode = "private" } };
            svc.CanEdit(def, "bob", Array.Empty<string>()).Should().BeFalse();
        }

        // ── DrillDownLink model ───────────────────────────────────────────────

        [TestMethod]
        public async Task DrillDownLink_is_persisted_and_retrieved()
        {
            var svc = BuildService();
            var def = new DashboardDefinition
            {
                Title = "DrillDown Test",
                Owner = "alice",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["sourceWidget"] = new WidgetDefinition
                    {
                        Type = "chart",
                        Title = "Source Chart",
                        Source = new WidgetSourceDefinition { Kind = "custom" },
                        DrillDown = new List<DrillDownLink>
                        {
                            new DrillDownLink
                            {
                                SourceField = "Region",
                                TargetFilterId = "regionFilter",
                                TargetWidgetId = "detailWidget"
                            }
                        }
                    }
                }
            };

            var id = await svc.CreateAsync(def);
            var retrieved = await svc.GetAsync(id);

            retrieved!.Widgets.Should().ContainKey("sourceWidget");
            var drillDown = retrieved.Widgets["sourceWidget"].DrillDown;
            drillDown.Should().NotBeNull().And.HaveCount(1);
            drillDown![0].SourceField.Should().Be("Region");
            drillDown[0].TargetFilterId.Should().Be("regionFilter");
            drillDown[0].TargetWidgetId.Should().Be("detailWidget");
        }

        [TestMethod]
        public void DrillDownLink_null_targetWidgetId_is_valid()
        {
            // TargetWidgetId is optional — null means "apply to global FilterBar, re-fetch all".
            var link = new DrillDownLink
            {
                SourceField = "Category",
                TargetFilterId = "catFilter",
                TargetWidgetId = null
            };
            link.TargetWidgetId.Should().BeNull();
        }

        // ── Timing ───────────────────────────────────────────────────────────

        [TestMethod]
        public async Task Create_stamps_createdAt_and_updatedAt()
        {
            var svc = BuildService();
            var before = DateTime.UtcNow.AddSeconds(-1);
            var def = new DashboardDefinition { Title = "Timestamp Test", Owner = "alice" };
            var id = await svc.CreateAsync(def);
            var after = DateTime.UtcNow.AddSeconds(1);

            var retrieved = await svc.GetAsync(id);
            retrieved!.CreatedAt.Should().BeAfter(before).And.BeBefore(after);
            retrieved.UpdatedAt.Should().BeAfter(before).And.BeBefore(after);
        }

        // ── Tenant isolation (IDOR security tests) ────────────────────────────
        //
        // These tests prove the three security findings are fixed.
        // Each test verifies that WITHOUT the fix the operation would succeed
        // (cross-tenant access), and WITH the fix it is denied.

        /// <summary>
        /// Finding 1: GetAsync must return null when called with the wrong tenantId.
        /// A TenantB caller must NOT be able to read TenantA's dashboard by ID.
        /// </summary>
        [TestMethod]
        public async Task GetAsync_CrossTenant_Returns_Null()
        {
            var svc = BuildService();

            // TenantA creates a private dashboard.
            var defA = new DashboardDefinition { Title = "TenantA Secret", Owner = "alice", TenantId = "tenantA" };
            var idA = await svc.CreateAsync(defA);

            // TenantB tries to read TenantA's dashboard — must get null.
            var result = await svc.GetAsync(idA, "tenantB");
            result.Should().BeNull("TenantB must not see TenantA's dashboard");

            // TenantA itself can still read it.
            var own = await svc.GetAsync(idA, "tenantA");
            own.Should().NotBeNull("TenantA must still be able to read its own dashboard");
        }

        /// <summary>
        /// Finding 2a: UpdateAsync with the wrong tenantId must throw KeyNotFoundException.
        /// A TenantB caller must NOT be able to overwrite TenantA's dashboard.
        /// </summary>
        [TestMethod]
        public async Task UpdateAsync_CrossTenant_Throws_KeyNotFound()
        {
            var svc = BuildService();

            // TenantA creates a dashboard.
            var defA = new DashboardDefinition { Title = "TenantA Dashboard", Owner = "alice", TenantId = "tenantA" };
            var idA = await svc.CreateAsync(defA);

            // TenantB tries to update it by guessing the ID and spoofing tenantId.
            var spoofed = new DashboardDefinition
            {
                Id = idA,
                Title = "Hijacked",
                Owner = "eve",
                TenantId = "tenantB"   // attacker supplies tenantB
            };

            Func<Task> act = () => svc.UpdateAsync(spoofed);
            await act.Should().ThrowAsync<KeyNotFoundException>(
                "UpdateAsync must not find TenantA's record when called with TenantB");

            // TenantA's record must be untouched.
            var unchanged = await svc.GetAsync(idA, "tenantA");
            unchanged!.Title.Should().Be("TenantA Dashboard");
        }

        /// <summary>
        /// Finding 2b: UpdateAsync must not allow a caller to change an existing record's
        /// TenantId or Owner — even when both the lookup tenant and the stored tenant match.
        /// </summary>
        [TestMethod]
        public async Task UpdateAsync_Cannot_Change_TenantId_Or_Owner()
        {
            var svc = BuildService();

            var def = new DashboardDefinition { Title = "Original", Owner = "alice", TenantId = "tenantA" };
            var id = await svc.CreateAsync(def);

            // Attempt to mutate TenantId and Owner via UpdateAsync.
            var mutation = new DashboardDefinition
            {
                Id = id,
                Title = "Changed Title",
                Owner = "attacker",       // attempt to steal ownership
                TenantId = "tenantA"      // same tenant — lookup will succeed
            };
            await svc.UpdateAsync(mutation);

            var after = await svc.GetAsync(id, "tenantA");
            after!.Title.Should().Be("Changed Title", "title update must persist");
            after.Owner.Should().Be("alice", "Owner must be immutable via UpdateAsync");
            after.TenantId.Should().Be("tenantA", "TenantId must be immutable via UpdateAsync");
        }

        /// <summary>
        /// Finding 3: DeleteAsync(id, tenantId) must be a no-op when called with the wrong
        /// tenantId — TenantB must NOT be able to delete TenantA's dashboard.
        /// </summary>
        [TestMethod]
        public async Task DeleteAsync_CrossTenant_Is_NoOp()
        {
            var svc = BuildService();

            // TenantA creates a dashboard.
            var defA = new DashboardDefinition { Title = "Protected Dashboard", Owner = "alice", TenantId = "tenantA" };
            var idA = await svc.CreateAsync(defA);

            // TenantB attempts to delete TenantA's dashboard — must be a no-op.
            await svc.DeleteAsync(idA, "tenantB");

            // TenantA's dashboard must still exist.
            var stillThere = await svc.GetAsync(idA, "tenantA");
            stillThere.Should().NotBeNull("TenantB delete must not affect TenantA's record");
        }

        /// <summary>
        /// Finding 3 (positive case): DeleteAsync(id, tenantId) with the correct tenantId
        /// must still delete the record.
        /// </summary>
        [TestMethod]
        public async Task DeleteAsync_SameTenant_Deletes_Record()
        {
            var svc = BuildService();

            var def = new DashboardDefinition { Title = "To Delete", Owner = "alice", TenantId = "tenantA" };
            var id = await svc.CreateAsync(def);

            await svc.DeleteAsync(id, "tenantA");

            var gone = await svc.GetAsync(id, "tenantA");
            gone.Should().BeNull("same-tenant delete must remove the record");
        }

        // ── Widget data (#843: TenantId bridging) ──────────────────────────────

        /// <summary>Captures the WidgetDataRequest passed to GetDataAsync for assertion.</summary>
        private sealed class CapturingDataSource : IWidgetDataSource
        {
            public string Name => "test-capture";
            public WidgetDataSourceKind Kind => WidgetDataSourceKind.Custom;
            public WidgetDataRequest? CapturedRequest { get; private set; }

            public Task<WidgetDataResult> GetDataAsync(WidgetDataRequest request, System.Threading.CancellationToken ct = default)
            {
                CapturedRequest = request;
                return Task.FromResult(new WidgetDataResult { Value = 42 });
            }
        }

        /// <summary>
        /// #843: WidgetDataRequest.TenantId existed as a field but was never populated by either
        /// dashboard service — AnalysisWidgetDataSource had no way to know which tenant a
        /// background job's widget belonged to, so it could not scope the DataContext correctly
        /// for no-HttpContext execution. Proves the tenant-aware GetWidgetDataAsync overload now
        /// threads tenantId all the way into the WidgetDataRequest handed to the data source.
        /// </summary>
        [TestMethod]
        public async Task GetWidgetData_bridges_tenantId_into_request()
        {
            var capture = new CapturingDataSource();
            var svc = BuildService(dataSources: new IWidgetDataSource[] { capture });

            var def = new DashboardDefinition
            {
                Title = "Tenant Bridge Test",
                Owner = "alice",
                TenantId = "tenantA",
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
                            ListVmType = "MyApp.ViewModels.OrderListVM"
                        }
                    }
                }
            };

            var id = await svc.CreateAsync(def);
            await svc.GetWidgetDataAsync(id, "w1", null, "tenantA");

            capture.CapturedRequest.Should().NotBeNull();
            capture.CapturedRequest!.TenantId.Should().Be("tenantA",
                "#843: the tenant-aware GetWidgetDataAsync overload must bridge tenantId into WidgetDataRequest.TenantId.");
        }

        // ── Minimal private DbContextFactory ─────────────────────────────────

        private sealed class TestDbContextFactory : IDbContextFactory<DashboardDbContext>
        {
            private readonly DbContextOptions<DashboardDbContext> _options;
            public TestDbContextFactory(DbContextOptions<DashboardDbContext> options)
                => _options = options;

            public DashboardDbContext CreateDbContext()
                => new DashboardDbContext(_options);

            public Task<DashboardDbContext> CreateDbContextAsync(
                System.Threading.CancellationToken cancellationToken = default)
                => Task.FromResult(new DashboardDbContext(_options));
        }
    }
}
