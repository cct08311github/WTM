#nullable enable
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// Unit tests for <see cref="OracleBulkLoader.IsUniqueColumnQuery"/>:
/// regression guard for Issue #664 — the schema-scope fix originally applied
/// to MssqlBulkLoader.IsUniqueColumnAsync (#391) was never ported to the
/// Oracle loader, which queried ALL_CONS_COLUMNS/ALL_CONSTRAINTS without an
/// OWNER filter, so a same-named table owned by another schema (but visible
/// via grants) could satisfy the uniqueness check.
/// </summary>
[TestClass]
public class OracleBulkLoaderSchemaTests
{
    // ─── IsUniqueColumnQuery: schema filter regression guard (#664) ───────

    [TestMethod]
    [Description(
        "Regression guard for Issue #664: IsUniqueColumnAsync must be scoped to the " +
        "connected schema. Oracle's USER_ dictionary views (USER_CONS_COLUMNS / " +
        "USER_CONSTRAINTS) are implicitly restricted to objects owned by the " +
        "connected user, mirroring the same-schema idiom this loader already uses " +
        "for EnsureStagingTableAsync (USER_TABLES) and GetColumnsAsync " +
        "(USER_TAB_COLUMNS). This test verifies the SQL constant exposed by " +
        "OracleBulkLoader no longer queries the unscoped ALL_ dictionary views.")]
    public void IsUniqueColumnQuery_UsesUserScopedDictionaryViews()
    {
        // The query is exposed as an internal static readonly string so we can
        // assert its content without a live Oracle connection.
        var query = OracleBulkLoader.IsUniqueColumnQuery;

        Assert.IsTrue(
            query.Contains("USER_CONS_COLUMNS", StringComparison.OrdinalIgnoreCase),
            "IsUniqueColumnAsync SQL must query USER_CONS_COLUMNS (scoped to the " +
            "connected schema) instead of the unscoped ALL_CONS_COLUMNS (Issue #664).");
        Assert.IsTrue(
            query.Contains("USER_CONSTRAINTS", StringComparison.OrdinalIgnoreCase),
            "IsUniqueColumnAsync SQL must query USER_CONSTRAINTS (scoped to the " +
            "connected schema) instead of the unscoped ALL_CONSTRAINTS (Issue #664).");
    }

    [TestMethod]
    [Description(
        "Regression guard for Issue #664: the unscoped ALL_ dictionary views must " +
        "no longer appear in IsUniqueColumnQuery — leaving them in place (even " +
        "alongside the USER_ views) would re-open the cross-schema false-positive " +
        "this fix closes.")]
    public void IsUniqueColumnQuery_DoesNotUseUnscopedAllViews()
    {
        var query = OracleBulkLoader.IsUniqueColumnQuery;

        Assert.IsFalse(
            query.Contains("ALL_CONS_COLUMNS", StringComparison.OrdinalIgnoreCase),
            "IsUniqueColumnQuery must not query the unscoped ALL_CONS_COLUMNS view (#664).");
        Assert.IsFalse(
            query.Contains("ALL_CONSTRAINTS", StringComparison.OrdinalIgnoreCase),
            "IsUniqueColumnQuery must not query the unscoped ALL_CONSTRAINTS view (#664).");
    }

    [TestMethod]
    [Description(
        "Regression guard for Issue #664: the bind variables and constraint-type " +
        "predicate must be preserved after switching to the USER_ dictionary views.")]
    public void IsUniqueColumnQuery_ContainsBindVariablesAndConstraintTypeFilter()
    {
        var query = OracleBulkLoader.IsUniqueColumnQuery;

        Assert.IsTrue(
            query.Contains(":tableName", StringComparison.Ordinal),
            "IsUniqueColumnAsync SQL must use the :tableName bind variable.");
        Assert.IsTrue(
            query.Contains(":colName", StringComparison.Ordinal),
            "IsUniqueColumnAsync SQL must use the :colName bind variable.");
        Assert.IsTrue(
            query.Contains("constraint_type IN ('P', 'U')", StringComparison.OrdinalIgnoreCase),
            "IsUniqueColumnQuery must restrict to PRIMARY KEY ('P') / UNIQUE ('U') constraints.");
    }
}
