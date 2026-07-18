using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Helper;
using WalkingTec.Mvvm.Mvc.Helper;
using WalkingTec.Mvvm.Etl.Pipeline;

namespace WalkingTec.Mvvm.Core.Test.Helper
{
    /// <summary>
    /// Perf(#677, #713): asserts the [GeneratedRegex]/cached-Regex accessors introduced for
    /// the request-path hot spots behave identically to the ad-hoc `new Regex(...)` patterns
    /// they replaced — same matches, same captured groups, same non-matches.
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

        // ==================== #713 (second wave) ====================

        // ---- DetailGridIndexRegex cache hardening: DetailGridPrix is request-bindable
        // (round-trips through a hidden form field emitted by DataTableTagHelper), NOT a
        // fixed developer constant. Prove (a) normal caching still returns the same instance
        // for a repeated prefix while there is room, and (b) feeding the cache more distinct
        // prefixes than MaxDetailGridIndexCacheEntries does not grow the dictionary past the
        // cap, while regex correctness is preserved for entries beyond the cap. Both
        // assertions live in one test (rather than separate [TestMethod]s) because the cache
        // is a process-wide static singleton with no reset hook — flooding it in a separate
        // test would permanently saturate it for the rest of the test run and make a
        // "there's room in the cache" assertion order-dependent and flaky.
        [TestMethod]
        public void DetailGridIndexRegex_CacheCachesNormallyThenStaysBoundedUnderHostileDistinctValueStream()
        {
            // (a) Normal behavior while the cache has room: repeated calls with the same
            // prefix return the same cached Regex instance.
            var stablePrefix = $"StablePrefix{System.Guid.NewGuid():N}";
            var r1 = CoreRegexes.GetDetailGridIndexRegex(stablePrefix);
            var r2 = CoreRegexes.GetDetailGridIndexRegex(stablePrefix);
            Assert.AreSame(r1, r2);

            // (b) Drive far more distinct DetailGridPrix values through the cache than any
            // legitimate app would ever define, simulating a hostile client submitting a
            // fresh hidden-field value on every postback.
            const int hostileDistinctValues = 5_000;
            for (int i = 0; i < hostileDistinctValues; i++)
            {
                var prefix = $"HostileDetailPrefix{i}";
                var regex = CoreRegexes.GetDetailGridIndexRegex(prefix);

                // Regex correctness must hold even once the cache stops accepting new entries.
                var m = regex.Match($"{prefix}[7]");
                Assert.IsTrue(m.Success);
                Assert.AreEqual("7", m.Groups[1].Value);
            }

            var cacheField = typeof(CoreRegexes).GetField("_detailGridIndexCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(cacheField);
            var cache = (System.Collections.Concurrent.ConcurrentDictionary<string, Regex>)cacheField!.GetValue(null)!;

            var maxField = typeof(CoreRegexes).GetField("MaxDetailGridIndexCacheEntries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.IsNotNull(maxField);
            var max = (int)maxField!.GetValue(null)!;

            Assert.IsTrue(cache.Count <= max, $"Cache grew to {cache.Count} entries, exceeding the defensive cap of {max}.");
            Assert.IsTrue(max < hostileDistinctValues, "Test is only meaningful if the hostile stream exceeds the cap.");
        }

        // ---- WTMContext.CreateDC / FrameworkServiceExtension base-URL normalization:
        // was new Regex("(http://|https://)?(.+?)(/)?$") ----
        [DataTestMethod]
        [DataRow("http://example.com/")]
        [DataRow("https://example.com")]
        [DataRow("example.com/")]
        [DataRow("example.com")]
        [DataRow("http://sub.example.com/path/")]
        [DataRow("")]
        public void BaseUrlDomainRegex_MatchesLegacyPattern_Core(string input)
        {
            var oldRegex = new Regex("(http://|https://)?(.+?)(/)?$");
            var oldMatch = oldRegex.Match(input);
            var newMatch = CoreRegexes.BaseUrlDomainRegex().Match(input);

            Assert.AreEqual(oldMatch.Success, newMatch.Success);
            if (oldMatch.Success)
            {
                Assert.AreEqual(oldMatch.Groups[2].Value, newMatch.Groups[2].Value);
            }
        }

        [DataTestMethod]
        [DataRow("http://example.com/")]
        [DataRow("https://example.com")]
        [DataRow("example.com/")]
        [DataRow("example.com")]
        [DataRow("http://sub.example.com/path/")]
        [DataRow("")]
        public void BaseUrlDomainRegex_MatchesLegacyPattern_Mvc(string input)
        {
            var oldRegex = new Regex("(http://|https://)?(.+?)(/)?$");
            var oldMatch = oldRegex.Match(input);
            var newMatch = MvcRegexes.BaseUrlDomainRegex().Match(input);

            Assert.AreEqual(oldMatch.Success, newMatch.Success);
            if (oldMatch.Success)
            {
                Assert.AreEqual(oldMatch.Groups[2].Value, newMatch.Groups[2].Value);
            }
        }

        // Core and Mvc holders duplicate the same pattern (CoreRegexes is internal to the
        // Core assembly, unreachable from Mvc) — prove they agree with each other too.
        [DataTestMethod]
        [DataRow("http://example.com/")]
        [DataRow("https://sub.example.com/path/")]
        [DataRow("example.com")]
        public void BaseUrlDomainRegex_CoreAndMvcHoldersAgree(string input)
        {
            var coreMatch = CoreRegexes.BaseUrlDomainRegex().Match(input);
            var mvcMatch = MvcRegexes.BaseUrlDomainRegex().Match(input);

            Assert.AreEqual(coreMatch.Success, mvcMatch.Success);
            Assert.AreEqual(coreMatch.Groups[2].Value, mvcMatch.Groups[2].Value);
        }

        // ---- FrameworkFilter: was Regex.IsMatch(x, ".*?\[.*?\]\..*?id", RegexOptions.IgnoreCase) ----
        [DataTestMethod]
        [DataRow("Entity.Majors[0].SchoolId", true)]
        [DataRow("Entity.Majors[0].SCHOOLID", true)]
        [DataRow("Entity.Majors[0].Name", false)]
        [DataRow("Entity.Foo", false)]
        [DataRow("", false)]
        public void ChildCollectionForeignKeyErrorRegex_MatchesLegacyPattern(string input, bool expected)
        {
            var oldResult = Regex.IsMatch(input, ".*?\\[.*?\\]\\..*?id", RegexOptions.IgnoreCase);
            var newResult = MvcRegexes.ChildCollectionForeignKeyErrorRegex().IsMatch(input);

            Assert.AreEqual(expected, oldResult);
            Assert.AreEqual(oldResult, newResult);
        }

        // ---- WtmAuthorizationService: was new("/do(batch.*)", RegexOptions.IgnoreCase | RegexOptions.Compiled) ----
        [DataTestMethod]
        [DataRow("/DoBatchDelete", "/BatchDelete")]
        [DataRow("/doBatchExport", "/BatchExport")]
        [DataRow("/Home/Index", "/Home/Index")]
        [DataRow("/DOBATCHFOO", "/BATCHFOO")]
        public void BatchActionRewriteRegex_MatchesLegacyPattern(string input, string expected)
        {
            var oldRegex = new Regex("/do(batch.*)", RegexOptions.IgnoreCase);
            var oldResult = oldRegex.Replace(input, "/$1");
            var newResult = CoreRegexes.BatchActionRewriteRegex().Replace(input, "/$1");

            Assert.AreEqual(expected, oldResult);
            Assert.AreEqual(oldResult, newResult);
        }

        // ---- TreeContainerTagHelper: was new Regex(@".*?Searcher\.", RegexOptions.Compiled) ----
        [DataTestMethod]
        [DataRow("Searcher.LevelId", "LevelId")]
        [DataRow("A.B.Searcher.Foo", "Foo")]
        [DataRow("NoSearcherHere", "NoSearcherHere")]
        public void SearcherPrefixStripRegex_MatchesLegacyPattern(string input, string expected)
        {
            var oldResult = new Regex(@".*?Searcher\.").Replace(input, "");
            var newResult = WalkingTec.Mvvm.TagHelpers.LayUI.LayUiRegexes.SearcherPrefixStripRegex().Replace(input, "");

            Assert.AreEqual(expected, oldResult);
            Assert.AreEqual(oldResult, newResult);
        }

        // ---- TreeContainerTagHelper: was new Regex(@"id=""(.*?)"" IsSearchButton", RegexOptions.Compiled) ----
        [DataTestMethod]
        [DataRow(@"<button id=""btn1"" IsSearchButton>Search</button>")]
        [DataRow("no button here")]
        public void SearchButtonIdRegex_MatchesLegacyPattern(string input)
        {
            var oldMatch = new Regex(@"id=""(.*?)"" IsSearchButton").Match(input);
            var newMatch = WalkingTec.Mvvm.TagHelpers.LayUI.LayUiRegexes.SearchButtonIdRegex().Match(input);

            Assert.AreEqual(oldMatch.Success, newMatch.Success);
            if (oldMatch.Success)
            {
                Assert.AreEqual(oldMatch.Groups[1].Value, newMatch.Groups[1].Value);
            }
        }

        // ---- TreeContainerTagHelper: was new Regex(@"(.*?)option = \{", RegexOptions.Compiled) ----
        [DataTestMethod]
        [DataRow("var grid1option = { url: '/x' };")]
        [DataRow("no option var here")]
        public void GridOptionVarRegex_MatchesLegacyPattern(string input)
        {
            var oldMatch = new Regex(@"(.*?)option = \{").Match(input);
            var newMatch = WalkingTec.Mvvm.TagHelpers.LayUI.LayUiRegexes.GridOptionVarRegex().Match(input);

            Assert.AreEqual(oldMatch.Success, newMatch.Success);
            if (oldMatch.Success)
            {
                Assert.AreEqual(oldMatch.Groups[1].Value, newMatch.Groups[1].Value);
            }
        }

        // ---- EtlErrorSanitizer: connection-string redaction patterns. EtlRegexes is
        // internal to the Etl assembly (no InternalsVisibleTo for Core.Test), so parity is
        // asserted through the public Sanitize/SanitizeRaw surface instead of the individual
        // regex accessors — proving the redaction behavior is unchanged after the #713
        // conversion to [GeneratedRegex]. ----
        [DataTestMethod]
        [DataRow("Server=db1;Password=hunter2;Database=wtm;", "[redacted];[redacted];[redacted];")]
        [DataRow("Data Source=host;User Id=sa;Pwd=secret;", "[redacted];[redacted];[redacted];")]
        [DataRow("no secrets here", "no secrets here")]
        public void EtlErrorSanitizer_RedactsConnectionStringFragments(string input, string expected)
        {
            var result = EtlErrorSanitizer.SanitizeRaw(input);

            Assert.AreEqual(expected, result);
        }
    }
}
