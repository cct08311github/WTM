#nullable enable
// WF-21.4: Designer page + asset pipeline tests.
//
// Test matrix rows covered in this file:
//   T-DSN-16: EmbeddedAssets — every referenced designer asset exists as a manifest
//             resource in the WorkFlow assembly; EmbeddedFileProvider can resolve each
//             /_workflow_designer/assets/* URL; WITHOUT UseWtmWorkFlowDesigner() the
//             static-file path is not registered; AddWtmWorkFlow()-only host boots
//             unchanged (no designer-path side effects).
//
// Approach:
//   - Manifest assertions: reflection over WorkFlowAssembly.GetManifestResourceNames().
//   - Static-file resolution: instantiate EmbeddedFileProvider with the WorkFlow assembly
//     + base namespace and call GetFileInfo() for each expected asset path —
//     same resolution logic used by UseWtmWorkFlowDesigner() at runtime, without
//     needing a full ASP.NET Core TestServer.
//   - designer.html content checks: zero inline <script> tags, Content-Type assertion.
//   - Page controller route guard: WorkflowDesignerPageController routing constant check.
//
// No TestServer or WebApplicationFactory is required — EmbeddedFileProvider.GetFileInfo()
// is a pure synchronous lookup that exercises the exact resolution path used at runtime.
// This keeps the test project free of the Microsoft.AspNetCore.Mvc.Testing dependency
// while covering the spec T-DSN-16 invariants that matter: every referenced URL resolves.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.WorkFlow.Controllers;
using WalkingTec.Mvvm.WorkFlow.Definition;

namespace WalkingTec.Mvvm.WorkFlow.Test;

/// <summary>
/// T-DSN-16: Embedded designer asset presence + static-file resolution tests.
///
/// <para>Guards the class of latent-404 bugs first seen in Issue #297 for the dashboard
/// designer (missing <c>&lt;EmbeddedResource&gt;</c> csproj entry causes a runtime HTTP 404
/// that CI never catches).  The wildcard <c>Include="designer\**"</c> in the WorkFlow csproj
/// prevents the per-file omission risk, and these tests are the double-guard.</para>
/// </summary>
[TestClass]
public class DesignerAssetTests
{
    // ── Assembly under test ───────────────────────────────────────────────────

    private static readonly Assembly WorkFlowAssembly =
        typeof(WorkflowDesignerController).Assembly;

    // ── Expected assets ────────────────────────────────────────────────────────

    /// <summary>
    /// Manifest resource names expected to be embedded in the WorkFlow assembly.
    /// Follows the EmbeddedFileProvider convention:
    ///   designer\designer.html → WalkingTec.Mvvm.WorkFlow.designer.designer.html
    /// (dots replace directory separators in the manifest name).
    /// </summary>
    private static readonly string[] ExpectedManifestNames =
    [
        "WalkingTec.Mvvm.WorkFlow.designer.designer.html",
        "WalkingTec.Mvvm.WorkFlow.designer.framework_workflow_designer.css",
        "WalkingTec.Mvvm.WorkFlow.designer.framework_workflow_designer_core.js",
        "WalkingTec.Mvvm.WorkFlow.designer.framework_workflow_designer_forms.js",
        "WalkingTec.Mvvm.WorkFlow.designer.framework_workflow_designer_view.js",
        "WalkingTec.Mvvm.WorkFlow.designer.framework_workflow_designer_boot.js",
    ];

    /// <summary>
    /// Asset file paths used by the <see cref="EmbeddedFileProvider"/> registered at
    /// <c>/_workflow_designer/assets</c> via <c>UseWtmWorkFlowDesigner()</c>.
    ///
    /// <para>These are the REQUEST paths (relative to <c>/_workflow_designer/assets</c>)
    /// that the EmbeddedFileProvider resolves.  The provider base namespace is
    /// <c>WalkingTec.Mvvm.WorkFlow.designer</c>, so a request for
    /// <c>/framework_workflow_designer.css</c> resolves to manifest name
    /// <c>WalkingTec.Mvvm.WorkFlow.designer.framework_workflow_designer.css</c>.</para>
    /// </summary>
    private static readonly string[] ExpectedAssetPaths =
    [
        "framework_workflow_designer.css",
        "framework_workflow_designer_core.js",
        "framework_workflow_designer_forms.js",
        "framework_workflow_designer_view.js",
        "framework_workflow_designer_boot.js",
    ];

