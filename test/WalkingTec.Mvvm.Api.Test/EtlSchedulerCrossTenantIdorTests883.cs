#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.Demo.Models._Admin;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Scheduling;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Issue #883 — a cross-vendor review of <c>b3dbae4b3</c> (#841/#862) found that seven of the
/// <c>IgnoreQueryFilters()</c> calls added to <c>EtlSchedulerService</c> to keep the background
/// Quartz scheduler working sit on paths ALSO reachable from HTTP controllers
/// (<c>_EtlJobController</c>/<c>_EtlRunLogController</c>/<c>EtlJobDefinitionVM</c>), with no
/// check that the caller-supplied <c>jobId</c>/<c>runLogId</c> belonged to the caller's own
/// tenant — an IDOR. Combined with <c>EtlProgressTracker</c> having no tenant dimension of its
/// own, tenant A's ETLAdmin could read tenant B's running <c>JobId</c> from
/// <c>_EtlMonitorController.Running</c> and feed it to <c>DryRun</c> to read tenant B's actual
/// source-data preview rows, or to <c>TriggerNow</c>/<c>Pause</c>/<c>Resume</c>/<c>Reschedule</c>/
/// <c>SkipNext</c>/<c>Rerun</c> to operate on tenant B's job directly.
///
/// <para>
/// <b>Why this could only be found and fixed after #876</b>: before #876, <c>Wtm</c> was always
/// null inside these five controllers' own <c>OnActionExecuting</c> role gates, so EVERY caller
/// — including a genuine same-tenant Admin — was denied with a clean 403/redirect before ever
/// reaching the vulnerable code below. That availability defect accidentally masked this IDOR.
/// #876 and #883 are deliberately sequenced/landed together so there is no window where the
/// gate works but the tenant scoping underneath it does not.
/// </para>
///
/// <para>
/// <b>Fix</b>: every HTTP-reachable <c>EtlSchedulerService</c> method now funnels through
/// <c>LoadJobDefinitionForCallerAsync</c> (job lookups) or <c>EnsureCallerOwnsJobAsync</c>
/// (Quartz-only calls), both requiring the caller's own <c>Wtm.LoginUserInfo?.CurrentTenant</c>
/// to match the row's <c>TenantCode</c> unless the caller explicitly declares
/// <c>declaredSystemQuery: true</c> (the #843 contract, reused verbatim) — no production HTTP
/// call site does. <c>EtlProgressTracker</c> gained the same <c>callerTenantCode</c>/
/// <c>declaredSystemQuery</c> parameters on <c>Get</c>/<c>GetAll</c>.
/// </para>
///
/// <para>
/// <b>Scope of what is tested here</b>: every listed HTTP entry point is proven to REJECT a
/// cross-tenant caller (a wrong-tenant id must be indistinguishable from a nonexistent one --
/// none of these assert a raw 403; they assert the SAME "not found"/no-op response the pre-fix
/// code already gave for a genuinely nonexistent id, now also covering a real-but-other-tenant
/// one). The positive control (same tenant succeeds) uses <c>SkipNext</c> -- the one action that
/// touches only the database, not Quartz's own trigger store, so it does not additionally
/// require a real Quartz-scheduled trigger to exist for a clean pass/fail signal.
/// </para>
/// </summary>
[TestClass]
public class EtlSchedulerCrossTenantIdorTests883
{
    private const string RoleCode = "ETLAdmin";
    private const string Password = "Passw0rd!883";

    private static DemoWebApplicationFactory _factory = null!;
    private static WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> _strictFactory = null!;

    private sealed record TenantContext(
        MyTenant Tenant, FrameworkUser User, EtlJobDefinition Job, EtlRunLog RunLog);

