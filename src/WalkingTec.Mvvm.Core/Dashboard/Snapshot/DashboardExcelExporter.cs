#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace WalkingTec.Mvvm.Core.Dashboard.Snapshot;

/// <summary>
/// Produces an Excel workbook from a <see cref="DashboardDefinition"/> and its widget data.
/// One sheet per widget; sheet name is truncated to the Excel 31-character limit.
/// Built with NPOI (no COM interop or external process required).
/// </summary>
internal static class DashboardExcelExporter
{
    /// <summary>Maximum sheet name length in Excel/XLSX.</summary>
    private const int MaxSheetNameLength = 31;

    /// <summary>
    /// Builds an XLSX workbook from the given widget data map.
    /// </summary>
    /// <param name="dashboard">Dashboard definition (title, widget metadata).</param>
    /// <param name="widgetResults">
    /// Map of widgetId → data result. Widgets absent from this map are written as an
    /// empty sheet with a "(no data)" notice.
    /// </param>
    /// <returns>Raw bytes of the .xlsx file.</returns>
    internal static byte[] Export(
        DashboardDefinition dashboard,
        IReadOnlyDictionary<string, WidgetDataResult> widgetResults)
    {
        if (dashboard == null) throw new ArgumentNullException(nameof(dashboard));
        if (widgetResults == null) throw new ArgumentNullException(nameof(widgetResults));

        var workbook = new XSSFWorkbook();

        // Header cell style — bold.
        var headerStyle = workbook.CreateCellStyle();
        var headerFont = workbook.CreateFont();
        headerFont.IsBold = true;
        headerStyle.SetFont(headerFont);

        // Process widgets in stable order.
        foreach (var (widgetId, widget) in dashboard.Widgets)
        {
            var sheetName = SanitiseSheetName(
                string.IsNullOrWhiteSpace(widget.Title) ? widgetId : widget.Title,
                workbook);

            var sheet = workbook.CreateSheet(sheetName);

            widgetResults.TryGetValue(widgetId, out var result);

            if (result == null || result.Error != null)
            {
                WriteNotice(sheet, result?.Error ?? "(no data returned)");
                continue;
            }

            if (result.Rows != null && result.Rows.Count > 0)
            {
                WriteRowData(sheet, result.Rows, headerStyle);
            }
            else if (result.Value != null)
            {
                WriteScalarValue(sheet, result.Value, headerStyle);
            }
            else if (result.Metadata != null)
            {
                WriteMetadata(sheet, result.Metadata, headerStyle);
            }
            else
            {
                WriteNotice(sheet, "(no data)");
            }
        }

        // If no widgets, add a placeholder sheet so the workbook is valid.
        if (dashboard.Widgets.Count == 0)
            workbook.CreateSheet("Dashboard").CreateRow(0).CreateCell(0).SetCellValue("(no widgets)");

        using var ms = new MemoryStream();
        workbook.Write(ms, leaveOpen: true);
        return ms.ToArray();
    }

    // ── Sheet writers ─────────────────────────────────────────────────────────

    private static void WriteRowData(
        ISheet sheet,
        List<Dictionary<string, object?>> rows,
        ICellStyle headerStyle)
    {
        if (rows.Count == 0)
        {
            WriteNotice(sheet, "(no rows)");
            return;
        }

        // Collect ordered column names from the first row.
        var columns = new List<string>(rows[0].Keys);

        // Header row.
        var headerRow = sheet.CreateRow(0);
        for (var c = 0; c < columns.Count; c++)
        {
            var cell = headerRow.CreateCell(c);
            cell.SetCellValue(columns[c]);
            cell.CellStyle = headerStyle;
        }

        // Data rows.
        for (var r = 0; r < rows.Count; r++)
        {
            var dataRow = sheet.CreateRow(r + 1);
            for (var c = 0; c < columns.Count; c++)
            {
                rows[r].TryGetValue(columns[c], out var raw);
                SetCellValue(dataRow.CreateCell(c), raw);
            }
        }
    }

