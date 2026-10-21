using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;

namespace WalkingTec.Mvvm.E2E.Support
{
    /// <summary>
    /// Builds an import file from the Excel template the site itself offers for download: the template's header row is kept and
    /// the given rows are appended as text cells (the sites read cells by column reference, so the column order of the template
    /// is what matters). No spreadsheet library is needed, and the test cannot drift from the template the product ships.
    /// </summary>
    public static class ExcelTemplate
    {
        public static byte[] WithRows(byte[] template, params string[][] rows)
        {
            using var input = new MemoryStream(template);
            using var output = new MemoryStream();
            using (var source = new ZipArchive(input, ZipArchiveMode.Read))
            using (var target = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                foreach (var entry in source.Entries)
                {
                    using var reader = entry.Open();
                    using var buffer = new MemoryStream();
                    reader.CopyTo(buffer);
                    var data = buffer.ToArray();
                    if (entry.FullName == "xl/worksheets/sheet1.xml")
                    {
                        data = Encoding.UTF8.GetBytes(AppendRows(Encoding.UTF8.GetString(data), rows));
                    }
                    var copy = target.CreateEntry(entry.FullName);
                    using var writer = copy.Open();
                    writer.Write(data, 0, data.Length);
                }
            }
            return output.ToArray();
        }

        /// <summary>The XML of the first sheet (cell texts are inline strings).</summary>
        public static string SheetXml(byte[] workbook)
        {
            using var zip = new ZipArchive(new MemoryStream(workbook), ZipArchiveMode.Read);
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml").Open());
            return reader.ReadToEnd();
        }

        /// <summary>The header texts of the first sheet, in column order (a trailing * marks required columns).</summary>
        public static string[] Headers(byte[] workbook)
        {
            using var zip = new ZipArchive(new MemoryStream(workbook), ZipArchiveMode.Read);
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml").Open());
            var xml = reader.ReadToEnd();
            var firstRow = xml.Substring(xml.IndexOf("<x:row", StringComparison.Ordinal));
            firstRow = firstRow.Substring(0, firstRow.IndexOf("</x:row>", StringComparison.Ordinal));
            return firstRow.Split("<x:t>").Skip(1).Select(x => x.Substring(0, x.IndexOf("</x:t>", StringComparison.Ordinal))).ToArray();
        }

        private static string AppendRows(string sheetXml, string[][] rows)
        {
            var sb = new StringBuilder();
            for (var r = 0; r < rows.Length; r++)
            {
                var rowNumber = r + 2;
                sb.Append($"<x:row r=\"{rowNumber}\">");
                for (var c = 0; c < rows[r].Length; c++)
                {
                    sb.Append($"<x:c r=\"{ColumnName(c)}{rowNumber}\" t=\"inlineStr\"><x:is><x:t>{SecurityElement.Escape(rows[r][c])}</x:t></x:is></x:c>");
                }
                sb.Append("</x:row>");
            }
            var end = sheetXml.IndexOf("</x:sheetData>", StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidDataException("The template has no sheet data element; its format changed.");
            }
            return sheetXml.Insert(end, sb.ToString());
        }

        private static string ColumnName(int index)
        {
            var name = "";
            for (var i = index; i >= 0; i = i / 26 - 1)
            {
                name = (char)('A' + i % 26) + name;
            }
            return name;
        }
    }
}
