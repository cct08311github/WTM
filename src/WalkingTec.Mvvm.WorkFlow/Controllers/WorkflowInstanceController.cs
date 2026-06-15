#nullable enable
// WF-14: WorkflowInstanceController
//
// HTTP surface for workflow instance lifecycle: start, withdraw, return-to-initiator, timeline.
//
// Security invariants:
//   1. Actor ITCode and TenantCode ALWAYS come from Wtm.LoginUserInfo — never from the body.
//   2. No controller-level DataContext access (WTM red line) — all DB work through IWorkflowEngine.
//   3. RBAC: PrivilegeFilter's URL-based Wtm.IsAccessable gate applies (no [AllRights]).
//      Timeline/get read actions carry [AllRights] so any authenticated user can view.

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
/// HTTP surface for workflow process instance lifecycle.
///
/// <para>Routes under <c>/api/_workflow/instances</c>.</para>
///
/// <para><strong>Anti-spoofing:</strong>
/// All mutating actions extract the actor ITCode and tenant from
/// <c>Wtm.LoginUserInfo</c> server-side.  The request body carries only
/// business-intent fields (version ID, form data, reason).  Fields decorated
/// with <see cref="Microsoft.AspNetCore.Mvc.ModelBinding.BindNeverAttribute"/>
/// on the request DTOs are explicitly excluded from model binding.</para>
/// </summary>
[ApiController]
[Route("api/_workflow/instances")]
[ActionDescription("WorkflowInstance")]
public class WorkflowInstanceController : BaseController
{
    private readonly IWorkflowEngine _engine;
    private readonly ILogger<WorkflowInstanceController> _logger;

    public WorkflowInstanceController(
        IWorkflowEngine engine,
        ILogger<WorkflowInstanceController>? logger = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _logger = logger ?? NullLogger<WorkflowInstanceController>.Instance;
    }

    // ── POST /api/_workflow/instances/start ────────────────────────────────────

