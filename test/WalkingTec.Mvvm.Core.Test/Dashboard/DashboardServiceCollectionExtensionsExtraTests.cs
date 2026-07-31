using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Dashboard;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    /// <summary>
    /// Additional tests to raise coverage of DashboardServiceCollectionExtensions
    /// to ≥75%: covers setupAction, partial-registration error, GroupByStrategyResolver
    /// singleton guard, AddWidgetDataSource&lt;T&gt;, and AddWidgetDataSourcesFromAssembly.
    /// </summary>
    [TestClass]
    public class DashboardServiceCollectionExtensionsExtraTests
    {
        // ── DashboardOptions via setupAction ─────────────────────────────────

        [TestMethod]
        public void AddWtmDashboard_with_setupAction_configures_DashboardOptions()
        {
            var services = new ServiceCollection();
            services.AddWtmDashboard(opt =>
            {
                opt.EnableEditing = false;
                opt.DefaultRefreshInterval = 120;
            });
            var provider = services.BuildServiceProvider();
            var opts = provider.GetRequiredService<IOptions<DashboardOptions>>().Value;
            Assert.IsFalse(opts.EnableEditing);
            Assert.AreEqual(120, opts.DefaultRefreshInterval);
        }

        [TestMethod]
        public void AddWtmDashboard_without_setupAction_uses_default_DashboardOptions()
        {
            var services = new ServiceCollection();
            services.AddWtmDashboard();
            var provider = services.BuildServiceProvider();
            var opts = provider.GetRequiredService<IOptions<DashboardOptions>>().Value;
            // Default instance — should not throw
            Assert.IsNotNull(opts);
        }

        // ── Partial Analysis Mode registration error ─────────────────────────

        [TestMethod]
        public void AddWtmDashboard_only_AnalysisVmRegistry_throws_InvalidOperationException()
        {
            var services = new ServiceCollection();
            services.AddSingleton<AnalysisVmRegistry>(); // only one dep
            Assert.ThrowsException<InvalidOperationException>(() =>
                services.AddWtmDashboard());
        }

        [TestMethod]
        public void AddWtmDashboard_only_AnalysisQueryEngine_throws_InvalidOperationException()
        {
            var services = new ServiceCollection();
            services.AddSingleton<AnalysisQueryEngine>(); // only the other dep
            Assert.ThrowsException<InvalidOperationException>(() =>
                services.AddWtmDashboard());
        }

        // ── GroupByStrategyResolver singleton guard ───────────────────────────

        [TestMethod]
        public void AddWtmDashboard_does_not_duplicate_GroupByStrategyResolver()
        {
            var services = new ServiceCollection();
            // Pre-register the resolver
            services.AddSingleton(GroupByStrategyResolver.Default);
            services.AddWtmDashboard();
            var provider = services.BuildServiceProvider();
            var resolvers = provider.GetServices<GroupByStrategyResolver>().ToList();
            Assert.AreEqual(1, resolvers.Count,
                "GroupByStrategyResolver should not be registered twice");
        }

        // ── AddWidgetDataSource<T> ────────────────────────────────────────────

        private class TestWidgetDataSource : IWidgetDataSource
        {
            public string Name => "test";
            public WidgetDataSourceKind Kind => WidgetDataSourceKind.Custom;
            public System.Threading.Tasks.Task<WidgetDataResult> GetDataAsync(
                WidgetDataRequest request,
                System.Threading.CancellationToken ct = default)
                => System.Threading.Tasks.Task.FromResult(new WidgetDataResult());
        }

        [TestMethod]
        public void AddWidgetDataSource_registers_custom_data_source()
        {
            var services = new ServiceCollection();
            services.AddWidgetDataSource<TestWidgetDataSource>();
            var provider = services.BuildServiceProvider();
            var sources = provider.GetServices<IWidgetDataSource>().ToList();
            Assert.IsTrue(sources.Any(s => s is TestWidgetDataSource),
                "TestWidgetDataSource should be registered");
        }

        [TestMethod]
        public void AddWidgetDataSource_returns_same_services_collection_for_chaining()
        {
            var services = new ServiceCollection();
            var returned = services.AddWidgetDataSource<TestWidgetDataSource>();
            Assert.AreSame(services, returned);
        }

        // ── AddWidgetDataSourcesFromAssembly ─────────────────────────────────

        [TestMethod]
        public void AddWidgetDataSourcesFromAssembly_registers_implementations_in_assembly()
        {
            var services = new ServiceCollection();
            // Use this test assembly — TestWidgetDataSource implements IWidgetDataSource
            services.AddWidgetDataSourcesFromAssembly(Assembly.GetExecutingAssembly());
            var provider = services.BuildServiceProvider();
            var sources = provider.GetServices<IWidgetDataSource>().ToList();
            Assert.IsTrue(sources.Any(s => s is TestWidgetDataSource),
                "TestWidgetDataSource from this assembly should be registered");
        }

        [TestMethod]
        public void AddWidgetDataSourcesFromAssembly_returns_same_services_collection_for_chaining()
        {
            var services = new ServiceCollection();
            var returned = services.AddWidgetDataSourcesFromAssembly(Assembly.GetExecutingAssembly());
            Assert.AreSame(services, returned);
        }

        [TestMethod]
        public void AddWidgetDataSourcesFromAssembly_empty_assembly_does_not_throw()
        {
            // Build a dynamic assembly with no IWidgetDataSource types
            var asmBuilder = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("EmptyTestAsm"),
                System.Reflection.Emit.AssemblyBuilderAccess.Run);
            var services = new ServiceCollection();
            // No types → should just return silently
            services.AddWidgetDataSourcesFromAssembly(asmBuilder);
            var provider = services.BuildServiceProvider();
            Assert.AreEqual(0, provider.GetServices<IWidgetDataSource>().Count());
        }

        // ── #948 review finding F1: AddWtmDashboardEgressPolicy<T> DI lifetime ────
        // A registration helper whose lifetime is wrong is invisible until someone boots
        // the app — RestWidgetDataSource is Transient, but it is only ever constructed
        // once, at root-container scope, as part of building the Singleton
        // IDashboardService's IEnumerable<IWidgetDataSource> constructor dependency. A
        // Scoped IDashboardEgressPolicy is therefore a captive-dependency error: ASP.NET
        // Core's own DI validation (ValidateScopes/ValidateOnBuild — on by default under
        // Host.CreateDefaultBuilder in Development, see demo/WalkingTec.Mvvm.Demo/Program.cs)
        // makes the host fail to start outright. Deleting "AddSingleton" and reverting to
        // "AddScoped" in AddWtmDashboardEgressPolicy<T> (DashboardServiceCollectionExtensions.cs)
        // turns this test red with exactly that AggregateException.

        [TestMethod]
        public void AddWtmDashboardEgressPolicy_BuildsCleanly_WithScopeAndBuildValidationEnabled()
        {
            var services = new ServiceCollection();
            services.AddWtmDashboard();
            services.AddWtmDashboardEgressPolicy<DiLifetimeProbeEgressPolicy>();

            // This is exactly the validation ASP.NET Core's Host.CreateDefaultBuilder turns
            // on by default under the Development environment — a captive-dependency /
            // scoped-from-singleton registration throws here, at Build() time, not later.
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

            // Resolving from within a real scope (not the root provider) is the shape a
            // request actually uses — must succeed, not just "Build() didn't throw".
            using var scope = provider.CreateScope();
            var dashboardService = scope.ServiceProvider.GetRequiredService<IDashboardService>();
            Assert.IsNotNull(dashboardService);

            var resolvedPolicy = scope.ServiceProvider.GetRequiredService<IDashboardEgressPolicy>();
            Assert.IsInstanceOfType(resolvedPolicy, typeof(DiLifetimeProbeEgressPolicy));
        }

        [TestMethod]
        public void AddWtmDashboardEgressPolicy_RegistersSingleton_SameInstanceAcrossScopes()
        {
            var services = new ServiceCollection();
            services.AddWtmDashboard();
            services.AddWtmDashboardEgressPolicy<DiLifetimeProbeEgressPolicy>();
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

            using var scope1 = provider.CreateScope();
            using var scope2 = provider.CreateScope();
            var a = scope1.ServiceProvider.GetRequiredService<IDashboardEgressPolicy>();
            var b = scope2.ServiceProvider.GetRequiredService<IDashboardEgressPolicy>();

            Assert.AreSame(a, b, "AddWtmDashboardEgressPolicy<T> must register Singleton — " +
                "one instance shared across scopes/requests, not one per scope.");
        }

        private sealed class DiLifetimeProbeEgressPolicy : IDashboardEgressPolicy
        {
            public Task<bool> IsAllowedAsync(DashboardEgressDestination destination, CancellationToken ct = default)
                => Task.FromResult(false);
        }

        // ── #948 review finding F2: the built-in ConfiguredAllowlistDashboardEgressPolicy
        // must also register cleanly under the same DI validation as any custom policy —
        // it is registered through the exact same AddWtmDashboardEgressPolicy<T>() helper.

        [TestMethod]
        public void AddWtmDashboardEgressPolicy_WithBuiltInAllowlistPolicy_BuildsCleanly_WithScopeAndBuildValidationEnabled()
        {
            var services = new ServiceCollection();
            services.AddWtmDashboard();
            services.Configure<DashboardEgressAllowlistOptions>(opt =>
                opt.Entries.Add(new DashboardEgressAllowlistEntry { Host = "10.1.2.3", Ports = new[] { 8080 } }));
            services.AddWtmDashboardEgressPolicy<ConfiguredAllowlistDashboardEgressPolicy>();

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

            using var scope = provider.CreateScope();
            var dashboardService = scope.ServiceProvider.GetRequiredService<IDashboardService>();
            Assert.IsNotNull(dashboardService);
        }
    }
}
