#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Schema;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// IEtlSchemaService surface — DTO shape, factory dispatch per
/// <see cref="DBTypeEnum"/>, and contract semantics verified
/// against an in-memory implementation.
/// </summary>
[TestClass]
public class EtlSchemaServiceTests
{
    // ── DTO ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void EtlTableInfo_FullName_combines_schema_and_name()
    {
        Assert.AreEqual("dbo.Orders", new EtlTableInfo("dbo", "Orders").FullName);
        Assert.AreEqual("Orders", new EtlTableInfo("", "Orders").FullName,
            "Empty schema → bare name (Oracle USER_TABLES path).");
    }

    [TestMethod]
    public void EtlColumnInfo_carries_nullability_and_pk()
    {
        var c = new EtlColumnInfo("OrderID", "INT", IsNullable: false, IsPrimaryKey: true);
        Assert.AreEqual("OrderID", c.Name);
        Assert.IsFalse(c.IsNullable);
        Assert.IsTrue(c.IsPrimaryKey);
    }

    // ── Factory ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Factory_dispatches_per_DBTypeEnum()
    {
        Assert.IsInstanceOfType(EtlSchemaServiceFactory.Create(DBTypeEnum.SqlServer),
            typeof(MssqlEtlSchemaService));
        Assert.IsInstanceOfType(EtlSchemaServiceFactory.Create(DBTypeEnum.Oracle),
            typeof(OracleEtlSchemaService));
    }

    [TestMethod]
    public void Factory_throws_for_unsupported_db_types()
    {
        // SQLite, MySQL, PostgreSQL etc. — would need their own
        // information_schema / sqlite_master implementations later.
        var ex = Assert.ThrowsException<NotSupportedException>(() =>
            EtlSchemaServiceFactory.Create(DBTypeEnum.SQLite));
        StringAssert.Contains(ex.Message, "SqlServer");
        StringAssert.Contains(ex.Message, "Oracle");
    }

    // ── In-memory contract: simulate a real impl's return shape ─────────

    [TestMethod]
    public async Task InMemory_implementation_round_trips_table_list()
    {
        IEtlSchemaService svc = new InMemoryEtlSchemaService(
            tables: new[]
            {
                new EtlTableInfo("dbo", "Orders"),
                new EtlTableInfo("dbo", "Customers"),
                new EtlTableInfo("hr",  "Employees"),
            });

        var allTables = await svc.ListTablesAsync("ignored", schemaFilter: null);
        Assert.AreEqual(3, allTables.Count);

        var dboOnly = await svc.ListTablesAsync("ignored", schemaFilter: "dbo");
        Assert.AreEqual(2, dboOnly.Count);
        Assert.IsTrue(dboOnly.All(t => t.Schema == "dbo"));
    }

    [TestMethod]
    public async Task InMemory_implementation_returns_columns_per_table()
    {
        IEtlSchemaService svc = new InMemoryEtlSchemaService(
            tables: new[] { new EtlTableInfo("dbo", "Orders") },
            columns: new Dictionary<string, IReadOnlyList<EtlColumnInfo>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Orders"] = new List<EtlColumnInfo>
                {
                    new("OrderID", "INT", false, true),
                    new("CustomerID", "INT", false, false),
                    new("OrderDate", "DATETIME", false, false),
                    new("Notes", "NVARCHAR(500)", true, false),
                },
            });

        var cols = await svc.ListColumnsAsync("ignored", "Orders");
        Assert.AreEqual(4, cols.Count);
        var pk = cols.Single(c => c.IsPrimaryKey);
        Assert.AreEqual("OrderID", pk.Name);

        var nullable = cols.Single(c => c.IsNullable);
        Assert.AreEqual("Notes", nullable.Name);
    }

    [TestMethod]
    public async Task ListColumns_with_blank_table_name_throws()
    {
        IEtlSchemaService svc = new InMemoryEtlSchemaService();
        await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await svc.ListColumnsAsync("ignored", " "));
    }

    // ── Test double ─────────────────────────────────────────────────────

    private sealed class InMemoryEtlSchemaService : IEtlSchemaService
    {
        private readonly IReadOnlyList<EtlTableInfo> _tables;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<EtlColumnInfo>> _columns;

        public InMemoryEtlSchemaService(
            IReadOnlyList<EtlTableInfo>? tables = null,
            IReadOnlyDictionary<string, IReadOnlyList<EtlColumnInfo>>? columns = null)
        {
            _tables = tables ?? Array.Empty<EtlTableInfo>();
            _columns = columns ?? new Dictionary<string, IReadOnlyList<EtlColumnInfo>>();
        }

        public Task<IReadOnlyList<EtlTableInfo>> ListTablesAsync(
            string connectionString, string? schemaFilter = null,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<EtlTableInfo> result = string.IsNullOrWhiteSpace(schemaFilter)
                ? _tables
                : _tables.Where(t => string.Equals(t.Schema, schemaFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<EtlColumnInfo>> ListColumnsAsync(
            string connectionString, string tableName, string? schemaName = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
            return Task.FromResult(
                _columns.TryGetValue(tableName, out var cols) ? cols : (IReadOnlyList<EtlColumnInfo>)Array.Empty<EtlColumnInfo>());
        }
    }
}
