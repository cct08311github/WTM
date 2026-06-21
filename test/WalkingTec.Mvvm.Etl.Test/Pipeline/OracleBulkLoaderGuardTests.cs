#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// Unit tests for the pure-logic guard paths in OracleBulkLoader.
///
/// COVERAGE NOTE — integration-only methods skipped (all require a live
/// Oracle connection):
///   • BulkLoadAsync     — opens OracleConnection, needs Oracle client
///   • MergeAsync        — same
///   • TruncateStagingAsync — same
///   • EnsureStagingTableAsync — same
///   • GetColumnsAsync   — same (private, called by Merge/Replace)
///   • IsUniqueColumnAsync — same
///
/// The ONLY pure-logic path in OracleBulkLoader that is unit-testable is the
/// SQL-injection guard at the top of ReplaceAsync, which calls
/// MssqlBulkLoader.IsSafeWhereClause() and throws ArgumentException BEFORE
/// opening any DB connection.  That path is exercised here.
///
/// The IsSafeWhereClause logic itself is tested more thoroughly via
/// ReplaceLoadModeTests + MssqlBulkLoader; these tests confirm the guard is
/// wired correctly in OracleBulkLoader and fires before any Oracle I/O.
/// </summary>
[TestClass]
public class OracleBulkLoaderGuardTests
{
    [TestMethod]
    public void OracleBulkLoader_instantiation_succeeds()
    {
        // Constructor is implicit (no args); confirms no static initialiser problems.
        var loader = new OracleBulkLoader();
        Assert.IsNotNull(loader);
    }

    // ── ReplaceAsync SQL-injection guard ──────────────────────────────────
    // These throw ArgumentException BEFORE attempting to open an Oracle connection.

    [TestMethod]
    public async Task ReplaceAsync_throws_ArgumentException_for_semicolon_in_whereClause()
    {
        var loader = new OracleBulkLoader();

        var ex = await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await loader.ReplaceAsync(
                "Data Source=fake;",
                "STG_ORDERS",
                "ORDERS",
                "1=1; DELETE FROM ORDERS",
                CancellationToken.None));

        StringAssert.Contains(ex.Message, "whereClause",
            "Exception message should reference the parameter name.");
    }

    [TestMethod]
    public async Task ReplaceAsync_throws_ArgumentException_for_comment_in_whereClause()
    {
        var loader = new OracleBulkLoader();

        await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await loader.ReplaceAsync(
                "Data Source=fake;",
                "STG_ORDERS",
                "ORDERS",
                "1=1 -- injected",
                CancellationToken.None));
    }

    [TestMethod]
    public async Task ReplaceAsync_throws_ArgumentException_for_block_comment_in_whereClause()
    {
        var loader = new OracleBulkLoader();

        await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await loader.ReplaceAsync(
                "Data Source=fake;",
                "STG_ORDERS",
                "ORDERS",
                "/* comment */ 1=1",
                CancellationToken.None));
    }

    [TestMethod]
    public async Task ReplaceAsync_throws_ArgumentException_for_xp_prefix_in_whereClause()
    {
        var loader = new OracleBulkLoader();

        await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await loader.ReplaceAsync(
                "Data Source=fake;",
                "STG_ORDERS",
                "ORDERS",
                "xp_cmdshell('whoami') = ''",
                CancellationToken.None));
    }

    [TestMethod]
    public async Task ReplaceAsync_throws_ArgumentException_for_sp_prefix_in_whereClause()
    {
        var loader = new OracleBulkLoader();

        await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await loader.ReplaceAsync(
                "Data Source=fake;",
                "STG_ORDERS",
                "ORDERS",
                "sp_executesql N'evil'",
                CancellationToken.None));
    }

    [TestMethod]
    public async Task ReplaceAsync_guard_is_case_insensitive_for_proc_prefixes()
    {
        var loader = new OracleBulkLoader();

        // XP_ in uppercase should also be rejected
        await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await loader.ReplaceAsync(
                "Data Source=fake;",
                "STG_ORDERS",
                "ORDERS",
                "XP_CMDSHELL('') = ''",
                CancellationToken.None));
    }

    // NOTE: We cannot test that null/empty whereClause proceeds past the guard
    // because after the guard, OracleBulkLoader attempts to open an Oracle
    // connection (GetColumnsAsync) which will fail without a real Oracle instance.
    // That behaviour is covered in Integration/OracleBulkLoaderIntegrationTests.cs.
}
