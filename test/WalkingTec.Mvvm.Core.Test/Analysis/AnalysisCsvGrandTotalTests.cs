#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    /// <summary>
    /// Tests for grand-total footer rendering in
    /// <c>_AnalysisController.BuildCsv</c> (private; accessed via
    /// reflection, same pattern as <c>CsvEscapeTest</c>). The CSV
    /// half of the <see cref="AnalysisQueryResponse.GrandTotalRow"/>
    /// contract — pairs with <see cref="AnalysisExcelExporter"/>'s
    /// rendering so xlsx and csv agree byte-for-character on what a
    /// "Total" row looks like.
    /// </summary>
    [TestClass]
    public class AnalysisCsvGrandTotalTests
    {
        private static MethodInfo? _buildCsv;

        [ClassInitialize]
        public static void ClassInit(TestContext _)
        {
            var controllerType = typeof(WalkingTec.Mvvm.Mvc._AnalysisController);
            _buildCsv = controllerType.GetMethod(
                "BuildCsv",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(_buildCsv, "BuildCsv method should exist");
        }

        private static string BuildCsv(AnalysisQueryResponse result, bool includeMetadata = false)
            => (string)_buildCsv!.Invoke(null, new object[] { result, includeMetadata })!;

        private static AnalysisQueryResponse MakeResp(
            List<string> columns,
            List<Dictionary<string, object?>> rows,
            Dictionary<string, object?>? grandTotal = null)
            => new()
            {
                Columns        = columns,
                Rows           = rows,
                TotalCount     = rows.Count,
                Truncated      = false,
                QueryHash      = "0000000000000000",
                GrandTotalRow  = grandTotal,
            };

        // ── No grand total → existing output unchanged ──────────────────

        [TestMethod]
        public void Null_grand_total_emits_no_extra_line()
        {
            var resp = MakeResp(
                new() { "Region", "Amount_Sum" },
                new()
                {
                    new() { ["Region"] = "北", ["Amount_Sum"] = 100m },
                    new() { ["Region"] = "南", ["Amount_Sum"] = 200m },
                });

            var csv = BuildCsv(resp);
            // Header + 2 rows = 2 newlines (StringBuilder.AppendLine).
            // No tail row.
            var lines = csv.TrimEnd('\r', '\n').Split('\n');
            Assert.AreEqual(3, lines.Length, "Header + 2 data lines, no footer.");
        }

        // ── Grand total appended after data rows ────────────────────────

        [TestMethod]
        public void Grand_total_row_is_appended_after_data()
        {
            var resp = MakeResp(
                new() { "Region", "Amount_Sum" },
                new()
                {
                    new() { ["Region"] = "北", ["Amount_Sum"] = 100m },
                    new() { ["Region"] = "南", ["Amount_Sum"] = 200m },
                },
                grandTotal: new()
                {
                    ["Region"] = null,
                    ["Amount_Sum"] = 300m,
                });

            var csv = BuildCsv(resp);
            var lines = csv.TrimEnd('\r', '\n').Split('\n')
                            .Select(l => l.TrimEnd('\r')).ToArray();

            Assert.AreEqual(4, lines.Length, "Header + 2 data rows + 1 footer.");
            Assert.AreEqual("總計,300", lines[3]);
        }

        [TestMethod]
        public void Grand_total_label_appears_in_first_null_dimension_only()
        {
            var resp = MakeResp(
                new() { "Region", "Channel", "Amount_Sum" },
                new()
                {
                    new() { ["Region"] = "北", ["Channel"] = "A", ["Amount_Sum"] = 100m },
                },
                grandTotal: new()
                {
                    ["Region"] = null,
                    ["Channel"] = null,
                    ["Amount_Sum"] = 100m,
                });

            var csv = BuildCsv(resp);
            var lines = csv.TrimEnd('\r', '\n').Split('\n')
                            .Select(l => l.TrimEnd('\r')).ToArray();

            // Last line should be "總計,,100" — label in col 0, blank col 1,
            // numeric in col 2.
            Assert.AreEqual("總計,,100", lines[^1]);
        }

        [TestMethod]
        public void Null_measure_cells_render_as_empty_strings()
        {
            // Avg / DistinctCount intentionally emit null in GrandTotalRow.
            var resp = MakeResp(
                new() { "Region", "Amount_Avg" },
                new()
                {
                    new() { ["Region"] = "北", ["Amount_Avg"] = 100m },
                },
                grandTotal: new()
                {
                    ["Region"] = null,
                    ["Amount_Avg"] = null,
                });

            var csv = BuildCsv(resp);
            var lines = csv.TrimEnd('\r', '\n').Split('\n')
                            .Select(l => l.TrimEnd('\r')).ToArray();

            // Footer: label + blank avg cell. NOT "總計,0".
            Assert.AreEqual("總計,", lines[^1],
                "null measure cell must render as empty (not 0, which would falsely suggest 'avg is zero').");
        }

        [TestMethod]
        public void Grand_total_with_metadata_section_still_renders()
        {
            var resp = MakeResp(
                new() { "Region", "Amount_Sum" },
                new()
                {
                    new() { ["Region"] = "北", ["Amount_Sum"] = 100m },
                },
                grandTotal: new()
                {
                    ["Region"] = null,
                    ["Amount_Sum"] = 100m,
                });

            var csv = BuildCsv(resp, includeMetadata: true);
            // Metadata block (4 lines) + blank separator + header + 1 data + 1 footer.
            Assert.IsTrue(csv.Contains("總計,100"));
            Assert.IsTrue(csv.Contains("匯出時間"),
                "Metadata block must still render alongside the new footer.");
        }

        // ── Defensive: formula injection on grand-total cell ────────────

        [TestMethod]
        public void Grand_total_dangerous_string_passes_through_EscapeCsvCell()
        {
            // Should never happen for a well-formed total, but if a caller
            // somehow lands a string starting with '=' / '+' / '-' / '@'
            // in the total row, the same anti-formula-injection escape
            // must kick in (defense in depth).
            var resp = MakeResp(
                new() { "Region", "Amount_Sum" },
                new() { new() { ["Region"] = "北", ["Amount_Sum"] = 100m } },
                grandTotal: new()
                {
                    ["Region"] = "=DANGEROUS()",
                    ["Amount_Sum"] = 100m,
                });

            var csv = BuildCsv(resp);
            // EscapeCsvCell prefixes a tab to neutralise.
            Assert.IsTrue(csv.Contains("\t=DANGEROUS()"),
                "Dangerous string in grand-total cell must be neutralised the same way as data rows.");
        }
    }
}
