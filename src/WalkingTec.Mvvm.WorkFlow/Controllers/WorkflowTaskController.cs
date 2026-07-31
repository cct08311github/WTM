#nullable enable
// WF-14: WorkflowTaskController
// WF-406: AddApprover / Delegate / ReturnToPrev / ReturnToNode / RevokeDelegation endpoints.
//
// HTTP surface for approval-task inbox and approver actions (approve / reject / return).
//
// Security invariants:
//   1. Actor ITCode ALWAYS comes from Wtm.LoginUserInfo — never from the request body.
//      A client cannot act as another user by supplying a different ITCode in the body.
//   2. Assignee validation: the engine (ApproveTaskAsync/RejectTaskAsync) verifies that
//      actorITCode matches ApprovalTask.AssigneeITCode. The controller's job is to pass
//      the server-side actor — NOT to perform its own assignee check.
//   3. Tenant scoping: inbox query uses Wtm.LoginUserInfo.ITCode + TenantCode server-side.
//   4. No controller-level DataContext access (WTM red line).
//   5. RBAC: PrivilegeFilter URL-based gate applies (no [AllRights] on mutating actions).
//      Inbox ([AllRights]) is accessible to any authenticated user.
//   6. revoke-delegation is admin-only: caller must hold the WorkflowAdmin privilege
//      (checked via Wtm.IsAccessable(WorkflowPrivileges.WorkflowAdmin)).
//      Non-admin requests are rejected with 403 Forbidden.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.WorkFlow.Engine;
using WalkingTec.Mvvm.WorkFlow.ViewModels;

namespace WalkingTec.Mvvm.WorkFlow.Controllers;

/// <summary>
/// HTTP surface for approval-task operations.
///
/// <para>Routes under <c>/api/_workflow/tasks</c>.</para>
///
/// <para><strong>Anti-spoofing:</strong>
/// Every approve/reject/return action extracts the actor ITCode from
/// <c>Wtm.LoginUserInfo</c> server-side.  The engine additionally validates that
/// the actor matches the task's <c>AssigneeITCode</c>, so even if this controller
/// were compromised, an unauthorized approval attempt would return
/// <see cref="WorkflowActionCode.TaskNotActive"/> from the engine CAS guard.</para>
/// </summary>
[ApiController]
[Route("api/_workflow/tasks")]
[ActionDescription("WorkflowTask")]
public class WorkflowTaskController : BaseController
{
    private readonly IWorkflowEngine _engine;
    private readonly ILogger<WorkflowTaskController> _logger;

    public WorkflowTaskController(
        IWorkflowEngine engine,
        ILogger<WorkflowTaskController>? logger = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _logger = logger ?? NullLogger<WorkflowTaskController>.Instance;
    }

    // ── GET /api/_workflow/tasks/mine ──────────────────────────────────────────

