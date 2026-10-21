using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace WalkingTec.Mvvm.E2E.Infrastructure
{
    /// <summary>Minimal editing of the .xlsx files the framework writes (inline strings), so that tests can fill an import template.</summary>
    public static class XlsxFile
    {
        private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        /// <summary>Appends a row to the first sheet. Numbers are written as numbers, everything else as inline text.</summary>
        public static void AppendRow(string path, params object[] values)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
            var entry = zip.Entries.First(e => e.FullName.EndsWith("worksheets/sheet1.xml"));
            XDocument doc;
            using (var read = entry.Open())
            {
                doc = XDocument.Load(read);
            }
            var data = doc.Descendants().First(e => e.Name.LocalName == "sheetData");
            var ns = data.Name.Namespace;
            var rowNumber = data.Elements().Count() + 1;
            var row = new XElement(ns + "row", new XAttribute("r", rowNumber));
            for (var i = 0; i < values.Length; i++)
            {
                var reference = (char)('A' + i) + rowNumber.ToString();
                if (values[i] is int or long or decimal or double)
                {
                    row.Add(new XElement(ns + "c", new XAttribute("r", reference), new XAttribute("t", "n"), new XElement(ns + "v", System.Convert.ToString(values[i], System.Globalization.CultureInfo.InvariantCulture))));
                }
                else
                {
                    row.Add(new XElement(ns + "c", new XAttribute("r", reference), new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", values[i]?.ToString()))));
                }
            }
            data.Add(row);
            entry.Delete();
            var created = zip.CreateEntry("xl/worksheets/sheet1.xml");
            using var write = created.Open();
            doc.Save(write);
        }
    }
}
