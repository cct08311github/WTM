using Microsoft.Extensions.Localization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// FResultExtension builds script text that framework_layui.js executes on the client.
    /// Values placed inside its string literals must not be able to end the literal or the script element.
    /// </summary>
    [TestClass]
    public class FResultExtensionTest
    {
        private static string Content(FResult r) => r.ContentBuilder.ToString();

        [TestMethod]
        public void Alert_NormalValues_AreUnchanged()
        {
            Assert.AreEqual("ff.Alert('Saved','Info');", Content(new FResult().Alert("Saved", "Info")));
            Assert.AreEqual("ff.Alert('儲存成功','提示');", Content(new FResult().Alert("儲存成功", "提示")));
        }

        [TestMethod]
        public void Message_NormalValues_AreUnchanged()
        {
            Assert.AreEqual("ff.Msg('Saved','Info');", Content(new FResult().Message("Saved", "Info")));
        }

        [TestMethod]
        public void Alert_QuotesBackslashAndScriptEnd_AreEscaped()
        {
            var js = Content(new FResult().Alert("it's \"x\" \\ </script>", "a'b"));
            Assert.AreEqual(
                "ff.Alert('it\\u0027s \\u0022x\\u0022 \\\\ \\u003C/script\\u003E','a\\u0027b');", js);
            Assert.IsFalse(js.Contains("</script>"));
        }

        [TestMethod]
        public void Message_QuotesAndLineBreaks_AreEscaped()
        {
            var js = Content(new FResult().Message("a');alert(1);//\r\nb", "t"));
            Assert.AreEqual("ff.Msg('a\\u0027);alert(1);//\\r\\nb','t');", js);
            Assert.IsFalse(js.Contains("\n"));
        }

        [TestMethod]
        public void Alert_NullMessage_BecomesEmptyString()
        {
            Assert.AreEqual("ff.Alert('','t');", Content(new FResult().Alert(null, "t")));
        }

        [TestMethod]
        public void Alert_DefaultTitle_ComesFromLocalizerAndIsEscaped()
        {
            var original = MvcProgram._localizer;
            try
            {
                var mock = new Mock<IStringLocalizer>();
                mock.Setup(x => x["Sys.Info"]).Returns(new LocalizedString("Sys.Info", "Info's"));
                MvcProgram._localizer = mock.Object;
                Assert.AreEqual("ff.Alert('m','Info\\u0027s');", Content(new FResult().Alert("m")));
            }
            finally
            {
                MvcProgram._localizer = original;
            }
        }

        [TestMethod]
        public void RefreshGrid_WindowId_IsEscaped_AndNormalIdUnchanged()
        {
            Assert.AreEqual("ff.RefreshGrid('win1',0);", Content(new FResult().RefreshGrid("win1")));
            Assert.AreEqual("ff.RefreshGrid('a\\u0027);x(\\u0027',2);", Content(new FResult().RefreshGrid("a');x('", 2)));
        }

        [TestMethod]
        public void RedirectUrl_SetsHref_AndEscapesUrl()
        {
            Assert.AreEqual("window.location.href='/Home/Index';", Content(new FResult().RedirectUrl("/Home/Index")));
            Assert.AreEqual("window.location.href='/a\\u0027;alert(1);//';", Content(new FResult().RedirectUrl("/a';alert(1);//")));
        }

        [TestMethod]
        public void Chained_ScriptsStayWellFormed_AndCustomScriptIsRaw()
        {
            var r = new FResult().CloseDialog().RefreshPage().RedirectUrl("/x").AddCustomScript("alert('raw');");
            Assert.AreEqual("ff.CloseDialog();layui.index.render();window.location.href='/x';alert('raw');", Content(r));
        }
    }
}
