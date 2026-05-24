#nullable enable
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test.Helper
{
    [TestClass]
    public class LogSanitizerTests
    {
        // ─── Null / empty ────────────────────────────────────────────────────────

        [TestMethod]
        public void Sanitize_Null_ReturnsEmptyString()
        {
            LogSanitizer.Sanitize(null).Should().Be(string.Empty);
        }

        [TestMethod]
        public void Sanitize_EmptyString_ReturnsEmptyString()
        {
            LogSanitizer.Sanitize(string.Empty).Should().Be(string.Empty);
        }

        // ─── Plain passthrough ───────────────────────────────────────────────────

        [TestMethod]
        public void Sanitize_PlainAlphanumericString_ReturnsUnchanged()
        {
            const string input = "tenant-abc_123";
            LogSanitizer.Sanitize(input).Should().Be(input);
        }

        [TestMethod]
        public void Sanitize_StringWithSpacesAndPunctuation_ReturnsUnchanged()
        {
            const string input = "/api/users?id=42&active=true";
            LogSanitizer.Sanitize(input).Should().Be(input);
        }

        // ─── CR / LF replacement ─────────────────────────────────────────────────

        [TestMethod]
        public void Sanitize_StringWithCR_ReplacesCRWithSpace()
        {
            LogSanitizer.Sanitize("line1\rline2").Should().Be("line1 line2");
        }

        [TestMethod]
        public void Sanitize_StringWithLF_ReplacesLFWithSpace()
        {
            LogSanitizer.Sanitize("line1\nFAKE LOG ENTRY").Should().Be("line1 FAKE LOG ENTRY");
        }

        [TestMethod]
        public void Sanitize_StringWithCRLF_ReplacesBothWithSpaces()
        {
            LogSanitizer.Sanitize("line1\r\nline2").Should().Be("line1  line2");
        }

        // ─── TAB replacement ─────────────────────────────────────────────────────

        [TestMethod]
        public void Sanitize_StringWithTab_ReplacesTabWithSpace()
        {
            LogSanitizer.Sanitize("col1\tcol2").Should().Be("col1 col2");
        }

        // ─── Other control characters dropped ────────────────────────────────────

        [TestMethod]
        public void Sanitize_StringWithNonPrintableControlChars_DropsControlChars()
        {
            // \x01 SOH, \x02 STX — neither CR nor LF/TAB
            LogSanitizer.Sanitize("\x01\x02value").Should().Be("value");
        }

        [TestMethod]
        public void Sanitize_StringWithBellChar_DropsIt()
        {
            LogSanitizer.Sanitize("abc\x07xyz").Should().Be("abcxyz");
        }

        // ─── Mixed: injection attempt ────────────────────────────────────────────

        [TestMethod]
        public void Sanitize_AttackerInjectsNewlineToFakeLogLine_InjectionNeutralized()
        {
            const string input = "normalUser\n[ERROR] FakeAlert: admin logged out";
            var result = LogSanitizer.Sanitize(input);
            result.Should().NotContain("\n");
            result.Should().Be("normalUser [ERROR] FakeAlert: admin logged out");
        }

        // ─── Boundary / length truncation ────────────────────────────────────────

        [TestMethod]
        public void Sanitize_ShortString_NotTruncated()
        {
            const string input = "hello world";
            LogSanitizer.Sanitize(input).Should().Be(input);
        }

        [TestMethod]
        public void Sanitize_StringExactlyAtDefaultMaxLength_NotTruncated()
        {
            // Default maxLength is 200 — a string of exactly 200 chars should not be truncated.
            var input = new string('a', 200);
            var result = LogSanitizer.Sanitize(input);
            result.Should().HaveLength(200);
            result.Should().NotEndWith("...[truncated]");
        }

        [TestMethod]
        public void Sanitize_StringOneCharOverDefaultMaxLength_Truncated()
        {
            var input = new string('a', 201);
            var result = LogSanitizer.Sanitize(input);
            result.Should().EndWith("...[truncated]");
            // Result is 200 chars of 'a' + "...[truncated]" = 214 chars
            result.Length.Should().Be(200 + "...[truncated]".Length);
        }

        [TestMethod]
        public void Sanitize_VeryLongString_TruncatedWithSuffix()
        {
            var input = new string('x', 1000);
            var result = LogSanitizer.Sanitize(input);
            result.Should().EndWith("...[truncated]");
            result.Should().StartWith("xx"); // begins with retained content
        }

        [TestMethod]
        public void Sanitize_CustomMaxLength_HonoredOverDefault()
        {
            const string input = "abcdefghij"; // 10 chars
            var result = LogSanitizer.Sanitize(input, maxLength: 5);
            result.Should().Be("abcde...[truncated]");
        }

        [TestMethod]
        public void Sanitize_MaxLengthZero_ReturnsOnlySuffix()
        {
            // When maxLength=0, no characters from the input are retained.
            // The method returns "...[truncated]" because even empty content
            // exceeds the 0-character budget.
            var result = LogSanitizer.Sanitize("anything", maxLength: 0);
            result.Should().Be("...[truncated]");
        }
    }
}
