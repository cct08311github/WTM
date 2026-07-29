using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Dashboard;
using WalkingTec.Mvvm.Demo.Models._Admin;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Issue #843 — <c>DCExtension.ApplyDataPrivilegeForAnalysis</c> used to fail OPEN whenever
/// <c>WTMContext.LoginUserInfo == null</c>: it returned the caller's query completely
/// unfiltered instead of denying it. Every background execution path has a null
/// <c>LoginUserInfo</c> — <see cref="WalkingTec.Mvvm.Core.Dashboard.Snapshot.DashboardSnapshotJob"/>
/// and <see cref="WalkingTec.Mvvm.Core.Dashboard.Alerting.DashboardAlertHostedService"/> both
/// resolve <see cref="WTMContext"/> from a bare <c>IServiceProvider.CreateScope()</c> with no
/// <c>HttpContext</c> — so row-level DataPrivilege was silently skipped for every widget those
/// jobs execute.
///
/// <para>
/// This file proves the companion, adjacent gap that made the fix's own acceptance test
/// meaningless without also being closed: <see cref="AnalysisWidgetDataSource"/>'s underlying
/// <c>WTMContext.DC</c> is created via <c>WTMContext.CreateDC()</c>, which derives the
/// DataContext's <c>TenantCode</c> exclusively from <c>LoginUserInfo.CurrentTenant</c>
/// (<c>WTMContext.CreateDC.cs</c>) — a background job would therefore resolve to
/// <c>TenantCode == null</c> regardless of which tenant's widget it was actually running, and
/// would see NEITHER its own tenant's rows NOR any other tenant's (this is the Dashboard-shaped
/// twin of #832, the ETL scheduler's identical gap, tracked separately and NOT fixed here).
/// #843 additionally threads <c>WidgetDataRequest.TenantId</c> (a field that existed but was
/// never populated by either <see cref="IDashboardService"/> implementation) through to
/// <see cref="AnalysisWidgetDataSource"/>, which now builds the DataContext explicitly for the
/// widget's own tenant via <c>IWtmDataContextFactory</c> — the same no-HttpContext DataContext
/// pattern <c>WorkflowEngine</c>/<c>WorkflowTimerHostedService</c> already use.
/// </para>
///
/// <para>
/// <b>What this test exercises, precisely</b>: the real, unmocked
/// <see cref="AnalysisWidgetDataSource"/> class, resolved from the demo app's own real DI
/// container (<c>IWtmDataContextFactory</c>, <c>AnalysisVmRegistry</c>, <c>AnalysisQueryEngine</c>
/// all wired exactly as <c>Startup.cs</c> configures them), running against a real SQLite-backed
/// <c>demo.db</c> with EF Core's real, unconditional <c>ITenant</c> global query filter — with NO
/// <c>HttpContext</c> in scope, matching exactly what <c>DashboardSnapshotJob</c> and
/// <c>DashboardAlertHostedService</c> hand it via their own bare
/// <c>_serviceProvider.CreateScope()</c>. It does not additionally re-exercise the
/// <c>WidgetSourceDefinition</c> → JSON-parameter bridging layer inside
/// <c>IDashboardService.GetWidgetDataAsync</c> (covered separately by
/// <c>JsonFileDashboardServiceTests.GetWidgetData_bridges_tenantId_into_request</c> /
/// <c>EfCoreDashboardServiceTests.GetWidgetData_bridges_tenantId_into_request</c>), nor
/// <c>DashboardSnapshotJob</c>'s own Excel-building glue (covered by
/// <c>DashboardReliabilityTests</c>/<c>DashboardSnapshotSinkTests</c>) — both are unmodified by
/// this fix.
/// </para>
///
/// <para>
/// The widget's underlying model (<see cref="FileAttachmentAnalysisView"/>, projected from the
/// real <c>FileAttachment</c> entity — itself <c>ITenant</c>) deliberately has NO DataPrivilege
/// rule configured, so <c>ApplyDataPrivilegeForAnalysis</c> is a no-op for it either way: this
/// test isolates the tenant-DataContext-scoping half of #843. The DataPrivilege fail-closed
/// default and its explicit escape hatch are proven separately and more surgically in
/// <c>DPWhereInMemoryTests.ApplyDataPrivilegeForAnalysisTests</c>
/// (<c>test/WalkingTec.Mvvm.Core.Test/Extensions/DPWhereInMemoryTests.cs</c>), which is also
/// where the registered mutant for #843 targets its assertion.
/// </para>
/// </summary>
[TestClass]
public class DashboardBackgroundTenantIsolationTests843
{
    // #837 patch 2 convention: no class-level factory — build a fresh
    // DemoWebApplicationFactory + EnableTenant=true variant inside each test method.
    private static WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> NewTenantEnabledFactory(
        DemoWebApplicationFactory factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["EnableTenant"] = "true",
                });
            });
        });

    private sealed record SeededTenant(MyTenant Tenant, FileAttachment File);

    /// <summary>
    /// Seeds one tenant + one FileAttachment row (tagged with that tenant's TCode) via
    /// <see cref="DbTestHelpers"/> — the same write path production code uses, not a hand-rolled
    /// INSERT. Mirrors <c>MultiTenantSeedFixtureTests.SeedTenant</c>.
    /// </summary>
    private static SeededTenant SeedTenant(WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory, string label)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];

        var tenant = DbTestHelpers.Seed(factory, new MyTenant
        {
            ID = Guid.NewGuid(),
            TCode = $"t843{label}{suffix}",
            TName = $"Issue #843 tenant {label}",
            Enabled = true,
            TenantCode = null,
        });

        var file = DbTestHelpers.Seed(factory, new FileAttachment
        {
            ID = Guid.NewGuid(),
            FileName = $"t843-{label}-{suffix}.txt",
            FileExt = "txt",
            Length = 0,
            UploadTime = DateTime.UtcNow,
            SaveMode = "local",
            TenantCode = tenant.TCode,
        });

        // See DbTestHelpers.InvalidateTenantCache's doc comment.
        DbTestHelpers.InvalidateTenantCache(factory);

        return new SeededTenant(tenant, file);
    }

    /// <summary>
    /// Projected analysis view over <see cref="FileAttachment"/>. Deliberately has no
    /// DataPrivilege rule configured anywhere in this test — isolates the tenant-DataContext-
    /// scoping half of #843 from the DataPrivilege fail-closed-default half (tested separately).
    /// </summary>
    private sealed class FileAttachmentAnalysisView : TopBasePoco
    {
        [Dimension(DisplayName = "FileName")]
        public string FileName { get; set; } = "";

        [Measure(AllowedFuncs = AggregateFunc.Count, DisplayName = "Count")]
        public int Cnt { get; set; }
    }

    [EnableAnalysis]
    private sealed class TenantFileAnalysisListVM : BasePagedListVM<FileAttachmentAnalysisView, BaseSearcher>
    {
        public override IOrderedQueryable<FileAttachmentAnalysisView> GetSearchQuery()
            => DC!.Set<FileAttachment>()
                .Select(f => new FileAttachmentAnalysisView { ID = f.ID, FileName = f.FileName, Cnt = 1 })
                .OrderByDescending(x => x.ID);
    }

    private static WidgetDataRequest BuildRequest(string tenantId) => new()
    {
        TenantId = tenantId,
        Parameters = new Dictionary<string, string>
        {
            ["listVmType"] = typeof(TenantFileAnalysisListVM).FullName!,
            ["dimensions"] = JsonSerializer.Serialize(new[] { "FileName" }),
            ["measures"] = JsonSerializer.Serialize(new[] { new { Field = "Cnt", Func = AggregateFunc.Count } }),
        }
    };

    private static async Task<HashSet<string>> RunJobForTenantAsync(
        WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> factory, string tenantId)
    {
        // Root-level scope, no ambient HttpContext — exactly the shape DashboardSnapshotJob and
        // DashboardAlertHostedService hand AnalysisWidgetDataSource via their own bare
        // _serviceProvider.CreateScope().
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetServices<IWidgetDataSource>()
            .OfType<AnalysisWidgetDataSource>()
            .Single();

        var result = await source.GetDataAsync(BuildRequest(tenantId));

        Assert.IsNull(result.Error, $"#843: widget data fetch for tenant {tenantId} must not error: {result.Error}");
        Assert.IsNotNull(result.Rows);

        return [.. result.Rows!.Select(r => r["FileName"]?.ToString() ?? "")];
    }

    [TestMethod]
    public async Task BackgroundJob_ForTenantA_DoesNotSeeTenantBsRows_AndStillSeesItsOwn()
    {
        using var factory = new DemoWebApplicationFactory();
        using var tenantFactory = NewTenantEnabledFactory(factory);

        var a = SeedTenant(tenantFactory, "A");
        var b = SeedTenant(tenantFactory, "B");

        var fileNamesForA = await RunJobForTenantAsync(tenantFactory, a.Tenant.TCode!);

        // Positive control: tenant A's background job still sees tenant A's own row.
        Assert.IsTrue(fileNamesForA.Contains(a.File.FileName),
            "#843: a background job correctly scoped to tenant A must still see tenant A's own FileAttachment row.");

        // The actual #843 assertion: tenant A's job must NOT see tenant B's row.
        Assert.IsFalse(fileNamesForA.Contains(b.File.FileName),
            "#843: a background job for tenant A must NOT see tenant B's FileAttachment row.");

        // Symmetric check the other direction, and a second positive control.
        var fileNamesForB = await RunJobForTenantAsync(tenantFactory, b.Tenant.TCode!);
        Assert.IsTrue(fileNamesForB.Contains(b.File.FileName),
            "#843: a background job correctly scoped to tenant B must still see tenant B's own FileAttachment row.");
        Assert.IsFalse(fileNamesForB.Contains(a.File.FileName),
            "#843: a background job for tenant B must NOT see tenant A's FileAttachment row.");
    }
}
