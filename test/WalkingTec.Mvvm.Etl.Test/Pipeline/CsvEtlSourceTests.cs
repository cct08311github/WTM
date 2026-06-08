#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Pipeline.Sources;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

[TestClass]
public class CsvEtlSourceTests
{
    // -----------------------------------------------------------------
    // Helper — writes CSV content to a temp file, returns its path.
    // -----------------------------------------------------------------
    private static string WriteTempCsv(string content, Encoding? encoding = null)
    {
        var path = Path.GetTempFileName() + ".csv";
        File.WriteAllText(path, content, encoding ?? Encoding.UTF8);
        return path;
    }

    // -----------------------------------------------------------------
    // Basic: header + data rows
    // -----------------------------------------------------------------

    [TestMethod]
    public async Task Simple_csv_with_header_parses_correctly()
    {
        var csv = "Name,Age,City\r\nAlice,30,Taipei\r\nBob,25,Kaohsiung\r\n";
        var path = WriteTempCsv(csv);
        try
        {
            using var src = new CsvEtlSource();
            var rows = await CollectAllRowsAsync(src, path, batchSize: 1000);

            rows.Should().HaveCount(2);
            rows[0]["Name"].Should().Be("Alice");
            rows[0]["Age"].Should().Be("30");
            rows[0]["City"].Should().Be("Taipei");
            rows[1]["Name"].Should().Be("Bob");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task Column_names_come_from_header_row()
    {
        var csv = "FirstName,LastName\r\nJohn,Doe\r\n";
        var path = WriteTempCsv(csv);
        try
        {
            using var src = new CsvEtlSource();
            DataTable? schema = null;
            await foreach (var batch in src.ExtractBatchesAsync(path, "", null, 1000))
                schema = batch;

            schema!.Columns["FirstName"].Should().NotBeNull();
            schema.Columns["LastName"].Should().NotBeNull();
        }
        finally { File.Delete(path); }
    }

    // -----------------------------------------------------------------
    // Quoted fields — comma inside field
    // -----------------------------------------------------------------

    [TestMethod]
    public async Task Quoted_field_with_embedded_comma_is_parsed_as_single_field()
    {
        // Field "Smith, John" has a comma inside quotes
        var csv = "Name,Score\r\n\"Smith, John\",95\r\n";
        var path = WriteTempCsv(csv);
        try
        {
            using var src = new CsvEtlSource();
            var rows = await CollectAllRowsAsync(src, path, batchSize: 1000);

            rows.Should().HaveCount(1);
            rows[0]["Name"].Should().Be("Smith, John");
            rows[0]["Score"].Should().Be("95");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task Quoted_field_with_embedded_double_quote_is_unescaped()
    {
        // RFC 4180: "" inside a quoted field = single literal "
        var csv = "Quote\r\n\"He said \"\"hello\"\"\"\r\n";
        var path = WriteTempCsv(csv);
        try
        {
            using var src = new CsvEtlSource();
            var rows = await CollectAllRowsAsync(src, path, batchSize: 1000);

            rows.Should().HaveCount(1);
            rows[0]["Quote"].Should().Be("He said \"hello\"");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task Quoted_field_with_embedded_newline_is_parsed_as_single_field()
    {
        // Field contains a literal newline character
        var csv = "Id,Note\r\n1,\"line1\nline2\"\r\n2,normal\r\n";
        var path = WriteTempCsv(csv);
        try
        {
            using var src = new CsvEtlSource();
            var rows = await CollectAllRowsAsync(src, path, batchSize: 1000);

            rows.Should().HaveCount(2);
            rows[0]["Id"].Should().Be("1");
            rows[0]["Note"].Should().Be("line1\nline2");
            rows[1]["Id"].Should().Be("2");
        }
        finally { File.Delete(path); }
    }

    // -----------------------------------------------------------------
    // Custom delimiter
    // -----------------------------------------------------------------

    [TestMethod]
    public async Task Tab_delimited_csv_parses_correctly()
    {
        var tsv = "Name\tValue\r\nAlpha\t100\r\n";
        var path = WriteTempCsv(tsv);
        try
        {
            using var src = new CsvEtlSource { Delimiter = '\t' };
            var rows = await CollectAllRowsAsync(src, path, batchSize: 1000);

            rows.Should().HaveCount(1);
            rows[0]["Name"].Should().Be("Alpha");
            rows[0]["Value"].Should().Be("100");
        }
        finally { File.Delete(path); }
    }

    // -----------------------------------------------------------------
    // No header
    // -----------------------------------------------------------------

    [TestMethod]
    public async Task No_header_row_generates_ColN_column_names()
    {
        var csv = "10,20,30\r\n40,50,60\r\n";
        var path = WriteTempCsv(csv);
        try
        {
            using var src = new CsvEtlSource { HasHeader = false };
            DataTable? schema = null;
            var allRows = new List<DataRow>();
            await foreach (var batch in src.ExtractBatchesAsync(path, "", null, 1000))
            {
                schema = batch;
                foreach (DataRow r in batch.Rows) allRows.Add(r);
            }

            schema!.Columns["Col0"].Should().NotBeNull();
            schema.Columns["Col1"].Should().NotBeNull();
            schema.Columns["Col2"].Should().NotBeNull();
            allRows.Should().HaveCount(2);
            allRows[0]["Col0"].Should().Be("10");
        }
        finally { File.Delete(path); }
    }

    // -----------------------------------------------------------------
    // Batching
    // -----------------------------------------------------------------

    [TestMethod]
    public async Task Batching_splits_rows_at_batchSize_boundary()
    {
        var sb = new StringBuilder("Id\r\n");
        for (int i = 1; i <= 5; i++) sb.AppendLine(i.ToString());
        var path = WriteTempCsv(sb.ToString());
        try
        {
            using var src = new CsvEtlSource();
            var batches = new List<DataTable>();
            await foreach (var batch in src.ExtractBatchesAsync(path, "", null, batchSize: 2))
                batches.Add(batch);

            batches.Should().HaveCount(3, "5 rows / batchSize 2 → 3 batches");
            batches[0].Rows.Count.Should().Be(2);
            batches[1].Rows.Count.Should().Be(2);
            batches[2].Rows.Count.Should().Be(1);
        }
        finally { File.Delete(path); }
    }

    // -----------------------------------------------------------------
    // Empty file
    // -----------------------------------------------------------------

    [TestMethod]
    public async Task Empty_file_with_header_only_yields_no_rows()
    {
        var path = WriteTempCsv("Name,Age\r\n");
        try
        {
            using var src = new CsvEtlSource();
            var rows = await CollectAllRowsAsync(src, path, batchSize: 1000);

            rows.Should().BeEmpty();
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task Completely_empty_file_yields_nothing()
    {
        var path = WriteTempCsv("");
        try
        {
            using var src = new CsvEtlSource();
            var rows = await CollectAllRowsAsync(src, path, batchSize: 1000);

            rows.Should().BeEmpty();
        }
        finally { File.Delete(path); }
    }

    // -----------------------------------------------------------------
    // Internal parser unit tests
    // -----------------------------------------------------------------

    [TestMethod]
    public void ParseCsvLine_plain_fields()
    {
        var fields = CsvEtlSource.ParseCsvLine("a,b,c", ',');
        fields.Should().BeEquivalentTo(["a", "b", "c"], o => o.WithStrictOrdering());
    }

    [TestMethod]
    public void ParseCsvLine_quoted_field_with_comma()
    {
        var fields = CsvEtlSource.ParseCsvLine("\"hello, world\",end", ',');
        fields.Should().BeEquivalentTo(["hello, world", "end"], o => o.WithStrictOrdering());
    }

    [TestMethod]
    public void ParseCsvLine_double_quote_escape()
    {
        var fields = CsvEtlSource.ParseCsvLine("\"say \"\"hi\"\"\",ok", ',');
        fields.Should().BeEquivalentTo(["say \"hi\"", "ok"], o => o.WithStrictOrdering());
    }

    [TestMethod]
    public void ParseCsvLine_trailing_delimiter_adds_empty_field()
    {
        var fields = CsvEtlSource.ParseCsvLine("a,b,", ',');
        fields.Should().HaveCount(3);
        fields[2].Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static async Task<List<DataRow>> CollectAllRowsAsync(
        CsvEtlSource src, string path, int batchSize)
    {
        var rows = new List<DataRow>();
        await foreach (var batch in src.ExtractBatchesAsync(path, "", null, batchSize))
            foreach (DataRow r in batch.Rows)
                rows.Add(r);
        return rows;
    }
}
