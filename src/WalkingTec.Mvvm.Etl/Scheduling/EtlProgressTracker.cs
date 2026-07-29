#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.Scheduling;

/// <summary>
/// 追蹤執行中 Job 的進度（in-memory ConcurrentDictionary）。
/// 由 EtlQuartzJob 透過 IProgress 寫入，由 Monitor Controller 讀取。
/// </summary>
/// <remarks>
/// #883: this is a single, process-wide, un-scoped dictionary keyed only by
/// <see cref="EtlProgress.JobId"/> -- before this fix, <c>_EtlMonitorController.Running()</c>
/// returned EVERY tenant's currently-running jobs (including their <c>JobId</c>) to any caller
/// who passed the (tenant-blind) Admin/ETLAdmin role gate. Combined with several
/// <c>EtlSchedulerService</c> methods that took a caller-controlled <c>jobId</c> and loaded the
/// row with <c>IgnoreQueryFilters()</c> and no tenant check of their own (also fixed in #883),
/// this let tenant A's ETLAdmin read tenant B's running job id here, then feed it to e.g.
/// <c>DryRun</c> to read tenant B's actual source-data preview rows. <see cref="GetAll"/> and
/// <see cref="Get"/> now take the SAME #843-style <c>declaredSystemQuery</c> contract
/// <c>EtlSchedulerService</c> uses -- a caller must pass its own tenant code, or explicitly
/// declare a system query, to see anything at all.
/// </remarks>
/// <remarks>
/// #883 review round 2: <paramref name="callerTenantCode"/> (renamed here for brevity, applies
/// to both <see cref="Get"/> and <see cref="GetAll"/>) is deliberately a REQUIRED parameter with
/// no default -- it originally defaulted to <c>null</c>, and exactly that default was how
/// <c>EtlDashboardService.BuildSummary</c> silently called the parameterless overload and leaked
/// host/null-tenant job metadata to every tenant while showing tenant callers none of their own
/// running jobs (found in review, fixed in the same commit as this doc comment). A defaultable
/// parameter here is a foot-gun for exactly the class of bug this whole issue exists to close;
/// requiring every call site to explicitly decide is the point, not an oversight. This is a
/// binary-breaking signature change from the shape #883 first shipped with -- see CHANGELOG.
/// </remarks>
public class EtlProgressTracker
{
    private readonly ConcurrentDictionary<Guid, EtlProgress> _progress = new();

    public void Update(EtlProgress progress)
    {
        _progress[progress.JobId] = progress;
    }

    /// <param name="jobId">Job ID to look up.</param>
    /// <param name="callerTenantCode">
    /// #883: caller's own tenant. Required, no default (see class remarks) -- pass the real
    /// caller's tenant explicitly, or <see langword="null"/> for a genuine null-tenant/host
    /// caller. When the tracked entry's <see cref="EtlProgress.TenantCode"/> does not match
    /// (and <paramref name="declaredSystemQuery"/> is false), this returns <c>null</c> --
    /// indistinguishable from "not currently running" -- rather than leaking another tenant's
    /// progress.
    /// </param>
    /// <param name="declaredSystemQuery">#843-style explicit escape hatch. Defaults false.</param>
    public EtlProgress? Get(Guid jobId, string? callerTenantCode, bool declaredSystemQuery = false)
    {
        if (!_progress.TryGetValue(jobId, out var p))
            return null;
        if (declaredSystemQuery)
            return p;
        return p.TenantCode == callerTenantCode ? p : null;
    }

    /// <param name="callerTenantCode">#883: caller's own tenant, required -- see <see cref="Get"/>.</param>
    /// <param name="declaredSystemQuery">#843-style explicit escape hatch. Defaults false.</param>
    public IReadOnlyList<EtlProgress> GetAll(string? callerTenantCode, bool declaredSystemQuery = false)
    {
        var values = _progress.Values;
        return (declaredSystemQuery ? values : values.Where(p => p.TenantCode == callerTenantCode))
            .ToList();
    }

    public void Remove(Guid jobId)
    {
        _progress.TryRemove(jobId, out _);
    }
}
