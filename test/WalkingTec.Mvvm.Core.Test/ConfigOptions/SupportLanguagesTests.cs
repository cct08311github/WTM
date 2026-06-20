#nullable enable
using System.Globalization;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.ConfigOptions;

namespace WalkingTec.Mvvm.Core.Test.ConfigOptions
{
    /// <summary>
    /// Tests for <see cref="Configs.SupportLanguages"/> i18n fallback hardening (#423).
    /// </summary>
    [TestClass]
    public class SupportLanguagesTests
    {
        // ─── Default / unset Languages ────────────────────────────────────────

        [TestMethod]
        public void SupportLanguages_WhenLanguagesNotSet_ReturnsSingleZhEntry()
        {
            // Languages default is "zh" when null
            var cfg = new Configs();
            cfg.SupportLanguages.Should().NotBeEmpty();
            cfg.SupportLanguages[0].Name.Should().Be("zh");
        }

        // ─── Guard: empty / whitespace Languages ──────────────────────────────

        [TestMethod]
        public void SupportLanguages_WhenLanguagesIsEmpty_FallsBackToDefaults()
        {
            var cfg = new Configs { Languages = "" };
            cfg.SupportLanguages.Should().NotBeEmpty();
        }

        [TestMethod]
        public void SupportLanguages_WhenLanguagesIsEmpty_FallbackContainsZh()
        {
            var cfg = new Configs { Languages = "" };
            cfg.SupportLanguages.Should().Contain(c => c.Name == "zh");
        }

        [TestMethod]
        public void SupportLanguages_WhenLanguagesIsEmpty_DefaultsToZhViaLanguagesGetter()
        {
            // Languages getter returns "zh" when the backing field is empty,
            // so SupportLanguages produces ["zh"] (the Languages-getter default),
            // not the zh/en pair — that pair only fires when tokens are present
            // but all blank (e.g. whitespace-only "   " or comma-only ",").
            var cfg = new Configs { Languages = "" };
            cfg.SupportLanguages.Should().HaveCount(1);
            cfg.SupportLanguages[0].Name.Should().Be("zh");
        }

        [TestMethod]
        public void SupportLanguages_WhenLanguagesIsWhitespace_FallsBackToDefaults()
        {
            var cfg = new Configs { Languages = "   " };
            cfg.SupportLanguages.Should().NotBeEmpty();
        }

        [TestMethod]
        public void SupportLanguages_WhenLanguagesIsWhitespace_FallbackContainsZh()
        {
            var cfg = new Configs { Languages = "   " };
            cfg.SupportLanguages.Should().Contain(c => c.Name == "zh");
        }

        [TestMethod]
        public void SupportLanguages_WhenLanguagesIsCommaOnly_FallsBackToDefaults()
        {
            // Comma with no tokens produces no valid cultures.
            var cfg = new Configs { Languages = "," };
            cfg.SupportLanguages.Should().NotBeEmpty();
        }

        [TestMethod]
        public void SupportLanguages_WhenLanguagesIsCommaOnly_FallbackContainsZh()
        {
            var cfg = new Configs { Languages = "," };
            cfg.SupportLanguages.Should().Contain(c => c.Name == "zh");
        }

        // ─── Guard: whitespace-padded tokens ──────────────────────────────────

        [TestMethod]
        public void SupportLanguages_WhenLanguagesHasSpacePaddedTokens_ParsesCorrectly()
        {
            // " zh , en " should parse to ["zh", "en"], not ["zh ", " en "]
            var cfg = new Configs { Languages = " zh , en " };
            cfg.SupportLanguages.Should().HaveCount(2);
            cfg.SupportLanguages[0].Name.Should().Be("zh");
            cfg.SupportLanguages[1].Name.Should().Be("en");
        }

        // ─── Configured cultures are unchanged ────────────────────────────────

        [TestMethod]
        public void SupportLanguages_WhenLanguagesIsZh_ReturnsSingleZhEntry()
        {
            var cfg = new Configs { Languages = "zh" };
            cfg.SupportLanguages.Should().HaveCount(1);
            cfg.SupportLanguages[0].Name.Should().Be("zh");
        }

        [TestMethod]
        public void SupportLanguages_WhenLanguagesIsZhCommaEn_ReturnsBothCultures()
        {
            var cfg = new Configs { Languages = "zh,en" };
            cfg.SupportLanguages.Should().HaveCount(2);
            cfg.SupportLanguages[0].Name.Should().Be("zh");
            cfg.SupportLanguages[1].Name.Should().Be("en");
        }

        [TestMethod]
        public void SupportLanguages_WhenLanguagesIsZhTW_ReturnsSingleZhTWEntry()
        {
            var cfg = new Configs { Languages = "zh-TW" };
            cfg.SupportLanguages.Should().HaveCount(1);
            cfg.SupportLanguages[0].Name.Should().Be("zh-TW");
        }

        [TestMethod]
        public void SupportLanguages_WhenLanguagesIsThreeEntries_ReturnsAllThree()
        {
            var cfg = new Configs { Languages = "zh,en,fr" };
            cfg.SupportLanguages.Should().HaveCount(3);
        }

        // ─── Fallback always produces a valid [0] index (no NRE / IndexOutOfRange) ─

        [TestMethod]
        public void SupportLanguages_FallbackFirstEntry_IsAccessibleWithoutException()
        {
            // Simulates the downstream access pattern: conf.SupportLanguages[0]
            var cfg = new Configs { Languages = "" };
            // Must not throw IndexOutOfRangeException or NullReferenceException
            var first = cfg.SupportLanguages[0];
            first.Should().NotBeNull();
        }

        // ─── Result is cached (repeated access returns same list instance) ──────

        [TestMethod]
        public void SupportLanguages_RepeatedAccess_ReturnsSameListInstance()
        {
            var cfg = new Configs { Languages = "zh,en" };
            var first = cfg.SupportLanguages;
            var second = cfg.SupportLanguages;
            first.Should().BeSameAs(second);
        }
    }
}
