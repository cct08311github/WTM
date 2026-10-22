using System;
using System.Globalization;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class HtmlAttributeEncoderTest
    {
        [TestMethod]
        public void NullAndEmpty_ReturnEmptyString()
        {
            Assert.AreEqual(string.Empty, HtmlAttributeEncoder.Encode((string)null));
            Assert.AreEqual(string.Empty, HtmlAttributeEncoder.Encode(string.Empty));
            Assert.AreEqual(string.Empty, HtmlAttributeEncoder.Encode((object)null));
        }

        [TestMethod]
        public void PlainTextAndCjk_AreReturnedUnchanged()
        {
            foreach (var text in new[] { "Name", "/api/schools?a=1", "請選擇", "確定刪除嗎？", "Sys.PleaseSelect", "a b-c_d.e", "café", "\U0001F600", "color:blue;cursor:pointer" })
            {
                Assert.AreSame(text, HtmlAttributeEncoder.Encode(text), text);
            }
        }

        [TestMethod]
        public void SpecialCharacters_AreEncoded()
        {
            Assert.AreEqual("a&amp;b&lt;c&gt;d&quot;e&#39;f", HtmlAttributeEncoder.Encode("a&b<c>d\"e'f"));
        }

        [TestMethod]
        public void QuoteBreakout_CannotEndEitherQuoteStyle()
        {
            var encoded = HtmlAttributeEncoder.Encode("x' onfocus='alert(1)\" autofocus=\"");

            Assert.IsFalse(encoded.Contains("'"));
            Assert.IsFalse(encoded.Contains("\""));
            Assert.IsFalse(encoded.Contains("<"));
            Assert.IsFalse(encoded.Contains(">"));
        }

        [TestMethod]
        public void BrowserDecoding_RestoresTheOriginalText()
        {
            foreach (var text in new[] { "a&b<c>d\"e'f", "&amp;", "&lt;script&gt;", "</div><img src=x>", "請&選'擇\"" })
            {
                Assert.AreEqual(text, WebUtility.HtmlDecode(HtmlAttributeEncoder.Encode(text)), text);
            }
        }

        [TestMethod]
        public void ObjectOverload_UsesToStringLikeInterpolation()
        {
            Assert.AreEqual("a&amp;b", HtmlAttributeEncoder.Encode((object)"a&b"));
            Assert.AreEqual("12", HtmlAttributeEncoder.Encode((object)12));
            Assert.AreEqual("True", HtmlAttributeEncoder.Encode((object)true));
            var id = Guid.NewGuid();
            Assert.AreEqual(id.ToString(), HtmlAttributeEncoder.Encode((object)id));

            var old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Assert.AreEqual($"{1.5}", HtmlAttributeEncoder.Encode((object)1.5));
                Assert.AreEqual($"{new DateTime(2024, 3, 4)}", HtmlAttributeEncoder.Encode((object)new DateTime(2024, 3, 4)));
            }
            finally
            {
                CultureInfo.CurrentCulture = old;
            }
        }

        // ---- EncodeOnce (#286): for text that may already have been HTML-encoded by the model binder ----

        [TestMethod]
        public void EncodeOnce_AlreadyEncodedText_IsNotEncodedAgain()
        {
            Assert.AreEqual("A&amp;B &lt;i&gt; &quot;q&quot; &#39;q&#39;", HtmlAttributeEncoder.EncodeOnce("A&amp;B &lt;i&gt; &quot;q&quot; &#39;q&#39;"));
        }

        [TestMethod]
        public void EncodeOnce_RawText_IsEncodedLikeEncode()
        {
            foreach (var text in new[] { "plain", "a&b<c>d\"e'f", "請&選'擇\"", "", "<script>alert(1)</script>" })
            {
                Assert.AreEqual(HtmlAttributeEncoder.Encode(text), HtmlAttributeEncoder.EncodeOnce(text), text);
            }
            Assert.AreEqual(string.Empty, HtmlAttributeEncoder.EncodeOnce(null));
        }

        [TestMethod]
        public void EncodeOnce_MixOfEntitiesAndRawMarkup_NeverLeavesActiveMarkup()
        {
            foreach (var text in new[] { "A&amp;B <script>x</script>", "&lt;b&gt;<b>", "&#60;img&#62;<img src=x onerror=1>", "&amp;lt;<", "a\" onload=\"x &amp; '" })
            {
                var once = HtmlAttributeEncoder.EncodeOnce(text);
                Assert.IsFalse(once.Contains("<") || once.Contains(">") || once.Contains("\"") || once.Contains("'"), text + " => " + once);
                // every '&' that remains starts one of the entities the encoder itself writes
                Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(once, "^([^&]|&amp;|&lt;|&gt;|&quot;|&#39;)*$"), text + " => " + once);
            }
        }

        [TestMethod]
        public void EncodeOnce_DisplaysTheTypedTextOnce()
        {
            // binder stores HtmlEncode(typed); the browser must show typed again after EncodeOnce
            foreach (var typed in new[] { "A&B", "<i>x</i>", "&lt;", "&amp;amp;", "a\"b'c", "請&選" })
            {
                var stored = WebUtility.HtmlEncode(typed);
                Assert.AreEqual(typed, WebUtility.HtmlDecode(HtmlAttributeEncoder.EncodeOnce(stored)), typed);
            }
        }
    }
}
