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
    }
}
