#nullable enable
// #727 regression test — audit of the #721 IDataContext→NullContext DI-resolution gap,
// applied to LookupCacheWarmupService.
//
// LookupCacheWarmupService is registered UNCONDITIONALLY by AddWtmContext (not opt-in) — every
// WTM application gets this hosted service. Before #727 it resolved
// scope.ServiceProvider.GetService<IDataContext>() as DbContext directly. AddWtmContext only
// ever registers services.TryAddScoped<IDataContext, NullContext>() as a placeholder default,
// and NullContext does NOT extend DbContext, so the `as DbContext` cast silently produced null
// in every real deployment — the warm-up pass always logged "skipped: EF Core DbContext not
// available" and quietly did nothing, on every single application boot, for every WTM app with
// any [CacheLookup(WarmOnStartup = true)] type. No prior test exercised this hosted service at
// all (see LookupCacheWarmupService — zero pre-#727 test files reference it), so the silent
// no-op was never caught.
//
// LookupCacheWarmupService.ExecuteAsync is protected and reflection-heavy (scans warm-up types
// via ILookupCacheService.GetWarmupTypes()), so a full end-to-end integration test would need a
// real [CacheLookup]-attributed entity wired into this test assembly's model. Instead, this test
// targets the exact private resolution helper that was broken — ResolveDataContext — directly
// via reflection, proving: (a) when WTMContext.CreateDC() returns a real DbContext, it is used
// and marked owned; (b) when WTMContext is unavailable, it falls back to raw IDataContext DI
// resolution (matching the DI-fallback path #721/#727 use throughout the framework).

using System;
using System.Reflection;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Cache;

namespace WalkingTec.Mvvm.Core.Test.Cache
{
    [TestClass]
    public class LookupCacheWarmupServiceProdDiTests
    {
        private sealed class FakeWtmContext : WTMContext
        {
            private readonly IDataContext? _dc;
            public FakeWtmContext(IDataContext? dc) : base(null) => _dc = dc;
            public override IDataContext? CreateDC(bool isLog = false, string? cskey = null, bool logerror = true)
                => _dc;
        }

        /// <summary>Minimal real DbContext + IDataContext double — enough to prove the resolved
        /// object round-trips as a genuine DbContext, not a NullContext no-op.</summary>
        private sealed class FakeDbContext : DbContext, IDataContext
        {
            protected override void OnConfiguring(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder b)
                => b.UseInMemoryDatabase(Guid.NewGuid().ToString());

            public string? TenantCode => null;
            public bool IsFake { get; set; }
            public bool IsDebug { get; set; }
            public bool EnableSensitiveQueryLogging { get; set; }
            public string? CurrentUserCode { get; set; }
            public string CSName { get; set; } = string.Empty;
            public DBTypeEnum DBType { get; set; } = DBTypeEnum.Memory;
            public void AddEntity<T>(T entity) where T : TopBasePoco => throw new NotSupportedException();
            public void UpdateEntity<T>(T entity) where T : TopBasePoco => throw new NotSupportedException();
            public void DeleteEntity<T>(T entity) where T : TopBasePoco => throw new NotSupportedException();
            public void CascadeDelete<T>(T entity) where T : TreePoco => throw new NotSupportedException();
            public void UpdateProperty<T>(T entity, System.Linq.Expressions.Expression<Func<T, object>> fieldExp) where T : TopBasePoco => throw new NotSupportedException();
            public void UpdateProperty<T>(T entity, string fieldName) where T : TopBasePoco => throw new NotSupportedException();
            public System.Threading.Tasks.Task<bool> DataInit(object? allModel, bool isSpa) => System.Threading.Tasks.Task.FromResult(false);
            public void EnsureCreate() { }
            public IDataContext CreateNew() => throw new NotSupportedException();
            public IDataContext ReCreate(Microsoft.Extensions.Logging.ILoggerFactory? logger = null) => throw new NotSupportedException();
            public System.Data.DataTable RunSP(string command, params object[] paras) => throw new NotSupportedException();
            public System.Collections.Generic.IEnumerable<TElement> RunSP<TElement>(string command, params object[] paras) => throw new NotSupportedException();
            public System.Data.DataTable RunSQL(string command, params object[] paras) => throw new NotSupportedException();
            public System.Collections.Generic.IEnumerable<TElement> RunSQL<TElement>(string sql, params object[] paras) => throw new NotSupportedException();
            public System.Data.DataTable Run(string sql, System.Data.CommandType commandType, params object[] paras) => throw new NotSupportedException();
            public System.Collections.Generic.IEnumerable<TElement> Run<TElement>(string sql, System.Data.CommandType commandType, params object[] paras) => throw new NotSupportedException();
            public object CreateCommandParameter(string name, object value, System.Data.ParameterDirection dir) => throw new NotSupportedException();
            public void SetLoggerFactory(Microsoft.Extensions.Logging.ILoggerFactory factory) { }
            public void SetTenantCode(string? tc) { }
        }

