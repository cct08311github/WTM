#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using WalkingTec.Mvvm.Core.Dashboard;
using WalkingTec.Mvvm.Core.Dashboard.Snapshot;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    [TestClass]
    public class DashboardExcelExporterTests
    {
        // ── Helper: parse NPOI workbook from bytes ────────────────────────────

        private static IWorkbook ReadWorkbook(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            return new XSSFWorkbook(ms);
        }

        // ── Basic single-widget export ────────────────────────────────────────

        [TestMethod]
        public void Export_with_rows_produces_one_sheet_with_header_and_data()
        {
            var dashboard = MakeDashboard("Sales", "w1", "Revenue");
            var widgetResults = new Dictionary<string, WidgetDataResult>
            {
                ["w1"] = new WidgetDataResult
                {
                    Rows = new List<Dictionary<string, object?>>
                    {
                        new Dictionary<string, object?> { ["region"] = "North", ["sales"] = 9500.0 },
                        new Dictionary<string, object?> { ["region"] = "South", ["sales"] = 7200.0 }
                    }
                }
            };

            var bytes = DashboardExcelExporter.Export(dashboard, widgetResults);

            bytes.Should().NotBeNullOrEmpty();
            var wb = ReadWorkbook(bytes);
            wb.NumberOfSheets.Should().Be(1);

            var sheet = wb.GetSheetAt(0);
            sheet.SheetName.Should().Be("Revenue");

            // Row 0 = header
            var header = sheet.GetRow(0);
            header.Should().NotBeNull();
            header.Cells.Count.Should().Be(2);

            // Row 1 = first data row
            var dataRow = sheet.GetRow(1);
            dataRow.Should().NotBeNull();
        }

        [TestMethod]
        public void Export_produces_one_sheet_per_widget()
        {
            var dashboard = new DashboardDefinition
            {
                Id = "db1",
                Title = "Multi Widget",
                Owner = "alice",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = MakeWidget("Revenue"),
                    ["w2"] = MakeWidget("Costs"),
                    ["w3"] = MakeWidget("Margin")
                }
            };

            var widgetResults = new Dictionary<string, WidgetDataResult>
            {
                ["w1"] = new WidgetDataResult { Value = 1000.0 },
                ["w2"] = new WidgetDataResult { Value = 800.0 },
                ["w3"] = new WidgetDataResult { Value = 200.0 }
            };

            var bytes = DashboardExcelExporter.Export(dashboard, widgetResults);
            var wb = ReadWorkbook(bytes);

            wb.NumberOfSheets.Should().Be(3, "one sheet per widget");
        }

        [TestMethod]
        public void Export_with_scalar_value_writes_value_sheet()
        {
            var dashboard = MakeDashboard("KPI", "w1", "Total Sales");
            var widgetResults = new Dictionary<string, WidgetDataResult>
            {
                ["w1"] = new WidgetDataResult { Value = 42_000.0 }
            };

            var bytes = DashboardExcelExporter.Export(dashboard, widgetResults);
            var wb = ReadWorkbook(bytes);

            var sheet = wb.GetSheetAt(0);
            sheet.SheetName.Should().Be("Total Sales");

            // Header row has "Value"
            var headerCell = sheet.GetRow(0).GetCell(0);
            headerCell.StringCellValue.Should().Be("Value");

            // Data row has the number.
            var dataCell = sheet.GetRow(1).GetCell(0);
            dataCell.NumericCellValue.Should().BeApproximately(42_000.0, 0.001);
        }

        [TestMethod]
        public void Export_with_metadata_writes_key_value_pairs()
        {
            var dashboard = MakeDashboard("Meta", "w1", "Metadata Widget");
            var widgetResults = new Dictionary<string, WidgetDataResult>
            {
                ["w1"] = new WidgetDataResult
                {
                    Metadata = new Dictionary<string, object?>
                    {
                        ["errorRate"] = 0.05,
                        ["requestCount"] = 1000
                    }
                }
            };

            var bytes = DashboardExcelExporter.Export(dashboard, widgetResults);
            var wb = ReadWorkbook(bytes);

            var sheet = wb.GetSheetAt(0);
            // Row 0 = header ("Key" / "Value")
            // Row 1, 2 = data pairs
            sheet.LastRowNum.Should().Be(2, "two metadata entries → two data rows plus header");
        }

        [TestMethod]
        public void Export_widget_with_error_writes_notice_not_data()
        {
            var dashboard = MakeDashboard("Error", "w1", "Broken Widget");
            var widgetResults = new Dictionary<string, WidgetDataResult>
            {
                ["w1"] = new WidgetDataResult { Error = "source unavailable" }
            };

            var bytes = DashboardExcelExporter.Export(dashboard, widgetResults);
            var wb = ReadWorkbook(bytes);

            var sheet = wb.GetSheetAt(0);
            var cell = sheet.GetRow(0).GetCell(0);
            cell.StringCellValue.Should().Contain("source unavailable");
        }

        [TestMethod]
        public void Export_widget_absent_from_results_writes_no_data_notice()
        {
            var dashboard = MakeDashboard("Missing", "w1", "No Data Widget");
            var widgetResults = new Dictionary<string, WidgetDataResult>();
            // w1 is intentionally absent.

            var bytes = DashboardExcelExporter.Export(dashboard, widgetResults);
            var wb = ReadWorkbook(bytes);

            var sheet = wb.GetSheetAt(0);
            var cell = sheet.GetRow(0).GetCell(0);
            cell.StringCellValue.Should().Contain("no data");
        }

        [TestMethod]
        public void Export_sheet_names_are_deduplicated_when_widget_titles_clash()
        {
            var dashboard = new DashboardDefinition
            {
                Id = "db2",
                Title = "Clash Test",
                Owner = "alice",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = MakeWidget("Revenue"),
                    ["w2"] = MakeWidget("Revenue"),  // same title → should be deduplicated
                }
            };
            var widgetResults = new Dictionary<string, WidgetDataResult>
            {
                ["w1"] = new WidgetDataResult { Value = 1.0 },
                ["w2"] = new WidgetDataResult { Value = 2.0 }
            };

            var bytes = DashboardExcelExporter.Export(dashboard, widgetResults);
            var wb = ReadWorkbook(bytes);

            wb.NumberOfSheets.Should().Be(2);
            var name0 = wb.GetSheetAt(0).SheetName;
            var name1 = wb.GetSheetAt(1).SheetName;
            name0.Should().NotBe(name1, "duplicate sheet names must be made unique");
        }

        [TestMethod]
        public void Export_long_widget_title_is_truncated_to_31_chars()
        {
            var longTitle = new string('A', 60); // exceeds Excel 31-char limit
            var dashboard = MakeDashboard("LongTitle", "w1", longTitle);
            var widgetResults = new Dictionary<string, WidgetDataResult>
            {
                ["w1"] = new WidgetDataResult { Value = 1.0 }
            };

            var bytes = DashboardExcelExporter.Export(dashboard, widgetResults);
            var wb = ReadWorkbook(bytes);

            wb.GetSheetAt(0).SheetName.Length.Should().BeLessOrEqualTo(31);
        }

        [TestMethod]
        public void Export_empty_dashboard_produces_valid_placeholder_workbook()
        {
            var dashboard = new DashboardDefinition
            {
                Id = "empty",
                Title = "Empty",
                Owner = "alice",
                Widgets = new Dictionary<string, WidgetDefinition>()
            };

            var bytes = DashboardExcelExporter.Export(dashboard, new Dictionary<string, WidgetDataResult>());

            bytes.Should().NotBeNullOrEmpty("even an empty dashboard should produce a valid workbook");
            var wb = ReadWorkbook(bytes);
            wb.NumberOfSheets.Should().Be(1, "placeholder sheet");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static DashboardDefinition MakeDashboard(string title, string widgetId, string widgetTitle)
        {
            return new DashboardDefinition
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = title,
                Owner = "alice",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    [widgetId] = MakeWidget(widgetTitle)
                }
            };
        }

        private static WidgetDefinition MakeWidget(string title) =>
            new WidgetDefinition
            {
                Type = "kpi",
                Title = title,
                Source = new WidgetSourceDefinition { Kind = "custom" }
            };
    }
}
