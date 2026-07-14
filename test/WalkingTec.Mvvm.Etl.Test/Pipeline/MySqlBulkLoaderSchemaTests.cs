#nullable enable
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// Unit tests for <see cref="MySqlBulkLoader.IsUniqueColumnQuery"/>:
/// regression guard for Issue #664 — the schema-scope fix originally applied
/// to MssqlBulkLoader.IsUniqueColumnAsync (#391) was never ported to the
/// MySQL loader, so a same-named table in another database on the same
/// server could satisfy the uniqueness check.
/// </summary>
[TestClass]
public class MySqlBulkLoaderSchemaTests
{
    // ─── IsUniqueColumnQuery: schema filter regression guard (#664) ───────

    [TestMethod]
    [Description(
        "Regression guard for Issue #664: IsUniqueColumnAsync must filter on both " +
        "TABLE_SCHEMA and TABLE_NAME so that a same-named table in another database " +
        "does not match a constraint intended for a different database. " +
        "This test verifies the SQL constant exposed by MySqlBulkLoader.")]
    public void IsUniqueColumnQuery_ContainsTableSchemaFilter()
    {
        // The query is exposed as an internal static readonly string so we can
        // assert its content without a live MySQL connection.
        Assert.IsTrue(
            MySqlBulkLoader.IsUniqueColumnQuery.Contains("table_schema", StringComparison.OrdinalIgnoreCase),
            "IsUniqueColumnAsync SQL must filter on table_schema to scope the " +
            "constraint lookup to the correct database (Issue #664). " +
            "Do not remove the table_schema predicate from IsUniqueColumnQuery.");
    }

    [TestMethod]
    [Description(
        "Regression guard for Issue #664: IsUniqueColumnAsync must resolve the " +
        "database (explicit 'db.table' prefix, else the connection's current " +
        "database) and bind @dbName separately from @tableName so that both " +
        "parameters are present in the query.")]
    public void IsUniqueColumnQuery_ContainsBothDbNameAndTableNameParameters()
    {
        Assert.IsTrue(
            MySqlBulkLoader.IsUniqueColumnQuery.Contains("@dbName", StringComparison.Ordinal),
            "IsUniqueColumnAsync SQL must use @dbName parameter (Issue #664).");
        Assert.IsTrue(
            MySqlBulkLoader.IsUniqueColumnQuery.Contains("@tableName", StringComparison.Ordinal),
            "IsUniqueColumnAsync SQL must use @tableName parameter (Issue #664).");
        Assert.IsTrue(
            MySqlBulkLoader.IsUniqueColumnQuery.Contains("@colName", StringComparison.Ordinal),
            "IsUniqueColumnAsync SQL must use @colName parameter.");
    }

    [TestMethod]
    [Description(
        "Regression guard for Issue #664: the JOIN condition between " +
        "key_column_usage and table_constraints must also include the schema " +
        "(constraint_schema) to prevent cross-database constraint name collisions.")]
    public void IsUniqueColumnQuery_JoinIncludesConstraintSchema()
    {
        var query = MySqlBulkLoader.IsUniqueColumnQuery;
        Assert.IsTrue(
            query.Contains("k.constraint_schema = t.constraint_schema", StringComparison.OrdinalIgnoreCase),
            "IsUniqueColumnQuery JOIN must match on constraint_schema in addition to " +
            "constraint_name to prevent cross-database CONSTRAINT_NAME collisions (#664).");
    }

    [TestMethod]
    [Description(
        "Regression guard for Issue #664: the constraint-type predicate must still " +
        "be present after the schema filter was added, so the query keeps checking " +
        "for PRIMARY KEY / UNIQUE constraints only.")]
    public void IsUniqueColumnQuery_FiltersOnConstraintType()
    {
        Assert.IsTrue(
            MySqlBulkLoader.IsUniqueColumnQuery.Contains(
                "constraint_type IN ('PRIMARY KEY', 'UNIQUE')", StringComparison.OrdinalIgnoreCase),
            "IsUniqueColumnQuery must restrict to PRIMARY KEY / UNIQUE constraints.");
    }
}