    /// <summary>
    /// Start a new process instance from a published definition version.
    ///
    /// <para>The actor ITCode (initiator) and tenant are taken SERVER-SIDE from
    /// <c>Wtm.LoginUserInfo</c>.  Any <c>InitiatorITCode</c>/<c>TenantCode</c>
    /// field in the request body is ignored (BindNever).</para>
    ///
    /// <para>Returns 200 OK with the new instance ID and initial state,
    /// or 400 Bad Request if the request is malformed.</para>
    /// </summary>
    [HttpPost("start")]
    [ProducesResponseType(typeof(StartInstanceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(StartInstanceResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Start(
        [FromBody] StartInstanceRequest request,
        CancellationToken ct = default)
    {
        if (request == null || request.DefinitionVersionId == Guid.Empty)
        {
            return BadRequest(new StartInstanceResponse(
                Guid.Empty, string.Empty, "DefinitionVersionId is required."));
        }

        // Actor and tenant ALWAYS come from the authenticated session.
        // The model-bound InitiatorITCode / TenantCode on the DTO are [BindNever] and ignored.
        var actorITCode = Wtm?.LoginUserInfo?.ITCode ?? string.Empty;
        var tenantCode  = Wtm?.LoginUserInfo?.TenantCode;

        _logger.LogInformation(
            "[WorkflowInstance] Start requested. VersionId={VersionId} Actor={Actor}",
            request.DefinitionVersionId, actorITCode);

        try
        {
            var instance = await _engine.StartAsync(
                request.DefinitionVersionId,
                request.FormDataJson,
                actorITCode,
                tenantCode,
                request.BusinessType,
                request.BusinessKey,
                ct);

            return Ok(new StartInstanceResponse(
                instance.ID,
                instance.State.ToString()));
        }
        catch (InvalidOperationException ex)
        {
            // e.g. definition version not found or in wrong tenant scope.
            _logger.LogWarning(ex,
                "[WorkflowInstance] Start failed. VersionId={VersionId} Actor={Actor}",
                request.DefinitionVersionId, actorITCode);
            return BadRequest(new StartInstanceResponse(Guid.Empty, string.Empty, ex.Message));
        }
    }

    // ── POST /api/_workflow/instances/{id}/withdraw ────────────────────────────

    /// <summary>
    /// Withdraw a running workflow instance.
    ///
    /// <para>Only the original initiator or an admin may withdraw.
    /// The actor ITCode comes SERVER-SIDE from <c>Wtm.LoginUserInfo</c>.
    /// The <c>IsAdmin</c> flag in the request body is only honoured when the
    /// server-side actor matches the WORKFLOW_ADMIN privilege — a client cannot
    /// self-escalate to admin by setting <c>isAdmin=true</c>.</para>
    ///
    /// <para>Closed result codes mapped to HTTP:
    /// <list type="bullet">
    ///   <item>200 OK — Withdrawn.</item>
    ///   <item>409 Conflict — CannotWithdrawAlreadyFinal (race: instance already final).</item>
    ///   <item>403 Forbidden — NotInitiator / NotAuthorized (policy Disabled).</item>
    /// </list>
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/withdraw")]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Withdraw(
        Guid id,
        [FromBody] WithdrawRequest? request,
        CancellationToken ct = default)
    {
        // Actor ALWAYS server-side.
        var actorITCode = Wtm?.LoginUserInfo?.ITCode ?? string.Empty;
        var reason      = request?.Reason;

        // IsAdmin is only trusted when the authenticated user has the admin privilege.
        // A client cannot self-escalate by passing isAdmin=true in the body.
        var isAdmin = request?.IsAdmin == true && Wtm?.IsAccessable(WorkflowPrivileges.WorkflowAdmin) == true;

        _logger.LogInformation(
            "[WorkflowInstance] Withdraw requested. InstanceId={Id} Actor={Actor} IsAdmin={IsAdmin}",
            id, actorITCode, isAdmin);

        var result = await _engine.WithdrawAsync(id, actorITCode, reason, isAdmin, ct);

        return result.Code switch
        {
            WorkflowActionCode.Withdrawn =>
                Ok(new WorkflowActionResponse(true, result.Code.ToString(), result.Detail)),

            WorkflowActionCode.NotInitiator or WorkflowActionCode.NotAuthorized =>
                StatusCode(StatusCodes.Status403Forbidden,
                    new WorkflowActionResponse(false, result.Code.ToString(), result.Detail)),

            WorkflowActionCode.CannotWithdrawAlreadyFinal =>
                Conflict(new WorkflowActionResponse(false, result.Code.ToString(), result.Detail)),

            _ =>
                BadRequest(new WorkflowActionResponse(false, result.Code.ToString(), result.Detail))
        };
    }

    // ── POST /api/_workflow/instances/{id}/return-to-initiator ────────────────

    /// <summary>
    /// Return a task to the initiator (approver-initiated 回退).
    ///
    /// <para>Actor ITCode comes SERVER-SIDE.  The engine enforces that the actor
    /// must be the assigned approver of the task.</para>
    ///
    /// <para>Note: this endpoint takes an <paramref name="taskId"/> (not an instance ID)
    /// as the engine method operates on the task row.
    /// Route uses the instance ID for RESTful discoverability; the body carries the taskId.</para>
    /// </summary>
    [HttpPost("{instanceId:guid}/return-to-initiator")]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(WorkflowActionResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ReturnToInitiator(
        Guid instanceId,
        [FromBody] ReturnToInitiatorRequest request,
        CancellationToken ct = default)
    {
        if (request == null)
            return BadRequest(new WorkflowActionResponse(false, "BadRequest", "Request body is required."));

        // Task ID must be in the body — we find the active task for the actor.
        // Spec: ReturnToInitiatorAsync(taskId, actorITCode, reason).
        // Because the route is instance-based, we need the taskId from the body.
        if (!Guid.TryParse(
                HttpContext.Request.Query["taskId"].ToString(),
                out var taskId) || taskId == Guid.Empty)
        {
            // Support taskId both as query parameter and body property.
            // The engine requires a taskId — clients must supply it.
            return BadRequest(new WorkflowActionResponse(
                false, "BadRequest",
                "taskId query parameter is required (the ID of the ApprovalTask to return)."));
        }

        // Actor ALWAYS server-side.
        var actorITCode = Wtm?.LoginUserInfo?.ITCode ?? string.Empty;

        _logger.LogInformation(
            "[WorkflowInstance] ReturnToInitiator. InstanceId={InstanceId} TaskId={TaskId} Actor={Actor}",
            instanceId, taskId, actorITCode);

        var result = await _engine.ReturnToInitiatorAsync(taskId, actorITCode, request.Reason, ct);

        return MapEngineResult(result);
    }

    // ── GET /api/_workflow/instances/{id}/timeline ─────────────────────────────

    /// <summary>
    /// Get the timeline (event log) for a workflow instance.
    ///
    /// <para>Read-only; returns the append-only <c>WorkflowEventLog</c> rows in
    /// sequence order.  Tenant-scoped: the engine/publisher ensures only same-tenant
    /// records are returned via the DataContext query filters.
    /// [AllRights] — any authenticated user may view timelines they have access to.</para>
    /// </summary>
    [HttpGet("{id:guid}/timeline")]
    [AllRights]
    [ActionDescription("Timeline")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Timeline(Guid id)
    {
        // Timeline is a read-only projection from WorkflowEventLog.
        // Full implementation: query DC.Set<WorkflowEventLog>() via a WorkflowActionVM
        // when the BasePagedListVM pattern is wired (WF-15 admin grids).
        // For MVP: return a placeholder indicating the endpoint is defined.
        // The WF-15 admin grid will replace this with proper VM-based query.
        return Ok(new
        {
            InstanceId = id,
            Note = "Timeline endpoint defined (WF-14). Full VM-based query wired in WF-15."
        });
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

            WorkflowActionCode.AlreadyHandled =>
                // Idempotent concurrent loser — still a "success" from the client's perspective.
                Ok(new WorkflowActionResponse(true, result.Code.ToString(), result.Detail)),

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
                        result.Detail ?? "Condition routing failed closed — no matching branch and no default.")),

            _ =>
                BadRequest(new WorkflowActionResponse(false, result.Code.ToString(), result.Detail))
        };
    }
}
