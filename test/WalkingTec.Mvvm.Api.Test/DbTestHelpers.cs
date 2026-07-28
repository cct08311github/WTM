using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Services;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Issue #837 patch 2 — before this, test/WalkingTec.Mvvm.Api.Test/ had never touched the
/// database: <c>grep -rn "DataContext|DbContext|Set&lt;" test/WalkingTec.Mvvm.Api.Test/</c>
/// only hit comments, and the existing UpdateModelProperty tests used
/// <see cref="Guid.NewGuid()"/> fake ids and asserted on HTTP status codes alone — a request
/// that never found a row (e.g. because the blocklist check was silently deleted and the
/// endpoint fell through to its "Entity not found" guard for an unrelated reason) looked
/// identical to a request that was correctly rejected. These helpers open a DI scope off a
/// <see cref="WebApplicationFactory{TEntryPoint}"/>'s <c>Services</c>, resolve a real
/// <see cref="IDataContext"/> — the same one <c>_FrameworkController</c>/<c>BaseVM</c> use in
/// production — and let a test seed a real row before the HTTP call and read the persisted
/// state back afterward, through a *fresh* scope/context instance so the read-back is a real
/// database read, not the change-tracked instance the write went through.
///
/// <para>
/// <b>Resolution: <see cref="IWtmDataContextFactory"/>, never <c>GetRequiredService&lt;IDataContext&gt;()</c>.</b>
/// <c>FrameworkServiceExtension.cs</c> deliberately registers
/// <c>services.TryAddScoped&lt;IDataContext, NullContext&gt;()</c> as a trap (#721/#727 pitfall
/// guard) — every member of <see cref="NullContext"/> throws
/// <see cref="NotImplementedException"/> except the handful needed to fail fast. Resolving
/// <see cref="IDataContext"/> straight from DI, which is the obvious thing to try, gets you that
/// stub. The canonical, HttpContext-free way to get a real context is
/// <see cref="IWtmDataContextFactory.CreateDC"/> — the same logic
/// <c>WTMContext.CreateDC</c>/<c>FrameworkTenant.CreateDC</c> use in a live request, factored out
/// specifically so it works with no <c>HttpContext</c> at all (see its own doc comment: "All
/// state is passed explicitly so the factory remains stateless and testable").
/// </para>
///
/// <para>
/// <b>demo.db residual guard:</b> <see cref="DemoWebApplicationFactory"/> points ContentRoot
/// at the demo project, whose "default" connection is the physical file
/// <c>demo/WalkingTec.Mvvm.Demo/demo.db</c> (<c>SyncDb=true</c> creates it on first run if
/// missing, but never migrates an existing file's schema). If a build's <c>bin/</c> output
/// carries a demo.db copied from an older schema version, EF's <c>EnsureCreated</c> sees a
/// database that already "exists" and skips creation — every test in this assembly then fails
/// with "no such column" for reasons unrelated to what any individual test exercises, and the
/// gate looks red for a defect that isn't there. Before trusting a red run in this assembly,
/// run <c>find . -name 'demo.db*' -path '*bin*' -delete</c> from the repo root and re-run.
/// </para>
///
/// <para>
/// <b>Isolation:</b> per the note on
/// <c>GetFileName_EnforceFlagEnabled_Forbid_Is302RedirectNotLiteral403</c> in
/// <c>MvcAuthHolesTests.cs</c>, a third <see cref="WebApplicationFactory{TEntryPoint}"/> booted
/// eagerly for every test in a class (most of which never touch it) was found to occasionally
/// race the shared demo.db schema sync during <c>ClassInitialize</c> — an intermittent flake
/// unrelated to what any individual test exercises. Do not add a class-level factory just to
/// call these helpers; build any extra factory variant inside the single test method that
/// needs it (via <c>_factory.WithWebHostBuilder(...)</c>), same as the existing convention.
/// </para>
/// </summary>
internal static class DbTestHelpers
{
    /// <summary>
    /// Opens a scope off <paramref name="factory"/>'s DI container, resolves a real
    /// <see cref="IDataContext"/> via <see cref="IWtmDataContextFactory"/>, adds
    /// <paramref name="entity"/>, and saves — the same write path
    /// _FrameworkController/BaseVM use in production, not a direct SQLite connection or a
    /// hand-rolled INSERT. Returns the same <paramref name="entity"/> instance for chaining.
    /// The write itself is not scoped by tenant — EF's global query filter only affects reads —
    /// so <paramref name="entity"/> carries whatever <c>TenantCode</c> the caller set on it.
    /// </summary>
    public static T Seed<T>(WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory, T entity)
        where T : TopBasePoco
    {
        using var scope = factory.Services.CreateScope();
        var dc = ResolveDc(scope, tenantCode: null);
        dc.AddEntity(entity);
        dc.SaveChanges();
        return entity;
    }

    /// <summary>
    /// Opens a FRESH scope (a new <see cref="IDataContext"/> instance, not the change-tracked
    /// one a prior <see cref="Seed{T}"/>/HTTP call went through) and reads
    /// <typeparamref name="T"/> back by id — a real database read. Honours the framework's
    /// global <c>ITenant</c>/<c>IsValid</c> query filters by default, same as any production
    /// read; pass <paramref name="ignoreQueryFilters"/> <c>true</c> only when the test's whole
    /// point is to look across a filter boundary it does not otherwise hold (e.g. asserting a
    /// row that a tenant-scoped read correctly can NOT see still physically exists).
    /// </summary>
    public static T? ReadBack<T>(WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory, Guid id, bool ignoreQueryFilters = false)
        where T : TopBasePoco
    {
        using var scope = factory.Services.CreateScope();
        var dc = ResolveDc(scope, tenantCode: null);
        var set = ignoreQueryFilters ? dc.Set<T>().IgnoreQueryFilters() : dc.Set<T>();
        return set.AsNoTracking().FirstOrDefault(x => x.ID == id);
    }

