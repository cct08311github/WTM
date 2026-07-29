#nullable enable
// WF-21.2: WorkflowDesignerController — definition catalog read/create/metadata endpoints.
// WF-21.3: Draft CRUD, raw validate/publish, bootstrap, designer-scoped antiforgery filter.
//
// Endpoints (WF-21.2):
//   GET  /api/_workflow/designer/definitions                  → list heads (paged)
//   POST /api/_workflow/designer/definitions                  → create head
//   PUT  /api/_workflow/designer/definitions/{code}           → metadata update
//   GET  /api/_workflow/designer/definitions/{code}/graph     → current published graph
//   GET  /api/_workflow/designer/definitions/{code}/versions  → version history
//   GET  /api/_workflow/designer/versions/{id}/graph          → one immutable version
//
// Endpoints (WF-21.3):
//   GET  /api/_workflow/designer/bootstrap                    → issue antiforgery token
//   GET  /api/_workflow/designer/definitions/{code}/draft     → load draft
//   PUT  /api/_workflow/designer/definitions/{code}/draft     → save draft (raw body)
//   DELETE /api/_workflow/designer/definitions/{code}/draft   → discard draft
//   POST /api/_workflow/designer/definitions/{code}/publish   → publish raw graph (CAS)
//   POST /api/_workflow/designer/validate                     → validate raw graph
//
// Security invariants (non-negotiable, per spec §4 and WTM red lines):
//   1. No [AllRights] — every action is protected by the PrivilegeFilter URL-RBAC gate
//      via WorkflowPrivileges.DesignerBase.  Admins register this URL in FunctionPrivilege.
//   2. Actor ITCode and TenantCode always sourced from Wtm.LoginUserInfo — never from
//      the request body or query string.
//   3. Controller NEVER touches IDataContext directly — all DB work goes through
//      IWorkflowDefinitionStore or IProcessDefinitionPublisher (WTM red line).
//   4. Code regex enforced before store call: ^[A-Za-z0-9_\-\.]{1,64}$
//   5. Head metadata fields keep the typed-binding LTGT defense (they ARE rendered in UI).
//   6. Antiforgery (X-WTM-WF-XSRF) applied ONLY on mutating actions via [WfDesignerAntiforgery]
//      — GET endpoints never require the header (idempotent reads are safe).
//   7. Raw body size capped at WorkFlowOptions.Designer.MaxGraphBytes (default 1 MiB)
//      before any JSON deserialization.

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.ViewModels;

namespace WalkingTec.Mvvm.WorkFlow.Controllers;

// ── WF-21.3 / FIX-B3c: Designer antiforgery action filter ────────────────────

/// <summary>
/// Action filter that validates the <c>X-WTM-WF-XSRF</c> antiforgery header on
/// designer mutating actions (POST create, PUT draft, DELETE draft, POST publish).
///
/// <para><strong>Scope:</strong> applied ONLY to the designer controller's mutating actions.
/// GET/HEAD endpoints are explicitly excluded.
/// This does NOT touch the global WTM antiforgery pipeline — it is the first and only
/// antiforgery usage in the framework, scoped entirely to the workflow designer (spec §4).</para>
///
/// <para><strong>FIX-B3c — AntiforgeryOptions.HeaderName = DesignerHeaderNames.Xsrf.</strong>
/// <c>AddWtmWorkFlowDesigner()</c> configures <c>AntiforgeryOptions.HeaderName</c> to
/// <c>"X-WTM-WF-XSRF"</c>.  This is the direct, reliable approach: ASP.NET Core's
/// <c>IAntiforgery.ValidateRequestAsync</c> reads the request token from the HTTP header
/// named by <c>AntiforgeryOptions.HeaderName</c>.  When that name is <c>"X-WTM-WF-XSRF"</c>,
/// the framework reads it directly from the designer header — no per-request header-injection
/// dance required.  The previous approach (injecting the token into <c>"X-XSRF-TOKEN"</c>)
/// was wrong because <c>"X-XSRF-TOKEN"</c> is NOT the ASP.NET Core default
/// (<c>"RequestVerificationToken"</c> is the framework default), so <c>ValidateRequestAsync</c>
/// never found the token → permanent 400 on every mutating designer call.</para>
///
/// <para>If <c>IAntiforgery</c> is not registered the filter short-circuits with 503.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class WfDesignerAntiforgeryAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Resolve IAntiforgery from the request services.
        var antiforgery = context.HttpContext.RequestServices.GetService(typeof(IAntiforgery)) as IAntiforgery;

        if (antiforgery is null)
        {
            // Host did not call AddAntiforgery() — surface the misconfiguration.
            context.Result = new ObjectResult(
                "Designer antiforgery not configured. " +
                "Call services.AddAntiforgery() before AddWtmWorkFlowDesigner() in your DI setup.")
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
            return;
        }

        // FIX-B3c: Because AddWtmWorkFlowDesigner() configures
        // AntiforgeryOptions.HeaderName = DesignerHeaderNames.Xsrf ("X-WTM-WF-XSRF"),
        // ValidateRequestAsync reads the token directly from X-WTM-WF-XSRF.
        // Guard the empty-header case early for a clear error message.
        var designerToken = context.HttpContext.Request.Headers[DesignerHeaderNames.Xsrf].ToString();
        if (string.IsNullOrEmpty(designerToken))
        {
            context.Result = new BadRequestObjectResult(
                $"Missing or invalid {DesignerHeaderNames.Xsrf} antiforgery token.");
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new BadRequestObjectResult(
                $"Missing or invalid {DesignerHeaderNames.Xsrf} antiforgery token.");
            return;
        }

        await next();
    }
}

