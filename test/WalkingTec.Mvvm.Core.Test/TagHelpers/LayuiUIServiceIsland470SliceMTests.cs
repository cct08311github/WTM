#nullable enable
using System;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.TagHelpers.LayUI.Common;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #470 Slice M: opt-in (WtmUIOptions.UseSelectIslandRender, default
/// OFF — the SAME flag #470 Slices J/K/L use) delegated data-wtm-click
/// dispatch for LayuiUIService.Make* (button family) and MakeDateTime
/// (grid-cell wiring), replacing the generated per-button
/// <c>&lt;script&gt;function x{guid}click(){...}&lt;/script&gt;</c> + onclick /
/// onclick='ff.SetGridCellDate(...)' with a data-wtm-click attribute + the
/// SAME fixed ff.* call, dispatched client-side via the delegated document
/// click listener (framework_layui.js).
///
/// Unlike BaseFieldTag-derived TagHelpers, LayuiUIService reads the flag via
/// constructor-injected IOptions&lt;WtmUIOptions&gt; (it is DI-activated, not a
/// TagHelper) — so these tests do NOT touch the process-wide
/// BaseFieldTag/WtmUIOptionsHolder static state; each test builds its own
/// LayuiUIService instance with the flag value it needs, and no
/// [TestCleanup] reset is required.
/// </summary>
[TestClass]
public class LayuiUIServiceIsland470SliceMTests
{
    private static LayuiUIService MakeService(bool useIsland) =>
        new(Options.Create(new WtmUIOptions { UseSelectIslandRender = useIsland }));

    // ═══════════════════ Flag OFF (default) — legacy, byte-identical ═══════

    [TestMethod]
    public void MakeDialogButton_FlagOff_ShowDialog_EmitsLegacyInlineScript_NoDataWtmClick()
    {
        var svc = MakeService(false);
        var html = svc.MakeDialogButton(ButtonTypesEnum.Button, "/edit/1", "Edit", 600, 400, title: "T", buttonID: "btn1", showDialog: true);

        StringAssert.Contains(html, "<script>function xbtn1click(){ff.OpenDialog(");
        StringAssert.Contains(html, "onclick='xbtn1click()'");
        Assert.IsFalse(html.Contains("data-wtm-click"), "Flag OFF must never emit data-wtm-click");
    }

    [TestMethod]
    public void MakeDialogButton_FlagOff_RunAction_EmitsLegacyInlineScript_NoDataWtmClick()
    {
        var svc = MakeService(false);
        var html = svc.MakeDialogButton(ButtonTypesEnum.Link, "/run/1", "Run", null, null, buttonID: "btn2", showDialog: false);

        StringAssert.Contains(html, "<script>function xbtn2click(){ff.RunAction('/run/1');;return false;}</script>");
        Assert.IsFalse(html.Contains("data-wtm-click"));
    }

    [TestMethod]
    public void MakeButton_FlagOff_AllRedirectTypes_EmitLegacyInlineScript()
    {
        var svc = MakeService(false);
        var layer = svc.MakeButton(ButtonTypesEnum.Button, "/l", "L", 1, 1, buttonID: "bA", rtype: RedirectTypesEnum.Layer);
        var self = svc.MakeButton(ButtonTypesEnum.Button, "/s", "S", 1, 1, buttonID: "bB", currentdivid: "d1", rtype: RedirectTypesEnum.Self);
        var win = svc.MakeButton(ButtonTypesEnum.Button, "/w", "W", 1, 1, buttonID: "bC", title: "W", rtype: RedirectTypesEnum.NewWindow);
        var tab = svc.MakeButton(ButtonTypesEnum.Button, "/t", "T", 1, 1, buttonID: "bD", title: "T", rtype: RedirectTypesEnum.NewTab);

        StringAssert.Contains(layer, "ff.OpenDialog(");
        StringAssert.Contains(self, "ff.BgRequest('/s',undefined,'d1');");
        StringAssert.Contains(win, "ff.LoadPage('/w',true,'W');");
        StringAssert.Contains(tab, "ff.LoadPage('/t',false,'T');");
        foreach (var html in new[] { layer, self, win, tab })
        {
            Assert.IsFalse(html.Contains("data-wtm-click"), "Flag OFF must never emit data-wtm-click");
            StringAssert.Contains(html, "onclick='x");
        }
    }

