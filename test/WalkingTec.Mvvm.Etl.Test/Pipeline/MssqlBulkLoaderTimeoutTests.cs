#nullable enable
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// Tests for L17/#153: <see cref="MssqlBulkLoader"/> must expose a
/// configurable timeout (defaulting to 300 s) and apply it to
/// <see cref="Microsoft.Data.SqlClient.SqlBulkCopy.BulkCopyTimeout"/> and all
/// SQL command <c>CommandTimeout</c> sites. Previously every site was hardcoded
/// to 0 (infinite), meaning a blocked network or SQL connection would hang the
/// ETL job permanently.
///
/// These tests do NOT require a live SQL Server connection; they verify the
/// constructor contract and the <see cref="MssqlBulkLoader.TimeoutSeconds"/>
/// property so the configured value is what reaches the SqlBulkCopy and
/// SqlCommand sites.
/// </summary>
[TestClass]
public class MssqlBulkLoaderTimeoutTests
{
    // ─── Default timeout ───────────────────────────────────────────────────

    [TestMethod]
    public void Default_constructor_sets_TimeoutSeconds_to_300()
    {
        var loader = new MssqlBulkLoader();

        loader.TimeoutSeconds.Should().Be(300,
            "300 s is the backward-compatible-safe default: jobs finishing under 5 minutes " +
            "are unaffected while pathological hangs now abort");
    }

    // ─── Custom timeout is honoured ────────────────────────────────────────

    [TestMethod]
    public void Custom_timeout_is_stored_on_TimeoutSeconds()
    {
        var loader = new MssqlBulkLoader(timeoutSeconds: 60);

        loader.TimeoutSeconds.Should().Be(60);
    }

    [TestMethod]
    public void Zero_timeout_disables_limit()
    {
        // 0 = infinite; the caller explicitly opts back into the old behaviour.
        var loader = new MssqlBulkLoader(timeoutSeconds: 0);

        loader.TimeoutSeconds.Should().Be(0);
    }

    [TestMethod]
    public void Large_timeout_value_is_preserved()
    {
        var loader = new MssqlBulkLoader(timeoutSeconds: 3600);

        loader.TimeoutSeconds.Should().Be(3600);
    }

    // ─── Parameterless construction still compiles (default parameter) ─────

    [TestMethod]
    public void Parameterless_construction_compiles_and_uses_default()
    {
        // Verifies that existing callers that use `new MssqlBulkLoader()` continue to
        // compile unchanged and still receive the 300-second default.
        MssqlBulkLoader loader = new();

        loader.TimeoutSeconds.Should().Be(300);
    }
}