    private static void WriteScalarValue(ISheet sheet, object value, ICellStyle headerStyle)
    {
        var headerRow = sheet.CreateRow(0);
        var h = headerRow.CreateCell(0);
        h.SetCellValue("Value");
        h.CellStyle = headerStyle;

        SetCellValue(sheet.CreateRow(1).CreateCell(0), value);
    }

    private static void WriteMetadata(
        ISheet sheet,
        Dictionary<string, object?> metadata,
        ICellStyle headerStyle)
    {
        var keyHeader = sheet.CreateRow(0).CreateCell(0);
        keyHeader.SetCellValue("Key");
        keyHeader.CellStyle = headerStyle;

        var valHeader = sheet.GetRow(0).CreateCell(1);
        valHeader.SetCellValue("Value");
        valHeader.CellStyle = headerStyle;

        var r = 1;
        foreach (var (k, v) in metadata)
        {
            var row = sheet.CreateRow(r++);
            row.CreateCell(0).SetCellValue(k);
            SetCellValue(row.CreateCell(1), v);
        }
    }

    private static void WriteNotice(ISheet sheet, string notice)
    {
        sheet.CreateRow(0).CreateCell(0).SetCellValue(notice);
    }

    // ── Cell value helper ─────────────────────────────────────────────────────

    private static void SetCellValue(ICell cell, object? raw)
    {
        if (raw == null)
        {
            cell.SetCellValue((string?)null);
            return;
        }

        // JsonElement (from System.Text.Json deserialization)
        if (raw is JsonElement je)
        {
            switch (je.ValueKind)
            {
                case JsonValueKind.Number:
                    cell.SetCellValue(je.GetDouble());
                    return;
                case JsonValueKind.True:
                    cell.SetCellValue(true);
                    return;
                case JsonValueKind.False:
                    cell.SetCellValue(false);
                    return;
                case JsonValueKind.String:
                    cell.SetCellValue(je.GetString());
                    return;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    cell.SetCellValue((string?)null);
                    return;
                default:
                    cell.SetCellValue(je.ToString());
                    return;
            }
        }

        // Numeric types → double cell
        if (raw is double d) { cell.SetCellValue(d); return; }
        if (raw is float f) { cell.SetCellValue(f); return; }
        if (raw is decimal dec) { cell.SetCellValue((double)dec); return; }
        if (raw is int i) { cell.SetCellValue(i); return; }
        if (raw is long l) { cell.SetCellValue(l); return; }
        if (raw is short s) { cell.SetCellValue(s); return; }
        if (raw is byte b) { cell.SetCellValue(b); return; }
        if (raw is bool bl) { cell.SetCellValue(bl); return; }
        if (raw is DateTime dt) { cell.SetCellValue(dt); return; }

        // Fallback: ToString
        cell.SetCellValue(raw.ToString());
    }

    // ── Sheet name sanitisation ───────────────────────────────────────────────

    /// <summary>
    /// Truncates the name to <see cref="MaxSheetNameLength"/> and appends a numeric suffix
    /// to avoid collisions with existing sheet names.
    /// Excel forbids: \ / ? * [ ] :
    /// </summary>
    private static string SanitiseSheetName(string raw, IWorkbook workbook)
    {
        // Strip forbidden characters.
        var clean = System.Text.RegularExpressions.Regex.Replace(raw, @"[\\/?*\[\]:]", "_");

        // Truncate base (leaving room for a potential " (N)" suffix).
        if (clean.Length > MaxSheetNameLength)
            clean = clean[..MaxSheetNameLength];

        // De-duplicate against existing sheets.
        var candidate = clean;
        var suffix = 1;
        while (workbook.GetSheetIndex(candidate) >= 0)
        {
            var suffixStr = $" ({suffix++})";
            var maxBase = MaxSheetNameLength - suffixStr.Length;
            candidate = (clean.Length > maxBase ? clean[..maxBase] : clean) + suffixStr;
        }

        return candidate;
    }
}