    /// <summary>
    /// Returns the caller's pending approval inbox (tenant-scoped).
    ///
    /// <para>The query is scoped to <c>Wtm.LoginUserInfo.ITCode</c> and
    /// <c>Wtm.LoginUserInfo.CurrentTenant</c> server-side (#899 session-half: CurrentTenant, not
    /// the raw TenantCode claim — see the method body's own comment).
    /// No client-supplied actor or tenant is used.</para>
    ///
    /// <para>[AllRights] — any authenticated user can view their own inbox.</para>
    /// </summary>
    [HttpGet("mine")]
    [AllRights]
    [ActionDescription("Inbox")]
    [ProducesResponseType(typeof(TaskInboxItem[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Inbox(CancellationToken ct = default)
    {
        // Actor and tenant ALWAYS from the server-side authenticated session.
        // The client cannot influence which user's inbox is returned.
        // #899 session-half: CurrentTenant, same source as the module DataContext's stamp
        // (ResolveAmbientTenant reads LoginUserInfo.CurrentTenant) -- see WorkflowInstanceController's
        // identical comment for the full rationale.
        var actorITCode = Wtm?.LoginUserInfo?.ITCode ?? string.Empty;
        var tenantCode  = Wtm?.LoginUserInfo?.CurrentTenant;

        // Route through the engine (not direct DC) to honour the WTM red line:
        // controllers never touch DataContext directly.
        var tasks = await _engine.GetPendingTasksAsync(actorITCode, tenantCode, ct);

        var items = tasks.Select(t => new TaskInboxItem(
            t.ID,
            t.NodeInstance?.InstanceId ?? Guid.Empty,
            t.NodeInstance?.NodeKey ?? string.Empty,
            t.State.ToString(),
            t.AssigneeITCode,
            t.DueUtc,
            t.Comment)).ToArray();

        return Ok(items);
    }

    // ── POST /api/_workflow/tasks/{id}/approve ─────────────────────────────────

    /// <summary>
    /// Approve the specified approval task.
    ///
    /// <para>The actor ITCode comes SERVER-SIDE from <c>Wtm.LoginUserInfo</c>.
    /// The engine verifies actor == AssigneeITCode via a guarded CAS; a non-assignee
    /// will receive <see cref="WorkflowActionCode.TaskNotActive"/> → 409.</para>
    ///
    /// <para>Result codes:
    /// <list type="bullet">
    ///   <item>200 OK — Advanced / NodeCompleted / InstanceApproved / AlreadyHandled.</item>
    ///   <item>409 Conflict — TaskNotActive / NodeClosed (non-assignee or already handled).</item>
    ///   <item>403 Forbidden — NotAuthorized (RBAC gate).</item>
    /// </list>
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Approve(
        Guid id,
        [FromBody] TaskActionRequest? request,
        CancellationToken ct = default)
    {
        // Actor ALWAYS server-side — the [BindNever] ActorITCode on the DTO is never bound.
        var actorITCode = Wtm?.LoginUserInfo?.ITCode ?? string.Empty;
        var comment     = request?.Comment;

        _logger.LogInformation(
            "[WorkflowTask] Approve requested. TaskId={TaskId} Actor={Actor}",
            id, actorITCode);

        var result = await _engine.ApproveTaskAsync(id, actorITCode, comment, ct);
        return MapEngineResult(result);
    }

    // ── POST /api/_workflow/tasks/{id}/reject ──────────────────────────────────

    /// <summary>
    /// Reject the specified approval task.
    ///
    /// <para>Actor ITCode SERVER-SIDE.  Engine validates actor == AssigneeITCode.
    /// A non-assignee attempting to reject receives 409 (TaskNotActive).</para>
    /// </summary>
    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Reject(
        Guid id,
        [FromBody] TaskActionRequest? request,
        CancellationToken ct = default)
    {
        // Actor ALWAYS server-side.
        var actorITCode = Wtm?.LoginUserInfo?.ITCode ?? string.Empty;
        var reason      = request?.Comment;

        _logger.LogInformation(
            "[WorkflowTask] Reject requested. TaskId={TaskId} Actor={Actor}",
            id, actorITCode);

        var result = await _engine.RejectTaskAsync(id, actorITCode, reason, ct);
        return MapEngineResult(result);
    }

    // ── POST /api/_workflow/tasks/{id}/return-to-initiator ────────────────────

    /// <summary>
    /// Return the task to the initiator (approver-initiated 回退).
    ///
    /// <para>Actor ITCode SERVER-SIDE.  Engine validates actor == AssigneeITCode.</para>
    /// </summary>
    [HttpPost("{id:guid}/return-to-initiator")]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ReturnToInitiator(
        Guid id,
        [FromBody] ReturnToInitiatorRequest? request,
        CancellationToken ct = default)
    {
        // Actor ALWAYS server-side.
        var actorITCode = Wtm?.LoginUserInfo?.ITCode ?? string.Empty;
        var reason      = request?.Reason;

        _logger.LogInformation(
            "[WorkflowTask] ReturnToInitiator. TaskId={TaskId} Actor={Actor}",
            id, actorITCode);

        var result = await _engine.ReturnToInitiatorAsync(id, actorITCode, reason, ct);
        return MapEngineResult(result);
    }

    // ── POST /api/_workflow/tasks/{id}/add-approver (WF-406 / WF-18) ──────────

    /// <summary>
    /// Current approver injects additional approvers into the active node (加签 — WF-18).
    ///
    /// <para>Actor ITCode SERVER-SIDE.  Engine validates that the actor owns a
    /// Pending task on the node and checks depth/state guards before inserting.</para>
    ///
    /// <para>Result codes:
    /// <list type="bullet">
    ///   <item>200 OK — Advanced (tasks injected).</item>
    ///   <item>400 Bad Request — NewApproverITCodes is empty.</item>
    ///   <item>403 Forbidden — NotAuthorized (actor has no active task on the node).</item>
    ///   <item>409 Conflict — NodeAlreadyDecided / MaxAddDepthExceeded / AlreadyHandled.</item>
    /// </list>
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/add-approver")]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AddApprover(
        Guid id,
        [FromBody] AddApproverRequest? request,
        CancellationToken ct = default)
    {
        if (request == null || request.NewApproverITCodes == null || request.NewApproverITCodes.Count == 0)
        {
            return BadRequest(new WorkflowActionResponse(
                false, "BadRequest", "NewApproverITCodes must contain at least one ITCode."));
        }

        // Actor ALWAYS server-side — never from the request body.
        var actorITCode = Wtm?.LoginUserInfo?.ITCode ?? string.Empty;

        _logger.LogInformation(
            "[WorkflowTask] AddApprover requested. TaskId={TaskId} Actor={Actor} Count={Count} Position={Position}",
            id, actorITCode, request.NewApproverITCodes.Count, request.Position);

        var result = await _engine.AddApproverAsync(
            id, actorITCode, request.NewApproverITCodes, request.Position, request.Reason, ct);
        return MapEngineResult(result);
    }

    // ── POST /api/_workflow/tasks/{id}/delegate (WF-406 / WF-19) ─────────────

    /// <summary>
    /// Mid-flight delegation (转办/委托-now): reassigns the actor's pending task to a delegatee.
    ///
    /// <para>Actor ITCode SERVER-SIDE.  Engine validates actor == AssigneeITCode via CAS.</para>
    ///
    /// <para>Result codes:
    /// <list type="bullet">
    ///   <item>200 OK — Advanced (slot reassigned).</item>
    ///   <item>400 Bad Request — DelegateeITCode is empty.</item>
    ///   <item>403 Forbidden — NotAuthorized (actor is not the assignee).</item>
    ///   <item>409 Conflict — DelegateAlreadyParticipant / TaskNotActive / NodeClosed / NodeAlreadyDecided / AlreadyHandled.</item>
    /// </list>
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/delegate")]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Delegate(
        Guid id,
        [FromBody] DelegateRequest? request,
        CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.DelegateeITCode))
        {
            return BadRequest(new WorkflowActionResponse(
                false, "BadRequest", "DelegateeITCode is required."));
        }

        // Actor ALWAYS server-side.
        var actorITCode = Wtm?.LoginUserInfo?.ITCode ?? string.Empty;

        _logger.LogInformation(
            "[WorkflowTask] Delegate requested. TaskId={TaskId} Actor={Actor} Delegatee={Delegatee}",
            id, actorITCode, LogSanitizer.Sanitize(request.DelegateeITCode));

        var result = await _engine.DelegateTaskAsync(
            id, actorITCode, request.DelegateeITCode, request.DelegationRuleId, request.Reason, ct);
        return MapEngineResult(result);
    }

