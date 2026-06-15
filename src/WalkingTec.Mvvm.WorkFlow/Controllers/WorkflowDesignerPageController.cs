#nullable enable
// WF-21.4: WorkflowDesignerPageController — serves the low-code designer HTML page.
//
// Route: GET /_workflow-designer   (avoids the legacy Elsa /_Workflow demo collision)
// Auth:  URL-RBAC via WorkflowPrivileges.DesignerPage (NO [AllRights] — privileged operation).
// Serving: streams the embedded designer/designer.html resource directly from the
//          WalkingTec.Mvvm.WorkFlow assembly — the WorkFlow project has no Razor/Views
//          infra, so we cannot use View() here.
//
// Design invariants (spec §8):
//   - designer.html has ZERO inline scripts (stricter than the dashboard #238 precedent).
//   - Boot config (antiforgery token, options, ?code= echo) arrives via URLSearchParams +
//     GET api/_workflow/designer/bootstrap.
//   - Assets (CSS + JS modules) are served by UseWtmWorkFlowDesigner() at
//     /_workflow_designer/assets/ — a separate StaticFileOptions registration
//     that is opt-in and does NOT modify UseWtmStaticFiles behavior.
//   - ETag/caching for the HTML page: no explicit ETag set here; the consumer may add
//     response caching middleware as needed (documented opt-in in WorkFlowOptions.Designer).
//
// FIX-A1 (Issue #300): Removed [HttpGet("/")] — a route template starting with "/" is
//   ABSOLUTE in ASP.NET attribute routing; it would register this action at the root GET /
//   path, hijacking every consumer's homepage on NuGet upgrade. Only [HttpGet("")] is kept.
//
// FIX-A2 (Issue #300): Added IServiceProvider-based designer feature guard. This controller
//   is auto-discovered via SDK ApplicationPart in every consumer host. When only
//   AddWtmWorkFlow() is called (not AddWtmWorkFlowDesigner()), IWorkflowDefinitionStore is
//   not registered — attempting to serve the page would 500. The guard detects the absent
//   marker and returns 404 instead.

using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;

namespace WalkingTec.Mvvm.WorkFlow.Controllers;

/// <summary>
/// Serves the low-code workflow designer page at <c>/_workflow-designer</c>.
///
/// <para>The page is a static embedded <c>designer/designer.html</c> resource within the
/// <c>WalkingTec.Mvvm.WorkFlow</c> assembly.  It is served by streaming the embedded
/// resource directly — the WorkFlow module has no Razor/Views infrastructure.</para>
///
/// <para>Authorization: URL-RBAC via
/// <see cref="WorkflowPrivileges.DesignerPage"/> — stricter than <c>[AllRights]</c>.
/// Consumers register the <c>/_workflow-designer</c> URL as a
/// <c>FunctionPrivilege</c> to grant access to specific admin roles only.</para>
///
/// <para>Boot config: the static HTML page has <strong>zero inline scripts</strong>.
/// The client reads <c>?code=</c> from <c>URLSearchParams</c> and calls
/// <c>GET api/_workflow/designer/bootstrap</c> to obtain the antiforgery token,
/// current user info, and designer options (spec §1 / §8).</para>
///
/// <para><strong>FIX-A2 guard:</strong> When the designer feature is not registered
/// (i.e., <c>AddWtmWorkFlowDesigner()</c> was not called), every action returns
/// <c>404 Not Found</c> before any other work.  This prevents the auto-discovered
/// controller from causing 500 activation errors in non-opted-in consumer hosts.</para>
/// </summary>
[Route("/_workflow-designer")]
[ActionDescription("WorkflowDesignerPage")]
public class WorkflowDesignerPageController : Mvc.BaseController
{
    // Manifest resource name for the embedded designer HTML page.
    // EmbeddedFileProvider mangles hyphens in folder names to underscores in the
    // manifest resource name, so designer\ → WalkingTec.Mvvm.WorkFlow.designer.
    // The file name itself keeps its original casing.
    private const string DesignerHtmlResourceName =
        "WalkingTec.Mvvm.WorkFlow.designer.designer.html";

    private static readonly Assembly WorkFlowAssembly =
        typeof(WorkflowDesignerPageController).Assembly;

    // ── FIX-A2: Designer feature marker check ─────────────────────────────────

    /// <summary>
    /// Returns true when the designer feature is registered in this host
    /// (i.e., <c>AddWtmWorkFlowDesigner()</c> was called).
    /// <c>IWorkflowDefinitionStore</c> is registered exclusively by the designer extension
    /// and serves as the presence marker.
    /// </summary>
    private bool IsDesignerRegistered =>
        HttpContext.RequestServices.GetService<IWorkflowDefinitionStore>() is not null;

    // ── GET /_workflow-designer ──────────────────────────────────────────────

    /// <summary>
    /// Serves the embedded <c>designer.html</c> shell page.
    /// URL-RBAC gate enforced by the framework <c>PrivilegeFilter</c> via
    /// <see cref="WorkflowPrivileges.DesignerPage"/>.
    ///
    /// <para>FIX-A1: Only <c>[HttpGet("")]</c> is registered here — the previously present
    /// <c>[HttpGet("/")]</c> was removed because a leading-slash template is ABSOLUTE
    /// in ASP.NET attribute routing, causing this action to also register at <c>GET /</c>
    /// (homepage hijack).</para>
    /// </summary>
    [HttpGet("")]
    [ActionDescription("Index")]
    public async Task<IActionResult> Index()
    {
        // FIX-A2: Non-opted-in hosts get 404, never 500.
        if (!IsDesignerRegistered)
            return NotFound();

        // Stream the embedded designer.html resource.
        var stream = WorkFlowAssembly.GetManifestResourceStream(DesignerHtmlResourceName);

        if (stream is null)
        {
            // The EmbeddedResource wildcard in the csproj should make this unreachable
            // in a correctly built assembly.  Return 503 so it is loud and diagnosable.
            return StatusCode(
                Microsoft.AspNetCore.Http.StatusCodes.Status503ServiceUnavailable,
                "Designer HTML resource not found. " +
                "Verify that <EmbeddedResource Include=\"designer\\**\" /> is present in " +
                "WalkingTec.Mvvm.WorkFlow.csproj and the assembly was rebuilt.");
        }

        Response.ContentType = "text/html; charset=utf-8";

        using (stream)
        {
            await stream.CopyToAsync(Response.Body);
        }

        // FileStreamResult would close the stream after sending; CopyToAsync + EmptyResult
        // is equivalent and does not require the stream to remain open after the action returns.
        return new EmptyResult();
    }
}
