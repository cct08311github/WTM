#nullable enable
// Issue #614: WorkflowDesignerPageController.Index() substitutes the embedded designer.html's
// %%WTM_LAYUI_BASE%% token with LayuiAssets.ResolveLayuiBase(configuration)'s result.
//
// This is an ACTION-LEVEL test: it invokes WorkflowDesignerPageController.Index() directly
// (constructing the controller + a minimal HttpContext, same pattern as the FIX-A2 regression
// test in DesignerAssetTests.cs) rather than a bare resource-text + LayuiAssets.Replace unit
// test, so the assertion exercises the exact production code path — including the real
// embedded designer.html resource and the controller's own token-substitution call — end to
// end. IWorkflowDefinitionStore is registered as a mock in RequestServices purely to satisfy
// the FIX-A2 "designer registered" guard (IsDesignerRegistered) so the action proceeds past
// the 404 short-circuit and reaches the substitution logic under test.

using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.WorkFlow.Controllers;
using WalkingTec.Mvvm.WorkFlow.Definition;

namespace WalkingTec.Mvvm.WorkFlow.Test;

[TestClass]
public class WorkflowDesignerPageControllerLayuiTokenTests
{
    private static WorkflowDesignerPageController CreateController()
    {
        var services = new ServiceCollection();
        // Registering IWorkflowDefinitionStore satisfies IsDesignerRegistered so Index()
        // proceeds past the FIX-A2 404 guard and reaches the token-substitution logic.
        services.AddSingleton(Mock.Of<IWorkflowDefinitionStore>());
        var provider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = provider };

        return new WorkflowDesignerPageController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            },
        };
    }

    private static IConfiguration ConfigWithAsset(string? value)
    {
        var data = new System.Collections.Generic.Dictionary<string, string?> { ["Layui:Asset"] = value };
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    /// <summary>
    /// Guards against someone re-hardcoding <c>/layui</c> (or <c>/layui-next</c>) directly into
    /// designer.html: the embedded resource must still carry the <c>%%WTM_LAYUI_BASE%%</c> token
    /// for <see cref="WorkflowDesignerPageController.Index"/> to substitute. Without this
    /// guard, a future edit could silently remove the token and the Index_* tests above would
    /// still pass (Contains checks would just never match the token in the first place).
    /// </summary>
    [TestMethod]
    public void DesignerHtmlResource_ContainsWtmLayuiBaseToken()
    {
        var assembly = typeof(WorkflowDesignerPageController).Assembly;
        var stream = assembly.GetManifestResourceStream(
            "WalkingTec.Mvvm.WorkFlow.designer.designer.html");

        stream.Should().NotBeNull(because: "designer.html must be embedded in the WorkFlow assembly");

        using var reader = new StreamReader(stream!, Encoding.UTF8);
        var html = reader.ReadToEnd();

        html.Should().Contain("%%WTM_LAYUI_BASE%%",
            because: "designer.html must carry the developer-authored token for " +
                      "WorkflowDesignerPageController.Index() to substitute — a hardcoded " +
                      "/layui or /layui-next literal would bypass the config-aware resolution");
    }

    [TestMethod]
    public async Task Index_LegacyConfig_SubstitutesLegacyLayuiBase_AndRemovesToken()
    {
        var controller = CreateController();
        var configuration = ConfigWithAsset("legacy");

        var result = await controller.Index(configuration) as ContentResult;

        result.Should().NotBeNull(because: "Index() must return a ContentResult when the designer is registered");
        result!.Content.Should().NotBeNullOrEmpty();
        result.Content.Should().Contain("/layui/css/layui.css",
            because: "Layui:Asset=legacy must resolve the CSS link to the /layui (2.6.3) tree");
        result.Content.Should().Contain("/layui/layui.js",
            because: "Layui:Asset=legacy must resolve the layui.js script to the /layui (2.6.3) tree");
        result.Content.Should().NotContain("%%WTM_LAYUI_BASE%%",
            because: "every occurrence of the token must be substituted — none may leak into the response");
    }

    [TestMethod]
    public async Task Index_DefaultConfig_SubstitutesNextLayuiBase_AndRemovesToken()
    {
        var controller = CreateController();
        var configuration = ConfigWithAsset(null);

        var result = await controller.Index(configuration) as ContentResult;

        result.Should().NotBeNull(because: "Index() must return a ContentResult when the designer is registered");
        result!.Content.Should().NotBeNullOrEmpty();
        result.Content.Should().Contain("/layui-next/css/layui.css",
            because: "absent Layui:Asset must default to the /layui-next (2.13.8) tree");
        result.Content.Should().Contain("/layui-next/layui.js",
            because: "absent Layui:Asset must default to the /layui-next (2.13.8) tree");
        result.Content.Should().NotContain("%%WTM_LAYUI_BASE%%",
            because: "every occurrence of the token must be substituted — none may leak into the response");
    }

    [TestMethod]
    public async Task Index_HostileAssetValue_NeverLeaksRawConfigIntoResponse()
    {
        var controller = CreateController();
        // A hostile config value must never be reflected into the response — the controller
        // only ever emits one of the two fixed LayuiAssets literals.
        var configuration = ConfigWithAsset("/layui-next\"><script>alert(1)</script>");

        var result = await controller.Index(configuration) as ContentResult;

        result.Should().NotBeNull();
        result!.Content.Should().Contain("/layui-next/css/layui.css",
            because: "a non-\"legacy\" value must still default to /layui-next, never the raw hostile string");
        result.Content.Should().NotContain("<script>alert(1)</script>",
            because: "SECURITY INVARIANT: the raw config value must never be concatenated into the response");
    }
}
