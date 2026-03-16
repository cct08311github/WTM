using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    /// <summary>
    /// Tests for _AnalysisController.EscapeCsvCell() — CSV formula injection
    /// prevention and RFC 4180 quoting. Uses reflection to test the private method.
    /// </summary>
    [TestClass]
    public class CsvEscapeTest
    {
        private static MethodInfo? _escapeCsvCell;

        [ClassInitialize]
        public static void ClassInit(TestContext _)
        {
            var controllerType = typeof(WalkingTec.Mvvm.Mvc._AnalysisController);
            _escapeCsvCell = controllerType.GetMethod(
                "EscapeCsvCell",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(_escapeCsvCell, "EscapeCsvCell method should exist");
        }

        private static string Escape(string val)
        {
            return (string)_escapeCsvCell!.Invoke(null, new object[] { val })!;
        }

        // ─── Empty / Normal Values ───────────────────────────────────

        [TestMethod]
        public void EscapeCsvCell_EmptyString_ReturnsEmpty()
        {
            Assert.AreEqual("", Escape(""));
        }

        [TestMethod]
        public void EscapeCsvCell_NullString_ReturnsEmpty()
        {
            var result = (string)_escapeCsvCell!.Invoke(null, new object[] { (string)null! })!;
            Assert.AreEqual("", result);
        }

        [TestMethod]
        public void EscapeCsvCell_PlainText_Unchanged()
        {
            Assert.AreEqual("hello world", Escape("hello world"));
        }

        [TestMethod]
        public void EscapeCsvCell_NumberString_Unchanged()
        {
            Assert.AreEqual("12345", Escape("12345"));
        }

        // ─── Formula Injection Prevention ────────────────────────────

        [TestMethod]
        public void EscapeCsvCell_EqualsSign_PrefixesTab()
        {
            var result = Escape("=SUM(A1:A10)");
            Assert.IsTrue(result.StartsWith("\t"), "Should prefix tab for formula injection");
        }

        [TestMethod]
        public void EscapeCsvCell_PlusSign_PrefixesTab()
        {
            var result = Escape("+cmd|' /C calc'!A0");
            Assert.IsTrue(result.StartsWith("\t"));
        }

        [TestMethod]
        public void EscapeCsvCell_MinusSign_PrefixesTab()
        {
            var result = Escape("-1+1");
            Assert.IsTrue(result.StartsWith("\t"));
        }

        [TestMethod]
        public void EscapeCsvCell_AtSign_PrefixesTab()
        {
            var result = Escape("@SUM(A1)");
            Assert.IsTrue(result.StartsWith("\t"));
        }

        [TestMethod]
        public void EscapeCsvCell_TabPrefix_PrefixesTab()
        {
            var result = Escape("\tmalicious");
            Assert.IsTrue(result.StartsWith("\t\t"), "Tab-prefixed value should get additional tab");
        }

        [TestMethod]
        public void EscapeCsvCell_CarriageReturn_PrefixesTab()
        {
            var result = Escape("\rmalicious");
            Assert.IsTrue(result.StartsWith("\"") && result.Contains("\t\r"));
        }

        // ─── RFC 4180 Quoting ────────────────────────────────────────

        [TestMethod]
        public void EscapeCsvCell_ContainsComma_Quoted()
        {
            var result = Escape("hello, world");
            Assert.AreEqual("\"hello, world\"", result);
        }

        [TestMethod]
        public void EscapeCsvCell_ContainsNewline_Quoted()
        {
            var result = Escape("line1\nline2");
            Assert.AreEqual("\"line1\nline2\"", result);
        }

        [TestMethod]
        public void EscapeCsvCell_ContainsQuote_EscapedAndQuoted()
        {
            var result = Escape("say \"hello\"");
            Assert.AreEqual("\"say \"\"hello\"\"\"", result);
        }

        // ─── Combined: Injection + Quoting ───────────────────────────

        [TestMethod]
        public void EscapeCsvCell_FormulaWithComma_TabPrefixedAndQuoted()
        {
            var result = Escape("=1,2");
            // First: tab prefixed (formula char), then: quoted (has comma)
            Assert.IsTrue(result.StartsWith("\t"), "Tab prefix should come first");
            Assert.IsTrue(result.Contains("\"=1"), "Should also be quoted due to comma");
        }

        [TestMethod]
        public void EscapeCsvCell_MinusWithNewline_TabPrefixedAndQuoted()
        {
            var result = Escape("-data\nmore");
            // First: tab prefixed (formula char), then: quoted (has newline)
            Assert.IsTrue(result.StartsWith("\t"), "Tab prefix should come first");
            Assert.IsTrue(result.Contains("\"-data"), "Should also be quoted due to newline");
        }
    }
}
