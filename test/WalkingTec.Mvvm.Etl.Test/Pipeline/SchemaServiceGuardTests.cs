#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Schema;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// Unit tests for the pure-logic guard path in MssqlEtlSchemaService and
/// OracleEtlSchemaService.
///
/// COVERAGE NOTE — integration-only paths skipped:
///   • ListTablesAsync (both) — immediately opens a DB connection; requires
///     a live MSSQL / Oracle instance. Covered in Integration/ tests.
///   • ListColumnsAsync body after the guard — same reason.
///     The guard (ArgumentException.ThrowIfNullOrWhiteSpace) fires before
///     any DB access and IS unit-testable, which is what this file covers.
/// </summary>
[TestClass]
public class SchemaServiceGuardTests
{
    // ── MssqlEtlSchemaService ──────────────────────────────────────────────

    [TestMethod]
    public void Mssql_instantiation_succeeds()
    {
        // Constructor has no side effects; confirms class is creatable.
        var svc = new MssqlEtlSchemaService();
        Assert.IsNotNull(svc);
    }

    [TestMethod]
    public async Task Mssql_ListColumnsAsync_throws_for_null_tableName()
    {
        var svc = new MssqlEtlSchemaService();

        // ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException
        // (a subclass of ArgumentException) when the value is null.
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
            await svc.ListColumnsAsync(
                "Server=fake;Database=X;",
                null!,
                cancellationToken: CancellationToken.None));
    }

    [TestMethod]
    public async Task Mssql_ListColumnsAsync_throws_for_empty_tableName()
    {
        var svc = new MssqlEtlSchemaService();

        await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await svc.ListColumnsAsync(
                "Server=fake;Database=X;",
                "",
                cancellationToken: CancellationToken.None));
    }

    [TestMethod]
    public async Task Mssql_ListColumnsAsync_throws_for_whitespace_tableName()
    {
        var svc = new MssqlEtlSchemaService();

        await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await svc.ListColumnsAsync(
                "Server=fake;Database=X;",
                "   ",
                cancellationToken: CancellationToken.None));
    }

    // ── OracleEtlSchemaService ─────────────────────────────────────────────

    [TestMethod]
    public void Oracle_instantiation_succeeds()
    {
        var svc = new OracleEtlSchemaService();
        Assert.IsNotNull(svc);
    }

    [TestMethod]
    public async Task Oracle_ListColumnsAsync_throws_for_null_tableName()
    {
        var svc = new OracleEtlSchemaService();

        // ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException
        // (a subclass of ArgumentException) when the value is null.
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(async () =>
            await svc.ListColumnsAsync(
                "Data Source=fake;User Id=u;Password=p;",
                null!,
                cancellationToken: CancellationToken.None));
    }

    [TestMethod]
    public async Task Oracle_ListColumnsAsync_throws_for_empty_tableName()
    {
        var svc = new OracleEtlSchemaService();

        await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await svc.ListColumnsAsync(
                "Data Source=fake;User Id=u;Password=p;",
                "",
                cancellationToken: CancellationToken.None));
    }

    [TestMethod]
    public async Task Oracle_ListColumnsAsync_throws_for_whitespace_tableName()
    {
        var svc = new OracleEtlSchemaService();

        await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await svc.ListColumnsAsync(
                "Data Source=fake;User Id=u;Password=p;",
                "   ",
                cancellationToken: CancellationToken.None));
    }

    // ── IEtlSchemaService contract (DTO + factory) — already in
    //    EtlSchemaServiceTests, verified here for traceability only ─────────

    [TestMethod]
    public void Factory_creates_MssqlEtlSchemaService_for_SqlServer()
    {
        var svc = EtlSchemaServiceFactory.Create(WalkingTec.Mvvm.Core.DBTypeEnum.SqlServer);
        Assert.IsInstanceOfType(svc, typeof(MssqlEtlSchemaService));
    }

    [TestMethod]
    public void Factory_creates_OracleEtlSchemaService_for_Oracle()
    {
        var svc = EtlSchemaServiceFactory.Create(WalkingTec.Mvvm.Core.DBTypeEnum.Oracle);
        Assert.IsInstanceOfType(svc, typeof(OracleEtlSchemaService));
    }
}