// ── Controller ────────────────────────────────────────────────────────────────

/// <summary>
/// HTTP surface for the low-code workflow designer — definition catalog operations
/// and (WF-21.3) draft CRUD, raw publish/validate, and bootstrap.
///
/// <para>Routes under <c>/api/_workflow/designer</c>.</para>
///
/// <para><strong>RBAC:</strong> No <c>[AllRights]</c> on this controller.
/// The PrivilegeFilter applies <c>Wtm.IsAccessable(controller.BaseUrl)</c> for every action,
/// matching against <see cref="WorkflowPrivileges.DesignerBase"/>.
/// Consumers must register that URL in their <c>FunctionPrivilege</c> set for the
/// relevant admin role(s).</para>
///
/// <para><strong>Actor/tenant anti-spoofing:</strong>
/// Actor ITCode and TenantCode are ALWAYS sourced from <c>Wtm.LoginUserInfo</c> —
/// never from the request body or query string.</para>
/// </summary>
[ApiController]
[Route("api/_workflow/designer")]
[ActionDescription("WorkflowDesigner")]
public class WorkflowDesignerController : BaseController
{
    // Definition code must be safe as a URL path segment and a DB key.
    // NodeKey uses the same charset (WF-21.0 validator requirement).
    private static readonly Regex DefinitionCodeRegex =
        new(@"^[A-Za-z0-9_\-\.]{1,64}$", RegexOptions.Compiled);

    // FIX-A2 (Issue #300): Designer services are resolved via IServiceProvider.GetService()
    // (optional resolution) so the controller can be instantiated in hosts that did NOT call
    // AddWtmWorkFlowDesigner(). When the designer feature is absent, all fields are null and
    // every action returns NotFound() before any further work. This prevents the auto-discovered
    // controller from causing 500 activation errors in non-opted-in consumer hosts.
    private readonly IWorkflowDefinitionStore? _store;
    private readonly IProcessDefinitionPublisher? _publisher;
    private readonly IOptions<WorkFlowOptions>? _options;
    private readonly ILogger<WorkflowDesignerController> _logger;

    // Whether the designer feature is available in this host.
    private bool IsDesignerRegistered => _store is not null;

    public WorkflowDesignerController(
        IServiceProvider serviceProvider,
        ILogger<WorkflowDesignerController>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        // Optional resolution: null when AddWtmWorkFlowDesigner() was not called.
        _store     = serviceProvider.GetService<IWorkflowDefinitionStore>();
        _publisher = serviceProvider.GetService<IProcessDefinitionPublisher>();
        _options   = serviceProvider.GetService<IOptions<WorkFlowOptions>>();
        _logger    = logger ?? NullLogger<WorkflowDesignerController>.Instance;
    }

    // ── WF-21.3: GET /api/_workflow/designer/bootstrap ────────────────────────