    /// <summary>
    /// Opens a scope and returns a fresh <see cref="IDataContext"/> created with
    /// <paramref name="tenantCode"/> as its current tenant — for tests that need to read
    /// through the framework's per-request tenant-scoping mechanism directly.
    /// <c>DataContext.OnModelCreating</c> applies a <c>TenantCode == this.TenantCode</c> global
    /// EF query filter to every <c>ITenant</c> entity UNCONDITIONALLY — regardless of
    /// <c>Configs.EnableTenant</c> — so this is the same mechanism a real tenant-scoped request
    /// pipeline relies on (see #837 patch 3 / MultiTenantSeedFixtureTests.cs).
    /// The caller owns disposal: dispose the returned scope (which disposes the context), not
    /// the context directly.
    /// </summary>
    public static (IServiceScope Scope, IDataContext Dc) OpenScopedContext(
        WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory, string? tenantCode)
    {
        var scope = factory.Services.CreateScope();
        var dc = ResolveDc(scope, tenantCode);
        return (scope, dc);
    }

    private static IDataContext ResolveDc(IServiceScope scope, string? tenantCode)
    {
        var dcFactory = scope.ServiceProvider.GetRequiredService<IWtmDataContextFactory>();
        return dcFactory.CreateDC(currentTenant: tenantCode)
            ?? throw new InvalidOperationException(
                "IWtmDataContextFactory.CreateDC returned null — check that the demo's " +
                "'default' connection is configured and enabled in appsettings.json.");
    }

    /// <summary>
    /// Evicts <see cref="GlobalData"/>'s cached tenant list
    /// (<c>FrameworkServiceExtension.cs</c>'s <c>SetTenantGetFunc</c> closure — cached via
    /// <see cref="IDistributedCache"/> with a 1-hour absolute expiration once populated).
    ///
    /// <para>
    /// <b>Call this after seeding a new tenant row and before anything that depends on the
    /// tenant being resolvable</b> — <see cref="WTMContext.DoLoginAsync"/> and
    /// <see cref="IWtmDataContextFactory.CreateDC"/> both read <c>GlobalData.AllTenant</c>, and
    /// <c>CreateDC</c> does so UNCONDITIONALLY on every single call, including the calls
    /// <see cref="Seed{T}"/>/<see cref="ReadBack{T}"/>/<see cref="OpenScopedContext"/> above make
    /// to resolve their own <see cref="IDataContext"/>. That means the very first call this file
    /// makes to seed a tenant's own row (before that row exists yet) already triggers — and,
    /// because the result is cached for an hour, permanently poisons — an empty tenant list for
    /// the rest of the factory's lifetime, unless it is evicted again afterward. This was found
    /// the hard way while building the #837 patch 3 fixture: a freshly seeded tenant's user
    /// could not log in until this eviction was added, even though the row demonstrably existed
    /// in the database (see <c>MultiTenantSeedFixtureTests.SeedTenant</c>, which calls this after
    /// every seed for exactly this reason).
    /// </para>
    ///
    /// <para>
    /// This is a test-only workaround, not a framework fix — it does not indicate a bug in
    /// <see cref="Seed{T}"/> etc. above, which correctly reuse the framework's own
    /// <see cref="IWtmDataContextFactory"/> resolution path. A real deployment that creates a
    /// new tenant while any earlier request has already cached the tenant list would hit this
    /// same 1-hour staleness window; that is a separate, pre-existing concern outside #837's
    /// scope (test infrastructure), not something introduced or fixed here.
    /// </para>
    /// </summary>
    public static void InvalidateTenantCache(WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
        cache.Delete(nameof(GlobalData.AllTenant));
    }

    /// <summary>
    /// Evicts <see cref="GlobalData"/>'s cached menu list — the same
    /// <see cref="IDistributedCache"/>-backed, 1-hour-absolute-expiration pattern as
    /// <see cref="InvalidateTenantCache"/> (<c>FrameworkServiceExtension.cs</c>'s
    /// <c>SetMenuGetFunc</c> closure, cache key <c>nameof(GlobalData.AllMenus)</c>).
    ///
    /// <para>
    /// Call this after seeding a <c>FrameworkMenu</c>/<c>FunctionPrivilege</c> pair and before
    /// anything that depends on <c>WTMContext.IsAccessable</c> resolving it — under
    /// <c>IsQuickDebug=false</c>, <c>AllMenus</c> is built from the real <c>FrameworkMenus</c>
    /// table (<c>IsQuickDebug=true</c> instead reflects over every controller action
    /// unconditionally, which is exactly the RBAC-bypass #837 patch 1 exists to stop relying
    /// on). demo's own seed data (<c>DataContext.DataInit</c>) never populates
    /// <c>FrameworkMenus</c>/<c>FunctionPrivileges</c> at all, so under a naive
    /// <c>_strictFactory</c> switch even the seeded admin has zero configured page privileges —
    /// discovered when <c>PrivilegeFilter_AdminUser_CanAccessPrivilegedPage</c> started
    /// returning a genuine 403 instead of the false-positive 200 <c>IsQuickDebug=true</c> had
    /// been giving it. See <c>MvcAuthHolesTests.ClassInit</c>, which seeds a real
    /// <c>/Student/Index</c> menu + role-001 privilege and calls this afterward.
    /// </para>
    /// </summary>
    public static void InvalidateMenuCache(WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
        cache.Delete(nameof(GlobalData.AllMenus));
    }
}
