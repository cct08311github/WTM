#nullable enable
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Issue #614: regression coverage for <see cref="LayuiAssets.ResolveLayuiBase(IConfiguration)"/>.
    ///
    /// <para>The XML doc on <see cref="LayuiAssets"/> declares a SECURITY INVARIANT: the raw
    /// <c>Layui:Asset</c> config value is never concatenated into a URL — the method only ever
    /// compares it for equality against the single literal <c>"legacy"</c> and returns one of
    /// exactly two hardcoded literals. These tests assert both the documented selection
    /// semantics (legacy vs. default) and — for every input, including hostile/injection
    /// strings — that the invariant holds.</para>
    /// </summary>
    [TestClass]
    public class LayuiAssetsTests
    {
        private static IConfiguration ConfigWithAsset(string? value)
        {
            var data = new Dictionary<string, string?> { ["Layui:Asset"] = value };
            return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        }

        // ── Documented selection semantics ─────────────────────────────────────

        [TestMethod]
        public void ResolveLayuiBase_ExactLegacy_ReturnsLegacyPath()
        {
            var config = ConfigWithAsset("legacy");

            LayuiAssets.ResolveLayuiBase(config).Should().Be("/layui",
                because: "\"legacy\" (Ordinal) is the only value that selects the vendored 2.6.3 tree");
        }

        [TestMethod]
        public void ResolveLayuiBase_AbsentKey_ReturnsNextPath()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

            LayuiAssets.ResolveLayuiBase(config).Should().Be("/layui-next",
                because: "an absent Layui:Asset key must default to the 2.13.8 tree");
        }

        [TestMethod]
        public void ResolveLayuiBase_NullConfig_ReturnsNextPath()
        {
            LayuiAssets.ResolveLayuiBase(null!).Should().Be("/layui-next",
                because: "a null IConfiguration must be handled null-safely, defaulting to /layui-next");
        }

        // ── Every other value (case variants, whitespace, hostile input) → default ─

        [DataTestMethod]
        [DataRow("next")]
        [DataRow("Legacy")]          // case-sensitive (Ordinal) — must NOT match
        [DataRow("LEGACY")]
        [DataRow(" legacy ")]        // whitespace padding must NOT match
        [DataRow("")]
        [DataRow("legacy;/../../etc")]
        [DataRow("/layui-next\"><script>")]
        [DataRow("javascript:alert(1)")]
        [DataRow("../../secret")]
        public void ResolveLayuiBase_AnyOtherValue_ReturnsNextPath(string value)
        {
            var config = ConfigWithAsset(value);

            LayuiAssets.ResolveLayuiBase(config).Should().Be("/layui-next",
                because: $"only the exact literal \"legacy\" selects /layui; \"{value}\" must default to /layui-next");
        }

        // ── SECURITY INVARIANT: result is always one of the two fixed literals ────

        [DataTestMethod]
        [DataRow("legacy")]
        [DataRow("next")]
        [DataRow("Legacy")]
        [DataRow("LEGACY")]
        [DataRow(" legacy ")]
        [DataRow("")]
        [DataRow("legacy;/../../etc")]
        [DataRow("/layui-next\"><script>")]
        [DataRow("javascript:alert(1)")]
        [DataRow("../../secret")]
        public void ResolveLayuiBase_NeverLeaksRawConfigValue_AlwaysOneOfTwoLiterals(string value)
        {
            var config = ConfigWithAsset(value);

            var result = LayuiAssets.ResolveLayuiBase(config);

            result.Should().BeOneOf(["/layui", "/layui-next"],
                because: "the SECURITY INVARIANT on LayuiAssets guarantees the raw config value " +
                          "is never concatenated into the returned path — only one of two fixed " +
                          $"literals may be returned, regardless of input (\"{value}\")");
        }

        [TestMethod]
        public void ResolveLayuiBase_NullConfig_AlsoSatisfiesSecurityInvariant()
        {
            var result = LayuiAssets.ResolveLayuiBase(null!);

            result.Should().BeOneOf("/layui", "/layui-next");
        }
    }
}