    private static TenantContext _tenantA = null!;
    private static TenantContext _tenantB = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        _factory = new DemoWebApplicationFactory();
        _strictFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["IsQuickDebug"] = "false",
                    ["EnableTenant"] = "true",
                    ["CookieOptions:AccessDeniedPath"] = "/AccessDenied883Test",
                });
            });
        });

        _tenantA = SeedTenant("A");
        _tenantB = SeedTenant("B");

        // FrameworkMenu is NOT ITenant -- one shared row per URL is granted to both tenants'
        // ETLAdmin role via a separate (tenant-scoped) FunctionPrivilege row each.
        GrantMenuAccess("EtlJob883", "/_EtlJob");
        GrantMenuAccess("EtlJobTriggerNow883", "/_EtlJob/TriggerNow");
        GrantMenuAccess("EtlJobReschedule883", "/_EtlJob/Reschedule");
        GrantMenuAccess("EtlJobSkipNext883", "/_EtlJob/SkipNext");
        GrantMenuAccess("EtlJobDryRun883", "/_EtlJob/DryRun");
        GrantMenuAccess("EtlJobPause883", "/_EtlJob/Pause");
        GrantMenuAccess("EtlJobResume883", "/_EtlJob/Resume");
        GrantMenuAccess("EtlJobAbort883", "/_EtlJob/Abort");
        GrantMenuAccess("EtlRunLogRerun883", "/_EtlRunLog/Rerun");
        GrantMenuAccess("EtlMonitorRunning883", "/_EtlMonitor/Running");
        GrantMenuAccess("EtlDashboardStats883", "/_EtlDashboard/Stats");
        DbTestHelpers.InvalidateMenuCache(_strictFactory);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _strictFactory.Dispose();
        _factory.Dispose();
    }

    private static TenantContext SeedTenant(string label)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];

        var tenant = DbTestHelpers.Seed(_strictFactory, new MyTenant
        {
            ID = Guid.NewGuid(),
            TCode = $"t883{label}{suffix}",
            TName = $"Issue #883 tenant {label}",
            Enabled = true,
            TenantCode = null,
        });

        var user = DbTestHelpers.Seed(_strictFactory, new FrameworkUser
        {
            ID = Guid.NewGuid(),
            ITCode = $"t883user{label}{suffix}",
            Password = PasswordHashHelper.HashPassword(Password),
            Name = $"Issue #883 ETLAdmin {label}",
            IsValid = true,
            TenantCode = tenant.TCode,
        });

        DbTestHelpers.Seed(_strictFactory, new FrameworkRole
        {
            ID = Guid.NewGuid(),
            RoleCode = RoleCode,
            RoleName = $"ETL Admin (#883 test {label})",
            TenantCode = tenant.TCode,
        });
        DbTestHelpers.Seed(_strictFactory, new FrameworkUserRole
        {
            ID = Guid.NewGuid(),
            UserCode = user.ITCode,
            RoleCode = RoleCode,
            TenantCode = tenant.TCode,
        });

        var job = DbTestHelpers.Seed(_strictFactory, new EtlJobDefinition
        {
            ID = Guid.NewGuid(),
            TenantCode = tenant.TCode,
            Name = $"Issue883Job{label}{suffix}",
            // Valid 6-field Quartz cron, once far in the future -- syntactically schedulable
            // (needed for the SkipNext positive control's Quartz-adjacent code paths to not
            // throw on an invalid expression) but will never actually fire during the test run.
            CronExpression = "0 0 0 1 1 ? 2099",
            JobClassName = "EtlQuartzJob",
            Status = EtlJobStatus.Disabled,
            SourceCsKey = "default",
            SourceDbType = DBTypeEnum.SQLite,
            TargetCsKey = "default",
            TargetDbType = DBTypeEnum.SQLite,
            TargetTableName = "Issue883Target",
            MergeKeyColumn = "ID",
            QueryTemplate = "SELECT 1",
        });

        var runLog = DbTestHelpers.Seed(_strictFactory, new EtlRunLog
        {
            ID = Guid.NewGuid(),
            JobId = job.ID,
            TenantCode = tenant.TCode,
            Trigger = EtlRunTrigger.Manual,
            Result = EtlRunResult.Success,
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            FinishedAt = DateTime.UtcNow.AddMinutes(-9),
            WatermarkSnapshot = "W0",
        });

        DbTestHelpers.InvalidateTenantCache(_strictFactory);
        return new TenantContext(tenant, user, job, runLog);
    }

    /// <summary>
    /// #883 test-infra note: idempotent by URL -- <c>FrameworkMenu</c> is not <c>ITenant</c>
    /// (one process-wide, cached-by-URL list; <c>WtmAuthorizationService.IsMenuAccessable</c>
    /// resolves whichever row <c>Utils.FindMenu</c> finds for a given URL). demo.db is a
    /// physical file that persists across separate <c>dotnet test</c> process invocations
    /// (including <c>test/mutants/run_mutant.py</c>'s own multiple internal runs of this same
    /// class within one mutant check, with no cleanup between them) -- a fresh
    /// <c>ID = Guid.NewGuid()</c> menu row every <c>ClassInitialize</c> would accumulate
    /// duplicate rows for the SAME URL over repeated invocations, and once more than one
    /// exists, <c>FindMenu</c> can resolve to a stale one this run's own
    /// <c>FunctionPrivilege</c> grants do not reference -- a 403 that looks like a production
    /// regression but is purely test-data accumulation. Querying for an existing row by URL
    /// first (and reusing its id) makes this correct regardless of how many times, or how
    /// interleaved with other test classes, this method has run against the same demo.db.
    /// </summary>
    private static void GrantMenuAccess(string pageName, string url)
    {
        using var lookupScope = _strictFactory.Services.CreateScope();
        var lookupDc = lookupScope.ServiceProvider.GetRequiredService<IWtmDataContextFactory>()
            .CreateDC(currentTenant: null)!;
        var existingMenuId = lookupDc.Set<FrameworkMenu>().AsNoTracking()
            .Where(m => m.Url == url)
            .Select(m => (Guid?)m.ID)
            .FirstOrDefault();

        var menuId = existingMenuId ?? DbTestHelpers.Seed(_strictFactory, new FrameworkMenu
        {
            ID = Guid.NewGuid(),
            PageName = pageName,
            Url = url,
            FolderOnly = false,
            IsInherit = false,
            ShowOnMenu = true,
            IsPublic = false,
            DisplayOrder = 0,
            IsInside = true,
            TenantAllowed = true,
        }).ID;

        DbTestHelpers.Seed(_strictFactory, new FunctionPrivilege
        {
            ID = Guid.NewGuid(),
            RoleCode = RoleCode,
            MenuItemId = menuId,
            Allowed = true,
            TenantCode = _tenantA.Tenant.TCode,
        });
        DbTestHelpers.Seed(_strictFactory, new FunctionPrivilege
        {
            ID = Guid.NewGuid(),
            RoleCode = RoleCode,
            MenuItemId = menuId,
            Allowed = true,
            TenantCode = _tenantB.Tenant.TCode,
        });
    }

    /// <summary>
    /// Logs in as <paramref name="itcode"/> under tenant <paramref name="tenantCode"/> via the
    /// real <c>/Login/Login</c> POST. Mirrors <c>EtlControllerGateHttpTests.NewAuthClientAsync</c>
    /// with a <c>Tenant</c> form field added, matching
    /// <c>MultiTenantSeedFixtureTests.TenantScopedUser_CanCompleteARealHttpLogin</c>.
    /// </summary>
    private static async Task<HttpClient> NewAuthClientAsync(string itcode, string tenantCode)
    {
        var client = _strictFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        var verifyCode = await CaptchaTestHelper.FetchAndSolveAsync(_strictFactory, client);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ITCode"] = itcode,
            ["Password"] = Password,
            ["Tenant"] = tenantCode,
            ["VerifyCode"] = verifyCode,
        });
        var loginResp = await client.PostAsync("/Login/Login", form);
        if (loginResp.StatusCode != HttpStatusCode.Redirect)
        {
            var loginBody = await loginResp.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"#883: login did not succeed for '{itcode}'/'{tenantCode}' -- expected a 302 " +
                $"redirect, got {loginResp.StatusCode}. Response: " +
                $"{loginBody[..Math.Min(500, loginBody.Length)]}");
        }
        return client;
    }

    // ─── Negative controls: tenant A's ETLAdmin must be rejected for tenant B's job ────────

    [TestMethod]
    public async Task TriggerNow_CrossTenantJobId_Rejected()
    {
        var client = await NewAuthClientAsync(_tenantA.User.ITCode, _tenantA.Tenant.TCode);
        var resp = await client.PostAsync($"/_EtlJob/TriggerNow?id={_tenantB.Job.ID}", null);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.NotFound, resp.StatusCode,
            $"#883: tenant A's ETLAdmin must not be able to trigger tenant B's job by id. " +
            $"Got {(int)resp.StatusCode} {resp.StatusCode}: {body}");
    }

    [TestMethod]
    public async Task DryRun_CrossTenantJobId_Rejected()
    {
        var client = await NewAuthClientAsync(_tenantA.User.ITCode, _tenantA.Tenant.TCode);
        var resp = await client.PostAsync($"/_EtlJob/DryRun?id={_tenantB.Job.ID}", null);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.NotFound, resp.StatusCode,
            $"#883: tenant A's ETLAdmin must not be able to dry-run (and thereby read source " +
            $"data preview rows from) tenant B's job by id. Got {(int)resp.StatusCode} " +
            $"{resp.StatusCode}: {body}");
    }

    [TestMethod]
    public async Task Pause_CrossTenantJobId_Rejected()
    {
        var client = await NewAuthClientAsync(_tenantA.User.ITCode, _tenantA.Tenant.TCode);
        var resp = await client.PostAsync($"/_EtlJob/Pause?id={_tenantB.Job.ID}", null);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            $"#883: tenant A's ETLAdmin must not be able to pause tenant B's job by id. " +
            $"Got {(int)resp.StatusCode} {resp.StatusCode}: {body}");
    }

    [TestMethod]
    public async Task Resume_CrossTenantJobId_Rejected()
    {
        var client = await NewAuthClientAsync(_tenantA.User.ITCode, _tenantA.Tenant.TCode);
        var resp = await client.PostAsync($"/_EtlJob/Resume?id={_tenantB.Job.ID}", null);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            $"#883: tenant A's ETLAdmin must not be able to resume tenant B's job by id. " +
            $"Got {(int)resp.StatusCode} {resp.StatusCode}: {body}");
    }

    [TestMethod]
    public async Task Abort_CrossTenantJobId_Rejected()
    {
        var client = await NewAuthClientAsync(_tenantA.User.ITCode, _tenantA.Tenant.TCode);
        var resp = await client.PostAsync($"/_EtlJob/Abort?id={_tenantB.Job.ID}", null);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            $"#883: tenant A's ETLAdmin must not be able to abort tenant B's job by id " +
            $"(not itself one of #883's own reported seven, fixed alongside them -- see " +
            $"AbortAsync's doc comment). Got {(int)resp.StatusCode} {resp.StatusCode}: {body}");
    }

    [TestMethod]
    public async Task SkipNext_CrossTenantJobId_SilentlyNoOps()
    {
        var client = await NewAuthClientAsync(_tenantA.User.ITCode, _tenantA.Tenant.TCode);
        var resp = await client.PostAsync($"/_EtlJob/SkipNext?id={_tenantB.Job.ID}", null);
        var body = await resp.Content.ReadAsStringAsync();
        // SkipNextAsync is an ExecuteUpdateAsync matching zero rows for a wrong-tenant id --
        // the controller action itself does not distinguish "matched 0 rows" from "matched 1",
        // matching its pre-fix behaviour for a genuinely nonexistent id (also a silent 200).
        // The real assertion is the DB-level one below: tenant B's SkipCount must be unchanged.
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#883: SkipNext's controller action returns 200 regardless of whether the update " +
            $"matched a row (same as a genuinely nonexistent id) -- got {(int)resp.StatusCode} " +
            $"{resp.StatusCode}: {body}");
        var reread = DbTestHelpers.ReadBack<EtlJobDefinition>(_strictFactory, _tenantB.Job.ID, ignoreQueryFilters: true);
        Assert.AreEqual(0, reread?.SkipCount ?? -1,
            "#883: tenant A's ETLAdmin must NOT have incremented tenant B's job's SkipCount.");
    }

    [TestMethod]
    public async Task Reschedule_CrossTenantJobId_Rejected()
    {
        var client = await NewAuthClientAsync(_tenantA.User.ITCode, _tenantA.Tenant.TCode);
        var content = new StringContent("{\"NewCron\":\"0 0/5 * * * ?\"}",
            System.Text.Encoding.UTF8, "application/json");
        var resp = await client.PostAsync($"/_EtlJob/Reschedule?id={_tenantB.Job.ID}", content);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.IsTrue(
            resp.StatusCode == HttpStatusCode.BadRequest || resp.StatusCode == HttpStatusCode.NotFound,
            $"#883: tenant A's ETLAdmin must not be able to reschedule tenant B's job by id. " +
            $"Got {(int)resp.StatusCode} {resp.StatusCode}: {body}");
        var reread = DbTestHelpers.ReadBack<EtlJobDefinition>(_strictFactory, _tenantB.Job.ID, ignoreQueryFilters: true);
        Assert.AreEqual("0 0 0 1 1 ? 2099", reread?.CronExpression,
            "#883: tenant B's job's CronExpression must be unchanged by tenant A's attempt.");
    }

    [TestMethod]
    public async Task Rerun_CrossTenantRunLogId_Rejected()
    {
        var client = await NewAuthClientAsync(_tenantA.User.ITCode, _tenantA.Tenant.TCode);
        var resp = await client.PostAsync($"/_EtlRunLog/Rerun?runLogId={_tenantB.RunLog.ID}", null);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#883: RerunFromSnapshotAsync silently returns (no exception) for a run-log id it " +
            $"cannot resolve for the caller's own tenant -- same as a genuinely nonexistent id " +
            $"-- so the controller reports success without having done anything. Got " +
            $"{(int)resp.StatusCode} {resp.StatusCode}: {body}");
        var reread = DbTestHelpers.ReadBack<EtlJobDefinition>(_strictFactory, _tenantB.Job.ID, ignoreQueryFilters: true);
        Assert.AreEqual(EtlJobStatus.Disabled, reread?.Status,
            "#883: tenant A's ETLAdmin must NOT have caused tenant B's job to be (re)triggered.");
    }

    // ─── Positive control: tenant A's ETLAdmin can still operate on tenant A's OWN job ─────

    [TestMethod]
    public async Task SkipNext_OwnTenantJobId_Succeeds()
    {
        var client = await NewAuthClientAsync(_tenantA.User.ITCode, _tenantA.Tenant.TCode);
        var resp = await client.PostAsync($"/_EtlJob/SkipNext?id={_tenantA.Job.ID}", null);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#883: tenant A's ETLAdmin must still be able to operate on tenant A's OWN job -- " +
            $"the tenant-ownership fix must not fail closed for the legitimate same-tenant " +
            $"case. Got {(int)resp.StatusCode} {resp.StatusCode}: {body}");
        var reread = DbTestHelpers.ReadBack<EtlJobDefinition>(_strictFactory, _tenantA.Job.ID, ignoreQueryFilters: true);
        Assert.AreEqual(1, reread?.SkipCount ?? -1,
            "#883: tenant A's own job's SkipCount must have been incremented by this call.");
    }

    // ─── EtlProgressTracker (#883, the read half of the IDOR chain) ────────────────────────
    //
    // Deliberately calls EtlProgressTracker directly (via the app's own DI container --
    // EtlProgressTracker is a real Singleton, not a fake) rather than round-tripping through
    // /_EtlMonitor/Running over HTTP: FrameworkMenu is NOT ITenant (shared across the whole
    // process's cached menu list, WtmAuthorizationService.IsMenuAccessable), so a SECOND
    // FrameworkMenu row for the same "/_EtlMonitor/Running" URL seeded by
    // EtlControllerGateHttpTests's #876 tests (a different, non-tenant-scoped class, sharing
    // the same demo.db within the same test run) collides with this class's tenant-scoped
    // FunctionPrivilege grants -- Utils.FindMenu resolves to WHICHEVER menu row it finds for a
    // given URL, and if the one it resolves to is #876's, this class's own FunctionPrivilege
    // rows (which reference a different MenuItemId) never match, an unrelated flake, not a
    // finding about the #883 fix. #876's own tests already prove the real end-to-end HTTP path
    // (role gate -> PrivilegeFilter -> action) works; this section's job is specifically to
    // prove EtlProgressTracker's own new tenant-scoping parameters, in isolation.

    [TestMethod]
    public void ProgressTracker_GetAll_ReturnsOnlyCallersOwnTenantsProgress()
    {
        using var scope = _strictFactory.Services.CreateScope();
        var tracker = scope.ServiceProvider.GetRequiredService<EtlProgressTracker>();
        var jobIdA = Guid.NewGuid();
        var jobIdB = Guid.NewGuid();
        tracker.Update(new EtlProgress { JobId = jobIdA, JobName = "A", TenantCode = _tenantA.Tenant.TCode, StartedAt = DateTime.UtcNow });
        tracker.Update(new EtlProgress { JobId = jobIdB, JobName = "B", TenantCode = _tenantB.Tenant.TCode, StartedAt = DateTime.UtcNow });
        try
        {
            var seenByA = tracker.GetAll(callerTenantCode: _tenantA.Tenant.TCode);
            Assert.IsTrue(seenByA.Any(p => p.JobId == jobIdA),
                "#883: tenant A's own running job must be present in tenant A's view.");
            Assert.IsFalse(seenByA.Any(p => p.JobId == jobIdB),
                "#883: tenant B's running job must NOT be visible in tenant A's view -- this is " +
                "the first link in the IDOR chain into DryRun/TriggerNow/etc.");

            var seenByB = tracker.GetAll(callerTenantCode: _tenantB.Tenant.TCode);
            Assert.IsTrue(seenByB.Any(p => p.JobId == jobIdB),
                "#883: tenant B's own running job must be present in tenant B's view.");
            Assert.IsFalse(seenByB.Any(p => p.JobId == jobIdA),
                "#883: tenant A's running job must NOT be visible in tenant B's view.");

            Assert.IsNull(tracker.Get(jobIdB, callerTenantCode: _tenantA.Tenant.TCode),
                "#883: tenant A must not be able to resolve tenant B's job progress by id " +
                "either, even knowing the exact JobId.");
            Assert.IsNotNull(tracker.Get(jobIdA, callerTenantCode: _tenantA.Tenant.TCode),
                "#883: tenant A must still be able to resolve its OWN job's progress by id.");
        }
        finally
        {
            tracker.Remove(jobIdA);
            tracker.Remove(jobIdB);
        }
    }

    /// <summary>
    /// #883 review round 2: <c>_EtlDashboardController.Stats</c> called
    /// <c>EtlProgressTracker.GetAll()</c> (parameterless) directly -- missed by the first pass
    /// because <c>_EtlMonitorController</c> was fixed and this sibling controller, reading the
    /// SAME tracker, was not. Worse than a plain leak: with the tracker's old default
    /// (<c>callerTenantCode == null</c>), this resolved to "host/null-tenant entries only" --
    /// tenant A could see a host-scope job's id/name/phase/rate on its OWN dashboard, while its
    /// OWN running jobs were invisible to it (a leak and a functional regression, in opposite
    /// directions, at the same call site). Real HTTP end to end: seeds a host-scope (no tenant)
    /// running entry and a tenant-A-scope one, hits <c>/_EtlDashboard/Stats</c> authenticated as
    /// tenant A, and asserts both halves.
    /// </summary>
    [TestMethod]
    public async Task DashboardStats_ReturnsOnlyCallersOwnTenantsRunningJobs_NotHostScope()
    {
        using var scope = _strictFactory.Services.CreateScope();
        var tracker = scope.ServiceProvider.GetRequiredService<EtlProgressTracker>();
        var hostJobId = Guid.NewGuid();
        var tenantAJobId = Guid.NewGuid();
        tracker.Update(new EtlProgress
        {
            JobId = hostJobId, JobName = "HostScopeJob883", TenantCode = null, StartedAt = DateTime.UtcNow,
        });
        tracker.Update(new EtlProgress
        {
            JobId = tenantAJobId, JobName = "TenantAJob883", TenantCode = _tenantA.Tenant.TCode, StartedAt = DateTime.UtcNow,
        });
        try
        {
            var client = await NewAuthClientAsync(_tenantA.User.ITCode, _tenantA.Tenant.TCode);
            var resp = await client.GetAsync("/_EtlDashboard/Stats");
            var body = await resp.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
                $"#883: tenant A's ETLAdmin must be able to load its own dashboard. Got " +
                $"{(int)resp.StatusCode} {resp.StatusCode}: {body}");

            StringAssert.Contains(body, tenantAJobId.ToString(),
                "#883: tenant A's OWN running job must appear in tenant A's dashboard -- this is " +
                "the functional-regression half: before this fix, the tracker's default resolved " +
                "to host-scope entries only, so a real tenant saw NONE of its own running jobs here.");
            StringAssert.DoesNotMatch(body, new System.Text.RegularExpressions.Regex(
                System.Text.RegularExpressions.Regex.Escape(hostJobId.ToString())),
                "#883: a host/null-tenant-scope running job must NOT appear on tenant A's " +
                "dashboard -- this is the leak half.");
        }
        finally
        {
            tracker.Remove(hostJobId);
            tracker.Remove(tenantAJobId);
        }
    }
}
