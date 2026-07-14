#nullable enable
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// Unit tests for <see cref="PostgreSqlBulkLoader.IsUniqueColumnQuery"/>:
/// regression guard for Issue #664 — the schema-scope fix originally applied
/// to MssqlBulkLoader.IsUniqueColumnAsync (#391) was never ported to the
/// PostgreSQL loader, which queried information_schema without a
/// table_schema/constraint_schema predicate, so a same-named table in
/// another schema could satisfy the uniqueness check.
/// </summary>
[TestClass]
public class PostgreSqlBulkLoaderSchemaTests
{
    // ─── IsUniqueColumnQuery: schema filter regression guard (#664) ───────

    [TestMethod]
    [Description(
        "Regression guard for Issue #664: IsUniqueColumnAsync must filter on both " +
        "table_schema and table_name so that a same-named table in another schema " +
        "does not match a constraint intended for a different schema. " +
        "This test verifies the SQL constant exposed by PostgreSqlBulkLoader.")]
    public void IsUniqueColumnQuery_ContainsTableSchemaFilter()
    {
        // The query is exposed as an internal static readonly string so we can
        // assert its content without a live PostgreSQL connection.
        Assert.IsTrue(
            PostgreSqlBulkLoader.IsUniqueColumnQuery.Contains("table_schema", StringComparison.OrdinalIgnoreCase),
            "IsUniqueColumnAsync SQL must filter on table_schema to scope the " +
            "constraint lookup to the correct schema (Issue #664). " +
            "Do not remove the table_schema predicate from IsUniqueColumnQuery.");
    }

    [TestMethod]
    [Description(
        "Regression guard for Issue #664: IsUniqueColumnAsync must parse a " +
        "schema-qualified table name (e.g. 'audit.stg_orders', defaulting to " +
        "'public') and bind @schemaName separately from @tableName so that both " +
        "parameters are present in the query.")]
    public void IsUniqueColumnQuery_ContainsBothSchemaAndTableNameParameters()
    {
        Assert.IsTrue(
            PostgreSqlBulkLoader.IsUniqueColumnQuery.Contains("@schemaName", StringComparison.Ordinal),
            "IsUniqueColumnAsync SQL must use @schemaName parameter (Issue #664).");
        Assert.IsTrue(
            PostgreSqlBulkLoader.IsUniqueColumnQuery.Contains("@tableName", StringComparison.Ordinal),
            "IsUniqueColumnAsync SQL must use @tableName parameter (Issue #664).");
        Assert.IsTrue(
            PostgreSqlBulkLoader.IsUniqueColumnQuery.Contains("@colName", StringComparison.Ordinal),
            "IsUniqueColumnAsync SQL must use @colName parameter.");
    }

    [TestMethod]
    [Description(
        "Regression guard for Issue #664: the JOIN condition between " +
        "key_column_usage and table_constraints must also include " +
        "constraint_schema to prevent cross-schema constraint name collisions.")]
    public void IsUniqueColumnQuery_JoinIncludesConstraintSchema()
    {
        var query = PostgreSqlBulkLoader.IsUniqueColumnQuery;
        Assert.IsTrue(
            query.Contains("k.constraint_schema = t.constraint_schema", StringComparison.OrdinalIgnoreCase),
            "IsUniqueColumnQuery JOIN must match on constraint_schema in addition to " +
            "constraint_name to prevent cross-schema CONSTRAINT_NAME collisions (#664).");
    }

    [TestMethod]
    [Description(
        "Regression guard for Issue #664: ParseSchemaAndTable must still default " +
        "un-qualified names to the 'public' schema so IsUniqueColumnAsync scopes " +
        "correctly even when the caller passes a bare table name.")]
    public void ParseSchemaAndTable_no_dot_defaults_schema_to_public()
    {
        var (schema, table) = PostgreSqlBulkLoader.ParseSchemaAndTable("stg_orders");

        Assert.AreEqual("public", schema);
        Assert.AreEqual("stg_orders", table);
    }

    [TestMethod]
    public void ParseSchemaAndTable_schema_prefix_splits_correctly()
    {
        var (schema, table) = PostgreSqlBulkLoader.ParseSchemaAndTable("audit.stg_orders");

        Assert.AreEqual("audit", schema);
        Assert.AreEqual("stg_orders", table);
    }
}
