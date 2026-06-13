#nullable enable
// #299: schemaVersion global validator gate tests.
//
// Tests:
//   T-SV-1: schemaVersion=999 rejected by WorkflowGraphValidator.Validate (typed path).
//   T-SV-2: schemaVersion=1 (CurrentSchemaVersion) passes WorkflowGraphValidator.Validate.
//   T-SV-3: schemaVersion=0 rejected by WorkflowGraphValidator.Validate (invalid: <1).
//   T-SV-4: schemaVersion=999 rejected by real ProcessDefinitionPublisher.PublishAsync —
//            no ProcessDefinitionVersion row created (gate fires before any DB write).
//   T-SV-5: schemaVersion=1 passes real ProcessDefinitionPublisher.PublishAsync —
//            a ProcessDefinitionVersion row IS created (proving the gate is the only
//            differentiator; the publisher itself is not broken).
//   T-SV-6: schemaVersion=999 rejected by designer ValidateRaw (via validator, no double error).
//   T-SV-7: WorkflowGraphSchema.CurrentSchemaVersion is 1 (const correctness).
//   T-SV-8: WorkflowGraph.SchemaVersion defaults to CurrentSchemaVersion.
//
// T-SV-1/2/3 validate the WorkflowGraphValidator directly (validator-behavior tests — correct
// as written; they are NOT claiming to test the publisher path).
// T-SV-4/5 use the REAL ProcessDefinitionPublisher via an IDataContext adapter backed by
// SQLite shared-in-memory (same pattern as FidelityFoundationTests / DesignerDraftTests;
// never EF InMemory — it cannot translate ExecuteUpdateAsync, cf. #119/#162).

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.WorkFlow.Controllers;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.ViewModels;

namespace WalkingTec.Mvvm.WorkFlow.Test;

// ── SQLite DbContext for T-SV-4 / T-SV-5 ────────────────────────────────────
//
// The real ProcessDefinitionPublisher.PublishAsync accesses:
//   - ProcessDefinition
//   - ProcessDefinitionVersion
//   - ProcessDefinitionDraft  (via DeleteDraftIfExistsAsync, always called inside the txn)
// All three tables must be present to avoid EF "entity type not found" at runtime.
// Schema mirrors FidelityTestContext (FidelityFoundationTests.cs) exactly.

/// <summary>
/// Minimal SQLite-backed DbContext for T-SV-4 / T-SV-5 real-publisher tests.
/// Mirrors <c>FidelityTestContext</c> but kept separate so schema changes do not
/// silently couple the two test classes.
/// </summary>
internal sealed class SvPublisherTestContext : DbContext
{
    private readonly string _connString;

    public DbSet<ProcessDefinition>        ProcessDefinitions        => Set<ProcessDefinition>();
    public DbSet<ProcessDefinitionVersion> ProcessDefinitionVersions => Set<ProcessDefinitionVersion>();
    public DbSet<ProcessDefinitionDraft>   ProcessDefinitionDrafts   => Set<ProcessDefinitionDraft>();

    public SvPublisherTestContext(string connString) { _connString = connString; }

    protected override void OnConfiguring(DbContextOptionsBuilder b)
        => b.UseSqlite($"DataSource={_connString}?mode=memory&cache=shared");

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<ProcessDefinition>(e =>
        {
            e.ToTable("Wf_ProcessDefinition");
            e.HasKey(x => x.ID);
            e.HasIndex(x => new { x.TenantCode, x.Code }).IsUnique();
            e.Property(x => x.Code).HasMaxLength(100).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TenantCode).HasMaxLength(50);
            e.Property(x => x.IsValid);
            e.HasOne(x => x.CurrentVersion)
                .WithMany()
                .HasForeignKey(x => x.CurrentVersionId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired(false);
            e.Ignore(x => x.Category);
        });

