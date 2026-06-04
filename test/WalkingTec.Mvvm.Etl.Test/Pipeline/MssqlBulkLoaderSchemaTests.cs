#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// Unit tests for MssqlBulkLoader.ParseSchemaAndTable (M23 fix):
/// EnsureStagingTableAsync must filter INFORMATION_SCHEMA.TABLES on both
/// TABLE_NAME and TABLE_SCHEMA so that a same-named table in another schema
/// does not cause the CREATE to be silently skipped.
/// </summary>
[TestClass]
public class MssqlBulkLoaderSchemaTests
{
    // ─── ParseSchemaAndTable: un-qualified names default to dbo ───────────

    [TestMethod]
    public void ParseSchemaAndTable_no_dot_defaults_schema_to_dbo()
    {
        var (schema, table) = MssqlBulkLoader.ParseSchemaAndTable("STG_Orders");

        Assert.AreEqual("dbo", schema);
        Assert.AreEqual("STG_Orders", table);
    }

    [TestMethod]
    public void ParseSchemaAndTable_plain_schema_prefix_splits_correctly()
    {
        var (schema, table) = MssqlBulkLoader.ParseSchemaAndTable("audit.STG_Orders");

        Assert.AreEqual("audit", schema);
        Assert.AreEqual("STG_Orders", table);
    }

    [TestMethod]
    public void ParseSchemaAndTable_bracketed_prefix_strips_brackets()
    {
        var (schema, table) = MssqlBulkLoader.ParseSchemaAndTable("[audit].[STG_Orders]");

        Assert.AreEqual("audit", schema);
        Assert.AreEqual("STG_Orders", table);
    }

    [TestMethod]
    public void ParseSchemaAndTable_bracketed_schema_only_strips_brackets()
    {
        // "[audit].STG_Orders" — only schema is bracketed
        var (schema, table) = MssqlBulkLoader.ParseSchemaAndTable("[audit].STG_Orders");

        Assert.AreEqual("audit", schema);
        Assert.AreEqual("STG_Orders", table);
    }

    // ─── Boundary: dbo schema is still dbo after round-trip ──────────────

    [TestMethod]
    public void ParseSchemaAndTable_explicit_dbo_prefix_returns_dbo()
    {
        var (schema, table) = MssqlBulkLoader.ParseSchemaAndTable("dbo.STG_Orders");

        Assert.AreEqual("dbo", schema);
        Assert.AreEqual("STG_Orders", table);
    }

    [TestMethod]
    public void ParseSchemaAndTable_bracketed_dbo_prefix_returns_dbo()
    {
        var (schema, table) = MssqlBulkLoader.ParseSchemaAndTable("[dbo].[STG_Orders]");

        Assert.AreEqual("dbo", schema);
        Assert.AreEqual("STG_Orders", table);
    }

    // ─── Boundary: empty-like inputs ─────────────────────────────────────

    [TestMethod]
    public void ParseSchemaAndTable_single_word_no_schema_gets_dbo()
    {
        // A bare table name with no dot must produce "dbo" as schema
        var (schema, table) = MssqlBulkLoader.ParseSchemaAndTable("MyTable");

        Assert.AreEqual("dbo", schema);
        Assert.AreEqual("MyTable", table);
    }

    [TestMethod]
    public void ParseSchemaAndTable_custom_schema_different_from_dbo()
    {
        // Ensures a non-default schema ("etl") is preserved as-is so the
        // INFORMATION_SCHEMA query uses the correct schema filter and does not
        // mistake a same-named table in 'dbo' for the etl schema staging table.
        var (schema, table) = MssqlBulkLoader.ParseSchemaAndTable("etl.STG_Sales");

        Assert.AreEqual("etl", schema);
        Assert.AreEqual("STG_Sales", table);
    }
}