    // ── POST /api/_workflow/tasks/{id}/return-to-prev (WF-406 / WF-16) ────────

    /// <summary>
    /// Return the flow to the immediately-preceding Approval node (ReturnToPrev — WF-16).
    ///
    /// <para>Actor ITCode SERVER-SIDE.  Engine validates actor == AssigneeITCode.
    /// If no preceding Approval node exists, returns 404 (NoDominatorTarget).</para>
    ///
    /// <para>Result codes:
    /// <list type="bullet">
    ///   <item>200 OK — Returned (span superseded, flow materialized at previous node).</item>
    ///   <item>403 Forbidden — NotAuthorized.</item>
    ///   <item>404 Not Found — NoDominatorTarget (no preceding Approval node).</item>
    ///   <item>409 Conflict — MaxReturnLoopsExceeded / AlreadyHandled / TaskNotActive / NodeClosed.</item>
    /// </list>
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/return-to-prev")]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ReturnToPrev(
        Guid id,
        [FromBody] ReturnToInitiatorRequest? request,
        CancellationToken ct = default)
    {
        // Actor ALWAYS server-side.
        var actorITCode = Wtm?.LoginUserInfo?.ITCode ?? string.Empty;
        var reason      = request?.Reason;

        _logger.LogInformation(
            "[WorkflowTask] ReturnToPrev requested. TaskId={TaskId} Actor={Actor}",
            id, actorITCode);

        var result = await _engine.ReturnToPrevAsync(id, actorITCode, reason, ct);
        return MapEngineResult(result);
    }

    // ── POST /api/_workflow/tasks/{id}/return-to-node (WF-406 / WF-16) ────────

    /// <summary>
    /// Return the flow to an arbitrary upstream Approval node (ReturnToNode — WF-16).
    ///
    /// <para>Actor ITCode SERVER-SIDE.  <paramref name="id"/> is the trigger task's ID.
    /// <c>TargetNodeKey</c> must identify an Approval node that dominates the trigger node.</para>
    ///
    /// <para>Result codes:
    /// <list type="bullet">
    ///   <item>200 OK — Returned.</item>
    ///   <item>400 Bad Request — TargetNodeKey is empty.</item>
    ///   <item>403 Forbidden — NotAuthorized.</item>
    ///   <item>404 Not Found — NoDominatorTarget (target is not a dominator).</item>
    ///   <item>409 Conflict — MaxReturnLoopsExceeded / AlreadyHandled / TaskNotActive / NodeClosed.</item>
    /// </list>
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/return-to-node")]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ReturnToNode(
        Guid id,
        [FromBody] ReturnToNodeRequest? request,
        CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.TargetNodeKey))
        {
            return BadRequest(new WorkflowActionResponse(
                false, "BadRequest", "TargetNodeKey is required."));
        }

        // Actor ALWAYS server-side.
        var actorITCode = Wtm?.LoginUserInfo?.ITCode ?? string.Empty;

        _logger.LogInformation(
            "[WorkflowTask] ReturnToNode requested. TaskId={TaskId} Actor={Actor} Target={Target}",
            id, actorITCode, LogSanitizer.Sanitize(request.TargetNodeKey));

        var result = await _engine.ReturnToNodeAsync(id, request.TargetNodeKey, actorITCode, request.Reason, ct);
        return MapEngineResult(result);
    }

    // ── POST /api/_workflow/revoke-delegation/{delegationRuleId} (WF-406 / WF-19, admin) ──

    /// <summary>
    /// Admin revocation: reverts all open Pending tasks produced by the given delegation rule
    /// back to their original principals (admin-only, WF-19).
    ///
    /// <para><strong>Authorization:</strong>
    /// The caller must hold the <see cref="WorkflowPrivileges.WorkflowAdmin"/> privilege.
    /// A non-admin caller receives 403 Forbidden before the engine is invoked.</para>
    ///
    /// <para>The actor ITCode comes SERVER-SIDE from <c>Wtm.LoginUserInfo</c>.</para>
    ///
    /// <para>Result codes:
    /// <list type="bullet">
    ///   <item>200 OK — Revoked, with the count of tasks reverted in the response body.</item>
    ///   <item>403 Forbidden — Caller lacks the WorkflowAdmin privilege.</item>
    /// </list>
    /// </para>
    /// </summary>
    [HttpPost("revoke-delegation/{delegationRuleId:guid}")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RevokeDelegation(
        Guid delegationRuleId,
        [FromBody] RevokeDelegationRequest? request,
        CancellationToken ct = default)
    {
        // Admin-only gate: check the WorkflowAdmin privilege server-side.
        // A client cannot self-escalate — the privilege check is done here, not in the engine.
        var isAdmin = Wtm?.IsAccessable(WorkflowPrivileges.WorkflowAdmin) == true;
        if (!isAdmin)
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new WorkflowActionResponse(false, "NotAuthorized",
                    "The WorkflowAdmin privilege is required to revoke delegation rules."));
        }

        // Actor ALWAYS server-side.
        var actorITCode = Wtm?.LoginUserInfo?.ITCode ?? string.Empty;
        var reason      = request?.Reason;

        _logger.LogInformation(
            "[WorkflowTask] RevokeDelegation requested. DelegationRuleId={RuleId} Actor={Actor}",
            delegationRuleId, actorITCode);

        var count = await _engine.RevokeDelegationAsync(delegationRuleId, actorITCode, reason, ct);

        return Ok(new { revoked = count });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private IActionResult MapEngineResult(WorkflowActionResult result)
    {
        return result.Code switch
        {
            WorkflowActionCode.Advanced
            or WorkflowActionCode.NodeCompleted
            or WorkflowActionCode.InstanceApproved
            or WorkflowActionCode.Rejected
            or WorkflowActionCode.Withdrawn
            or WorkflowActionCode.ReturnedToInitiator
            or WorkflowActionCode.Returned =>           // WF-406: 回退-to-node / ReturnToPrev success
                Ok(new WorkflowActionResponse(true, result.Code.ToString(), result.Detail)),

            // Idempotent concurrent loser — the client's intent was fulfilled by a concurrent actor.
            WorkflowActionCode.AlreadyHandled =>
                Ok(new WorkflowActionResponse(true, result.Code.ToString(), result.Detail)),

            // Race: task pointer moved or node already closed — the actor was not the active approver.
            WorkflowActionCode.TaskNotActive
            or WorkflowActionCode.NodeClosed
            or WorkflowActionCode.NodeAlreadyDecided     // WF-406: 加签 to a decided node
            or WorkflowActionCode.MaxAddDepthExceeded    // WF-406: 加签 depth cap exceeded
            or WorkflowActionCode.MaxReturnLoopsExceeded // WF-406: return loop cap exceeded
            or WorkflowActionCode.DelegateAlreadyParticipant // WF-406: delegate collision
            or WorkflowActionCode.CannotWithdrawAlreadyFinal =>
                Conflict(new WorkflowActionResponse(false, result.Code.ToString(), result.Detail)),

            WorkflowActionCode.NoDominatorTarget =>      // WF-406: no valid return target
                NotFound(new WorkflowActionResponse(false, result.Code.ToString(), result.Detail)),

            WorkflowActionCode.NotInitiator
            or WorkflowActionCode.NotAuthorized =>
                StatusCode(StatusCodes.Status403Forbidden,
                    new WorkflowActionResponse(false, result.Code.ToString(), result.Detail)),

            WorkflowActionCode.FailClosedRouting =>
                StatusCode(StatusCodes.Status500InternalServerError,
                    new WorkflowActionResponse(false, result.Code.ToString(),
                        result.Detail ?? "Condition routing failed closed.")),

            _ =>
                BadRequest(new WorkflowActionResponse(false, result.Code.ToString(), result.Detail))
        };
    }
}
