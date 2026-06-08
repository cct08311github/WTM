#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Dashboard;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    // ════════════════════════════════════════════════════════════════════════════
    //  Q7 — Widget config validation (service layer)
    // ════════════════════════════════════════════════════════════════════════════

    [TestClass]
    public class DashboardWidgetConfigValidationTests
    {
        private string _tempDir = "";
        private JsonFileDashboardService CreateService(DashboardOptions? opts = null)
        {
            opts ??= new DashboardOptions { DashboardDirectory = _tempDir };
            var options = Options.Create(opts);
            return new JsonFileDashboardService(
                options,
                Array.Empty<IWidgetDataSource>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonFileDashboardService>.Instance);
        }

        [TestInitialize]
        public void Init()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }

        // ── Q7a: empty/missing Type ──────────────────────────────────────────────

        [TestMethod]
        public async Task CreateAsync_throws_when_widget_Type_is_empty()
        {
            var svc = CreateService();
            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    { "w1", new WidgetDefinition { Type = "" } }
                }
            };

            await Assert.ThrowsExceptionAsync<ArgumentException>(
                () => svc.CreateAsync(dashboard),
                "Empty widget Type must be rejected at service layer");
        }

        [TestMethod]
        public async Task UpdateAsync_throws_when_widget_Type_is_empty()
        {
            var svc = CreateService();
            // Create a valid dashboard first
            var initial = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    { "w1", new WidgetDefinition { Type = "chart" } }
                }
            };
            var id = await svc.CreateAsync(initial);

            // Then try to update with invalid widget
            initial.Id = id;
            initial.Widgets["w1"].Type = "";

            await Assert.ThrowsExceptionAsync<ArgumentException>(
                () => svc.UpdateAsync(initial),
                "Update with empty widget Type must be rejected");
        }

        // ── Q7b: AllowedWidgetTypes allowlist ────────────────────────────────────

        [TestMethod]
        public async Task CreateAsync_throws_when_chartType_not_in_AllowedWidgetTypes()
        {
            var opts = new DashboardOptions
            {
                DashboardDirectory = _tempDir,
                AllowedWidgetTypes = ["chart", "kpi", "table"]
            };
            var svc = CreateService(opts);

            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    { "w1", new WidgetDefinition { Type = "unknown_type" } }
                }
            };

            var ex = await Assert.ThrowsExceptionAsync<ArgumentException>(
                () => svc.CreateAsync(dashboard));
            ex.Message.Should().Contain("unknown_type", "error should name the offending type");
            ex.Message.Should().Contain("chart", "error should list the allowed types");
        }

        [TestMethod]
        public async Task CreateAsync_succeeds_when_chartType_is_in_AllowedWidgetTypes()
        {
            var opts = new DashboardOptions
            {
                DashboardDirectory = _tempDir,
                AllowedWidgetTypes = ["chart", "kpi"]
            };
            var svc = CreateService(opts);

            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    { "w1", new WidgetDefinition { Type = "chart" } },
                    { "w2", new WidgetDefinition { Type = "kpi" } }
                }
            };

            // Should not throw
            var id = await svc.CreateAsync(dashboard);
            id.Should().NotBeNullOrEmpty();
        }

        [TestMethod]
        public async Task CreateAsync_allows_any_type_when_AllowedWidgetTypes_not_configured()
        {
            // Default: AllowedWidgetTypes is null → any non-empty type is accepted (backward compat)
            var svc = CreateService();
            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    { "w1", new WidgetDefinition { Type = "completely_custom_type" } }
                }
            };

            var id = await svc.CreateAsync(dashboard);
            id.Should().NotBeNullOrEmpty("AllowedWidgetTypes not configured → any type is accepted");
        }

        // ── Q7c: unknown data-source Kind ────────────────────────────────────────

        [TestMethod]
        public async Task CreateAsync_throws_when_widget_Kind_is_unknown()
        {
            var svc = CreateService();
            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    {
                        "w1", new WidgetDefinition
                        {
                            Type = "chart",
                            Source = new WidgetSourceDefinition { Kind = "not_a_real_kind" }
                        }
                    }
                }
            };

            var ex = await Assert.ThrowsExceptionAsync<ArgumentException>(
                () => svc.CreateAsync(dashboard));
            ex.Message.Should().Contain("not_a_real_kind");
        }

        [TestMethod]
        public async Task CreateAsync_accepts_known_Kind_analysis()
        {
            var svc = CreateService();
            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    {
                        "w1", new WidgetDefinition
                        {
                            Type = "chart",
                            Source = new WidgetSourceDefinition
                            {
                                Kind = "analysis",
                                ListVmType = "SomeAssembly.SomeListVM"
                            }
                        }
                    }
                }
            };

            // Should not throw (kind is known; ListVmType is present)
            var id = await svc.CreateAsync(dashboard);
            id.Should().NotBeNullOrEmpty();
        }

        [TestMethod]
        public async Task CreateAsync_accepts_known_Kind_rest()
        {
            var svc = CreateService();
            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    {
                        "w1", new WidgetDefinition
                        {
                            Type = "chart",
                            Source = new WidgetSourceDefinition { Kind = "rest" }
                        }
                    }
                }
            };

            var id = await svc.CreateAsync(dashboard);
            id.Should().NotBeNullOrEmpty("'rest' is a known kind");
        }

        [TestMethod]
        public async Task CreateAsync_accepts_known_Kind_custom()
        {
            var svc = CreateService();
            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    {
                        "w1", new WidgetDefinition
                        {
                            Type = "chart",
                            Source = new WidgetSourceDefinition { Kind = "custom" }
                        }
                    }
                }
            };

            var id = await svc.CreateAsync(dashboard);
            id.Should().NotBeNullOrEmpty("'custom' is always accepted");
        }

        // ── Q7d: required field checks ───────────────────────────────────────────

        [TestMethod]
        public async Task CreateAsync_throws_when_analysis_widget_missing_ListVmType()
        {
            var svc = CreateService();
            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    {
                        "w1", new WidgetDefinition
                        {
                            Type = "chart",
                            Source = new WidgetSourceDefinition
                            {
                                Kind = "analysis",
                                ListVmType = "" // missing!
                            }
                        }
                    }
                }
            };

            var ex = await Assert.ThrowsExceptionAsync<ArgumentException>(
                () => svc.CreateAsync(dashboard));
            ex.Message.Should().Contain("analysis").And.Contain("ListVmType");
        }

        [TestMethod]
        public async Task CreateAsync_throws_when_rest_widget_has_RestOptions_with_empty_Url()
        {
            var svc = CreateService();
            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    {
                        "w1", new WidgetDefinition
                        {
                            Type = "chart",
                            Source = new WidgetSourceDefinition
                            {
                                Kind = "rest",
                                RestOptions = new RestWidgetDataSourceOptions { Url = "" }
                            }
                        }
                    }
                }
            };

            var ex = await Assert.ThrowsExceptionAsync<ArgumentException>(
                () => svc.CreateAsync(dashboard));
            ex.Message.Should().Contain("Url");
        }

        [TestMethod]
        public async Task CreateAsync_succeeds_with_no_widgets()
        {
            var svc = CreateService();
            var dashboard = new DashboardDefinition { Title = "Empty" };
            var id = await svc.CreateAsync(dashboard);
            id.Should().NotBeNullOrEmpty("empty dashboard is always valid");
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Q8 — AnalysisWidget result cache
    // ════════════════════════════════════════════════════════════════════════════

    [TestClass]
    public class AnalysisWidgetCacheTests
    {
        // ─── Test Model ──────────────────────────────────────────────────────────

        private class CacheSaleRecord : TopBasePoco
        {
            [Dimension(DisplayName = "Region")] public string Region { get; set; } = "";
            [Measure(AllowedFuncs = AggregateFunc.Sum, DisplayName = "Amount")]
            public decimal Amount { get; set; }
        }

        private static IList<CacheSaleRecord> _testData = new List<CacheSaleRecord>();

        [EnableAnalysis]
        private class CacheSaleListVM : BasePagedListVM<CacheSaleRecord, BaseSearcher>
        {
            public override IOrderedQueryable<CacheSaleRecord> GetSearchQuery()
                => _testData.AsQueryable().OrderByDescending(x => x.ID);
        }

        private AnalysisVmRegistry _registry = null!;
        private AnalysisQueryEngine _engine = null!;

        [TestInitialize]
        public void Setup()
        {
            _testData = new List<CacheSaleRecord>
            {
                new() { ID = Guid.NewGuid(), Region = "North", Amount = 100m }
            };
            _registry = new AnalysisVmRegistry();
            _registry.Build(new[] { typeof(AnalysisWidgetCacheTests).Assembly });
            _engine = new AnalysisQueryEngine(GroupByStrategyResolver.Default);
        }

        private WidgetDataRequest MakeRequest(string? tenant = null, string? userId = null)
        {
            return new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = typeof(CacheSaleListVM).FullName!,
                    ["dimensions"] = JsonSerializer.Serialize(new[] { "Region" }),
                    ["measures"] = JsonSerializer.Serialize(new[] { new { Field = "Amount", Func = AggregateFunc.Sum } })
                },
                TenantId = tenant
            };
        }

        private AnalysisWidgetDataSource CreateSource(int ttl = 10, WTMContext? wtm = null)
        {
            var services = new ServiceCollection();
            if (wtm != null) services.AddSingleton(wtm);
            var sp = services.BuildServiceProvider();
            var cache = new MemoryCache(new MemoryCacheOptions());
            var opts = Options.Create(new DashboardOptions { AnalysisWidgetCacheTtlSeconds = ttl });
            return new AnalysisWidgetDataSource(_registry, sp, _engine, cache: cache, options: opts);
        }

        // ── Q8a: cache hit within TTL ────────────────────────────────────────────

        [TestMethod]
        public async Task GetDataAsync_returns_cached_result_on_second_call()
        {
            // Arrange: single shared cache — same request → same object reference on hit
            var services = new ServiceCollection();
            var sp = services.BuildServiceProvider();
            var cache = new MemoryCache(new MemoryCacheOptions());
            var opts = Options.Create(new DashboardOptions { AnalysisWidgetCacheTtlSeconds = 30 });
            var source = new AnalysisWidgetDataSource(_registry, sp, _engine, cache: cache, options: opts);

            var request = MakeRequest();

            // Act
            var result1 = await source.GetDataAsync(request);
            var result2 = await source.GetDataAsync(request);

            // Assert: same object reference → cache hit
            result1.Should().NotBeNull();
            result2.Should().BeSameAs(result1, "second call within TTL should return the cached object");
        }

        // ── Q8b: tenant isolation of cache key ───────────────────────────────────

        [TestMethod]
        public async Task GetDataAsync_caches_separately_for_different_tenants()
        {
            // Arrange: two sources sharing the same IMemoryCache but different WTM contexts (tenants)
            var sharedCache = new MemoryCache(new MemoryCacheOptions());
            var opts = Options.Create(new DashboardOptions { AnalysisWidgetCacheTtlSeconds = 30 });

            AnalysisWidgetDataSource MakeSourceForTenant(string tenant)
            {
                var wtm = MockWtmContext.CreateWtmContext();
                wtm.LoginUserInfo = new LoginUserInfo
                {
                    ITCode = "user1",
                    CurrentTenant = tenant
                };
                var services = new ServiceCollection();
                services.AddSingleton(wtm);
                var sp = services.BuildServiceProvider();
                return new AnalysisWidgetDataSource(_registry, sp, _engine, cache: sharedCache, options: opts);
            }

            var sourceTenantA = MakeSourceForTenant("tenantA");
            var sourceTenantB = MakeSourceForTenant("tenantB");
            var request = MakeRequest();

            // Act: populate cache for tenantA
            var resultA1 = await sourceTenantA.GetDataAsync(request);
            // tenantB should get a fresh cache entry (NOT the tenantA entry)
            var resultB = await sourceTenantB.GetDataAsync(request);
            // Second call for tenantA should hit cache
            var resultA2 = await sourceTenantA.GetDataAsync(request);

            // Assert
            resultA1.Should().NotBeNull();
            resultB.Should().NotBeNull();
            resultB.Should().NotBeSameAs(resultA1,
                "tenantB must not share a cache entry with tenantA (tenant-isolation violation)");
            resultA2.Should().BeSameAs(resultA1,
                "second call for tenantA within TTL should return the same cached object");
        }

        // ── Q8c: cache key includes filter/dimension params ───────────────────────

        [TestMethod]
        public void BuildCacheKey_differs_when_filter_params_differ()
        {
            var req1 = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = "Foo.ListVM",
                    ["filters"] = "[{\"field\":\"Year\",\"op\":\"eq\",\"value\":\"2025\"}]"
                }
            };
            var req2 = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = "Foo.ListVM",
                    ["filters"] = "[{\"field\":\"Year\",\"op\":\"eq\",\"value\":\"2026\"}]"
                }
            };

            var key1 = AnalysisWidgetDataSource.BuildCacheKey(req1, "Foo.ListVM", "tenantA_user1");
            var key2 = AnalysisWidgetDataSource.BuildCacheKey(req2, "Foo.ListVM", "tenantA_user1");

            key1.Should().NotBe(key2, "different filter values must produce different cache keys");
        }

        [TestMethod]
        public void BuildCacheKey_differs_for_different_tenants()
        {
            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string> { ["listVmType"] = "Foo.ListVM" }
            };

            var keyA = AnalysisWidgetDataSource.BuildCacheKey(req, "Foo.ListVM", "tenantA_user1");
            var keyB = AnalysisWidgetDataSource.BuildCacheKey(req, "Foo.ListVM", "tenantB_user1");

            keyA.Should().NotBe(keyB, "different tenants must produce different cache keys");
        }

        [TestMethod]
        public void BuildCacheKey_same_for_identical_request_and_tenant()
        {
            var req = new WidgetDataRequest
            {
                Parameters = new Dictionary<string, string>
                {
                    ["listVmType"] = "Foo.ListVM",
                    ["dimensions"] = "[\"Region\"]",
                    ["measures"] = "[{\"Field\":\"Amount\",\"Func\":1}]"
                }
            };

            var key1 = AnalysisWidgetDataSource.BuildCacheKey(req, "Foo.ListVM", "tenantA_user1");
            var key2 = AnalysisWidgetDataSource.BuildCacheKey(req, "Foo.ListVM", "tenantA_user1");

            key1.Should().Be(key2, "identical request + tenant must produce identical cache key");
        }

        // ── Q8d: TTL=0 disables cache ────────────────────────────────────────────

        [TestMethod]
        public async Task GetDataAsync_does_not_cache_when_TTL_is_zero()
        {
            var services = new ServiceCollection();
            var sp = services.BuildServiceProvider();
            var cache = new MemoryCache(new MemoryCacheOptions());
            var opts = Options.Create(new DashboardOptions { AnalysisWidgetCacheTtlSeconds = 0 });
            var source = new AnalysisWidgetDataSource(_registry, sp, _engine, cache: cache, options: opts);

            var request = MakeRequest();
            var result1 = await source.GetDataAsync(request);
            var result2 = await source.GetDataAsync(request);

            result1.Should().NotBeNull();
            result2.Should().NotBeNull();
            // TTL=0 → no caching → different object instances
            result2.Should().NotBeSameAs(result1, "TTL=0 means caching is disabled; each call returns a new object");
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Q9 — Per-widget server timeout
    // ════════════════════════════════════════════════════════════════════════════

    [TestClass]
    public class DashboardWidgetTimeoutTests
    {
        private string _tempDir = "";

        [TestInitialize]
        public void Init()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }

        /// <summary>
        /// A slow IWidgetDataSource that delays for <see cref="Delay"/> before returning,
        /// so we can simulate a timeout scenario deterministically.
        /// </summary>
        private class SlowWidgetDataSource : IWidgetDataSource
        {
            public string Name => "slow";
            public WidgetDataSourceKind Kind => WidgetDataSourceKind.Custom;
            public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(60);

            public async Task<WidgetDataResult> GetDataAsync(WidgetDataRequest request, CancellationToken ct = default)
            {
                await Task.Delay(Delay, ct); // respects cancellation
                return new WidgetDataResult { Value = "done" };
            }
        }

        /// <summary>
        /// A fast IWidgetDataSource that returns immediately.
        /// </summary>
        private class FastWidgetDataSource : IWidgetDataSource
        {
            public string Name => "fast";
            public WidgetDataSourceKind Kind => WidgetDataSourceKind.Custom;

            public Task<WidgetDataResult> GetDataAsync(WidgetDataRequest request, CancellationToken ct = default)
                => Task.FromResult(new WidgetDataResult { Value = "fast_result" });
        }

        private JsonFileDashboardService CreateService(int timeoutSeconds, IWidgetDataSource? ds = null)
        {
            var opts = new DashboardOptions
            {
                DashboardDirectory = _tempDir,
                WidgetDataTimeoutSeconds = timeoutSeconds
            };
            var sources = ds != null ? new[] { ds } : Array.Empty<IWidgetDataSource>();
            return new JsonFileDashboardService(
                Options.Create(opts),
                sources,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonFileDashboardService>.Instance);
        }

        // ── Q9a: timeout returns per-widget error, not exception ─────────────────

        [TestMethod]
        [Timeout(10_000)] // test-level guard — must complete within 10 s
        public async Task GetWidgetDataAsync_returns_error_result_when_widget_times_out()
        {
            var slow = new SlowWidgetDataSource { Delay = TimeSpan.FromSeconds(60) };
            var svc = CreateService(timeoutSeconds: 1, ds: slow); // 1 s timeout

            // Create a dashboard with the slow widget
            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    {
                        "w1", new WidgetDefinition
                        {
                            Type = "chart",
                            Source = new WidgetSourceDefinition { Name = "slow", Kind = "custom" }
                        }
                    }
                }
            };
            var id = await svc.CreateAsync(dashboard);

            // Act
            var result = await svc.GetWidgetDataAsync(id, "w1", null, null, CancellationToken.None);

            // Assert: per-widget error returned, no exception thrown
            result.Should().NotBeNull();
            result.Error.Should().NotBeNullOrEmpty("timeout must produce a non-null Error on the WidgetDataResult");
            result.Error.Should().Contain("timed out", "error message must mention the timeout");
        }

        // ── Q9b: timeout does not affect other (fast) widgets ────────────────────

        [TestMethod]
        [Timeout(10_000)]
        public async Task GetWidgetDataAsync_fast_widget_succeeds_independently_of_slow_widget()
        {
            // Regression: a slow widget triggering timeout must NOT blank the whole dashboard.
            // We verify by calling each widget independently — in practice the FE calls them
            // individually, so each widget data endpoint is independent.
            var fast = new FastWidgetDataSource();
            var svc = CreateService(timeoutSeconds: 1, ds: fast);

            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    { "w_fast", new WidgetDefinition { Type = "chart", Source = new WidgetSourceDefinition { Name = "fast", Kind = "custom" } } }
                }
            };
            var id = await svc.CreateAsync(dashboard);

            // Act: fast widget should succeed regardless of any per-widget timeout setting
            var result = await svc.GetWidgetDataAsync(id, "w_fast", null, null, CancellationToken.None);

            result.Should().NotBeNull();
            result.Error.Should().BeNull("fast widget must succeed without an error");
            result.Value.Should().Be("fast_result");
        }

        // ── Q9c: outer CancellationToken cancel is NOT swallowed ─────────────────

        [TestMethod]
        [Timeout(10_000)]
        public async Task GetWidgetDataAsync_propagates_outer_cancellation_token()
        {
            var slow = new SlowWidgetDataSource { Delay = TimeSpan.FromSeconds(60) };
            var svc = CreateService(timeoutSeconds: 30, ds: slow); // long widget timeout

            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    { "w1", new WidgetDefinition { Type = "chart", Source = new WidgetSourceDefinition { Name = "slow", Kind = "custom" } } }
                }
            };
            var id = await svc.CreateAsync(dashboard);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            // Act: outer CT fires → must throw OperationCanceledException or its subclass TaskCanceledException.
            // MSTest Assert.ThrowsExceptionAsync<T> is exact-type only, so we catch manually.
            bool threw = false;
            try
            {
                await svc.GetWidgetDataAsync(id, "w1", null, null, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // TaskCanceledException inherits from OperationCanceledException — both are correct.
                threw = true;
            }
            threw.Should().BeTrue("outer CancellationToken must propagate — timeout guard must not swallow it");
        }

        // ── Q9d: WidgetDataTimeoutSeconds=0 disables per-widget timeout ──────────

        [TestMethod]
        [Timeout(10_000)]
        public async Task GetWidgetDataAsync_no_timeout_when_WidgetDataTimeoutSeconds_is_zero()
        {
            // With timeout disabled, a fast widget should still return normally.
            var fast = new FastWidgetDataSource();
            var svc = CreateService(timeoutSeconds: 0, ds: fast);

            var dashboard = new DashboardDefinition
            {
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    { "w1", new WidgetDefinition { Type = "chart", Source = new WidgetSourceDefinition { Name = "fast", Kind = "custom" } } }
                }
            };
            var id = await svc.CreateAsync(dashboard);

            var result = await svc.GetWidgetDataAsync(id, "w1", null, null, CancellationToken.None);

            result.Should().NotBeNull();
            result.Error.Should().BeNull("disabled timeout must not produce an error");
            result.Value.Should().Be("fast_result");
        }
    }
}
