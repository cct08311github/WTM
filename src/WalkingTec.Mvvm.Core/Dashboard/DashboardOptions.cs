using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Dashboard;
public class DashboardOptions
{
    public string DashboardDirectory { get; set; } = "App_Data/dashboards";

    /// <summary>
    /// Global kill switch for dashboard authoring. When <c>false</c>, every write/design
    /// endpoint — <c>_DashboardController.Create</c>, <c>.Update</c>, <c>.Delete</c>, and
    /// <c>_DashboardDesignerController.Preview</c> (which also persists, transiently) —
    /// returns <c>403 Forbidden</c> for every caller, regardless of ownership or role.
    /// Default <c>true</c> (unchanged behaviour): editing stays available exactly as it
    /// was before this flag did anything.
    /// </summary>
    /// <remarks>
    /// <b>Issue #948: before this fix, this property was declared (with no doc comment
    /// at all — the summary above and this remarks section are both new) but was never
    /// read anywhere in <c>src/</c>; editing authorization via this flag did not
    /// exist.</b> (Issue #955 review correction: an earlier version of this remark
    /// claimed the pre-fix property "had this exact doc comment", which cannot be true —
    /// the summary above describes the 403 behaviour this same fix adds, so it could not
    /// have existed before the fix that introduces it.) A setting that appears to gate a
    /// capability but does nothing is worse than no setting at all: it reads as
    /// protection to anyone configuring a deployment. This is deliberately a coarse,
    /// all-or-nothing switch —
    /// not a per-user/per-role policy (that already exists via
    /// <see cref="IDashboardService.CanEdit"/>/<see cref="AdminRoles"/>, unaffected by
    /// this flag) — for a deployment that wants to ship pre-built dashboards only and
    /// remove the entire caller-write attack surface (including the REST-widget SSRF
    /// surface a caller-supplied widget definition can otherwise reach — see
    /// <see cref="IDashboardEgressPolicy"/>), without standing up a full authorization
    /// policy just to turn authoring off.
    /// </remarks>
    public bool EnableEditing { get; set; } = true;
    public int DefaultRefreshInterval { get; set; } = 60;
    public bool AllowIframeSameOrigin { get; set; } = false;

    /// <summary>
    /// Role names that are treated as administrators for Dashboard access.
    /// Administrators can view and edit all dashboards regardless of ownership or sharing settings.
    /// Defaults to ["Admin"] for backward compatibility.
    /// Configure this if your deployment uses a different admin role name (e.g. "SystemAdmin", "超級管理員").
    /// </summary>
    public string[] AdminRoles { get; set; } = ["Admin"];

    /// <summary>
    /// Allowlist of widget UI type strings (i.e. <see cref="WidgetDefinition.Type"/>).
    /// When non-empty, Create/Update requests whose widget <c>Type</c> is not in this set
    /// are rejected with HTTP 400.
    /// When null or empty, any non-empty <c>Type</c> string is accepted (default — backward compatible).
    /// Example: <c>["chart", "kpi", "table", "stat", "text", "iframe", "gauge"]</c>
    /// </summary>
    public string[]? AllowedWidgetTypes { get; set; }

    /// <summary>
    /// Short-TTL in-memory result cache for <c>AnalysisWidgetDataSource</c>.
    /// Each unique combination of (widgetId, tenant, user, filter values) is cached for this many seconds.
    /// Set to 0 to disable caching (opt-out).
    /// Default: 10 seconds.
    /// </summary>
    public int AnalysisWidgetCacheTtlSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum number of seconds each widget data fetch is allowed to run before it is cancelled.
    /// When a widget times out, a per-widget error result is returned instead of failing the whole dashboard.
    /// Set to 0 to disable the per-widget timeout (not recommended in production).
    /// Default: 30 seconds.
    /// </summary>
    public int WidgetDataTimeoutSeconds { get; set; } = 30;
}