#nullable enable
#pragma warning disable WTM789   // tests intentionally exercise the obsolete FResult helpers
using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Tests for FResultExtension (all extension methods).
    /// FResult is marked [Obsolete(WTM789)] — we suppress that diagnostic for test purposes.
    /// </summary>
    [TestClass]
    public class FResultExtensionTests
    {
        // Helper: builds a fresh FResult with an empty ContentBuilder
        private static FResult NewFResult() => new FResult();

        // ── CloseDialog ───────────────────────────────────────────────────────

        [TestMethod]
        public void CloseDialog_appends_correct_script()
        {
            var r = NewFResult().CloseDialog();
            StringAssert.Contains(r.ContentBuilder.ToString(), "ff.CloseDialog()");
        }

        [TestMethod]
        public void CloseDialog_returns_same_instance_for_chaining()
        {
            var r = NewFResult();
            var returned = r.CloseDialog();
            Assert.AreSame(r, returned);
        }

        // ── Alert ────────────────────────────────────────────────────────────

        [TestMethod]
        public void Alert_with_msg_and_title_appends_alert_script()
        {
            var r = NewFResult().Alert("Hello", "Info");
            StringAssert.Contains(r.ContentBuilder.ToString(), "ff.Alert('Hello','Info')");
        }

        [TestMethod]
        public void Alert_with_null_title_falls_back_to_localizer_or_null()
        {
            // MvcProgram._localizer is null in unit-test context — should not throw
            var r = NewFResult().Alert("msg");
            StringAssert.Contains(r.ContentBuilder.ToString(), "ff.Alert('msg',");
        }

        [TestMethod]
        public void Alert_returns_same_instance_for_chaining()
        {
            var r = NewFResult();
            Assert.AreSame(r, r.Alert("x", "y"));
        }

        // ── Message ───────────────────────────────────────────────────────────

        [TestMethod]
        public void Message_appends_msg_script()
        {
            var r = NewFResult().Message("Hello", "Title");
            StringAssert.Contains(r.ContentBuilder.ToString(), "ff.Msg('Hello','Title')");
        }

        [TestMethod]
        public void Message_returns_same_instance_for_chaining()
        {
            var r = NewFResult();
            Assert.AreSame(r, r.Message("x"));
        }

        // ── RefreshGrid ───────────────────────────────────────────────────────

        [TestMethod]
        public void RefreshGrid_with_explicit_winId_appends_script()
        {
            var r = NewFResult().RefreshGrid("myWindow", 0);
            StringAssert.Contains(r.ContentBuilder.ToString(), "ff.RefreshGrid('myWindow',0)");
        }

        [TestMethod]
        public void RefreshGrid_default_winId_appends_script_with_provided_id()
        {
            // Passing a non-empty winId bypasses Controller lookup
            var r = NewFResult().RefreshGrid("LAY_app_body", 0);
            StringAssert.Contains(r.ContentBuilder.ToString(), "ff.RefreshGrid('LAY_app_body',0)");
        }

        [TestMethod]
        public void RefreshGrid_returns_same_instance_for_chaining()
        {
            var r = NewFResult();
            Assert.AreSame(r, r.RefreshGrid("x", 1));
        }

        // ── RefreshGridRow ────────────────────────────────────────────────────

        [TestMethod]
        public void RefreshGridRow_Guid_delegates_to_RefreshGrid()
        {
            var r = NewFResult().RefreshGridRow(Guid.NewGuid(), "myWin");
            StringAssert.Contains(r.ContentBuilder.ToString(), "ff.RefreshGrid('myWin',0)");
        }

        [TestMethod]
        public void RefreshGridRow_string_delegates_to_RefreshGrid()
        {
            var r = NewFResult().RefreshGridRow("row-1", "myWin");
            StringAssert.Contains(r.ContentBuilder.ToString(), "ff.RefreshGrid('myWin',0)");
        }

        [TestMethod]
        public void RefreshGridRow_long_delegates_to_RefreshGrid()
        {
            var r = NewFResult().RefreshGridRow(99L, "myWin");
            StringAssert.Contains(r.ContentBuilder.ToString(), "ff.RefreshGrid('myWin',0)");
        }

        // ── RefreshPage ───────────────────────────────────────────────────────

        [TestMethod]
        public void RefreshPage_appends_layui_render_script()
        {
            var r = NewFResult().RefreshPage();
            StringAssert.Contains(r.ContentBuilder.ToString(), "layui.index.render()");
        }

        [TestMethod]
        public void RefreshPage_returns_same_instance_for_chaining()
        {
            var r = NewFResult();
            Assert.AreSame(r, r.RefreshPage());
        }

        // ── RedirectUrl ───────────────────────────────────────────────────────

        [TestMethod]
        public void RedirectUrl_appends_location_script()
        {
            var r = NewFResult().RedirectUrl("/home");
            StringAssert.Contains(r.ContentBuilder.ToString(), "window.location.url='/home'");
        }

        [TestMethod]
        public void RedirectUrl_returns_same_instance_for_chaining()
        {
            var r = NewFResult();
            Assert.AreSame(r, r.RedirectUrl("/"));
        }

        // ── AddCustomScript ───────────────────────────────────────────────────

        [TestMethod]
        public void AddCustomScript_appends_arbitrary_script()
        {
            const string script = "console.log('hi');";
            var r = NewFResult().AddCustomScript(script);
            StringAssert.Contains(r.ContentBuilder.ToString(), script);
        }

        [TestMethod]
        public void AddCustomScript_returns_same_instance_for_chaining()
        {
            var r = NewFResult();
            Assert.AreSame(r, r.AddCustomScript("x;"));
        }

        // ── Chaining ─────────────────────────────────────────────────────────

        [TestMethod]
        public void Chained_calls_accumulate_in_ContentBuilder()
        {
            var r = NewFResult()
                .CloseDialog()
                .Alert("ok", "T")
                .AddCustomScript("done();");
            var content = r.ContentBuilder.ToString();
            StringAssert.Contains(content, "ff.CloseDialog()");
            StringAssert.Contains(content, "ff.Alert('ok','T')");
            StringAssert.Contains(content, "done();");
        }
    }
}
