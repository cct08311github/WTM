#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Governance;
using WalkingTec.Mvvm.Etl.Models;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Testing;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Etl.Test.Governance;

/// <summary>
/// ETL-004 Dead-letter, ETL-005 Lineage, ETL-006 Tenant isolation tests.
/// Uses SQLite shared in-memory (not EF InMemory) so that the full EF pipeline
/// including SaveChangesAsync, query filters, and indexes works correctly.
/// </summary>
[TestClass]
public class EtlGovernanceTests : IDisposable
{
    private GovernanceTestDataContext _dc = null!;
    private SqliteConnection _keepAlive = null!;

    [TestInitialize]
    public void Setup()
    {
        var dbName = $"EtlGovernance_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        _dc = new GovernanceTestDataContext(
            $"DataSource={dbName}?mode=memory&cache=shared", DBTypeEnum.SQLite);
        _dc.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dc?.Dispose();
        _keepAlive?.Dispose();
    }

    // ────────────────────────────────────────────────────────────────────────
    // ETL-004: Dead-letter / quarantine
    // ────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task DeadLetter_disabled_by_default_no_rows_written()
    {
        // dead-letter is off unless EnableDeadLetter = true
        var (source, loader) = MakeSourceLoader(rows: 5, failQuality: true);
        var governance = new DbEtlGovernanceStore(_dc);
        var executor = new EtlPipelineExecutor(source, loader, governanceStore: governance);

        var config = MakeConfig(enableDeadLetter: false);
        var result = await executor.ExecuteAsync(config, FullLoadWatermark());

        result.QualityFailedRows.Should().BeGreaterThan(0, "quality rules are applied");

        // No dead-letter rows should have been written
        var rows = await governance.QueryDeadLetterAsync(config.JobId);
        rows.Should().BeEmpty("dead-letter is disabled");
    }

    [TestMethod]
    public async Task DeadLetter_captures_failing_row_with_reason()
    {
        // Row with Amount = -999 will fail the Range rule (min 0)
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        dt.Columns.Add("Amount", typeof(decimal));

        var goodRow = dt.NewRow(); goodRow["OrderNo"] = "ORD-001"; goodRow["Amount"] = 100m;
        var badRow  = dt.NewRow(); badRow["OrderNo"]  = "ORD-002"; badRow["Amount"]  = -999m;
        dt.Rows.Add(goodRow);
        dt.Rows.Add(badRow);

        var source = new MockEtlSource();
        source.SetData(dt);
        var loader = new MockBulkLoader();
        var governance = new DbEtlGovernanceStore(_dc);
        var executor = new EtlPipelineExecutor(source, loader, governanceStore: governance);

        var config = new EtlPipelineConfig
        {
            JobId = Guid.NewGuid(),
            JobName = "DL-Test",
            SourceConnectionString = "src=test",
            TargetConnectionString = "tgt=test",
            QueryTemplate = "SELECT * FROM t",
            TargetTableName = "T",
            MergeKeyColumn = "OrderNo",
            BatchSize = 50_000,
            StagingTable = new StagingTableSpec("stg_t",
                new StagingColumn("OrderNo", "NVARCHAR(50)"),
                new StagingColumn("Amount", "DECIMAL(18,2)")),
            QualityRules = new List<EtlQualityRule>
            {
                new() { Column = "Amount", RuleType = EtlQualityRuleType.Range, Min = 0m }
            },
            QualityRuleAction = EtlQualityRuleAction.Drop,
            EnableDeadLetter = true,
        };

        var result = await executor.ExecuteAsync(config, FullLoadWatermark());

        result.Success.Should().BeTrue();
        result.QualityFailedRows.Should().Be(1);

        var dlRows = await governance.QueryDeadLetterAsync(config.JobId);
        dlRows.Should().HaveCount(1, "one bad row should be quarantined");
        dlRows[0].JobId.Should().Be(config.JobId);
        dlRows[0].Source.Should().Be(EtlDeadLetterSource.QualityRule);
        dlRows[0].Reason.Should().Contain("Amount");   // violation message mentions the column
        dlRows[0].RowJson.Should().Contain("ORD-002"); // bad row serialized
    }

    [TestMethod]
    public async Task DeadLetter_row_json_is_sanitized_no_credentials_leaked()
    {
        // Row data that contains a connection-string-like value should be redacted
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        dt.Columns.Add("Amount", typeof(decimal));

        // Simulate a row whose Amount field value is "Password=secret" (adversarial)
        var badRow = dt.NewRow();
        badRow["OrderNo"] = "ORD-X; Password=secret; Server=db";
        badRow["Amount"]  = -1m; // triggers quality rule
        dt.Rows.Add(badRow);

        var source = new MockEtlSource();
        source.SetData(dt);
        var loader = new MockBulkLoader();
        var governance = new DbEtlGovernanceStore(_dc);
        var executor = new EtlPipelineExecutor(source, loader, governanceStore: governance);

        var config = new EtlPipelineConfig
        {
            JobId = Guid.NewGuid(),
            JobName = "DL-Sanitize",
            SourceConnectionString = "src=test",
            TargetConnectionString = "tgt=test",
            QueryTemplate = "SELECT * FROM t",
            TargetTableName = "T",
            MergeKeyColumn = "OrderNo",
            BatchSize = 50_000,
            StagingTable = new StagingTableSpec("stg_t",
                new StagingColumn("OrderNo", "NVARCHAR(50)"),
                new StagingColumn("Amount", "DECIMAL(18,2)")),
            QualityRules = new List<EtlQualityRule>
            {
                new() { Column = "Amount", RuleType = EtlQualityRuleType.Range, Min = 0m }
            },
            QualityRuleAction = EtlQualityRuleAction.Drop,
            EnableDeadLetter = true,
        };

        await executor.ExecuteAsync(config, FullLoadWatermark());

        var dlRows = await governance.QueryDeadLetterAsync(config.JobId);
        dlRows.Should().HaveCount(1);
        dlRows[0].RowJson.Should().NotContain("secret", "password values must be redacted");
        dlRows[0].RowJson.Should().NotContain("Password=secret", "full credential fragment must be redacted");
    }

    // ────────────────────────────────────────────────────────────────────────
    // ETL-005: Data lineage
    // ────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Lineage_records_source_target_and_counts_on_successful_run()
    {
        var dt = new DataTable();
        dt.Columns.Add("Id", typeof(int));
        for (int i = 1; i <= 5; i++) { var r = dt.NewRow(); r["Id"] = i; dt.Rows.Add(r); }

        var source = new MockEtlSource(); source.SetData(dt);
        var loader = new MockBulkLoader();
        var governance = new DbEtlGovernanceStore(_dc);
        var executor = new EtlPipelineExecutor(source, loader, governanceStore: governance);

        var jobId = Guid.NewGuid();
        var config = new EtlPipelineConfig
        {
            JobId = jobId,
            JobName = "LineageTest",
            SourceConnectionString = "src=test",
            TargetConnectionString = "tgt=test",
            QueryTemplate = "SELECT * FROM src",
            TargetTableName = "Orders",
            MergeKeyColumn = "Id",
            BatchSize = 50_000,
            StagingTable = new StagingTableSpec("stg_orders",
                new StagingColumn("Id", "INT")),
            EnableLineage = true,
            LineageSourceKind = "SqlServer",
            ColumnMappings = new Dictionary<string, string> { ["Id"] = "OrderId" },
        };

        var result = await executor.ExecuteAsync(config, FullLoadWatermark());

        result.Success.Should().BeTrue();

        // Query lineage records directly
        var lineage = _dc.Set<EtlLineageRecord>()
            .Where(l => l.JobId == jobId)
            .ToList();

        lineage.Should().HaveCount(1, "one lineage record per run");
        lineage[0].SourceKind.Should().Be("SqlServer");
        lineage[0].TargetTable.Should().Be("Orders");
        lineage[0].ExtractedRows.Should().Be(5);
        lineage[0].LoadedRows.Should().Be(5);
        lineage[0].QualityFailedRows.Should().Be(0);
        lineage[0].RunId.Should().Be(result.RunId);

        // Column mappings JSON should be recorded
        lineage[0].ColumnMappingsJson.Should().NotBeNullOrEmpty();
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(lineage[0].ColumnMappingsJson!);
        map.Should().ContainKey("Id").WhoseValue.Should().Be("OrderId");
    }

    [TestMethod]
    public async Task Lineage_not_written_when_disabled()
    {
        var dt = new DataTable(); dt.Columns.Add("Id", typeof(int));
        var r = dt.NewRow(); r["Id"] = 1; dt.Rows.Add(r);

        var source = new MockEtlSource(); source.SetData(dt);
        var loader = new MockBulkLoader();
        var governance = new DbEtlGovernanceStore(_dc);
        var executor = new EtlPipelineExecutor(source, loader, governanceStore: governance);

        var jobId = Guid.NewGuid();
        var config = new EtlPipelineConfig
        {
            JobId = jobId,
            JobName = "NoLineage",
            SourceConnectionString = "src=test",
            TargetConnectionString = "tgt=test",
            QueryTemplate = "SELECT * FROM src",
            TargetTableName = "T",
            MergeKeyColumn = "Id",
            BatchSize = 50_000,
            StagingTable = new StagingTableSpec("stg",
                new StagingColumn("Id", "INT")),
            EnableLineage = false,
        };

        await executor.ExecuteAsync(config, FullLoadWatermark());

        var count = _dc.Set<EtlLineageRecord>().Count(l => l.JobId == jobId);
        count.Should().Be(0, "lineage is disabled");
    }

    // ────────────────────────────────────────────────────────────────────────
    // ETL-006: Per-tenant job isolation
    // ────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void TenantJobIsolation_EtlJobDefinition_implements_ITenant()
    {
        var job = new EtlJobDefinition();
        job.Should().BeAssignableTo<ITenant>("EtlJobDefinition must implement ITenant for ETL-006");
    }

    [TestMethod]
    public void TenantJobIsolation_TenantCode_can_be_set_and_read()
    {
        var job = new EtlJobDefinition { TenantCode = "TENANT_A" };
        job.TenantCode.Should().Be("TENANT_A");
    }

    [TestMethod]
    public void TenantJobIsolation_single_tenant_TenantCode_defaults_to_null()
    {
        var job = new EtlJobDefinition();
        job.TenantCode.Should().BeNull("single-tenant scenario: TenantCode not set");
    }

    [TestMethod]
    public async Task TenantJobIsolation_job_visible_under_owning_tenant_context()
    {
        // Use a fresh DB so we control all data
        var dbName = $"TenantIso_{Guid.NewGuid():N}";
        using var keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        keepAlive.Open();

        // Insert-context: TenantCode null so global filter doesn't restrict inserts
        using var insertDc = new GovernanceTestDataContext(
            $"DataSource={dbName}?mode=memory&cache=shared", DBTypeEnum.SQLite);
        insertDc.Database.EnsureCreated();

        // Insert TENANT_A job directly (TenantCode set manually, bypasses automatic basket)
        var jobA = MakeJobDef("job-for-A", "TENANT_A");
        var jobB = MakeJobDef("job-for-B", "TENANT_B");
        insertDc.Set<EtlJobDefinition>().Add(jobA);
        insertDc.Set<EtlJobDefinition>().Add(jobB);
        insertDc.SaveChanges();

        // Query-context: open a second context scoped to TENANT_A
        using var queryDc = new GovernanceTestDataContext(
            $"DataSource={dbName}?mode=memory&cache=shared", DBTypeEnum.SQLite);
        queryDc.SetTenantCode("TENANT_A");

        var visibleJobs = queryDc.Set<EtlJobDefinition>().Select(j => j.TenantCode).ToList();

        visibleJobs.Should().AllSatisfy(tc =>
            tc.Should().Be("TENANT_A"),
            "global filter must scope jobs to TENANT_A only");
        visibleJobs.Should().NotContain("TENANT_B",
            "TENANT_B job must not be visible under TENANT_A context");
    }

    private static EtlJobDefinition MakeJobDef(string name, string? tenantCode) =>
        new()
        {
            Name            = name,
            CronExpression  = "0 * * * *",
            JobClassName    = "TestJob",
            SourceCsKey     = "src",
            TargetCsKey     = "tgt",
            TargetTableName = "T",
            MergeKeyColumn  = "Id",
            QueryTemplate   = "SELECT 1",
            TenantCode      = tenantCode,
        };

    [TestMethod]
    public async Task TenantJobIsolation_null_tenantcode_visible_in_single_tenant()
    {
        // In single-tenant mode (TenantCode = null on context), all jobs are accessible
        var dbName = $"TenantSingle_{Guid.NewGuid():N}";
        using var keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        keepAlive.Open();
        using var dc = new GovernanceTestDataContext(
            $"DataSource={dbName}?mode=memory&cache=shared", DBTypeEnum.SQLite);
        dc.Database.EnsureCreated();

        // Insert a job with null TenantCode (single-tenant scenario)
        var job = new EtlJobDefinition
        {
            Name             = "single-tenant-job",
            CronExpression   = "0 * * * *",
            JobClassName     = "TestJob",
            SourceCsKey      = "src",
            TargetCsKey      = "tgt",
            TargetTableName  = "T",
            MergeKeyColumn   = "Id",
            QueryTemplate    = "SELECT 1",
            TenantCode       = null,  // single-tenant
        };
        dc.Set<EtlJobDefinition>().Add(job);
        dc.SaveChanges();

        // Context has TenantCode = null → ITenant filter evaluates
        // TenantCode == null → null == null → true → job is visible
        // (DataContext ITenant filter: `x.TenantCode == this.TenantCode`)
        var count = dc.Set<EtlJobDefinition>().Count();
        count.Should().Be(1, "single-tenant job with TenantCode=null is visible when context TenantCode=null");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static (MockEtlSource source, MockBulkLoader loader)
        MakeSourceLoader(int rows, bool failQuality)
    {
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        dt.Columns.Add("Amount", typeof(decimal));

        for (int i = 0; i < rows; i++)
        {
            var r = dt.NewRow();
            r["OrderNo"] = $"ORD-{i}";
            r["Amount"]  = failQuality ? -1m : 100m; // negative triggers range rule
            dt.Rows.Add(r);
        }

        var source = new MockEtlSource();
        source.SetData(dt);
        return (source, new MockBulkLoader());
    }

    private static EtlPipelineConfig MakeConfig(bool enableDeadLetter = false)
    {
        return new EtlPipelineConfig
        {
            JobId = Guid.NewGuid(),
            JobName = "GovernanceTest",
            SourceConnectionString = "src=test",
            TargetConnectionString = "tgt=test",
            QueryTemplate = "SELECT * FROM t",
            TargetTableName = "T",
            MergeKeyColumn = "OrderNo",
            BatchSize = 50_000,
            StagingTable = new StagingTableSpec("stg_t",
                new StagingColumn("OrderNo", "NVARCHAR(50)"),
                new StagingColumn("Amount", "DECIMAL(18,2)")),
            QualityRules = new List<EtlQualityRule>
            {
                new() { Column = "Amount", RuleType = EtlQualityRuleType.Range, Min = 0m }
            },
            QualityRuleAction = EtlQualityRuleAction.Drop,
            EnableDeadLetter = enableDeadLetter,
        };
    }

    private static WatermarkStrategy FullLoadWatermark()
        => new(EtlWatermarkType.FullLoad, null, null);
}
