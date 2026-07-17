#nullable enable
using System;
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

    // ─── QuoteQualified: schema-qualified name produces per-part brackets ─

    [TestMethod]
    public void QuoteQualified_plain_name_uses_dbo_schema()
    {
        var result = MssqlBulkLoader.QuoteQualified("STG_Orders");
        Assert.AreEqual("[dbo].[STG_Orders]", result);
    }

    [TestMethod]
    public void QuoteQualified_schema_qualified_name_brackets_each_part()
    {
        var result = MssqlBulkLoader.QuoteQualified("audit.STG_Orders");
        Assert.AreEqual("[audit].[STG_Orders]", result);
    }

    [TestMethod]
    public void QuoteQualified_already_bracketed_name_strips_then_re_brackets_correctly()
    {
        var result = MssqlBulkLoader.QuoteQualified("[audit].[STG_Orders]");
        Assert.AreEqual("[audit].[STG_Orders]", result);
    }

    // ─── QuoteIdentifier: ']' escaping (#680) ─────────────────────────────
    // MSSQL's bracket-quoting escape sequence for a literal ']' inside an
    // identifier is to double it ("]]"). Without this, an identifier
    // containing ']' could prematurely close the bracket and let the
    // remainder execute as SQL rather than being treated as part of the name.

    [TestMethod]
    public void QuoteIdentifier_plain_name_is_simply_bracketed()
    {
        Assert.AreEqual("[OrderNo]", MssqlBulkLoader.QuoteIdentifier("OrderNo"));
    }

    [TestMethod]
    public void QuoteIdentifier_escapes_embedded_closing_bracket()
    {
        // "Weird]Name" must become "[Weird]]Name]" — the doubled ']]' is the
        // escape, not an early close.
        Assert.AreEqual("[Weird]]Name]", MssqlBulkLoader.QuoteIdentifier("Weird]Name"));
    }

    [TestMethod]
    public void QuoteIdentifier_escapes_injection_attempt_via_bracket_close()
    {
        // A hostile "name" trying to close the bracket and append SQL must be
        // fully neutralised into a single (very long, harmless) identifier.
        var hostile = "X]; DROP TABLE Users; --";
        var result = MssqlBulkLoader.QuoteIdentifier(hostile);

        Assert.AreEqual("[X]]; DROP TABLE Users; --]", result);
        // The only unescaped ']' characters are the opening/closing brackets
        // themselves — every embedded ']' must be doubled.
        Assert.AreEqual(1, CountUnescapedClosingBrackets(result));
    }

    [TestMethod]
    public void QuoteQualified_schema_and_table_each_escape_embedded_brackets()
    {
        var result = MssqlBulkLoader.QuoteQualified("aud]it.STG]Orders");
        Assert.AreEqual("[aud]]it].[STG]]Orders]", result);
    }

    /// <summary>Counts ']' characters that are NOT part of a doubled "]]" escape pair.</summary>
    private static int CountUnescapedClosingBrackets(string s)
    {
        int count = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != ']') continue;
            if (i + 1 < s.Length && s[i + 1] == ']')
            {
                i++; // skip the escaped pair
                continue;
            }
            count++;
        }
        return count;
    }

    // ─── IsUniqueColumnQuery: schema filter regression guard (#391) ───────

    [TestMethod]
    [Description(
        "Regression guard for Issue #391: IsUniqueColumnAsync must filter on both " +
        "TABLE_SCHEMA and TABLE_NAME so that a same-named table in another schema " +
        "does not match a constraint intended for a different schema. " +
        "This test verifies the SQL constant exposed by MssqlBulkLoader.")]
    public void IsUniqueColumnQuery_ContainsTableSchemaFilter()
    {
        // The query is exposed as an internal static readonly string so we can
        // assert its content without a live MSSQL connection.
        Assert.IsTrue(
            MssqlBulkLoader.IsUniqueColumnQuery.Contains("TABLE_SCHEMA", StringComparison.OrdinalIgnoreCase),
            "IsUniqueColumnAsync SQL must filter on TABLE_SCHEMA to scope the " +
            "constraint lookup to the correct schema (Issue #391). " +
            "Do not remove the TABLE_SCHEMA predicate from IsUniqueColumnQuery.");
    }

    [TestMethod]
    [Description(
        "Regression guard for Issue #391: IsUniqueColumnAsync must parse a " +
        "schema-qualified table name (e.g. 'audit.STG_Orders') and bind @schemaName " +
        "separately from @tableName so that both parameters are present in the query.")]
    public void IsUniqueColumnQuery_ContainsBothSchemaAndTableNameParameters()
    {
        Assert.IsTrue(
            MssqlBulkLoader.IsUniqueColumnQuery.Contains("@schemaName", StringComparison.Ordinal),
            "IsUniqueColumnAsync SQL must use @schemaName parameter (Issue #391).");
        Assert.IsTrue(
            MssqlBulkLoader.IsUniqueColumnQuery.Contains("@tableName", StringComparison.Ordinal),
            "IsUniqueColumnAsync SQL must use @tableName parameter (Issue #391).");
    }

    [TestMethod]
    [Description(
        "Regression guard for Issue #391: the JOIN condition between KEY_COLUMN_USAGE " +
        "and TABLE_CONSTRAINTS must also include TABLE_SCHEMA to prevent cross-schema " +
        "constraint name collisions.")]
    public void IsUniqueColumnQuery_JoinIncludesTableSchema()
    {
        // Count occurrences of TABLE_SCHEMA — must appear at least twice:
        // once in the JOIN ON clause and once in the WHERE clause.
        var query = MssqlBulkLoader.IsUniqueColumnQuery;
        int occurrences = 0;
        int idx = 0;
        while ((idx = query.IndexOf("TABLE_SCHEMA", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            occurrences++;
            idx++;
        }
        Assert.IsTrue(occurrences >= 2,
            $"TABLE_SCHEMA must appear at least twice in IsUniqueColumnQuery " +
            $"(JOIN condition + WHERE clause) but found {occurrences} occurrence(s). " +
            $"Issue #391 requires the JOIN ON also filters on TABLE_SCHEMA to prevent " +
            $"cross-schema CONSTRAINT_NAME collisions.");
    }
}
