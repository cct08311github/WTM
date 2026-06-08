#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Pipeline.Sources;
using WalkingTec.Mvvm.Etl.Testing;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

[TestClass]
public class EtlSourceRegistryTests
{
    // -------------------------------------------------------------------
    // Basic registration and resolution
    // -------------------------------------------------------------------

    [TestMethod]
    public void Register_and_Create_returns_configured_instance()
    {
        var registry = new EtlSourceRegistry();
        registry.Register("test", () => new MockEtlSource());

        var src = registry.Create("test");

        src.Should().NotBeNull().And.BeOfType<MockEtlSource>();
        src.Dispose();
    }

    [TestMethod]
    public void Create_is_case_insensitive()
    {
        var registry = new EtlSourceRegistry();
        registry.Register("MySource", () => new MockEtlSource());

        using var src1 = registry.Create("mysource");
        using var src2 = registry.Create("MYSOURCE");
        using var src3 = registry.Create("MySource");

        src1.Should().NotBeNull();
        src2.Should().NotBeNull();
        src3.Should().NotBeNull();
    }

    [TestMethod]
    public void TryCreate_returns_false_for_unknown_key()
    {
        var registry = new EtlSourceRegistry();

        var result = registry.TryCreate("nonexistent", out var src);

        result.Should().BeFalse();
        src.Should().BeNull();
    }

    [TestMethod]
    public void TryCreate_returns_true_for_registered_key()
    {
        var registry = new EtlSourceRegistry();
        registry.Register("csv", () => new CsvEtlSource());

        var result = registry.TryCreate("csv", out var src);

        result.Should().BeTrue();
        src.Should().NotBeNull().And.BeOfType<CsvEtlSource>();
        src!.Dispose();
    }

    [TestMethod]
    public void Create_throws_NotSupportedException_for_unknown_key()
    {
        var registry = new EtlSourceRegistry();

        var act = () => registry.Create("does-not-exist");

        act.Should().Throw<NotSupportedException>()
           .WithMessage("*does-not-exist*");
    }

    [TestMethod]
    public void Later_registration_overrides_earlier_for_same_key()
    {
        var registry = new EtlSourceRegistry();
        registry.Register("dual", () => new MockEtlSource());
        registry.Register("dual", () => new CsvEtlSource()); // overrides

        using var src = registry.Create("dual");

        src.Should().BeOfType<CsvEtlSource>("later registration should win");
    }

    [TestMethod]
    public void RegisteredKinds_reflects_all_registrations()
    {
        var registry = new EtlSourceRegistry();
        registry.Register("alpha", () => new MockEtlSource());
        registry.Register("beta",  () => new MockEtlSource());

        registry.RegisteredKinds.Should().Contain("alpha").And.Contain("beta");
    }

    [TestMethod]
    public void Register_throws_on_null_or_whitespace_key()
    {
        var registry = new EtlSourceRegistry();

        var act1 = () => registry.Register(null!, () => new MockEtlSource());
        var act2 = () => registry.Register("  ",  () => new MockEtlSource());

        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void Register_throws_on_null_factory()
    {
        var registry = new EtlSourceRegistry();

        var act = () => registry.Register("ok", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // -------------------------------------------------------------------
    // Default registry contains built-in source kinds
    // -------------------------------------------------------------------

    [TestMethod]
    public void DefaultRegistry_contains_builtin_oracle_sqlserver_csv_excel()
    {
        var reg = EtlSourceFactory.DefaultRegistry;

        reg.RegisteredKinds.Should().Contain("oracle");
        reg.RegisteredKinds.Should().Contain("sqlserver");
        reg.RegisteredKinds.Should().Contain("csv");
        reg.RegisteredKinds.Should().Contain("excel");
    }

    [TestMethod]
    public void DefaultRegistry_creates_oracle_source()
    {
        using var src = EtlSourceFactory.DefaultRegistry.Create("oracle");
        src.Should().BeOfType<OracleSource>();
    }

    [TestMethod]
    public void DefaultRegistry_creates_sqlserver_source()
    {
        using var src = EtlSourceFactory.DefaultRegistry.Create("sqlserver");
        src.Should().BeOfType<MssqlSource>();
    }

    [TestMethod]
    public void DefaultRegistry_creates_csv_source()
    {
        using var src = EtlSourceFactory.DefaultRegistry.Create("csv");
        src.Should().BeOfType<CsvEtlSource>();
    }

    [TestMethod]
    public void DefaultRegistry_creates_excel_source()
    {
        using var src = EtlSourceFactory.DefaultRegistry.Create("excel");
        src.Should().BeOfType<ExcelEtlSource>();
    }

    // -------------------------------------------------------------------
    // Custom source registration + end-to-end resolve
    // -------------------------------------------------------------------

    [TestMethod]
    public async Task Custom_source_registered_and_resolved_via_registry()
    {
        var registry = new EtlSourceRegistry();

        // Register a custom source backed by MockEtlSource
        var dt = new DataTable();
        dt.Columns.Add("Id", typeof(int));
        dt.Rows.Add(42);

        registry.Register("custom-mock", () =>
        {
            var mock = new MockEtlSource();
            mock.SetData(dt);
            return mock;
        });

        using var src = registry.Create("custom-mock");
        var rows = new List<DataRow>();

        await foreach (var batch in src.ExtractBatchesAsync("", "", null, 1000))
            foreach (DataRow r in batch.Rows)
                rows.Add(r);

        rows.Should().HaveCount(1);
        rows[0]["Id"].Should().Be(42);
    }

    // -------------------------------------------------------------------
    // Back-compat: EtlSourceFactory.CreateSource(DBTypeEnum) still works
    // -------------------------------------------------------------------

    [TestMethod]
    public void Factory_CreateSource_DBTypeEnum_SqlServer_still_resolves()
    {
        using var src = EtlSourceFactory.CreateSource(WalkingTec.Mvvm.Core.DBTypeEnum.SqlServer);
        src.Should().BeOfType<MssqlSource>();
    }

    [TestMethod]
    public void Factory_CreateSource_DBTypeEnum_Oracle_still_resolves()
    {
        using var src = EtlSourceFactory.CreateSource(WalkingTec.Mvvm.Core.DBTypeEnum.Oracle);
        src.Should().BeOfType<OracleSource>();
    }

    [TestMethod]
    public void Factory_CreateSource_string_key_csv_resolves()
    {
        using var src = EtlSourceFactory.CreateSource("csv");
        src.Should().BeOfType<CsvEtlSource>();
    }

    [TestMethod]
    public void Factory_CreateSource_string_key_excel_resolves()
    {
        using var src = EtlSourceFactory.CreateSource("excel");
        src.Should().BeOfType<ExcelEtlSource>();
    }
}