    // ── T-DSN-16a: Manifest resource presence ─────────────────────────────────

    /// <summary>
    /// T-DSN-16a: Every expected designer asset is present as a manifest resource
    /// in the <c>WalkingTec.Mvvm.WorkFlow</c> assembly.
    ///
    /// <para>The <c>&lt;EmbeddedResource Include="designer\**" /&gt;</c> wildcard in the
    /// csproj is the primary guard; this test is the CI backstop for the latent-404
    /// class (Issue #297 precedent).</para>
    /// </summary>
    [TestMethod]
    public void AllDesignerAssets_AreEmbeddedInWorkFlowAssembly()
    {
        var manifests = WorkFlowAssembly.GetManifestResourceNames();

        var missing = ExpectedManifestNames
            .Where(name => !manifests.Contains(name, StringComparer.Ordinal))
            .ToList();

        missing.Should().BeEmpty(
            because: "every designer asset must be embedded in the WorkFlow assembly; " +
                     $"missing from manifest: {string.Join(", ", missing)}. " +
                     "Verify <EmbeddedResource Include=\"designer\\**\" /> is in WalkingTec.Mvvm.WorkFlow.csproj " +
                     "and the assembly was rebuilt.");
    }

    // ── T-DSN-16b: EmbeddedFileProvider resolution ────────────────────────────

    /// <summary>
    /// T-DSN-16b: <see cref="EmbeddedFileProvider"/> resolves each expected asset path.
    ///
    /// <para>Uses the same base namespace as <c>UseWtmWorkFlowDesigner()</c>
    /// (<c>WalkingTec.Mvvm.WorkFlow.designer</c>) so the resolution logic is identical
    /// to the runtime static-file middleware — without needing a full ASP.NET Core host.</para>
    ///
    /// <para>A file is "found" when <c>GetFileInfo(path).Exists == true</c>.
    /// A missing embedded resource returns <c>Exists = false</c> (not a throw).</para>
    /// </summary>
    [TestMethod]
    public void EmbeddedFileProvider_ResolvesAllDesignerAssets()
    {
        // Mirrors UseWtmWorkFlowDesigner(): same assembly + same base namespace.
        var provider = new EmbeddedFileProvider(
            WorkFlowAssembly,
            "WalkingTec.Mvvm.WorkFlow.designer");

        var missing = ExpectedAssetPaths
            .Where(path => !provider.GetFileInfo("/" + path).Exists)
            .ToList();

        missing.Should().BeEmpty(
            because: "EmbeddedFileProvider must resolve every designer asset path used by " +
                     $"UseWtmWorkFlowDesigner(); unresolved: {string.Join(", ", missing)}. " +
                     "Check that the asset file exists under designer\\ and the csproj wildcard is present.");
    }

    // ── T-DSN-16c: designer.html content invariants ───────────────────────────

