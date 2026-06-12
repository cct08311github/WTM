#nullable enable
// WF-14: Closed set of workflow FunctionPrivilege URL constants.
// WF-21.2: Designer URL constants added (DesignerBase, DesignerPage).
//
// These match the controller/action URL patterns that the WTM PrivilegeFilter
// uses to gate access via Wtm.IsAccessable(url).
// Registered as menu items in the admin startup seeding (consumer side).

namespace WalkingTec.Mvvm.WorkFlow.Controllers;

/// <summary>
/// WorkFlow module FunctionPrivilege URL constants.
///
/// <para>WTM's RBAC system gates access by URL path — the PrivilegeFilter calls
/// <c>Wtm.IsAccessable(controller.BaseUrl)</c> which matches against registered
/// <c>FunctionPrivilege</c> records by URL pattern.  Controller actions that do NOT
/// carry <see cref="WalkingTec.Mvvm.Core.AllRightsAttribute"/> automatically have
/// this URL-based check applied.</para>
///
/// <para>Consumers register these URLs as <c>SimpleMenu</c> items with associated
/// <c>FunctionPrivilege</c> rows during application startup (exactly like other WTM
/// framework menus) to grant/deny access per role.</para>
/// </summary>
public static class WorkflowPrivileges
{
    // ── Definition management ─────────────────────────────────────────────────
    /// <summary>URL prefix for workflow definition management (publish, validate, list).</summary>
    public const string DefinitionBase    = "/api/_workflow/definitions";

    /// <summary>Publish a workflow graph (write privilege for process administrators).</summary>
    public const string WorkflowAdmin     = "/api/_workflow/definitions/{code}/publish";

    // ── Instance management ───────────────────────────────────────────────────
    /// <summary>Start a new workflow instance.</summary>
    public const string InstanceStart     = "/api/_workflow/instances/start";

    /// <summary>Withdraw a running workflow instance.</summary>
    public const string InstanceWithdraw  = "/api/_workflow/instances/{id}/withdraw";

    // ── Task operations ───────────────────────────────────────────────────────
    /// <summary>Inbox — view pending tasks assigned to the caller.</summary>
    public const string TaskInbox         = "/api/_workflow/tasks/mine";

    /// <summary>Approve an approval task.</summary>
    public const string TaskApprove       = "/api/_workflow/tasks/{id}/approve";

    /// <summary>Reject an approval task.</summary>
    public const string TaskReject        = "/api/_workflow/tasks/{id}/reject";

    // ── WF-21.2: Designer operations ─────────────────────────────────────────

    /// <summary>
    /// URL prefix for the designer API controller (<c>WorkflowDesignerController</c>).
    ///
    /// <para>Register this as a <c>FunctionPrivilege</c> to grant access to all designer
    /// API actions (list, create, metadata, graph fetch, draft, publish).
    /// This is deliberately stricter than <c>[AllRights]</c> — design and publish
    /// operations are privileged and must be explicitly granted to specific roles.</para>
    /// </summary>
    public const string DesignerBase      = "/api/_workflow/designer";

    /// <summary>
    /// URL for the designer page controller (<c>WorkflowDesignerPageController</c>).
    ///
    /// <para>Register this as a <c>FunctionPrivilege</c> to allow access to the
    /// low-code designer HTML page at <c>/_workflow-designer</c>.</para>
    /// </summary>
    public const string DesignerPage      = "/_workflow-designer";
}
