#nullable enable
// WF-14: WorkflowTaskController
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
    /// <c>Wtm.LoginUserInfo.TenantCode</c> server-side.
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
        var actorITCode = Wtm?.LoginUserInfo?.ITCode ?? string.Empty;
        var tenantCode  = Wtm?.LoginUserInfo?.TenantCode;

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
            or WorkflowActionCode.ReturnedToInitiator =>
                Ok(new WorkflowActionResponse(true, result.Code.ToString(), result.Detail)),

            // Idempotent concurrent loser — the client's intent was fulfilled by a concurrent actor.
            WorkflowActionCode.AlreadyHandled =>
                Ok(new WorkflowActionResponse(true, result.Code.ToString(), result.Detail)),

            // Race: task pointer moved or node already closed — the actor was not the active approver.
            WorkflowActionCode.TaskNotActive
            or WorkflowActionCode.NodeClosed =>
                Conflict(new WorkflowActionResponse(false, result.Code.ToString(), result.Detail)),

            WorkflowActionCode.NotInitiator
            or WorkflowActionCode.NotAuthorized =>
                StatusCode(StatusCodes.Status403Forbidden,
                    new WorkflowActionResponse(false, result.Code.ToString(), result.Detail)),

            WorkflowActionCode.CannotWithdrawAlreadyFinal =>
                Conflict(new WorkflowActionResponse(false, result.Code.ToString(), result.Detail)),

            WorkflowActionCode.FailClosedRouting =>
                StatusCode(StatusCodes.Status500InternalServerError,
                    new WorkflowActionResponse(false, result.Code.ToString(),
                        result.Detail ?? "Condition routing failed closed.")),

            _ =>
                BadRequest(new WorkflowActionResponse(false, result.Code.ToString(), result.Detail))
        };
    }
}