        private static (object? Dc, bool Owned) InvokeResolveDataContext(IServiceProvider sp)
        {
            var method = typeof(LookupCacheWarmupService).GetMethod(
                "ResolveDataContext", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "LookupCacheWarmupService.ResolveDataContext not found — signature changed?");
            var result = method.Invoke(null, new object?[] { sp })!;
            var t = result.GetType();
            var dc = t.GetField("Item1")!.GetValue(result);
            var owned = (bool)t.GetField("Item2")!.GetValue(result)!;
            return (dc, owned);
        }

        [TestMethod]
        public void ResolveDataContext_WtmContextAvailable_ReturnsRealDbContext_Owned()
        {
            var services = new ServiceCollection();
            services.TryAddScoped<IDataContext, NullContext>();
            var realDc = new FakeDbContext();
            services.AddScoped<WTMContext>(_ => new FakeWtmContext(realDc));
            using var scope = services.BuildServiceProvider().CreateScope();

            var (dc, owned) = InvokeResolveDataContext(scope.ServiceProvider);

            Assert.IsNotNull(dc);
            Assert.IsInstanceOfType(dc, typeof(DbContext),
                "Before #727 this path always resolved NullContext (not a DbContext), so callers " +
                "silently treated the warm-up as unavailable.");
            Assert.AreSame(realDc, dc);
            Assert.IsTrue(owned, "A DataContext obtained via WTMContext.CreateDC() must be owned/disposed by the caller.");
        }

        [TestMethod]
        public void ResolveDataContext_NoWtmContext_FallsBackToDiIDataContext()
        {
            var services = new ServiceCollection();
            var fallbackDc = new FakeDbContext();
            services.AddScoped<IDataContext>(_ => fallbackDc);
            // No WTMContext registered — mirrors host/test setups that re-register IDataContext
            // directly without going through WTMContext (the DI-fallback masking pattern).
            using var scope = services.BuildServiceProvider().CreateScope();

            var (dc, owned) = InvokeResolveDataContext(scope.ServiceProvider);

            Assert.AreSame(fallbackDc, dc);
            Assert.IsFalse(owned, "The DI-fallback instance's lifetime is owned by the caller's scope, not by us.");
        }

        [TestMethod]
        public void ResolveDataContext_ProdLikeDI_NullContextOnly_ReturnsNullDbContext()
        {
            // The exact pre-#727 production shape: only AddWtmContext's placeholder registered,
            // no WTMContext, no consumer override. Before #727 this was the ONLY path every real
            // WTM app took, and warm-up always silently skipped.
            var services = new ServiceCollection();
            services.TryAddScoped<IDataContext, NullContext>();
            using var scope = services.BuildServiceProvider().CreateScope();

            var (dc, owned) = InvokeResolveDataContext(scope.ServiceProvider);

            Assert.IsNull(dc, "NullContext is not a DbContext — the caller (ExecuteAsync) must " +
                "detect this and log the documented skip-warning, not throw.");
            Assert.IsFalse(owned);
        }
    }
}