    [TestMethod]
    public void MakeViewButton_FlagOff_EmitsLegacyInlineScript_NoDataWtmClick()
    {
        var svc = MakeService(false);
        var fileId = Guid.NewGuid();
        var html = svc.MakeViewButton(ButtonTypesEnum.Img, fileId, "View");

        StringAssert.Contains(html, "layui.layer.photos({photos: {data: [{src: '/_Framework/GetFile/" + fileId);
        Assert.IsFalse(html.Contains("data-wtm-click"));
    }

    [TestMethod]
    public void MakeScriptButton_FlagOff_AnyScript_EmitsLegacyInlineScript_RegardlessOfShape()
    {
        var svc = MakeService(false);
        var identifierCall = svc.MakeScriptButton(ButtonTypesEnum.Link, "Go", script: "myFunc()", buttonID: "sb1");
        var compound = svc.MakeScriptButton(ButtonTypesEnum.Link, "Go2", script: "a() && b()", buttonID: "sb2");

        StringAssert.Contains(identifierCall, "<script>$('#sb1').on('click',function(){myFunc();return false;});</script>");
        StringAssert.Contains(compound, "<script>$('#sb2').on('click',function(){a() && b();return false;});</script>");
        Assert.IsFalse(identifierCall.Contains("data-wtm-click"));
        Assert.IsFalse(compound.Contains("data-wtm-click"));
    }

    [TestMethod]
    public void MakeDateTime_FlagOff_EmitsOnclickAttribute_NoDataWtmClick()
    {
        var svc = MakeService(false);
        var html = svc.MakeDateTime(name: "MyField");

        StringAssert.Contains(html, "onclick='ff.SetGridCellDate(");
        Assert.IsFalse(html.Contains("data-wtm-click"));
    }

    [TestMethod]
    public void MakeDownloadButton_NeverEmitsScriptOrDataAttrs_RegardlessOfFlag()
    {
        // MakeDownloadButton is a plain <a href='...'> in BOTH flag states —
        // nothing to islandify (no inline script/onclick to begin with).
        var fileId = Guid.NewGuid();
        var off = MakeService(false).MakeDownloadButton(ButtonTypesEnum.Link, fileId, "Download");
        var on = MakeService(true).MakeDownloadButton(ButtonTypesEnum.Link, fileId, "Download");

        Assert.AreEqual(off, on, "MakeDownloadButton output must be flag-independent");
        Assert.IsFalse(off.Contains("<script>"));
        Assert.IsFalse(off.Contains("data-wtm-click"));
        Assert.IsFalse(off.Contains("onclick"));
    }

    // ═══════════════════ Flag ON — delegated island, no inline script ══════

    [TestMethod]
    public void MakeDialogButton_FlagOn_ShowDialog_EmitsDataWtmClick_NoScript_NoOnclick()
    {
        var svc = MakeService(true);
        var html = svc.MakeDialogButton(ButtonTypesEnum.Button, "/edit/1", "Edit", 600, 400, title: "T", buttonID: "btn1", showDialog: true, max: true);

        StringAssert.Contains(html, "data-wtm-click='openDialog'");
        StringAssert.Contains(html, "data-wtm-url='/edit/1'");
        StringAssert.Contains(html, "data-wtm-title='T'");
        StringAssert.Contains(html, "data-wtm-width='600'");
        StringAssert.Contains(html, "data-wtm-height='400'");
        StringAssert.Contains(html, "data-wtm-max='true'");
        Assert.IsFalse(html.Contains("<script>"), "Flag ON must not emit a generated <script> function");
        Assert.IsFalse(html.Contains("onclick"), "Flag ON must not emit onclick=");
    }