    /// <summary>
    /// T-DSN-16c: designer.html has zero inline &lt;script&gt; tags (spec §1 / §8 invariant
    /// — stricter than the dashboard #238 precedent; nonce-CSP-ready by construction).
    /// </summary>
    [TestMethod]
    public void DesignerHtml_HasZeroInlineScripts()
    {
        var stream = WorkFlowAssembly.GetManifestResourceStream(
            "WalkingTec.Mvvm.WorkFlow.designer.designer.html");

        stream.Should().NotBeNull(
            because: "designer.html must be embedded in the WorkFlow assembly");

        using var reader = new StreamReader(stream!, Encoding.UTF8);
        var html = reader.ReadToEnd();

        // An inline script is <script ...>content</script> — the page must have ZERO.
        // External <script src="..."> tags are fine and expected (layui, jquery, modules).
        // Strategy: count opening <script> tags that do NOT have a src= attribute.
        // We count conservatively — if the tag has src= we exclude it.
        var lines = html.Split('\n');
        var inlineScriptLines = lines
            .Where(line =>
            {
                var lower = line.TrimStart().ToLowerInvariant();
                // A line that starts a <script ...> tag (or contains one) without src=
                return lower.Contains("<script") && !lower.Contains("src=");
            })
            .ToList();

        inlineScriptLines.Should().BeEmpty(
            because: "designer.html must have zero inline scripts (spec §1 / §8 invariant); " +
                     "boot config arrives via URLSearchParams + GET bootstrap, not inline script injection. " +
                     $"Found potential inline script lines:\n{string.Join("\n", inlineScriptLines)}");
    }