    /// <summary>
    /// Bootstrap endpoint: sets the antiforgery cookie and returns its value.
    ///
    /// <para>The JS designer calls this once on load; the response sets a cookie that
    /// the browser attaches to subsequent requests, and the JSON body contains the
    /// token value to be sent as <c>X-WTM-WF-XSRF</c> on mutating calls.</para>
    ///
    /// <para>GET is idempotent — no antiforgery check required here.</para>
    /// </summary>
    [HttpGet("bootstrap")]
    [ActionDescription("Bootstrap")]
    [ProducesResponseType(typeof(BootstrapResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult Bootstrap()
    {
        // FIX-A2: non-opted-in hosts return 404 before any action work.
        if (!IsDesignerRegistered) return NotFound();

        var antiforgery = HttpContext.RequestServices.GetService(typeof(IAntiforgery)) as IAntiforgery;

        if (antiforgery is null)
        {
            // Antiforgery not configured — return a sentinel so the client can detect it.
            return Ok(new BootstrapResponseDto(null, "Antiforgery not configured."));
        }

        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new BootstrapResponseDto(tokens.RequestToken, null));
    }

    // ── GET /api/_workflow/designer/definitions ────────────────────────────────

    /// <summary>
    /// Return a paged list of definition heads. Intended to be scoped to the current tenant,
    /// but the tenant filter does not currently reach <c>ProcessDefinition</c> (#899) -- the
    /// list currently includes all tenants' definitions.
    /// </summary>
    /// <param name="page">1-based page number (default: 1).</param>
    /// <param name="pageSize">Items per page (default: 20; max 200).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("definitions")]
    [ActionDescription("ListDefinitions")]
    [ProducesResponseType(typeof(DefinitionListResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListDefinitions(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!IsDesignerRegistered) return NotFound();
        var result = await _store!.ListDefinitionsAsync(page, pageSize, ct);
        return Ok(new DefinitionListResponseDto(result.Items, result.TotalCount, result.Page, result.PageSize));
    }

    // ── POST /api/_workflow/designer/definitions ───────────────────────────────

    /// <summary>
    /// Create a new definition head with no initial version.
    ///
    /// <para>Outcomes:
    /// <list type="bullet">
    ///   <item>201 Created — new head inserted; <c>Location</c> header set.</item>
    ///   <item>400 Bad Request — invalid code format or model-state error.</item>
    ///   <item>409 Conflict — code already exists. Currently checked globally, not
    ///   per-tenant (#899): a code used by a different tenant is also rejected here.</item>
    /// </list>
    /// </para>
    /// </summary>
    [HttpPost("definitions")]
    [WfDesignerAntiforgery]
    [ProducesResponseType(typeof(CreateDefinitionResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(CreateDefinitionResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(CreateDefinitionResponseDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateDefinition(
        [FromBody] CreateDefinitionRequest request,
        CancellationToken ct = default)
    {
        if (!IsDesignerRegistered) return NotFound();
        if (request is null)
            return BadRequest(new CreateDefinitionResponseDto(false, null, null, "Request body is required."));

        if (!ModelState.IsValid)
        {
            var firstError = GetFirstModelError();
            return BadRequest(new CreateDefinitionResponseDto(false, null, null, firstError));
        }

        // Enforce code charset: ^[A-Za-z0-9_\-\.]{1,64}$
        if (!DefinitionCodeRegex.IsMatch(request.Code))
        {
            return BadRequest(new CreateDefinitionResponseDto(
                false, null, null,
                "Definition code must match ^[A-Za-z0-9_\\-\\.]{1,64}$."));
        }

        // Anti-spoofing: actor and tenant always server-side.
        var tenantCode = Wtm?.LoginUserInfo?.TenantCode;
        var createdBy  = Wtm?.LoginUserInfo?.ITCode;

        _logger.LogInformation(
            "[WorkflowDesigner] CreateDefinition Code={Code} Actor={Actor}",
            LogSanitizer.Sanitize(request.Code), createdBy);

        var result = await _store!.CreateDefinitionAsync(request, tenantCode, createdBy, ct);

        if (result.Outcome == CreateDefinitionOutcome.DuplicateCode)
            // #899: the duplicate check is currently global (no TenantCode predicate), so
            // do not claim "in this tenant" here -- that would be inaccurate today.
            return Conflict(new CreateDefinitionResponseDto(
                false, null, null,
                $"A definition with code '{request.Code}' already exists."));

        // 201 Created with Location pointing at the graph endpoint.
        var location = Url.Action(
            nameof(GetCurrentGraph),
            new { code = result.Code }) ?? $"/api/_workflow/designer/definitions/{result.Code}/graph";

        return Created(location,
            new CreateDefinitionResponseDto(true, result.Id, result.Code, null));
    }

    // ── PUT /api/_workflow/designer/definitions/{code} ─────────────────────────

    /// <summary>
    /// Update mutable head metadata: Name, Category, and/or IsEnabled.
    ///
    /// <para>Code and TenantCode are immutable and cannot be changed via this endpoint.</para>
    /// </summary>
    [HttpPut("definitions/{code}")]
    [WfDesignerAntiforgery]
    [ActionDescription("UpdateDefinitionMetadata")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateDefinitionMetadata(
        string code,
        [FromBody] UpdateDefinitionMetadataRequest request,
        CancellationToken ct = default)
    {
        if (!IsDesignerRegistered) return NotFound();
        if (request is null)
            return BadRequest("Request body is required.");

        if (!ModelState.IsValid)
            return BadRequest(GetFirstModelError());

        // All three fields null = no-op (still succeeds if definition exists).
        var updated = await _store!.UpdateDefinitionMetadataAsync(code, request, ct);

        if (!updated)
            return NotFound();

        return NoContent();
    }

    // ── GET /api/_workflow/designer/definitions/{code}/graph ───────────────────

    /// <summary>
    /// Return the current published version's GraphJson and metadata for a definition.
    ///
    /// <para>GraphJson is the verbatim stored string — byte-faithful to the canonical raw
    /// bytes persisted by <c>PublishRawAsync</c>.  When the definition has no published
    /// version yet, <c>GraphJson</c> is <c>null</c>.</para>
    /// </summary>
    [HttpGet("definitions/{code}/graph")]
    [ActionDescription("GetCurrentGraph")]
    [ProducesResponseType(typeof(DefinitionGraphEnvelope), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCurrentGraph(string code, CancellationToken ct = default)
    {
        if (!IsDesignerRegistered) return NotFound();
        var envelope = await _store!.GetCurrentGraphAsync(code, ct);
        if (envelope is null)
            return NotFound();

        return Ok(envelope);
    }

    // ── GET /api/_workflow/designer/definitions/{code}/versions ────────────────

    /// <summary>
    /// Return the complete version history for a definition, newest first.
    /// </summary>
    [HttpGet("definitions/{code}/versions")]
    [ActionDescription("GetVersionHistory")]
    [ProducesResponseType(typeof(VersionHistoryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetVersionHistory(string code, CancellationToken ct = default)
    {
        if (!IsDesignerRegistered) return NotFound();
        var result = await _store!.GetVersionHistoryAsync(code, ct);
        if (result is null)
            return NotFound();

        return Ok(result);
    }

    // ── GET /api/_workflow/designer/versions/{id}/graph ────────────────────────

    /// <summary>
    /// Return the verbatim GraphJson of one immutable version.
    ///
    /// <para>Intended to make cross-tenant ID access behave as 404 via the DataContext tenant
    /// filter, but that filter does not currently reach this entity type (#899) — do not rely
    /// on this until it lands.</para>
    /// </summary>
    [HttpGet("versions/{id:guid}/graph")]
    [ActionDescription("GetVersionGraph")]
    [ProducesResponseType(typeof(VersionGraphEnvelope), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetVersionGraph(Guid id, CancellationToken ct = default)
    {
        if (!IsDesignerRegistered) return NotFound();
        var envelope = await _store!.GetVersionGraphAsync(id, ct);
        if (envelope is null)
            return NotFound();

        return Ok(envelope);
    }

    // ── WF-21.3: GET /api/_workflow/designer/definitions/{code}/draft ──────────

    /// <summary>
    /// Return the current draft for a definition, or 404 when none exists.
    ///
    /// <para>Returns 404 when the definition does not exist OR when there is no draft.
    /// The JS designer distinguishes via the preceding graph envelope's
    /// <c>Draft</c> field (from <see cref="GetCurrentGraph"/>).</para>
    /// </summary>
    [HttpGet("definitions/{code}/draft")]
    [ActionDescription("GetDraft")]
    [ProducesResponseType(typeof(DraftInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDraft(string code, CancellationToken ct = default)
    {
        if (!IsDesignerRegistered) return NotFound();
        if (!DefinitionCodeRegex.IsMatch(code ?? string.Empty))
            return BadRequest("Invalid definition code format.");

        var draft = await _store!.GetDraftAsync(code, ct);
        if (draft is null)
            return NotFound();

        // Echo the RowVersion as ETag for subsequent If-Match requests.
        Response.Headers["ETag"] = $"\"{draft.RowVer}\"";
        return Ok(draft);
    }

    // ── WF-21.3: PUT /api/_workflow/designer/definitions/{code}/draft ──────────

    /// <summary>
    /// Create or update the draft for a definition (raw body = graph JSON).
    ///
    /// <para>Concurrency semantics via HTTP conditional headers:</para>
    /// <list type="bullet">
    ///   <item><c>If-None-Match: *</c> → create semantics; returns 409 if draft already exists.</item>
    ///   <item><c>If-Match: "N"</c> → update semantics; RowVersion must match N; row-gone → 409
    ///     (post-publish resurrection guard).</item>
    /// </list>
    ///
    /// <para>Body: raw graph JSON (Content-Type: application/json).
    /// Body size is capped at <see cref="WorkFlowOptions.DesignerOptions.MaxGraphBytes"/>.</para>
    ///
    /// <para>Antiforgery: <c>X-WTM-WF-XSRF</c> header required (mutating action).</para>
    /// </summary>
    [HttpPut("definitions/{code}/draft")]
    [WfDesignerAntiforgery]
    [ActionDescription("SaveDraft")]
    [ProducesResponseType(typeof(SaveDraftResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413RequestEntityTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<IActionResult> SaveDraft(string code, CancellationToken ct = default)
    {
        if (!IsDesignerRegistered) return NotFound();
        if (!DefinitionCodeRegex.IsMatch(code ?? string.Empty))
            return BadRequest("Invalid definition code format.");

        // Content-Type gate: raw JSON bodies only.
        if (Request.ContentType is null || !Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                "Content-Type must be application/json.");

        var maxBytes = _options!.Value.Designer.MaxGraphBytes;
        if (Request.ContentLength.HasValue && Request.ContentLength.Value > maxBytes)
            return StatusCode(StatusCodes.Status413RequestEntityTooLarge,
                $"Request body exceeds the maximum allowed size of {maxBytes} bytes.");

        string graphJson;
        try
        {
            graphJson = await ReadRawBodyAsync(Request, maxBytes, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Body exceeds"))
        {
            return StatusCode(StatusCodes.Status413RequestEntityTooLarge, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(graphJson))
            return BadRequest("Request body must be a non-empty JSON object.");

        // Parse the If-None-Match / If-Match conditional headers.
        bool create;
        uint expectedRowVersion = 0;

        var ifNoneMatch = Request.Headers["If-None-Match"].ToString();
        var ifMatch     = Request.Headers["If-Match"].ToString();

        if (ifNoneMatch == "*")
        {
            create = true;
        }
        else if (!string.IsNullOrEmpty(ifMatch))
        {
            // Expect: If-Match: "N" where N is an unsigned integer RowVersion.
            var stripped = ifMatch.Trim('"');
            if (!uint.TryParse(stripped, out expectedRowVersion))
                return BadRequest("If-Match value must be a quoted unsigned integer (e.g. \"3\").");
            create = false;
        }
        else
        {
            return BadRequest(
                "One of If-None-Match: * (create) or If-Match: \"N\" (update) is required.");
        }

        // Optional BaseContentHash — the published version hash the client started editing from.
        var baseContentHash = Request.Headers[DesignerHeaderNames.BaseHash].ToString();
        if (string.IsNullOrEmpty(baseContentHash)) baseContentHash = null;

        var savedBy = Wtm?.LoginUserInfo?.ITCode;

        _logger.LogDebug(
            "[WorkflowDesigner] SaveDraft code={Code} create={Create} actor={Actor}",
            LogSanitizer.Sanitize(code), create, savedBy);

        var result = await _store!.SaveDraftAsync(
            code, graphJson, baseContentHash,
            expectedRowVersion, create, savedBy, ct);

        return result.Outcome switch
        {
            SaveDraftOutcome.Saved =>
                Ok(new SaveDraftResponseDto(result.NewRowVersion, result.NewRowVersion.ToString())),
            SaveDraftOutcome.DefinitionNotFound =>
                NotFound(),
            SaveDraftOutcome.Conflict =>
                Conflict("The draft was modified or deleted concurrently. " +
                         "Reload the current draft (GET draft) and retry."),
            _ => StatusCode(StatusCodes.Status500InternalServerError, "Unexpected outcome."),
        };
    }

    // ── WF-21.3: DELETE /api/_workflow/designer/definitions/{code}/draft ───────

    /// <summary>
    /// Explicitly discard the draft for a definition.
    ///
    /// <para>Idempotent: 204 No Content whether or not a draft existed.
    /// Returns 404 only when the <em>definition</em> itself does not exist.</para>
    ///
    /// <para>Antiforgery: <c>X-WTM-WF-XSRF</c> header required (mutating action).</para>
    /// </summary>
    [HttpDelete("definitions/{code}/draft")]
    [WfDesignerAntiforgery]
    [ActionDescription("DeleteDraft")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteDraft(string code, CancellationToken ct = default)
    {
        if (!IsDesignerRegistered) return NotFound();
        if (!DefinitionCodeRegex.IsMatch(code ?? string.Empty))
            return BadRequest("Invalid definition code format.");

        var found = await _store!.DeleteDraftAsync(code, ct);
        if (!found)
            return NotFound();

        return NoContent();
    }

    // ── WF-21.3: POST /api/_workflow/designer/definitions/{code}/publish ────────

    /// <summary>
    /// Publish the raw graph JSON for a definition.
    ///
    /// <para>Body: verbatim graph JSON from the designer canvas.
    /// The server canonicalizes, hashes, validates, and persists.
    /// Round-trip fidelity is guaranteed: no-op save ≡ byte-identical canonical
    /// JSON ≡ same ContentHash.</para>
    ///
    /// <para>CAS: <c>X-WTM-WF-Expected-Hash</c> header — when supplied, must equal the
    /// current published version's ContentHash; mismatch → 409 (BaseVersionChanged).</para>
    ///
    /// <para>Outcomes:
    /// <list type="bullet">
    ///   <item>200 OK — new version persisted; draft deleted atomically.</item>
    ///   <item>400 Bad Request — validation failed (malformed JSON, schema error, etc.).</item>
    ///   <item>404 Not Found — definition does not exist.</item>
    ///   <item>409 Conflict — <c>X-WTM-WF-Expected-Hash</c> mismatch (concurrent publish).</item>
    ///   <item>413 Request Entity Too Large — body exceeds MaxGraphBytes.</item>
    /// </list>
    /// </para>
    ///
    /// <para>Antiforgery: <c>X-WTM-WF-XSRF</c> header required (mutating action).</para>
    /// </summary>
    [HttpPost("definitions/{code}/publish")]
    [WfDesignerAntiforgery]
    [ProducesResponseType(typeof(PublishResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(PublishResponseDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PublishResponseDto), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413RequestEntityTooLarge)]
    public async Task<IActionResult> PublishDraft(string code, CancellationToken ct = default)
    {
        if (!IsDesignerRegistered) return NotFound();
        if (!DefinitionCodeRegex.IsMatch(code ?? string.Empty))
            return BadRequest(new PublishResponseDto(false, null, 0, null, "InvalidCode",
                "Invalid definition code format."));

        // Content-Type gate.
        if (Request.ContentType is null || !Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                "Content-Type must be application/json.");

        var maxBytes = _options!.Value.Designer.MaxGraphBytes;
        if (Request.ContentLength.HasValue && Request.ContentLength.Value > maxBytes)
            return StatusCode(StatusCodes.Status413RequestEntityTooLarge,
                $"Request body exceeds the maximum allowed size of {maxBytes} bytes.");

        string rawJson;
        try
        {
            rawJson = await ReadRawBodyAsync(Request, maxBytes, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Body exceeds"))
        {
            return StatusCode(StatusCodes.Status413RequestEntityTooLarge, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(rawJson))
            return BadRequest(new PublishResponseDto(false, null, 0, null, "EmptyBody",
                "Request body must be a non-empty JSON object."));

        var publishedBy = Wtm?.LoginUserInfo?.ITCode;

        // Optional CAS hash from client header (FIX-B1: use DesignerHeaderNames constant).
        var expectedBaseHash = Request.Headers[DesignerHeaderNames.ExpectedHash].ToString();
        if (string.IsNullOrEmpty(expectedBaseHash)) expectedBaseHash = null;

        _logger.LogInformation(
            "[WorkflowDesigner] PublishDraft code={Code} actor={Actor}",
            LogSanitizer.Sanitize(code), publishedBy);

        var result = await _publisher!.PublishRawAsync(code, rawJson, publishedBy, expectedBaseHash, ct);

        return result.Outcome switch
        {
            PublishOutcome.Published =>
                Ok(new PublishResponseDto(true, result.VersionId, result.VersionNo,
                    result.ContentHash, nameof(PublishOutcome.Published), null)),
            PublishOutcome.IdempotentNoOp =>
                Ok(new PublishResponseDto(true, result.VersionId, result.VersionNo,
                    result.ContentHash, nameof(PublishOutcome.IdempotentNoOp), null)),
            PublishOutcome.DefinitionNotFound =>
                NotFound(),
            PublishOutcome.ValidationFailed =>
                BadRequest(new PublishResponseDto(false, null, 0, null,
                    nameof(PublishOutcome.ValidationFailed), result.ErrorMessage)),
            PublishOutcome.BaseVersionChanged =>
                Conflict(new PublishResponseDto(false, null, 0, null,
                    nameof(PublishOutcome.BaseVersionChanged),
                    "The definition was published by another user. Reload and retry.")),
            _ => StatusCode(StatusCodes.Status500InternalServerError,
                new PublishResponseDto(false, null, 0, null, "Unexpected",
                    "Unexpected publish outcome.")),
        };
    }

    // ── WF-21.3: POST /api/_workflow/designer/validate ────────────────────────

    /// <summary>
    /// Validate a raw graph JSON document without persisting anything.
    ///
    /// <para>Used by the designer canvas to surface errors inline as the user edits.
    /// No state is changed. No antiforgery required (idempotent side-effect-free).</para>
    ///
    /// <para>Response includes an optional <c>NodeKey</c> when the error is node-specific
    /// (T-DSN-7) so the UI can focus the offending node.</para>
    /// </summary>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(ValidateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413RequestEntityTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<IActionResult> ValidateGraph(CancellationToken ct = default)
    {
        if (!IsDesignerRegistered) return NotFound();

        // FIX-B3c / FIX-B3e: Content-Type gate aligns with publish pipeline.
        // A null or missing Content-Type must NOT bypass this check (it must 415).
        if (Request.ContentType is null || !Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                "Content-Type must be application/json.");

        var maxBytes = _options!.Value.Designer.MaxGraphBytes;
        if (Request.ContentLength.HasValue && Request.ContentLength.Value > maxBytes)
            return StatusCode(StatusCodes.Status413RequestEntityTooLarge,
                $"Request body exceeds the maximum allowed size of {maxBytes} bytes.");

        string rawJson;
        try
        {
            rawJson = await ReadRawBodyAsync(Request, maxBytes, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Body exceeds"))
        {
            return StatusCode(StatusCodes.Status413RequestEntityTooLarge, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(rawJson))
            return BadRequest(new ValidateResponseDto(false, "Request body must be a non-empty JSON object."));

        // Validate via publisher's raw validation path (canonicalize → parse → validate).
        var (isValid, error, nodeKey) = ValidateRaw(rawJson);
        return Ok(new ValidateResponseDto(isValid, error, nodeKey));
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Read the raw request body as UTF-8 string, enforcing the MaxGraphBytes cap.
    ///
    /// <para><strong>FIX-B3e — byte-accurate size cap:</strong>
    /// The cap is enforced on raw UTF-8 BYTES, not decoded character count.
    /// A single Unicode character can take up to 4 bytes in UTF-8; counting chars
    /// would allow oversized payloads to slip through.  We read into a byte buffer and
    /// count bytes directly.  The accumulated bytes are decoded to string at the end.</para>
    ///
    /// Throws <see cref="InvalidOperationException"/> when body exceeds the cap.
    /// </summary>
    private static async Task<string> ReadRawBodyAsync(
        HttpRequest request, int maxBytes, CancellationToken ct)
    {
        // Enable buffering so we can re-read if needed.
        request.EnableBuffering();

        // FIX-B3e: count raw bytes, not chars, to honour MaxGraphBytes accurately.
        using var ms = new MemoryStream(capacity: Math.Min(maxBytes, 65536));
        var buf = new byte[4096];
        int totalBytes = 0;
        int read;

        while ((read = await request.Body.ReadAsync(buf, 0, buf.Length, ct)) > 0)
        {
            totalBytes += read;
            if (totalBytes > maxBytes)
                throw new InvalidOperationException(
                    $"Body exceeds the maximum allowed size of {maxBytes} bytes.");
            ms.Write(buf, 0, read);
        }

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>
    /// Validate raw graph JSON via the canonical serializer path (no persist).
    /// Returns (isValid, error, nodeKey).
    /// </summary>
    private static (bool isValid, string? error, string? nodeKey) ValidateRaw(string rawJson)
    {
        // Canonicalize (unknown fields survive, number fidelity preserved).
        string canonicalJson;
        try
        {
            canonicalJson = Definition.WorkflowGraphSerializer.Canonicalize(rawJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return (false, $"Invalid JSON: {ex.Message}", null);
        }

        // Deserialize to typed model for structural validation.
        // WorkflowGraph is in the Definition namespace (not Models).
        Definition.WorkflowGraph? graph;
        try
        {
            graph = System.Text.Json.JsonSerializer.Deserialize<Definition.WorkflowGraph>(
                canonicalJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (System.Text.Json.JsonException ex)
        {
            return (false, $"Graph deserialization failed: {ex.Message}", null);
        }

        if (graph is null)
            return (false, "Graph document is null or not a JSON object.", null);

        // Structural validation (includes schemaVersion gate via GraphValidationError.SchemaVersionUnsupported —
        // #299: the local schemaVersion check was removed here; the global validator now owns it).
        var validation = Definition.WorkflowGraphValidator.Validate(graph);
        if (!validation.IsValid)
        {
            // T-DSN-7: For node-specific errors, attempt to extract the NodeKey from the
            // error message.  The validator embeds nodeKey in single quotes in the message
            // (e.g. "nodeKey 'Approve' ...").  This is best-effort — null when not extractable.
            string? nodeKey = null;
            if (validation.Error is Definition.GraphValidationError.InvalidNodeKey
                or Definition.GraphValidationError.DuplicateNodeKey
                or Definition.GraphValidationError.DanglingTransitionFrom
                or Definition.GraphValidationError.DanglingTransitionTo
                or Definition.GraphValidationError.ConditionNodeMissingDefault
                or Definition.GraphValidationError.ConditionNodeDanglingDefault
                or Definition.GraphValidationError.GatewayMissingJoinNodeKey
                or Definition.GraphValidationError.GatewayDanglingJoinNodeKey
                or Definition.GraphValidationError.GatewayJoinNodeKeyNotJoinKind
                or Definition.GraphValidationError.ApprovalNodeMissingApproverRule)
            {
                var msg = validation.ErrorMessage;
                if (msg is not null)
                {
                    var start = msg.IndexOf('\'');
                    var end   = start >= 0 ? msg.IndexOf('\'', start + 1) : -1;
                    if (start >= 0 && end > start)
                        nodeKey = msg.Substring(start + 1, end - start - 1);
                }
            }

            return (false, validation.ErrorMessage, nodeKey);
        }

        return (true, null, null);
    }

    private string GetFirstModelError()
    {
        foreach (var entry in ModelState.Values)
        {
            foreach (var error in entry.Errors)
            {
                if (!string.IsNullOrEmpty(error.ErrorMessage))
                    return error.ErrorMessage;
            }
        }
        return "Invalid request.";
    }
}

// ── WF-21.3: Response DTOs for bootstrap and draft save ──────────────────────
// These are declared here (not in WorkflowDtos.cs) to keep designer-HTTP-specific
// shapes co-located with the controller that produces them.

/// <summary>Response from <c>GET /api/_workflow/designer/bootstrap</c>.</summary>
/// <remarks>
/// WTM's global JSON policy sets <c>PropertyNamingPolicy = null</c> (PascalCase output).
/// <c>[JsonPropertyName]</c> on <c>RequestToken</c> forces camelCase emission so the
/// JS client and e2e smoke can consistently read <c>data.requestToken</c>.
/// Without this attribute the server would emit "RequestToken" and the JS would silently
/// get <c>undefined</c> → antiforgery header never sent → CreateDefinition returns 400.
/// </remarks>
public sealed record BootstrapResponseDto(
    [property: System.Text.Json.Serialization.JsonPropertyName("requestToken")]
    string? RequestToken,
    [property: System.Text.Json.Serialization.JsonPropertyName("error")]
    string? Error);

/// <summary>Response from <c>PUT /api/_workflow/designer/definitions/{code}/draft</c>.</summary>
public sealed record SaveDraftResponseDto(
    uint NewRowVersion,
    string ETag);
