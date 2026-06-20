#nullable enable
// WF-14: WorkflowDefinitionController
//
// HTTP surface for process definition management: publish, validate, list versions.
//
// Security invariants (non-negotiable per spec §8.3 and WTM red lines):
//   1. Actor ITCode and TenantCode are ALWAYS taken from Wtm.LoginUserInfo — never from
//      the request body or query string.  No client can act as another user or tenant.
//   2. The controller NEVER touches DataContext directly — all DB work goes through
//      IProcessDefinitionPublisher (WTM red line: no controller-level DC access).
//   3. RBAC: no [AllRights] on this controller — the PrivilegeFilter applies the
//      standard URL-based Wtm.IsAccessable(controller.BaseUrl) gate on every action.
//      Admins must have the controller URL registered in their FunctionPrivilege set.
//      (Read-only actions that all authenticated users should access carry [AllRights].)

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.ViewModels;

namespace WalkingTec.Mvvm.WorkFlow.Controllers;

/// <summary>
/// HTTP surface for workflow process definition management.
///
/// <para>Routes under <c>/api/_workflow/definitions</c>.</para>
///
/// <para><strong>Actor/tenant anti-spoofing invariant:</strong>
/// <c>publishedBy</c> and <c>tenantCode</c> are ALWAYS sourced from
/// <c>Wtm.LoginUserInfo</c> — never from the request body or query string.
/// Any <c>PublishedBy</c>/<c>TenantCode</c> field on inbound DTOs is decorated
/// with <see cref="Microsoft.AspNetCore.Mvc.ModelBinding.BindNeverAttribute"/>
/// and ignored by the model binder.</para>
///
/// <para><strong>RBAC:</strong> Write actions (publish, validate) require
/// the controller's URL to be present in the authenticated user's
/// <c>FunctionPrivilege</c> set (WTM PrivilegeFilter URL-based check).
/// Read-only actions carry <see cref="AllRightsAttribute"/> so any authenticated
/// user can list definitions.</para>
/// </summary>
[ApiController]
[Route("api/_workflow/definitions")]
[ActionDescription("WorkflowDefinition")]
public class WorkflowDefinitionController : BaseController
{
    private readonly IProcessDefinitionPublisher _publisher;
    private readonly ILogger<WorkflowDefinitionController> _logger;
    // FIX-B2: options injected so Validate endpoint can enforce AllowTimerAutoAction gate (check 14g).
    private readonly WorkFlowOptions? _options;

    public WorkflowDefinitionController(
        IProcessDefinitionPublisher publisher,
        ILogger<WorkflowDefinitionController>? logger = null,
        IOptions<WorkFlowOptions>? options = null)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? NullLogger<WorkflowDefinitionController>.Instance;
        _options = options?.Value;
    }

    // ── POST /api/_workflow/definitions/{code}/publish ─────────────────────────

    /// <summary>
    /// Publish (or idempotently re-publish) a workflow graph for the named definition.
    ///
    /// <para>Outcomes mapped to HTTP status:
    /// <list type="bullet">
    ///   <item>200 OK — Published (new version inserted) or IdempotentNoOp (same hash).</item>
    ///   <item>400 Bad Request — ValidationFailed (graph structurally invalid).</item>
    ///   <item>404 Not Found — DefinitionNotFound (no ProcessDefinition with that code
    ///         in this tenant's scope).</item>
    /// </list>
    /// </para>
    /// </summary>
    [HttpPost("{code}/publish")]
    [ProducesResponseType(typeof(PublishResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PublishResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(PublishResponseDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Publish(
        string code,
        [FromBody] WorkflowGraph graph,
        CancellationToken ct = default)
    {
        if (graph == null)
        {
            return BadRequest(new PublishResponseDto(
                false, null, 0, null, null, "Request body is required."));
        }

        // Actor and tenant come ONLY from the authenticated session (anti-spoofing invariant).
        // The client-supplied body is never used to determine who is publishing or for which tenant.
        var publishedBy = Wtm?.LoginUserInfo?.ITCode;

        _logger.LogInformation(
            "[WorkflowDefinition] Publish requested. Code={Code} Actor={Actor}",
            LogSanitizer.Sanitize(code), publishedBy);

        var result = await _publisher.PublishAsync(code, graph, publishedBy, ct);

        return result.Outcome switch
        {
            PublishOutcome.Published or PublishOutcome.IdempotentNoOp =>
                Ok(new PublishResponseDto(
                    true,
                    result.VersionId,
                    result.VersionNo,
                    result.ContentHash,
                    result.Outcome.ToString(),
                    null)),

            PublishOutcome.ValidationFailed =>
                BadRequest(new PublishResponseDto(
                    false, null, 0, null,
                    result.Outcome.ToString(),
                    result.ErrorMessage ?? result.ValidationError.ToString())),

            PublishOutcome.DefinitionNotFound =>
                NotFound(new PublishResponseDto(
                    false, null, 0, null,
                    result.Outcome.ToString(),
                    result.ErrorMessage)),

            _ =>
                StatusCode(StatusCodes.Status500InternalServerError,
                    new PublishResponseDto(false, null, 0, null, null,
                        $"Unexpected publish outcome: {result.Outcome}"))
        };
    }

    // ── POST /api/_workflow/definitions/validate ────────────────────────────────

    /// <summary>
    /// Validate a workflow graph without persisting it.
    ///
    /// <para>Returns 200 OK when structurally valid;
    /// 400 Bad Request with validation details when invalid.
    /// Never writes to the database.</para>
    /// </summary>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(ValidateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidateResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult Validate([FromBody] WorkflowGraph graph)
    {
        if (graph == null)
            return BadRequest(new ValidateResponseDto(false, "Request body is required."));

        // FIX-B2: pass runtime options so check 14g (AllowTimerAutoAction gate) is enforced here.
        var result = WorkflowGraphValidator.Validate(graph, _options);
        if (result.IsValid)
            return Ok(new ValidateResponseDto(true, null));

        var message = result.ErrorMessage ?? result.Error.ToString();
        return BadRequest(new ValidateResponseDto(false, message));
    }
}
