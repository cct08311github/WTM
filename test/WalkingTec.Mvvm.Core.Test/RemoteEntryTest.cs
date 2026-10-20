using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// RemoteEntry returns a script that navigates to the redirect parameter.
    /// The (URL-decoded) value is written inside a JS string literal, so it must be JS-string-encoded
    /// while normal URLs keep working exactly as before.
    /// Note: unlike SetLanguageForBlazor, this script has no trailing semicolon; the format is kept as is.
    /// </summary>
    [TestClass]
    public class RemoteEntryTest
    {
        [TestMethod]
        public void NormalUrl_OutputIsUnchanged()
        {
            Assert.AreEqual(
                "<script>window.location.href='/Home/Index?a=1'</script>",
                _FrameworkController.BuildRemoteEntryScript("/Home/Index?a=1"));
            Assert.AreEqual(
                "<script>window.location.href='/'</script>",
                _FrameworkController.BuildRemoteEntryScript("/"));
            Assert.AreEqual(
                "<script>window.location.href='https://example.com/a/b'</script>",
                _FrameworkController.BuildRemoteEntryScript("https://example.com/a/b"));
        }

        [TestMethod]
        public void Ampersand_IsEncodedAndDecodesBackInTheBrowser()
        {
            // & is evaluated by the JS engine back to '&', so the navigation target is the same
            Assert.AreEqual(
                "<script>window.location.href='/Home/Index?a=1\\u0026b=2'</script>",
                _FrameworkController.BuildRemoteEntryScript("/Home/Index?a=1&b=2"));
        }

        [TestMethod]
        public void UrlEncodedValue_IsStillDecodedOnce()
        {
            Assert.AreEqual(
                "<script>window.location.href='/a b/c'</script>",
                _FrameworkController.BuildRemoteEntryScript("%2Fa%20b%2Fc"));
            // a double-encoded value is decoded only once
            Assert.AreEqual(
                "<script>window.location.href='/a%20b'</script>",
                _FrameworkController.BuildRemoteEntryScript("%2Fa%2520b"));
        }

        [TestMethod]
        public void NullAndEmpty_BehaveAsBefore()
        {
            // HttpUtility.UrlDecode(null) is null and interpolates as an empty string
            const string expected = "<script>window.location.href=''</script>";
            Assert.AreEqual(expected, _FrameworkController.BuildRemoteEntryScript(null));
            Assert.AreEqual(expected, _FrameworkController.BuildRemoteEntryScript(string.Empty));
        }

        [TestMethod]
        public void QuoteBackslashAndLineBreak_CannotEndTheStringLiteral()
        {
            var script = _FrameworkController.BuildRemoteEntryScript("a'b\"c\\d\r\ne");
            Assert.AreEqual(
                "<script>window.location.href='a\\u0027b\\u0022c\\\\d\\r\\ne'</script>", script);
        }

        [TestMethod]
        public void ScriptEndTag_CannotEndTheScriptElement()
        {
            var script = _FrameworkController.BuildRemoteEntryScript("x</script>y");
            Assert.AreEqual("<script>window.location.href='x\\u003C/script\\u003Ey'</script>", script);
            // only the wrapper's own closing tag remains
            Assert.AreEqual(script.LastIndexOf("</script>"), script.IndexOf("</script>"));
        }

        [TestMethod]
        public void EncodedSpecialCharacters_AreEncodedAfterDecoding()
        {
            // the value is decoded first, then encoded, so decoded quotes/tags are also neutralised
            var script = _FrameworkController.BuildRemoteEntryScript("%27%3C%2Fscript%3E");
            Assert.AreEqual("<script>window.location.href='\\u0027\\u003C/script\\u003E'</script>", script);
        }
    }
}
