using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// The selector dialog view writes request values into an inline script. SelectorScript encodes every value for the place it is
    /// written to; ordinary values must come out unchanged.
    /// </summary>
    [TestClass]
    public class SelectorScriptTest
    {
        private const string Hostile = "a');alert(1);//</script><img src=x onerror=alert(2)>\\\r\n\u2028";

        [TestMethod]
        public void JsString_OrdinaryValues_AreUnchanged()
        {
            Assert.AreEqual("Entity_ItemId", SelectorScript.JsString("Entity_ItemId"));
            Assert.AreEqual("Name", SelectorScript.JsString("Name"));
            Assert.AreEqual("名稱 欄位", SelectorScript.JsString("名稱 欄位"));
            Assert.AreEqual("/Api/Check?type=1", SelectorScript.JsString("/Api/Check?type=1"));
            Assert.AreEqual("", SelectorScript.JsString(null));
        }

        [TestMethod]
        public void JsString_QuotesBackslashLineBreaksAndScriptEnd_AreEscaped()
        {
            var js = SelectorScript.JsString(Hostile);
            Assert.AreEqual("a\\u0027);alert(1);//\\u003C/script\\u003E\\u003Cimg src=x onerror=alert(2)\\u003E\\\\\\r\\n\\u2028", js);
            foreach (var raw in new[] { "'", "\"", "<", ">", "\r", "\n", "\u2028" })
            {
                Assert.IsFalse(js.Contains(raw), "raw " + raw + " must not remain");
            }
        }

        [TestMethod]
        public void HtmlAttributeInJsString_OrdinaryValues_AreUnchanged()
        {
            Assert.AreEqual("Entity.Items", SelectorScript.HtmlAttributeInJsString("Entity.Items"));
            Assert.AreEqual("Entity.Items[0].Id", SelectorScript.HtmlAttributeInJsString("Entity.Items[0].Id"));
        }

        [TestMethod]
        public void HtmlAttributeInJsString_IsEncodedForTheAttributeAndTheStringLiteral()
        {
            var js = SelectorScript.HtmlAttributeInJsString("x' onfocus='alert(1)\"</script>");
            // the attribute encoding (&#39; &quot; &lt; &gt;) is itself written as a script escape: the browser first reads the string literal and
            // then the markup, so the value stays one attribute value
            Assert.AreEqual("x\\u0026#39; onfocus=\\u0026#39;alert(1)\\u0026quot;\\u0026lt;/script\\u0026gt;", js);
            Assert.IsFalse(js.Contains("'"));
            Assert.IsFalse(js.Contains("<"));
        }

        [TestMethod]
        public void Boolean_IsAlwaysTrueOrFalse()
        {
            Assert.AreEqual("true", SelectorScript.Boolean(true));
            Assert.AreEqual("false", SelectorScript.Boolean(false));
            Assert.AreEqual("true", SelectorScript.Boolean("True"));
            Assert.AreEqual("false", SelectorScript.Boolean(null));
            Assert.AreEqual("false", SelectorScript.Boolean("true;alert(1)"));
        }

        [TestMethod]
        public void FunctionReference_NamesAndMemberPaths_AreKeptAsTheyAre()
        {
            Assert.AreEqual("onPick", SelectorScript.FunctionReference("onPick"));
            Assert.AreEqual("_pick$1", SelectorScript.FunctionReference("_pick$1"));
            Assert.AreEqual("MyPage.Dialog.onPick", SelectorScript.FunctionReference("MyPage.Dialog.onPick"));
            Assert.AreEqual("選取", SelectorScript.FunctionReference("選取"));
        }

        [TestMethod]
        public void FunctionReference_AnythingElse_IsLeftOut()
        {
            Assert.IsNull(SelectorScript.FunctionReference(null));
            Assert.IsNull(SelectorScript.FunctionReference(""));
            Assert.IsNull(SelectorScript.FunctionReference("x;alert(1)//"));
            Assert.IsNull(SelectorScript.FunctionReference("x(1)"));
            Assert.IsNull(SelectorScript.FunctionReference("a.b."));
            Assert.IsNull(SelectorScript.FunctionReference("1abc"));
            Assert.IsNull(SelectorScript.FunctionReference("a b"));
            Assert.IsNull(SelectorScript.FunctionReference("</script>"));
        }

        [TestMethod]
        public void JsonData_OrdinaryData_IsUnchanged()
        {
            const string json = "[{\"ID\":\"1\",\"Name\":\"A & B 'x' \\\"q\\\" 中文\"}]";
            Assert.AreEqual(json, SelectorScript.JsonData(json));
            Assert.AreEqual("[]", SelectorScript.JsonData(null));
            Assert.AreEqual("[]", SelectorScript.JsonData(""));
        }

        [TestMethod]
        public void JsonData_MarkupInAValue_CannotCloseTheScriptElement()
        {
            var js = SelectorScript.JsonData("[{\"Name\":\"</script><script>alert(1)</script><!--\"}]");
            Assert.IsFalse(js.Contains("<"));
            Assert.AreEqual("[{\"Name\":\"\\u003C/script>\\u003Cscript>alert(1)\\u003C/script>\\u003C!--\"}]", js);
            // still the same data
            using var doc = System.Text.Json.JsonDocument.Parse(js);
            Assert.AreEqual("</script><script>alert(1)</script><!--", doc.RootElement[0].GetProperty("Name").GetString());
        }

        [TestMethod]
        public void TriggerScript_OrdinaryValues_ProduceTheUsualScript()
        {
            var js = SelectorScript.TriggerScript("/Api/Changed", "ID", "Entity_ItemId");
            StringAssert.Contains(js, "var url = '/Api/Changed?t=' + new Date().getTime();");
            StringAssert.Contains(js, "url += '&id=' +selected[i]['ID'];");
            StringAssert.Contains(js, "ff.ChainChange(url, $('#Entity_ItemId_Display')[0]);");
        }

        [TestMethod]
        public void TriggerScript_HostileValues_StayInsideTheirStrings()
        {
            var js = SelectorScript.TriggerScript(Hostile, Hostile, Hostile);
            var escaped = SelectorScript.JsString(Hostile);
            StringAssert.Contains(js, "var url = '" + escaped + "?t='");
            StringAssert.Contains(js, "selected[i]['" + escaped + "']");
            StringAssert.Contains(js, "$('#" + escaped + "_Display')");
            Assert.IsFalse(js.Contains("</script>"));
            Assert.IsFalse(js.Contains("alert(1);//</"));
            Assert.IsFalse(js.Contains("<img"));
        }
    }
}