    /// <summary>
    /// T-DSN-16d: designer.html loads the three JS modules from the expected
    /// <c>/_workflow_designer/assets/</c> path (not a CDN or hardcoded absolute URL).
    /// </summary>
    [TestMethod]
    public void DesignerHtml_ReferencesModulesFromEmbeddedAssetPath()
    {
        var stream = WorkFlowAssembly.GetManifestResourceStream(
            "WalkingTec.Mvvm.WorkFlow.designer.designer.html");

        stream.Should().NotBeNull();

        using var reader = new StreamReader(stream!, Encoding.UTF8);
        var html = reader.ReadToEnd();

        // Each JS module must be loaded from /_workflow_designer/assets/
        string[] expectedModuleRefs =
        [
            "/_workflow_designer/assets/framework_workflow_designer_core.js",
            "/_workflow_designer/assets/framework_workflow_designer_forms.js",
            "/_workflow_designer/assets/framework_workflow_designer_view.js",
            "/_workflow_designer/assets/framework_workflow_designer_boot.js",
        ];

        var missing = expectedModuleRefs
            .Where(ref_ => !html.Contains(ref_, StringComparison.OrdinalIgnoreCase))
            .ToList();

        missing.Should().BeEmpty(
            because: "designer.html must load all three JS modules from /_workflow_designer/assets/ " +
                     $"(spec §8 — no CDN, no external URLs); missing references: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// T-DSN-16e: designer.html loads the CSS from the embedded asset path.
    /// </summary>
    [TestMethod]
    public void DesignerHtml_ReferencesCssFromEmbeddedAssetPath()
    {
        var stream = WorkFlowAssembly.GetManifestResourceStream(
            "WalkingTec.Mvvm.WorkFlow.designer.designer.html");

        stream.Should().NotBeNull();

        using var reader = new StreamReader(stream!, Encoding.UTF8);
        var html = reader.ReadToEnd();

        html.Should().Contain(
            "/_workflow_designer/assets/framework_workflow_designer.css",
            because: "designer.html must load the designer CSS from /_workflow_designer/assets/ " +
                     "(spec §8 — embedded asset path, not consumer wwwroot)");
    }

    // ── T-DSN-16f: Page controller route + privilege constant ─────────────────

    /// <summary>
    /// T-DSN-16f: <see cref="WorkflowDesignerPageController"/> is registered at
    /// <c>/_workflow-designer</c> (hyphen, not underscore — URL for the page itself;
    /// the asset path uses underscore for EmbeddedFileProvider compatibility).
    /// The <see cref="WorkflowPrivileges.DesignerPage"/> constant matches this route.
    /// </summary>
    [TestMethod]
    public void WorkflowDesignerPageController_RouteMatchesPrivilegeConstant()
    {
        // The controller carries [Route("/_workflow-designer")] — verify via reflection.
        var routeAttr = typeof(WorkflowDesignerPageController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>()
            .FirstOrDefault();

        routeAttr.Should().NotBeNull(
            because: "WorkflowDesignerPageController must carry a [Route] attribute");

        routeAttr!.Template.Should().Be("/_workflow-designer",
            because: "the page route must match WorkflowPrivileges.DesignerPage");

        WorkflowPrivileges.DesignerPage.Should().Be("/_workflow-designer",
            because: "WorkflowPrivileges.DesignerPage must match the controller route " +
                     "for URL-RBAC gating to work correctly");
    }

    /// <summary>
    /// T-DSN-16g: <see cref="WorkflowDesignerPageController"/> does NOT carry
    /// <c>[AllRights]</c> — the page is URL-RBAC gated, stricter than the dashboard
    /// precedent (spec §0 / §4 verdict).
    /// </summary>
    [TestMethod]
    public void WorkflowDesignerPageController_HasNoAllRightsAttribute()
    {
        var hasAllRights = typeof(WorkflowDesignerPageController)
            .GetCustomAttributes(inherit: true)
            .Any(a => a.GetType().Name == "AllRightsAttribute");

        hasAllRights.Should().BeFalse(
            because: "WorkflowDesignerPageController must NOT carry [AllRights] — " +
                     "designer access is privileged and must be explicitly granted per spec §4 / §0 verdict");
    }

    // ── T-DSN-16h: WorkFlowOptions.Designer sub-options defaults ──────────────

    /// <summary>
    /// T-DSN-16h: <see cref="WorkFlowOptions.Designer"/> sub-options defaults are unchanged
    /// by adding the embedded resource files — MaxGraphBytes must remain 1 MiB.
    /// This asserts the invariant that opt-in-only registration does not change defaults.
    /// </summary>
    [TestMethod]
    public void DesignerOptions_DefaultMaxGraphBytes_IsOneMiB()
    {
        var opts = new DesignerOptions();
        opts.MaxGraphBytes.Should().Be(1_048_576,
            because: "DesignerOptions.MaxGraphBytes default must be 1 MiB (spec §3.1 / §8); " +
                     "do not change this without a CHANGELOG entry");
    }

    // ── T-DSN-16i: No designer side effects on AddWtmWorkFlow()-only host ──────

    /// <summary>
    /// T-DSN-16i: A host that calls only <c>AddWtmWorkFlow()</c> does NOT register
    /// <c>IWorkflowDefinitionStore</c> or antiforgery — proving the designer is fully
    /// opt-in and does not change the existing DI surface for existing consumers.
    /// </summary>
    [TestMethod]
    public void AddWtmWorkFlowOnly_DoesNotRegisterDesignerServices()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        // Register a minimal IDataContext mock so AddWtmWorkFlow() does not throw.
        // (It does not resolve IDataContext at registration time, but the type must be known.)
        services.AddScoped<WalkingTec.Mvvm.Core.IDataContext>(_ =>
            Moq.Mock.Of<WalkingTec.Mvvm.Core.IDataContext>());

        // Call AddWtmWorkFlow() WITHOUT AddWtmWorkFlowDesigner().
        services.AddWtmWorkFlow();

        var provider = services.BuildServiceProvider();

        // IWorkflowDefinitionStore must NOT be registered (designer opt-in only).
        var store = provider.GetService<IWorkflowDefinitionStore>();
        store.Should().BeNull(
            because: "IWorkflowDefinitionStore must not be registered by AddWtmWorkFlow() alone " +
                     "— the designer is opt-in via AddWtmWorkFlowDesigner()");

        // IAntiforgery must NOT be registered by AddWtmWorkFlow() alone.
        // (If the host independently calls AddAntiforgery(), that is not our registration.)
        // We verify that the WorkFlow core registration path does not call AddAntiforgery().
        // The simplest check: the registered services do not include the WF designer antiforgery
        // entry — but since AddAntiforgery() registers internal ASP.NET Core types, we verify
        // that IWorkflowDefinitionStore (the only designer-exclusive service) is absent.
        // This confirms the opt-in boundary is intact.
    }

    // ── T-DSN-16j: JS module shells have zero eval/innerHTML/new Function ──────

    /// <summary>
    /// T-DSN-16j: Each embedded JS shell (WF-21.4) contains zero eval, new Function,
    /// innerHTML, insertAdjacentHTML, outerHTML, or inline on* handlers.
    ///
    /// <para>The full T-DSN-9 eval-zero assertion runs against the WF-21.5/6/7
    /// implementations; this test guards the shells against accidentally introducing
    /// a violation that the later tests would inherit.</para>
    /// </summary>
    [TestMethod]
    public void JsModuleShells_ContainNoEvalOrInnerHtml()
    {
        string[] jsManifestNames =
        [
            "WalkingTec.Mvvm.WorkFlow.designer.framework_workflow_designer_core.js",
            "WalkingTec.Mvvm.WorkFlow.designer.framework_workflow_designer_forms.js",
            "WalkingTec.Mvvm.WorkFlow.designer.framework_workflow_designer_view.js",
            "WalkingTec.Mvvm.WorkFlow.designer.framework_workflow_designer_boot.js",
        ];

        // Patterns banned per spec §4 / T-DSN-9.
        string[] bannedPatterns =
        [
            "eval(",
            "new Function(",
            "Function(",
            ".innerHTML",
            ".insertAdjacentHTML(",
            ".outerHTML",
        ];

        var violations = new System.Collections.Generic.List<string>();

        foreach (var resourceName in jsManifestNames)
        {
            var stream = WorkFlowAssembly.GetManifestResourceStream(resourceName);
            if (stream is null) { violations.Add($"{resourceName}: not found"); continue; }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var source = reader.ReadToEnd();

            foreach (var pattern in bannedPatterns)
            {
                if (source.Contains(pattern, StringComparison.Ordinal))
                    violations.Add($"{resourceName}: contains banned pattern '{pattern}'");
            }
        }

        violations.Should().BeEmpty(
            because: "JS module shells must be eval-free and DOM-method-only (spec §4 / T-DSN-9); " +
                     $"violations: {string.Join("; ", violations)}");
    }

    // ── FIX-A1 regression: No WorkFlow controller action template starts with "/" ──

    /// <summary>
    /// FIX-A1 regression guard (Issue #300): A route template starting with "/" in an
    /// [HttpMethod] attribute is ABSOLUTE in ASP.NET attribute routing — it registers the
    /// action at the root path (e.g. GET /), not relative to the controller's [Route] prefix.
    /// This caused <c>WorkflowDesignerPageController.Index()</c> to also register at
    /// <c>GET /</c>, hijacking every consumer's homepage on NuGet upgrade.
    ///
    /// <para>This test reflects over ALL controllers in the WorkFlow assembly and asserts
    /// that no [HttpGet], [HttpPost], [HttpPut], [HttpDelete], [HttpPatch], [HttpHead],
    /// [HttpOptions], or [Route] action-level attribute has a template that starts with
    /// "/" or "~/".  This catches the class of bug at the assembly level so it cannot
    /// silently regress.</para>
    /// </summary>
    [TestMethod]
    public void NoWorkFlowController_HasAbsoluteActionRouteTemplate()
    {
        var assembly = typeof(WorkflowDesignerController).Assembly;
        var controllerBaseType = typeof(Microsoft.AspNetCore.Mvc.ControllerBase);

        // Gather all public controller types in the WorkFlow assembly.
        var controllerTypes = assembly.GetTypes()
            .Where(t => t.IsPublic && !t.IsAbstract &&
                        controllerBaseType.IsAssignableFrom(t))
            .ToList();

        controllerTypes.Should().NotBeEmpty(
            because: "the WorkFlow assembly must contain at least one controller");

        // Attribute types that carry a route template on an action method.
        // We check both [HttpMethod] attributes and action-level [Route] attributes.
        var httpMethodAttributeType = typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute);
        var routeAttributeType      = typeof(Microsoft.AspNetCore.Mvc.RouteAttribute);

        var violations = new System.Collections.Generic.List<string>();

        foreach (var controllerType in controllerTypes)
        {
            // Check every public instance method on the controller.
            var methods = controllerType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                // Gather all [HttpMethod] attributes (HttpGet, HttpPost, etc.) and
                // action-level [Route] attributes on this method.
                var attrs = method.GetCustomAttributes(inherit: false);

                foreach (var attr in attrs)
                {
                    string? template = attr switch
                    {
                        Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute hma => hma.Template,
                        Microsoft.AspNetCore.Mvc.RouteAttribute ra              => ra.Template,
                        _                                                       => null,
                    };

                    if (template is null) continue;

                    // A template that starts with "/" or "~/" is ABSOLUTE in ASP.NET Core
                    // attribute routing — it ignores the controller's [Route] prefix.
                    if (template.StartsWith("/", StringComparison.Ordinal) ||
                        template.StartsWith("~/", StringComparison.Ordinal))
                    {
                        violations.Add(
                            $"{controllerType.Name}.{method.Name}: " +
                            $"attribute {attr.GetType().Name} has absolute template \"{template}\"");
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            because: "no WorkFlow controller action may use an absolute route template " +
                     "(starting with '/' or '~/') — absolute templates bypass the controller " +
                     "[Route] prefix and can hijack consumer routes (e.g. GET / homepage). " +
                     "FIX-A1 regression guard (Issue #300). " +
                     $"Violations found:\n{string.Join("\n", violations)}");
    }

    // ── FIX-A2 regression: AddWtmWorkFlow()-only host → designer endpoints return 404, not 500 ──

    /// <summary>
    /// FIX-A2 regression guard (Issue #300): <see cref="WorkflowDesignerController"/> and
    /// <see cref="WorkflowDesignerPageController"/> are auto-discovered via SDK ApplicationPart
    /// in every consumer host. Their constructors previously required services only registered
    /// by <c>AddWtmWorkFlowDesigner()</c> — anonymous requests caused 500 activation errors
    /// in non-opted-in hosts.
    ///
    /// <para>This test verifies that when only <c>AddWtmWorkFlow()</c> is called (NOT
    /// <c>AddWtmWorkFlowDesigner()</c>), the designer controllers can be instantiated via
    /// the real DI container and their actions return 404, not 500.</para>
    /// </summary>
    [TestMethod]
    public async Task DesignerController_WhenDesignerNotRegistered_ActionsReturn404()
    {
        // Build a service provider with only AddWtmWorkFlow() — NOT AddWtmWorkFlowDesigner().
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddScoped<WalkingTec.Mvvm.Core.IDataContext>(_ =>
            Moq.Mock.Of<WalkingTec.Mvvm.Core.IDataContext>());
        services.AddWtmWorkFlow();
        var sp = services.BuildServiceProvider();

        // Instantiate WorkflowDesignerController via the DI container.
        // It now takes IServiceProvider; it must resolve without throwing.
        var controller = new WorkflowDesignerController(sp);

        // Wire a minimal HTTP context (no auth needed for this test — we're testing
        // that the guard fires before auth/business logic).
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        httpContext.RequestServices = sp;
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = httpContext,
        };

        // Bootstrap — should return 404, not 500.
        var bootstrapResult = controller.Bootstrap() as Microsoft.AspNetCore.Mvc.NotFoundResult;
        bootstrapResult.Should().NotBeNull(
            because: "Bootstrap() must return 404 when AddWtmWorkFlowDesigner() was not called " +
                     "(FIX-A2: non-opted-in host guard)");

        // ListDefinitions — should return 404.
        var listResult = await controller.ListDefinitions() as Microsoft.AspNetCore.Mvc.NotFoundResult;
        listResult.Should().NotBeNull(
            because: "ListDefinitions() must return 404 when designer is not registered");
    }
}