        m.Entity<ProcessDefinitionVersion>(e =>
        {
            e.ToTable("Wf_ProcessDefinitionVersion");
            e.HasKey(x => x.ID);
            e.HasIndex(x => new { x.TenantCode, x.DefinitionId, x.VersionNo }).IsUnique();
            e.HasIndex(x => x.ContentHash);
            e.Property(x => x.GraphJson).IsRequired();
            e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.TenantCode).HasMaxLength(50);
            e.Property(x => x.PublishedBy).HasMaxLength(50);
            e.Property(x => x.IsValid);
            e.HasOne(x => x.Definition)
                .WithMany()
                .HasForeignKey(x => x.DefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ProcessDefinitionPublisher.PublishAsync calls DeleteDraftIfExistsAsync inside the
        // transaction — this table must be registered even though T-SV-4/5 do not use drafts.
        m.Entity<ProcessDefinitionDraft>(e =>
        {
            e.ToTable("Wf_ProcessDefinitionDraft");
            e.HasKey(x => x.ID);
            e.HasIndex(x => new { x.TenantCode, x.DefinitionId }).IsUnique()
                .HasDatabaseName("IX_Wf_ProcessDefinitionDraft_Tenant_Definition");
            e.Property(x => x.TenantCode).HasMaxLength(50);
            e.Property(x => x.GraphJson).IsRequired();
            e.Property(x => x.BaseContentHash).HasMaxLength(64);
            e.Property(x => x.LastSavedBy).HasMaxLength(50);
            e.Property(x => x.IsValid);
            e.HasOne(x => x.Definition)
                .WithMany()
                .HasForeignKey(x => x.DefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>
/// Minimal <see cref="IDataContext"/> adapter wrapping <see cref="SvPublisherTestContext"/>.
/// Mirrors <see cref="FidelityTestDataContext"/> so the real
/// <see cref="ProcessDefinitionPublisher"/> can be constructed without a full WTM stack.
/// </summary>
internal sealed class SvPublisherTestDataContext : IDataContext
{
    private readonly SvPublisherTestContext _inner;

    public string? TenantCode { get; private set; }

    public SvPublisherTestDataContext(SvPublisherTestContext inner) { _inner = inner; }

    public DbSet<T> Set<T>() where T : class => _inner.Set<T>();
    public DatabaseFacade Database => _inner.Database;
    public IModel Model => _inner.Model;

    public void AddEntity<T>(T entity) where T : TopBasePoco => _inner.Add(entity);
    public void UpdateEntity<T>(T entity) where T : TopBasePoco => _inner.Update(entity);
    public void DeleteEntity<T>(T entity) where T : TopBasePoco => _inner.Remove(entity);
    public void CascadeDelete<T>(T entity) where T : TreePoco => _inner.Remove(entity);

    public void UpdateProperty<T>(T entity, Expression<Func<T, object>> fieldExp) where T : TopBasePoco
        => _inner.Entry(entity).State = EntityState.Modified;

    public void UpdateProperty<T>(T entity, string fieldName) where T : TopBasePoco
        => _inner.Entry(entity).Property(fieldName).IsModified = true;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => _inner.SaveChangesAsync(cancellationToken);

    public Task<int> SaveChangesAsync(bool acceptAllChanges, CancellationToken cancellationToken = default)
        => _inner.SaveChangesAsync(acceptAllChanges, cancellationToken);

    public bool IsFake { get; set; }
    public bool IsDebug { get; set; }
    public string? CurrentUserCode { get; set; }
    public string CSName { get; set; } = string.Empty;
    public DBTypeEnum DBType { get; set; } = DBTypeEnum.SQLite;

    public int SaveChanges() => _inner.SaveChanges();
    public int SaveChanges(bool acceptAllChanges) => _inner.SaveChanges(acceptAllChanges);

    public Task<bool> DataInit(object? allModel, bool isSpa) => Task.FromResult(false);
    public void EnsureCreate() { }
    public IDataContext CreateNew() => throw new NotSupportedException();
    public IDataContext ReCreate(ILoggerFactory? logger = null) => throw new NotSupportedException();
    public DataTable RunSP(string command, params object[] paras) => throw new NotSupportedException();
    public IEnumerable<TElement> RunSP<TElement>(string command, params object[] paras) => throw new NotSupportedException();
    public DataTable RunSQL(string command, params object[] paras) => throw new NotSupportedException();
    public IEnumerable<TElement> RunSQL<TElement>(string sql, params object[] paras) => throw new NotSupportedException();
    public DataTable Run(string sql, CommandType commandType, params object[] paras) => throw new NotSupportedException();
    public IEnumerable<TElement> Run<TElement>(string sql, CommandType commandType, params object[] paras) => throw new NotSupportedException();
    public object CreateCommandParameter(string name, object value, ParameterDirection dir) => throw new NotSupportedException();
    public void SetLoggerFactory(ILoggerFactory factory) { }
    public void SetTenantCode(string? tc) { TenantCode = tc; }
    public void Dispose() => _inner.Dispose();
}

// ── Test fixture ──────────────────────────────────────────────────────────────

/// <summary>
/// #299 — schemaVersion global validator gate.
/// Covers the typed validate path (WorkflowGraphValidator), the designer ValidateRaw helper,
/// and the typed publish path (ProcessDefinitionPublisher).
/// </summary>
[TestClass]
public class SchemaVersionGateTests : IDisposable
{
    // ── Per-test SQLite shared-in-memory DB (T-SV-4 / T-SV-5 only) ────────────
    //
    // The keep-alive connection holds the SQLite in-memory DB open for the lifetime
    // of each test.  T-SV-1/2/3/6/7/8 are pure in-memory/object tests and do not use it.

    private SqliteConnection? _keepAlive;
    private string? _dbName;

    [TestInitialize]
    public void Setup()
    {
        _dbName   = $"WfSvGate_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={_dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        using var db = MakeDb();
        db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _keepAlive?.Close();
        _keepAlive?.Dispose();
        _keepAlive = null;
    }

    public void Dispose() => Cleanup();

    // ── DB helpers ────────────────────────────────────────────────────────────

    private SvPublisherTestContext MakeDb()
        => new(_dbName!);

    /// <summary>Construct the REAL ProcessDefinitionPublisher against the test SQLite DB.</summary>
    private ProcessDefinitionPublisher MakePublisher(SvPublisherTestContext db)
        => new ProcessDefinitionPublisher(new SvPublisherTestDataContext(db));

    private async Task<ProcessDefinition> SeedDefinitionAsync(string code = "SvGateProc")
    {
        await using var db = MakeDb();
        var def = new ProcessDefinition
        {
            ID        = Guid.NewGuid(),
            Code      = code,
            Name      = $"Schema Version Gate Test — {code}",
            IsValid   = true,
            IsEnabled = true,
        };
        db.Add(def);
        await db.SaveChangesAsync();
        return def;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Minimal valid graph with a given schemaVersion.</summary>
    private static WorkflowGraph MinimalGraph(int schemaVersion, string key = "SvTest") => new()
    {
        SchemaVersion = schemaVersion,
        Key = key,
        Name = "Schema Version Test",
        Nodes = new List<NodeDef>
        {
            new() { NodeKey = "start", Kind = NodeKind.Start },
            new()
            {
                NodeKey      = "approver",
                Kind         = NodeKind.Approval,
                ApproveMode  = ApproveMode.Sequential,
                RejectPolicy = RejectPolicy.ReturnToInitiator,
                ApproverRule = new ApproverRuleDef { Type = "Role", Value = "MANAGER" },
            },
            new() { NodeKey = "end", Kind = NodeKind.End },
        },
        Transitions = new List<TransitionDef>
        {
            new() { From = "start",    To = "approver" },
            new() { From = "approver", To = "end" },
        },
    };

    /// <summary>Build a designer controller wired with the given raw JSON body.</summary>
    private static WorkflowDesignerController BuildDesignerController(string rawJson)
    {
        var storeMock     = new Mock<IWorkflowDefinitionStore>(MockBehavior.Loose);
        var publisherMock = new Mock<IProcessDefinitionPublisher>(MockBehavior.Loose);
        var sp = new ServiceCollection()
            .AddSingleton(storeMock.Object)
            .AddSingleton(publisherMock.Object)
            .AddSingleton(Options.Create(new WorkFlowOptions()))
            .BuildServiceProvider();

        var controller = new WorkflowDesignerController(sp);
        ControllerTestHelpers.WireWtm(controller, itCode: "tester");

        var bodyBytes = Encoding.UTF8.GetBytes(rawJson);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.ContentLength = bodyBytes.Length;

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
        controller.Wtm = MockWtmContext.CreateWtmContext(usercode: "tester");
        return controller;
    }

    // ── T-SV-7 + T-SV-8: const and default correctness ───────────────────────

    /// <summary>T-SV-7: WorkflowGraphSchema.CurrentSchemaVersion is 1.</summary>
    [TestMethod]
    public void CurrentSchemaVersion_Is1()
    {
        Assert.AreEqual(1, WorkflowGraphSchema.CurrentSchemaVersion,
            "CurrentSchemaVersion must be 1 (single source of truth for the supported schema).");
    }

    /// <summary>T-SV-8: A new WorkflowGraph defaults its SchemaVersion to CurrentSchemaVersion.</summary>
    [TestMethod]
    public void WorkflowGraph_DefaultSchemaVersion_IsCurrentSchemaVersion()
    {
        var graph = new WorkflowGraph();
        Assert.AreEqual(WorkflowGraphSchema.CurrentSchemaVersion, graph.SchemaVersion,
            "WorkflowGraph.SchemaVersion must default to WorkflowGraphSchema.CurrentSchemaVersion.");
    }

    // ── T-SV-1: schemaVersion=999 rejected by validator ──────────────────────

    /// <summary>T-SV-1: schemaVersion=999 is rejected by the typed validator with SchemaVersionUnsupported.</summary>
    [TestMethod]
    public void Validate_SchemaVersion999_ReturnsSchemaVersionUnsupported()
    {
        var graph = MinimalGraph(999);

        var result = WorkflowGraphValidator.Validate(graph);

        Assert.IsFalse(result.IsValid,
            "schemaVersion=999 must be rejected (T-SV-1).");
        Assert.AreEqual(GraphValidationError.SchemaVersionUnsupported, result.Error,
            "Error code must be SchemaVersionUnsupported (T-SV-1).");
        Assert.IsNotNull(result.ErrorMessage,
            "A descriptive error message must be included.");
        Assert.IsTrue(result.ErrorMessage!.Contains("999"),
            "Error message must include the offending schemaVersion value.");
    }

    // ── T-SV-2: schemaVersion=1 passes validator ──────────────────────────────

    /// <summary>T-SV-2: schemaVersion=1 passes the validator (no SchemaVersionUnsupported).</summary>
    [TestMethod]
    public void Validate_SchemaVersion1_Passes()
    {
        var graph = MinimalGraph(1);

        var result = WorkflowGraphValidator.Validate(graph);

        Assert.IsTrue(result.IsValid,
            "schemaVersion=1 must pass validation (T-SV-2).");
        Assert.AreEqual(GraphValidationError.None, result.Error,
            "No validation error expected for a valid schemaVersion=1 graph.");
    }

    // ── T-SV-3: schemaVersion=0 rejected by validator ────────────────────────

    /// <summary>T-SV-3: schemaVersion=0 (below 1) is rejected as unsupported.</summary>
    [TestMethod]
    public void Validate_SchemaVersion0_ReturnsSchemaVersionUnsupported()
    {
        var graph = MinimalGraph(0);

        var result = WorkflowGraphValidator.Validate(graph);

        Assert.IsFalse(result.IsValid,
            "schemaVersion=0 must be rejected as invalid (T-SV-3).");
        Assert.AreEqual(GraphValidationError.SchemaVersionUnsupported, result.Error,
            "Error code must be SchemaVersionUnsupported for schemaVersion<1 (T-SV-3).");
    }

    // ── T-SV-4: real publisher rejects schemaVersion=999, no version row written ──

    /// <summary>
    /// T-SV-4: ProcessDefinitionPublisher.PublishAsync rejects schemaVersion=999 with
    /// ValidationFailed / SchemaVersionUnsupported — AND does NOT create a
    /// ProcessDefinitionVersion row (proving the gate fires BEFORE any DB write).
    ///
    /// Uses the REAL ProcessDefinitionPublisher backed by SQLite shared-in-memory
    /// (same harness as FidelityFoundationTests / DesignerDraftTests).
    /// </summary>
    [TestMethod]
    public async Task Publish_SchemaVersion999_FailsWithSchemaVersionUnsupported_NoVersionRowCreated()
    {
        await SeedDefinitionAsync("SvGate999Proc");

        // Count version rows before the publish attempt.
        await using var countBefore = MakeDb();
        var before = await countBefore.ProcessDefinitionVersions
            .Where(v => v.Definition!.Code == "SvGate999Proc")
            .CountAsync();

        // Call the REAL ProcessDefinitionPublisher with a schemaVersion=999 graph.
        await using var db = MakeDb();
        using var publisher = MakePublisher(db);
        var result = await publisher.PublishAsync(
            definitionCode: "SvGate999Proc",
            graph:          MinimalGraph(999, key: "SvGate999Proc"),
            publishedBy:    "tester");

        // ── Outcome assertions ────────────────────────────────────────────────
        Assert.AreEqual(PublishOutcome.ValidationFailed, result.Outcome,
            "T-SV-4: schemaVersion=999 must yield ValidationFailed from the real publisher.");
        Assert.AreEqual(GraphValidationError.SchemaVersionUnsupported, result.ValidationError,
            "T-SV-4: ValidationError must be SchemaVersionUnsupported.");
        Assert.IsNull(result.VersionId,
            "T-SV-4: VersionId must be null — no version was created.");

        // ── Load-bearing assertion: no ProcessDefinitionVersion row created ───
        // This proves the schemaVersion gate fires BEFORE the DB INSERT, matching
        // the spec comment in ProcessDefinitionPublisher.cs § Step 1.
        await using var countAfter = MakeDb();
        var after = await countAfter.ProcessDefinitionVersions
            .Where(v => v.Definition!.Code == "SvGate999Proc")
            .CountAsync();

        Assert.AreEqual(before, after,
            "T-SV-4: schemaVersion=999 rejection must not create any ProcessDefinitionVersion row. " +
            $"Row count was {before} before and {after} after the rejected PublishAsync call.");
    }

    // ── T-SV-5: real publisher accepts schemaVersion=1, version row IS created ──

    /// <summary>
    /// T-SV-5: ProcessDefinitionPublisher.PublishAsync accepts schemaVersion=1 and creates
    /// a ProcessDefinitionVersion row — proving the schemaVersion gate is the ONLY reason
    /// T-SV-4 is rejected (not a broken publisher).
    ///
    /// Uses the REAL ProcessDefinitionPublisher backed by SQLite shared-in-memory.
    /// </summary>
    [TestMethod]
    public async Task Publish_SchemaVersion1_Succeeds_VersionRowCreated()
    {
        await SeedDefinitionAsync("SvGate1Proc");

        // Count version rows before the publish.
        await using var countBefore = MakeDb();
        var before = await countBefore.ProcessDefinitionVersions
            .Where(v => v.Definition!.Code == "SvGate1Proc")
            .CountAsync();
        Assert.AreEqual(0, before, "T-SV-5: baseline — no version rows before first publish.");

        // Call the REAL ProcessDefinitionPublisher with a schemaVersion=1 graph.
        await using var db = MakeDb();
        using var publisher = MakePublisher(db);
        var result = await publisher.PublishAsync(
            definitionCode: "SvGate1Proc",
            graph:          MinimalGraph(1, key: "SvGate1Proc"),
            publishedBy:    "tester");

        // ── Outcome assertions ────────────────────────────────────────────────
        Assert.AreEqual(PublishOutcome.Published, result.Outcome,
            "T-SV-5: schemaVersion=1 must yield Published from the real publisher.");
        Assert.IsNotNull(result.VersionId,
            "T-SV-5: VersionId must be non-null after a successful publish.");
        Assert.AreEqual(1, result.VersionNo,
            "T-SV-5: First publish must produce VersionNo=1.");

        // ── Load-bearing assertion: exactly one ProcessDefinitionVersion row created ──
        // This symmetrically proves the gate (not a broken publisher) is the only
        // reason T-SV-4 produced zero rows.
        await using var countAfter = MakeDb();
        var after = await countAfter.ProcessDefinitionVersions
            .Where(v => v.Definition!.Code == "SvGate1Proc")
            .CountAsync();

        Assert.AreEqual(1, after,
            "T-SV-5: schemaVersion=1 publish must create exactly one ProcessDefinitionVersion row.");

        // Verify the stored row has the expected SchemaVersion.
        var storedVersion = await countAfter.ProcessDefinitionVersions
            .Where(v => v.Definition!.Code == "SvGate1Proc")
            .SingleAsync();
        Assert.AreEqual(1, storedVersion.SchemaVersion,
            "T-SV-5: The stored ProcessDefinitionVersion.SchemaVersion must be 1.");
    }

    // ── T-SV-6: designer ValidateRaw — schemaVersion=999 rejected, no double error ──

    /// <summary>
    /// T-SV-6: Designer ValidateGraph endpoint rejects schemaVersion=999 as IsValid=false with
    /// a single error message (no duplicate error from both local and validator gates).
    /// </summary>
    [TestMethod]
    public async Task ValidateGraph_SchemaVersion999_RejectsWithSingleError()
    {
        // Raw JSON with schemaVersion=999.  Use a structurally complete graph to ensure
        // the rejection is due to schemaVersion, not a structural issue.
        var rawJson = @"{
            ""schemaVersion"": 999,
            ""key"": ""SvTest"",
            ""name"": ""Schema Version Test"",
            ""nodes"": [
                { ""nodeKey"": ""start"", ""kind"": ""Start"" },
                {
                    ""nodeKey"": ""approver"",
                    ""kind"": ""Approval"",
                    ""approveMode"": ""Sequential"",
                    ""rejectPolicy"": ""ReturnToInitiator"",
                    ""approverRule"": { ""type"": ""Role"", ""value"": ""MANAGER"" }
                },
                { ""nodeKey"": ""end"", ""kind"": ""End"" }
            ],
            ""transitions"": [
                { ""from"": ""start"", ""to"": ""approver"" },
                { ""from"": ""approver"", ""to"": ""end"" }
            ]
        }";

        var controller = BuildDesignerController(rawJson);
        var result = await controller.ValidateGraph();

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok, "ValidateGraph always returns 200 OK.");

        var dto = ok.Value as ValidateResponseDto;
        Assert.IsNotNull(dto);
        Assert.IsFalse(dto.IsValid,
            "schemaVersion=999 must result in IsValid=false (T-SV-6).");
        Assert.IsNotNull(dto.Error,
            "A descriptive error message must be present.");

        // The error message must mention the unsupported schemaVersion value and the keyword
        // "schemaVersion" — these are the meaningful single-source assertions.
        Assert.IsTrue(dto.Error!.Contains("999", StringComparison.OrdinalIgnoreCase),
            "Error must mention the unsupported schemaVersion value (T-SV-6).");
        Assert.IsTrue(dto.Error.Contains("schemaVersion", StringComparison.OrdinalIgnoreCase),
            "Error must mention 'schemaVersion' (T-SV-6).");
    }

    /// <summary>
    /// T-SV-6b: Designer ValidateGraph with schemaVersion=1 returns IsValid=true
    /// (the removed local gate must not interfere with valid graphs).
    /// </summary>
    [TestMethod]
    public async Task ValidateGraph_SchemaVersion1_IsValidTrue()
    {
        var rawJson = @"{
            ""schemaVersion"": 1,
            ""key"": ""SvTest"",
            ""name"": ""Schema Version Test"",
            ""nodes"": [
                { ""nodeKey"": ""start"", ""kind"": ""Start"" },
                {
                    ""nodeKey"": ""approver"",
                    ""kind"": ""Approval"",
                    ""approveMode"": ""Sequential"",
                    ""rejectPolicy"": ""ReturnToInitiator"",
                    ""approverRule"": { ""type"": ""Role"", ""value"": ""MANAGER"" }
                },
                { ""nodeKey"": ""end"", ""kind"": ""End"" }
            ],
            ""transitions"": [
                { ""from"": ""start"", ""to"": ""approver"" },
                { ""from"": ""approver"", ""to"": ""end"" }
            ]
        }";

        var controller = BuildDesignerController(rawJson);
        var result = await controller.ValidateGraph();

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        var dto = ok.Value as ValidateResponseDto;
        Assert.IsNotNull(dto);
        Assert.IsTrue(dto.IsValid,
            "A schemaVersion=1 graph must be reported as valid (T-SV-6b).");
        Assert.IsNull(dto.Error);
    }
}