    [TestMethod]
    public void MakeDialogButton_FlagOn_RunAction_EmitsDataWtmClick_NoScript()
    {
        var svc = MakeService(true);
        var html = svc.MakeDialogButton(ButtonTypesEnum.Link, "/run/1", "Run", null, null, buttonID: "btn2", showDialog: false);

        StringAssert.Contains(html, "data-wtm-click='runAction'");
        StringAssert.Contains(html, "data-wtm-url='/run/1'");
        Assert.IsFalse(html.Contains("<script>"));
    }

    [TestMethod]
    public void MakeButton_FlagOn_AllRedirectTypes_EmitDataWtmClick_NoScript()
    {
        var svc = MakeService(true);
        var layer = svc.MakeButton(ButtonTypesEnum.Button, "/l", "L", 100, 200, buttonID: "bA", rtype: RedirectTypesEnum.Layer);
        var self = svc.MakeButton(ButtonTypesEnum.Button, "/s", "S", 1, 1, buttonID: "bB", currentdivid: "d1", rtype: RedirectTypesEnum.Self);
        var win = svc.MakeButton(ButtonTypesEnum.Button, "/w", "W", 1, 1, buttonID: "bC", title: "WT", rtype: RedirectTypesEnum.NewWindow);
        var tab = svc.MakeButton(ButtonTypesEnum.Button, "/t", "T", 1, 1, buttonID: "bD", title: "TT", rtype: RedirectTypesEnum.NewTab);

        StringAssert.Contains(layer, "data-wtm-click='openDialog'");
        StringAssert.Contains(layer, "data-wtm-width='100'");
        StringAssert.Contains(self, "data-wtm-click='bgRequest'");
        StringAssert.Contains(self, "data-wtm-divid='d1'");
        StringAssert.Contains(win, "data-wtm-click='loadPage'");
        StringAssert.Contains(win, "data-wtm-newwindow='true'");
        StringAssert.Contains(tab, "data-wtm-click='loadPage'");
        StringAssert.Contains(tab, "data-wtm-newwindow='false'");
        foreach (var html in new[] { layer, self, win, tab })
        {
            Assert.IsFalse(html.Contains("<script>"), "Flag ON must not emit a generated <script> function");
            Assert.IsFalse(html.Contains("onclick"));
        }
    }

    [TestMethod]
    public void MakeViewButton_FlagOn_EmitsDataWtmClick_NoScript_AllButtonTypes()
    {
        var svc = MakeService(true);
        var fileId = Guid.NewGuid();
        var link = svc.MakeViewButton(ButtonTypesEnum.Link, fileId, "View");
        var button = svc.MakeViewButton(ButtonTypesEnum.Button, fileId, "View");
        var img = svc.MakeViewButton(ButtonTypesEnum.Img, fileId, "View");

        foreach (var html in new[] { link, button, img })
        {
            StringAssert.Contains(html, "data-wtm-click='view'");
            StringAssert.Contains(html, "data-wtm-url='/_Framework/GetFile/" + fileId);
            Assert.IsFalse(html.Contains("<script>"));
            Assert.IsFalse(html.Contains("onclick"));
        }
    }

    [TestMethod]
    public void MakeScriptButton_FlagOn_BareIdentifierCall_EmitsDataWtmClickScriptCall_NoInlineScript()
    {
        var svc = MakeService(true);
        var html = svc.MakeScriptButton(ButtonTypesEnum.Link, "Go", script: "myFunc()", buttonID: "sb1");

        StringAssert.Contains(html, "data-wtm-click='scriptCall'");
        StringAssert.Contains(html, "data-wtm-fn='myFunc'");
        Assert.IsFalse(html.Contains("<script>"), "A resolvable bare-call script must not keep the legacy inline <script>");
    }

    [TestMethod]
    public void MakeScriptButton_FlagOn_CompoundExpression_KeepsLegacyInlineScript_WithWarning()
    {
        // "a() && b()" is NOT reducible to a single bare no-arg call — must
        // keep the exact legacy inline path (never silently truncated/dropped)
        // and surface a console.warn since the flag is ON.
        var svc = MakeService(true);
        var html = svc.MakeScriptButton(ButtonTypesEnum.Link, "Go", script: "a() && b()", buttonID: "sb2");

        Assert.IsFalse(html.Contains("data-wtm-click"), "Non-bare-call scripts must not be islandified");
        StringAssert.Contains(html, "<script>");
        StringAssert.Contains(html, "console.warn(");
        StringAssert.Contains(html, "$('#sb2').on('click',function(){a() && b();return false;});");
    }

