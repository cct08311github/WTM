using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Helper;
using WalkingTec.Mvvm.Mvc.Helper;

namespace WalkingTec.Mvvm.Core.Test.Helper
{
    /// <summary>
    /// Perf(#677): asserts the [GeneratedRegex]/cached-Regex accessors introduced for the
    /// request-path hot spots behave identically to the ad-hoc `new Regex(...)` patterns they
    /// replaced — same matches, same captured groups, same non-matches.
    /// </summary>
    [TestClass]
    public class GeneratedRegexParityTests
    {
        // ---- FrameworkFilter: was new Regex("(.*?)\[.*?\](.*?$)") ----
        private static readonly Regex _oldChildCollectionIndexRegex = new Regex("(.*?)\\[.*?\\](.*?$)");

        [DataTestMethod]
        [DataRow("Entity.Majors[0].SchoolId")]
        [DataRow("Entity.Majors[12].SchoolId")]
        [DataRow("Entity.Majors[].SchoolId")]
        [DataRow("Entity.Foo")]
        [DataRow("")]
        [DataRow("[0]")]
        [DataRow("a[b][c]d")]
        public void ChildCollectionIndexRegex_MatchesLegacyPattern(string input)
        {
            var oldMatch = _oldChildCollectionIndexRegex.Match(input);
            var newMatch = MvcRegexes.ChildCollectionIndexRegex().Match(input);

            Assert.AreEqual(oldMatch.Success, newMatch.Success);
            if (oldMatch.Success)
            {
                Assert.AreEqual(oldMatch.Groups[1].Value, newMatch.Groups[1].Value);
                Assert.AreEqual(oldMatch.Groups[2].Value, newMatch.Groups[2].Value);
            }
        }

        // ---- WTMContext.IsAccessable: was new Regex("^" + au + "[/\?]?", RegexOptions.IgnoreCase) ----
        [DataTestMethod]
        [DataRow("/Home/Index", "/home/index")]
        [DataRow("/Home/Index", "/HOME/INDEX")]
        [DataRow("/Home/Index", "/Home/Index/")]
        [DataRow("/Home/Index", "/Home/Index?id=1")]
        [DataRow("/Home/Index", "/Home/IndexSomethingElse")]
        [DataRow("/Home/Index", "/Other/Path")]
        [DataRow("/", "/")]
        public void UrlPrefixRegex_MatchesLegacyPattern(string au, string url)
        {
            var oldRegex = new Regex("^" + au + "[/\\?]?", RegexOptions.IgnoreCase);
            var newRegex = CoreRegexes.GetUrlPrefixRegex(au);

            Assert.AreEqual(oldRegex.IsMatch(url), newRegex.IsMatch(url));
        }

        [TestMethod]
        public void UrlPrefixRegex_CachesByPattern_ReturnsSameInstanceForSameAu()
        {
            var r1 = CoreRegexes.GetUrlPrefixRegex("/Home/Index");
            var r2 = CoreRegexes.GetUrlPrefixRegex("/Home/Index");

            Assert.AreSame(r1, r2);
        }

        // ---- PropertyHelper.GetPropertySiblingValues: was new Regex("(.*?)\[\-?\d?\]\.(.*?)$") ----
        [DataTestMethod]
        [DataRow("Majors[0].SchoolId")]
        [DataRow("Majors[-1].SchoolId")]
        [DataRow("Majors[].SchoolId")]
        [DataRow("A.B.Majors[3].SchoolId.Name")]
        [DataRow("NoIndexHere")]
        public void PropertySiblingPathRegex_MatchesLegacyPattern(string input)
        {
            var oldRegex = new Regex("(.*?)\\[\\-?\\d?\\]\\.(.*?)$");
            var newMatch = CoreRegexes.PropertySiblingPathRegex().Match(input);
            var oldMatch = oldRegex.Match(input);

            Assert.AreEqual(oldMatch.Success, newMatch.Success);
            if (oldMatch.Success)
            {
                Assert.AreEqual(oldMatch.Groups[1].Value, newMatch.Groups[1].Value);
                Assert.AreEqual(oldMatch.Groups[2].Value, newMatch.Groups[2].Value);
            }
        }

        // ---- BasePagedListVM.ProcessListError: was new Regex($"{DetailGridPrix}\[(.*?)\]") ----
        [DataTestMethod]
        [DataRow("Details", "Details[3]", "3")]
        [DataRow("Details", "Details[12].SubField", "12")]
        [DataRow("Details", "Other[3]", null)]
        public void DetailGridIndexRegex_MatchesLegacyPattern(string prefix, string input, string? expectedGroup1)
        {
            var oldRegex = new Regex($"{prefix}\\[(.*?)\\]");
            var newMatch = CoreRegexes.GetDetailGridIndexRegex(prefix).Match(input);
            var oldMatch = oldRegex.Match(input);

            Assert.AreEqual(oldMatch.Success, newMatch.Success);
            if (expectedGroup1 != null)
            {
                Assert.AreEqual(expectedGroup1, newMatch.Groups[1].Value);
            }
        }

        // ---- BasePagedListVM export cell HTML-strip: was Regex.Replace(text, @"<[^>]*>", "") ----
        [DataTestMethod]
        [DataRow("<b>bold</b> text", "bold text")]
        [DataRow("plain text", "plain text")]
        [DataRow("<div class=\"x\">a</div><span>b</span>", "ab")]
        [DataRow("", "")]
        public void HtmlTagStripRegex_MatchesLegacyPattern(string input, string expected)
        {
            var oldResult = Regex.Replace(input, @"<[^>]*>", string.Empty);
            var newResult = CoreRegexes.HtmlTagStripRegex().Replace(input, string.Empty);

            Assert.AreEqual(expected, newResult);
            Assert.AreEqual(oldResult, newResult);
        }

        // ---- DataTableTagHelper: was new Regex("<script>.*?</script>") ----
        [DataTestMethod]
        [DataRow("{\"a\":1}<script>alert(1)</script>", "{\"a\":1}")]
        [DataRow("no script here", "no script here")]
        [DataRow("<script></script>", "")]
        public void ScriptBlockRegex_MatchesLegacyPattern(string input, string expected)
        {
            var oldResult = new Regex("<script>.*?</script>").Replace(input, "");
            var newResult = WalkingTec.Mvvm.TagHelpers.LayUI.LayUiRegexes.ScriptBlockRegex().Replace(input, "");

            Assert.AreEqual(expected, newResult);
            Assert.AreEqual(oldResult, newResult);
        }
    }
}
