using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// SetLanguageForBlazor returns a script that navigates to the redirect parameter.
    /// The (URL-decoded) value is written inside a JS string literal, so it must be JS-string-encoded
    /// while normal URLs keep working exactly as before.
    /// </summary>
    [TestClass]
    public class SetLanguageForBlazorTest
    {
        [TestMethod]
        public void UEditorSuccess_IsBuiltByTheSerializer()
        {
            var ordinary = System.Text.Json.JsonDocument.Parse(_FrameworkController.BuildUEditorSuccess("/_Framework/GetFile?id=1&stream=true", "圖 a.png")).RootElement;
            Assert.AreEqual(200, ordinary.GetProperty("Code").GetInt32());
            Assert.AreEqual("success", ordinary.GetProperty("Msg").GetString());
            Assert.AreEqual("/_Framework/GetFile?id=1&stream=true", ordinary.GetProperty("Data").GetProperty("src").GetString());
            Assert.AreEqual("圖 a.png", ordinary.GetProperty("Data").GetProperty("FileName").GetString());

            const string hostile = "x\",\"Code\":500,\"y\":\"\\</script>";
            var json = _FrameworkController.BuildUEditorSuccess("/u", hostile);
            var data = System.Text.Json.JsonDocument.Parse(json).RootElement;
            Assert.AreEqual(200, data.GetProperty("Code").GetInt32(), "the file name cannot add or replace a member");
            Assert.AreEqual(hostile, data.GetProperty("Data").GetProperty("FileName").GetString());
            Assert.AreEqual(2, System.Linq.Enumerable.Count(data.GetProperty("Data").EnumerateObject()));
        }

        [TestMethod]
        public void NormalUrl_OutputIsUnchanged()
        {
            Assert.AreEqual(
                "<script>window.location.href='/Home/Index?a=1';</script>",
                _FrameworkController.BuildBlazorRedirectScript("/Home/Index?a=1"));
            Assert.AreEqual(
                "<script>window.location.href='/blazor/page';</script>",
                _FrameworkController.BuildBlazorRedirectScript("/blazor/page"));
            Assert.AreEqual(
                "<script>window.location.href='https://example.com/a/b';</script>",
                _FrameworkController.BuildBlazorRedirectScript("https://example.com/a/b"));
        }

        [TestMethod]
        public void Ampersand_IsEncodedAndDecodesBackInTheBrowser()
        {
            // \u0026 is evaluated by the JS engine back to '&', so the navigation target is the same
            Assert.AreEqual(
                "<script>window.location.href='/Home/Index?a=1\\u0026b=2';</script>",
                _FrameworkController.BuildBlazorRedirectScript("/Home/Index?a=1&b=2"));
        }

        [TestMethod]
        public void UrlEncodedValue_IsStillDecodedOnce()
        {
            Assert.AreEqual(
                "<script>window.location.href='/a b/c';</script>",
                _FrameworkController.BuildBlazorRedirectScript("%2Fa%20b%2Fc"));
            Assert.AreEqual(
                "<script>window.location.href='/中文';</script>",
                _FrameworkController.BuildBlazorRedirectScript("%2F%E4%B8%AD%E6%96%87"));
        }

        [TestMethod]
        public void NullAndEmpty_BehaveAsBefore()
        {
            const string expected = "<script>window.location.href='';</script>";
            Assert.AreEqual(expected, _FrameworkController.BuildBlazorRedirectScript(null));
            Assert.AreEqual(expected, _FrameworkController.BuildBlazorRedirectScript(string.Empty));
        }

        [TestMethod]
        public void QuoteBackslashAndLineBreak_CannotEndTheStringLiteral()
        {
            var script = _FrameworkController.BuildBlazorRedirectScript("a'b\"c\\d\r\ne");
            Assert.AreEqual(
                "<script>window.location.href='a\\u0027b\\u0022c\\\\d\\r\\ne';</script>", script);
        }

        [TestMethod]
        public void ScriptEndTag_CannotEndTheScriptElement()
        {
            var script = _FrameworkController.BuildBlazorRedirectScript("x</script>y");
            Assert.AreEqual("<script>window.location.href='x\\u003C/script\\u003Ey';</script>", script);
            // only the wrapper's own closing tag remains
            Assert.AreEqual(script.LastIndexOf("</script>"), script.IndexOf("</script>"));
        }

        [TestMethod]
        public void EncodedSpecialCharacters_AreEncodedAfterDecoding()
        {
            // the value is decoded first, then encoded, so decoded quotes/tags are also neutralised
            var script = _FrameworkController.BuildBlazorRedirectScript("%27%3C%2Fscript%3E");
            Assert.AreEqual("<script>window.location.href='\\u0027\\u003C/script\\u003E';</script>", script);
        }
    }
}
