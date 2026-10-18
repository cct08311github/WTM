using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class JsStringEncoderTest
    {
        [TestMethod]
        public void NullAndEmpty_ReturnEmptyString()
        {
            Assert.AreEqual(string.Empty, JsStringEncoder.Escape(null));
            Assert.AreEqual(string.Empty, JsStringEncoder.Escape(string.Empty));
        }

        [TestMethod]
        public void PlainTextAndCjk_AreReturnedUnchanged()
        {
            foreach (var text in new[] { "Name", "/api/schools?a=1", "請選擇", "確定刪除嗎？", "Sys.PleaseSelect", "a b-c_d.e", "café", "\U0001F600" })
            {
                Assert.AreSame(text, JsStringEncoder.Escape(text), text);
            }
        }

        [TestMethod]
        public void QuotesAndBackslash_AreEscapedForBothQuoteStyles()
        {
            Assert.AreEqual("a\\u0027b\\u0022c\\\\d", JsStringEncoder.Escape("a'b\"c\\d"));
        }

        [TestMethod]
        public void ScriptEndTagAndMarkup_AreEscaped()
        {
            var escaped = JsStringEncoder.Escape("a'b\"c</script>");

            Assert.AreEqual("a\\u0027b\\u0022c\\u003C/script\\u003E", escaped);
            Assert.IsFalse(escaped.Contains("<"));
            Assert.IsFalse(escaped.Contains(">"));
            Assert.IsFalse(escaped.Contains("'"));
            Assert.IsFalse(escaped.Contains("\""));
        }

        [TestMethod]
        public void Ampersand_IsEscaped()
        {
            Assert.AreEqual("a\\u0026b", JsStringEncoder.Escape("a&b"));
        }

        [TestMethod]
        public void LineBreaksAndControlCharacters_AreEscaped()
        {
            Assert.AreEqual("a\\nb\\rc\\td", JsStringEncoder.Escape("a\nb\rc\td"));
            Assert.AreEqual("\\u0000\\u001F\\u007F", JsStringEncoder.Escape("\u0000\u001F\u007F"));
        }

        [TestMethod]
        public void LineAndParagraphSeparators_AreEscaped()
        {
            Assert.AreEqual("a\\u2028b\\u2029c", JsStringEncoder.Escape("a" + (char)0x2028 + "b" + (char)0x2029 + "c"));
        }
    }
}