    [TestMethod]
    public void MakeScriptButton_FlagOn_BareVariableReference_NoParens_KeepsLegacyInlineScript()
    {
        // A bare variable reference (no call parens) is NOT a function call —
        // must keep the legacy inline path, not be misidentified as island-safe.
        var svc = MakeService(true);
        var html = svc.MakeScriptButton(ButtonTypesEnum.Link, "Go", script: "myFlag", buttonID: "sb3");

        Assert.IsFalse(html.Contains("data-wtm-click"));
        StringAssert.Contains(html, "<script>");
        StringAssert.Contains(html, "console.warn(");
    }

    [TestMethod]
    public void MakeDateTime_FlagOn_EmitsDataWtmClickDateClick_NoOnclick()
    {
        var svc = MakeService(true);
        var html = svc.MakeDateTime(name: "MyField");

        StringAssert.Contains(html, "data-wtm-click='dateClick'");
        StringAssert.Contains(html, "data-wtm-date-type='datetime'");
        Assert.IsFalse(html.Contains("onclick"), "Flag ON must not emit the raw onclick attribute");
    }

    // ═══════════════════ Attribute-context HtmlEncoding ═════════════════════

    [TestMethod]
    public void MakeDialogButton_FlagOn_TitleWithSingleQuoteAndMarkup_IsHtmlEncoded_NoAttributeBreakout()
    {
        var svc = MakeService(true);
        var html = svc.MakeDialogButton(
            ButtonTypesEnum.Button, "/edit/1", "Edit",
            600, 400, title: "It's a <b>test</b>", buttonID: "btnX", showDialog: true);

        // The single-quote must be encoded (attributes are single-quoted in
        // this file's output convention) so the attribute cannot be broken
        // out of, and the markup must not appear raw.
        Assert.IsFalse(html.Contains("data-wtm-title='It's"),
            "A raw single quote in title must not be able to break out of the single-quoted attribute");
        StringAssert.Contains(html, "&#39;");
        StringAssert.Contains(html, "&lt;b&gt;");
    }

    [TestMethod]
    public void MakeScriptButton_FlagOn_FnNameWithSpecialChars_NeverReachesIslandUnescaped()
    {
        // A malicious-looking url containing a single quote must never break
        // out of the data-wtm-url attribute.
        var svc = MakeService(true);
        var html = svc.MakeDialogButton(ButtonTypesEnum.Link, "/x?a='onmouseover='alert(1)", "Go", null, null, buttonID: "btnY", showDialog: false);

        Assert.IsFalse(html.Contains("data-wtm-url='/x?a='onmouseover="),
            "A raw single quote in url must not be able to break out of data-wtm-url");
        StringAssert.Contains(html, "&#39;");
    }

    // ═══════════════════ Constructor / DI wiring ═════════════════════════════

    [TestMethod]
    public void Constructor_ParameterlessCtor_DefaultsToFlagOff_BackwardCompatible()
    {
        // Existing callers (e.g. XssEncodingTests) construct LayuiUIService()
        // with no arguments — must keep working, flag OFF (legacy behavior).
        var svc = new LayuiUIService();
        var html = svc.MakeDialogButton(ButtonTypesEnum.Button, "/edit/1", "Edit", null, null, buttonID: "btnZ");

        Assert.IsFalse(html.Contains("data-wtm-click"));
        StringAssert.Contains(html, "<script>function xbtnZclick(");
    }

    [TestMethod]
    public void Constructor_NullOptionsValue_FallsBackToFlagOff()
    {
        var svc = new LayuiUIService(Options.Create<WtmUIOptions>(null!));
        var html = svc.MakeDialogButton(ButtonTypesEnum.Button, "/edit/1", "Edit", null, null, buttonID: "btnW");

        Assert.IsFalse(html.Contains("data-wtm-click"));
    }
}
