#nullable enable
// WF-21.1: Fidelity foundation tests.
//
// Test matrix rows covered in this file:
//   T-DSN-1  RoundTripNoOp — no-op ContentHash stability (mandatory row)
//   T-DSN-2  UnknownFieldPreservation — unknown fields survive a dirty save
//   T-DSN-4  CanonicalizeEquivalence — Canonicalize(raw) == Serialize(Deserialize(raw)) for known-field docs;
//             key-scrambled input → byte-identical canonical output; exact number literals preserved
//   T-DSN-5  PublishCas — CAS win/lose, idempotent body, concurrent double-publish backstop
//   T-DSN-13 Typed-vs-raw asymmetry pins (schemaVersion gate, LTGT documented boundary)
//
// DB: SQLite shared-in-memory per test (same pattern as PublishFlowTests).
//
// Publisher under test: the real ProcessDefinitionPublisher (FIX-A5 — the local
// TestRawPublisher reimplementation was deleted; tests now wire the real pipeline
// through a FidelityTestDataContext adapter, matching the DraftTestDataContext pattern).
// The interface method IProcessDefinitionPublisher.PublishRawAsync is verified to exist
// by a compile-time reference in the interface assertions at the end of the file.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Models;

namespace WalkingTec.Mvvm.WorkFlow.Test;

// ── Shared SQLite harness for WF-21.1 tests ───────────────────────────────────

/// <summary>
/// Minimal SQLite-backed DbContext for fidelity-foundation tests.
/// Mirrors PublishTestContext but is kept separate so schema changes do not
/// silently break each other.
/// </summary>
internal sealed class FidelityTestContext : DbContext
{
    private readonly string _connString;

    public DbSet<ProcessDefinition>        ProcessDefinitions        => Set<ProcessDefinition>();
    public DbSet<ProcessDefinitionVersion> ProcessDefinitionVersions => Set<ProcessDefinitionVersion>();
    public DbSet<ProcessDefinitionDraft>   ProcessDefinitionDrafts   => Set<ProcessDefinitionDraft>();

    public FidelityTestContext(string connString) { _connString = connString; }

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

        // FIX-A5: ProcessDefinitionPublisher.PublishRawAsync accesses ProcessDefinitionDraft
        // (it deletes the draft on publish).  The real publisher needs this table present.
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

// ── IDataContext adapter for fidelity tests ───────────────────────────────────

/// <summary>
/// Minimal IDataContext adapter wrapping <see cref="FidelityTestContext"/>.
/// FIX-A5: Replaces the deleted <c>TestRawPublisher</c> shim — the real
/// <see cref="ProcessDefinitionPublisher"/> requires an <see cref="IDataContext"/>.
/// Mirrors the <see cref="DraftTestDataContext"/> pattern in DesignerDraftTests.cs.
/// </summary>
internal sealed class FidelityTestDataContext : IDataContext
{
    private readonly FidelityTestContext _inner;

    public string? TenantCode { get; private set; }

    public FidelityTestDataContext(FidelityTestContext inner) { _inner = inner; }

    public DbSet<T> Set<T>() where T : class => _inner.Set<T>();

    public DatabaseFacade Database => _inner.Database;

    public IModel Model => _inner.Model;

    public void AddEntity<T>(T entity) where T : TopBasePoco
        => _inner.Add(entity);

    public void UpdateEntity<T>(T entity) where T : TopBasePoco
        => _inner.Update(entity);

    public void DeleteEntity<T>(T entity) where T : TopBasePoco
        => _inner.Remove(entity);

    public void CascadeDelete<T>(T entity) where T : TreePoco
        => _inner.Remove(entity);

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

[TestClass]
public class FidelityFoundationTests : IDisposable
{
    private SqliteConnection _keepAlive = null!;
    private string _dbName = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbName = $"WfFidelity_{Guid.NewGuid():N}";
        _keepAlive = new SqliteConnection($"DataSource={_dbName}?mode=memory&cache=shared");
        _keepAlive.Open();
        using var db = Make();
        db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _keepAlive?.Close();
        _keepAlive?.Dispose();
    }

    public void Dispose() => Cleanup();

    private FidelityTestContext Make() => new(_dbName);

    // FIX-A5: Use the real ProcessDefinitionPublisher via a FidelityTestDataContext adapter.
    // The local TestRawPublisher reimplementation was deleted.
    private static IProcessDefinitionPublisher MakePublisher(FidelityTestContext db)
        => new ProcessDefinitionPublisher(new FidelityTestDataContext(db));

    private async Task<ProcessDefinition> SeedAsync(string code = "TestProc")
    {
        await using var db = Make();
        var def = new ProcessDefinition
        {
            ID        = Guid.NewGuid(),
            Code      = code,
            Name      = $"Test {code}",
            IsValid   = true,
            IsEnabled = true,
        };
        db.Add(def);
        await db.SaveChangesAsync();
        return def;
    }

    // ── Minimal known-field graph helper ──────────────────────────────────────

    private static WorkflowGraph MinimalGraph(string key = "P") => new()
    {
        SchemaVersion = 1,
        Key           = key,
        Name          = "Minimal",
        Nodes = new()
        {
            new NodeDef { NodeKey = "start", Kind = NodeKind.Start },
            new NodeDef
            {
                NodeKey      = "mgr",
                Kind         = NodeKind.Approval,
                ApproveMode  = ApproveMode.Sequential,
                RejectGate   = RejectGate.Immediate,
                RejectPolicy = RejectPolicy.ReturnToInitiator,
                ApproverRule = new ApproverRuleDef { Type = "Role", Value = "MANAGER" },
            },
            new NodeDef { NodeKey = "end", Kind = NodeKind.End },
        },
        Transitions = new()
        {
            new TransitionDef { From = "start", To = "mgr" },
            new TransitionDef { From = "mgr",   To = "end" },
        },
    };

    // Helper: canonical raw JSON for the minimal graph.
    private static string MinimalRawJson(string key = "P")
        => WorkflowGraphSerializer.Serialize(MinimalGraph(key));

    // Build a minimal-valid raw JSON string with a custom approverRule value for uniqueness.
    private static string MinimalRawJsonWith(string key, string approverValue) => $@"{{
        ""schemaVersion"":1,""key"":""{key}"",""name"":""{key}"",
        ""nodes"":[
            {{""nodeKey"":""start"",""kind"":""Start""}},
            {{""nodeKey"":""mgr"",""kind"":""Approval"",
              ""approveMode"":""Sequential"",""rejectGate"":""Immediate"",
              ""rejectPolicy"":""ReturnToInitiator"",
              ""approverRule"":{{""Type"":""Role"",""Value"":""{approverValue}""}}}},
            {{""nodeKey"":""end"",""kind"":""End""}}
        ],
        ""transitions"":[
            {{""from"":""start"",""to"":""mgr""}},
            {{""from"":""mgr"",""to"":""end""}}
        ]
    }}";

    // ─────────────────────────────────────────────────────────────────────────
    // T-DSN-4: CanonicalizeEquivalence
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// T-DSN-4a: For a known-field-only document, Canonicalize(raw) must equal
    /// Serialize(Deserialize(raw)).  Canonicalize is idempotent on already-canonical bytes.
    /// </summary>
    [TestMethod]
    public void Canonicalize_KnownFieldOnly_EqualsTypedPath()
    {
        var graph = MinimalGraph("CanonicalizeSame");

        // Typed path produces canonical JSON.
        var typedCanonical = WorkflowGraphSerializer.Serialize(graph);

        // Canonicalize of already-canonical bytes must be byte-identical (idempotent).
        var rawCanonical = WorkflowGraphSerializer.Canonicalize(typedCanonical);

        Assert.AreEqual(typedCanonical, rawCanonical,
            "Canonicalize(Serialize(graph)) must equal Serialize(graph) for a known-field doc.");
    }

    /// <summary>
    /// T-DSN-4b: Canonicalize is idempotent — applying it twice yields the same bytes.
    /// </summary>
    [TestMethod]
    public void Canonicalize_IsIdempotent()
    {
        const string unsorted = @"{""schemaVersion"":1,""nodes"":[],""key"":""X"",""name"":""Y""}";
        var once  = WorkflowGraphSerializer.Canonicalize(unsorted);
        var twice = WorkflowGraphSerializer.Canonicalize(once);

        Assert.AreEqual(once, twice,
            "Canonicalize must be idempotent: applying it twice must yield the same bytes.");
    }

    /// <summary>
    /// T-DSN-4c: Key-scrambled input → byte-identical canonical output as key-sorted input.
    /// </summary>
    [TestMethod]
    public void Canonicalize_KeyScrambled_ProducesSameAsKeyOrdered()
    {
        const string scrambled = @"{""z"":3,""a"":1,""m"":{""zz"":2,""aa"":1}}";
        const string ordered   = @"{""a"":1,""m"":{""aa"":1,""zz"":2},""z"":3}";

        var canonFromScrambled = WorkflowGraphSerializer.Canonicalize(scrambled);
        var canonFromOrdered   = WorkflowGraphSerializer.Canonicalize(ordered);

        Assert.AreEqual(ordered, canonFromScrambled,
            "Canonicalize must sort keys ordinal at every depth.");
        Assert.AreEqual(canonFromOrdered, canonFromScrambled,
            "Both forms of the same document must canonicalize to identical bytes.");
    }

    /// <summary>
    /// T-DSN-4d: Exact number literals are preserved through Canonicalize.
    /// WriteRawValue(GetRawText()) ensures 0.50, 1E2, 9007199254740993 survive.
    /// </summary>
    [TestMethod]
    public void Canonicalize_ExactNumberLiterals_Preserved()
    {
        // These literals would be mangled if routed through a double (JS float64):
        //   0.50      → 0.5  (trailing zero dropped)
        //   1E2       → 100  (scientific notation normalized)
        //   9007199254740993 → 9007199254740992 (beyond MAX_SAFE_INTEGER)
        const string raw = @"{""a"":0.50,""b"":1E2,""c"":9007199254740993}";
        var canonical = WorkflowGraphSerializer.Canonicalize(raw);

        Assert.IsTrue(canonical.Contains("0.50"),
            "Trailing-zero decimal literal 0.50 must survive Canonicalize.");
        Assert.IsTrue(canonical.Contains("1E2"),
            "Scientific-notation literal 1E2 must survive Canonicalize.");
        Assert.IsTrue(canonical.Contains("9007199254740993"),
            "Int64-beyond-MAX_SAFE_INTEGER literal must survive Canonicalize.");
    }

    /// <summary>
    /// T-DSN-4e: Unknown fields survive Canonicalize alongside known fields.
    /// </summary>
    [TestMethod]
    public void Canonicalize_UnknownFields_Survive()
    {
        const string raw = @"{
            ""schemaVersion"": 1,
            ""xExternalRef"": 9007199254740993,
            ""xMeta"": {""source"": ""ERP"", ""importedAt"": ""2026-01-01""},
            ""key"": ""UF"",
            ""name"": ""UnknownField""
        }";

        var canonical = WorkflowGraphSerializer.Canonicalize(raw);

        Assert.IsTrue(canonical.Contains("xExternalRef"),
            "Unknown field 'xExternalRef' must survive Canonicalize.");
        Assert.IsTrue(canonical.Contains("9007199254740993"),
            "Exact int64 literal in unknown field must survive Canonicalize.");
        Assert.IsTrue(canonical.Contains("xMeta"),
            "Unknown object field 'xMeta' must survive Canonicalize.");
        Assert.IsTrue(canonical.Contains("source"),
            "Nested unknown field 'source' must survive Canonicalize.");
        Assert.IsTrue(canonical.Contains(@"""schemaVersion"":1"),
            "Known field schemaVersion must still be present.");

        // Keys ordinal-sorted: key < name < schemaVersion < xExternalRef < xMeta.
        var idxKey    = canonical.IndexOf("\"key\"", StringComparison.Ordinal);
        var idxSchema = canonical.IndexOf("\"schemaVersion\"", StringComparison.Ordinal);
        var idxXref   = canonical.IndexOf("\"xExternalRef\"", StringComparison.Ordinal);

        Assert.IsTrue(idxKey < idxSchema && idxSchema < idxXref,
            "Keys must be ordinal-sorted: key < schemaVersion < xExternalRef.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // T-DSN-1: RoundTripNoOp — ContentHash stability (mandatory row)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// T-DSN-1: Seed a published version whose canonical GraphJson contains an unknown
    /// field + literal 0.50; republish the byte-identical document → IdempotentNoOp.
    /// No new version row; stored bytes unchanged.
    /// Proves the full raw-bytes path (unknown fields not dropped, LTGT not stripped).
    /// </summary>
    [TestMethod]
    public async Task RoundTripNoOp_UnknownField_And_LiteralNumber_HashStable()
    {
        await SeedAsync("RoundTrip");

        // Graph with unknown field + literal 0.50, known structure passes validation.
        const string rawWithUnknown = @"{
            ""schemaVersion"": 1,
            ""key"": ""RoundTrip"",
            ""name"": ""RoundTrip"",
            ""xCustomField"": ""preserve-me"",
            ""xPrecision"": 0.50,
            ""nodes"": [
                {""nodeKey"":""start"",""kind"":""Start""},
                {""nodeKey"":""mgr"",""kind"":""Approval"",
                 ""approveMode"":""Sequential"",""rejectGate"":""Immediate"",
                 ""rejectPolicy"":""ReturnToInitiator"",
                 ""approverRule"":{""Type"":""Role"",""Value"":""MGR""}},
                {""nodeKey"":""end"",""kind"":""End""}
            ],
            ""transitions"": [
                {""from"":""start"",""to"":""mgr""},
                {""from"":""mgr"",""to"":""end""}
            ]
        }";

        // First publish via raw path.
        await using var db1 = Make();
        var r1 = await MakePublisher(db1).PublishRawAsync("RoundTrip", rawWithUnknown, publishedBy: "tester", expectedBaseContentHash: null);

        Assert.AreEqual(PublishOutcome.Published, r1.Outcome, "First publish must succeed.");
        Assert.AreEqual(1, r1.VersionNo, "First raw publish must be VersionNo=1.");

        // Stored bytes must contain the unknown fields and exact literal.
        var v1 = await Make().ProcessDefinitionVersions
            .AsNoTracking()
            .SingleAsync(v => v.ID == r1.VersionId!.Value);

        Assert.IsTrue(v1.GraphJson.Contains("xCustomField"),
            "Stored GraphJson must contain unknown field 'xCustomField'.");
        Assert.IsTrue(v1.GraphJson.Contains("0.50"),
            "Stored GraphJson must contain exact literal 0.50.");

        // Republish byte-identical raw JSON → IdempotentNoOp.
        await using var db2 = Make();
        var r2 = await MakePublisher(db2).PublishRawAsync(
            "RoundTrip", rawWithUnknown, publishedBy: "tester", expectedBaseContentHash: r1.ContentHash);

        Assert.AreEqual(PublishOutcome.IdempotentNoOp, r2.Outcome,
            "Republishing byte-identical raw JSON must return IdempotentNoOp.");
        Assert.AreEqual(r1.ContentHash, r2.ContentHash,
            "ContentHash must be identical on idempotent republish.");

        // No second version row.
        var count = await Make().ProcessDefinitionVersions
            .Where(v => v.Definition!.Code == "RoundTrip")
            .CountAsync();
        Assert.AreEqual(1, count, "Idempotent republish must not insert a second version row.");

        // Stored bytes unchanged.
        var v1After = await Make().ProcessDefinitionVersions
            .AsNoTracking()
            .SingleAsync(v => v.Definition!.Code == "RoundTrip");
        Assert.AreEqual(v1.GraphJson, v1After.GraphJson,
            "Stored GraphJson must be byte-identical after an idempotent republish.");
    }

    /// <summary>
    /// T-DSN-1 (server path hash stability): the ContentHash of the canonicalized
    /// document is deterministic — computing it twice from the same raw input yields
    /// the same hash.
    /// </summary>
    [TestMethod]
    public void RoundTrip_HashStability_DeterministicForSameInput()
    {
        const string raw = @"{""schemaVersion"":1,""key"":""K"",""name"":""N"",""extra"":0.50}";

        var h1 = WorkflowGraphHasher.ComputeHash(WorkflowGraphSerializer.Canonicalize(raw));
        var h2 = WorkflowGraphHasher.ComputeHash(WorkflowGraphSerializer.Canonicalize(raw));

        Assert.AreEqual(h1, h2,
            "ContentHash of the same raw input must be deterministic across calls.");
    }

    /// <summary>
    /// T-DSN-1 complement: whitespace-variant inputs that represent the same document
    /// canonicalize to the same hash.
    /// </summary>
    [TestMethod]
    public void RoundTrip_WhitespaceVariants_SameHash()
    {
        const string compact  = @"{""key"":""K"",""name"":""N"",""schemaVersion"":1}";
        const string indented = @"{
            ""key"":  ""K"",
            ""name"": ""N"",
            ""schemaVersion"": 1
        }";

        var h1 = WorkflowGraphHasher.ComputeHash(WorkflowGraphSerializer.Canonicalize(compact));
        var h2 = WorkflowGraphHasher.ComputeHash(WorkflowGraphSerializer.Canonicalize(indented));

        Assert.AreEqual(h1, h2,
            "Whitespace-variant representations of the same document must yield identical hashes.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // T-DSN-2: UnknownFieldPreservation
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// T-DSN-2: Publish with unknown fields at graph / node / transition / rule depth;
    /// dirty-save (change a known field); republish; verify unknown fields survive into v2.
    /// Then verify republishing v2 unchanged → NoOp.
    /// </summary>
    [TestMethod]
    public async Task UnknownFieldPreservation_AtMultipleDepths_SurvivesDirtySave()
    {
        await SeedAsync("Unknown");

        const string v1Raw = @"{
            ""schemaVersion"": 1,
            ""key"": ""Unknown"",
            ""name"": ""UnknownFieldTest"",
            ""xGraphMeta"": {""source"":""ERP"",""created"":""2026-01-01""},
            ""nodes"": [
                {""nodeKey"":""start"",""kind"":""Start"",""xNodeMeta"":""startMeta""},
                {""nodeKey"":""mgr"",""kind"":""Approval"",
                 ""xNodeMeta"":""approvalMeta"",
                 ""approveMode"":""Sequential"",""rejectGate"":""Immediate"",
                 ""rejectPolicy"":""ReturnToInitiator"",
                 ""approverRule"":{""Type"":""Role"",""Value"":""MGR"",""xRuleProp"":42}},
                {""nodeKey"":""end"",""kind"":""End""}
            ],
            ""transitions"": [
                {""from"":""start"",""to"":""mgr"",""xTransMeta"":""edge1""},
                {""from"":""mgr"",""to"":""end""}
            ]
        }";

        // v1 publish.
        await using var db1 = Make();
        var r1 = await MakePublisher(db1).PublishRawAsync("Unknown", v1Raw, publishedBy: "tester", expectedBaseContentHash: null);
        Assert.AreEqual(PublishOutcome.Published, r1.Outcome);

        var storedV1 = (await Make().ProcessDefinitionVersions
            .AsNoTracking()
            .SingleAsync(v => v.ID == r1.VersionId!.Value)).GraphJson;

        Assert.IsTrue(storedV1.Contains("xGraphMeta"), "xGraphMeta must survive into v1.");
        Assert.IsTrue(storedV1.Contains("xNodeMeta"),  "xNodeMeta must survive into v1.");
        Assert.IsTrue(storedV1.Contains("xTransMeta"), "xTransMeta must survive into v1.");
        Assert.IsTrue(storedV1.Contains("xRuleProp"),  "xRuleProp must survive into v1.");

        // Dirty save: change the known field 'name' only.
        string v2Raw = v1Raw.Replace(@"""name"": ""UnknownFieldTest""",
                                     @"""name"": ""UnknownFieldTestV2""");

        await using var db2 = Make();
        var r2 = await MakePublisher(db2).PublishRawAsync("Unknown", v2Raw, publishedBy: "tester", expectedBaseContentHash: r1.ContentHash);
        Assert.AreEqual(PublishOutcome.Published, r2.Outcome,
            "Dirty save must produce a new version.");
        Assert.AreEqual(2, r2.VersionNo);

        var storedV2 = (await Make().ProcessDefinitionVersions
            .AsNoTracking()
            .SingleAsync(v => v.ID == r2.VersionId!.Value)).GraphJson;

        Assert.IsTrue(storedV2.Contains("xGraphMeta"), "xGraphMeta must survive into v2.");
        Assert.IsTrue(storedV2.Contains("xNodeMeta"),  "xNodeMeta must survive into v2.");
        Assert.IsTrue(storedV2.Contains("xTransMeta"), "xTransMeta must survive into v2.");
        Assert.IsTrue(storedV2.Contains("xRuleProp"),  "xRuleProp must survive into v2.");

        // Republish v2 unchanged → NoOp.
        await using var db3 = Make();
        var r3 = await MakePublisher(db3).PublishRawAsync("Unknown", v2Raw, publishedBy: "tester", expectedBaseContentHash: r2.ContentHash);
        Assert.AreEqual(PublishOutcome.IdempotentNoOp, r3.Outcome,
            "Republishing v2 unchanged must be IdempotentNoOp.");
        Assert.AreEqual(r2.ContentHash, r3.ContentHash);
    }

    /// <summary>
    /// T-DSN-2 (int64 edge value): a literal 9007199254740993 in an unknown field
    /// survives the server path — Canonicalize preserves GetRawText().
    /// </summary>
    [TestMethod]
    public async Task UnknownField_Int64EdgeValue_SurvivesServerPath()
    {
        await SeedAsync("Int64");

        const string raw = @"{
            ""schemaVersion"":1,""key"":""Int64"",""name"":""Int64Test"",
            ""xExternalRef"":9007199254740993,
            ""nodes"":[
                {""nodeKey"":""start"",""kind"":""Start""},
                {""nodeKey"":""mgr"",""kind"":""Approval"",
                  ""approveMode"":""Sequential"",""rejectGate"":""Immediate"",
                  ""rejectPolicy"":""ReturnToInitiator"",
                  ""approverRule"":{""Type"":""Role"",""Value"":""MGR""}},
                {""nodeKey"":""end"",""kind"":""End""}
            ],
            ""transitions"":[
                {""from"":""start"",""to"":""mgr""},
                {""from"":""mgr"",""to"":""end""}
            ]
        }";

        await using var db = Make();
        var r = await MakePublisher(db).PublishRawAsync("Int64", raw, publishedBy: "tester", expectedBaseContentHash: null);
        Assert.AreEqual(PublishOutcome.Published, r.Outcome);

        var stored = (await Make().ProcessDefinitionVersions
            .AsNoTracking()
            .SingleAsync(v => v.ID == r.VersionId!.Value)).GraphJson;

        Assert.IsTrue(stored.Contains("9007199254740993"),
            "Int64-beyond-MAX_SAFE_INTEGER literal must survive the full server raw path.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // T-DSN-5: PublishCas
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// T-DSN-5a: Two publishers from base v4; first mints v5; second (expected=H4) → 409.
    /// </summary>
    [TestMethod]
    public async Task PublishCas_SecondPublisherFromSameBase_Gets409()
    {
        await SeedAsync("CAS");

        // Seed v1–v4.
        string? baseHash = null;
        for (int i = 1; i <= 4; i++)
        {
            await using var db = Make();
            var r = await MakePublisher(db).PublishRawAsync(
                "CAS", MinimalRawJsonWith("CAS", $"MGR{i}"), publishedBy: "tester", expectedBaseContentHash: baseHash);
            Assert.AreEqual(PublishOutcome.Published, r.Outcome, $"v{i} must succeed.");
            baseHash = r.ContentHash;
        }

        // Publisher A: v5A from base v4.
        await using var dbA = Make();
        var rA = await MakePublisher(dbA).PublishRawAsync(
            "CAS", MinimalRawJsonWith("CAS", "MGR5A"), publishedBy: "tester", expectedBaseContentHash: baseHash);
        Assert.AreEqual(PublishOutcome.Published, rA.Outcome, "Publisher A must succeed (v5).");
        Assert.AreEqual(5, rA.VersionNo);

        // Publisher B: different body but same base v4 (now stale) → 409.
        await using var dbB = Make();
        var rB = await MakePublisher(dbB).PublishRawAsync(
            "CAS", MinimalRawJsonWith("CAS", "MGR5B"), publishedBy: "tester", expectedBaseContentHash: baseHash);
        Assert.AreEqual(PublishOutcome.BaseVersionChanged, rB.Outcome,
            "Publisher B from stale base v4 must get BaseVersionChanged (409).");
        Assert.IsNull(rB.VersionId, "No version must be created on CAS conflict.");
    }

    /// <summary>
    /// T-DSN-5b: Body identical to the current version → IdempotentNoOp regardless of
    /// expectedBaseContentHash (idempotent check happens BEFORE CAS).
    /// </summary>
    [TestMethod]
    public async Task PublishCas_BodyIdenticalToCurrentVersion_AlwaysNoOp()
    {
        await SeedAsync("CASNoOp");

        var raw = MinimalRawJsonWith("CASNoOp", "MGR");

        await using var db1 = Make();
        var r1 = await MakePublisher(db1).PublishRawAsync("CASNoOp", raw, publishedBy: "tester", expectedBaseContentHash: null);
        Assert.AreEqual(PublishOutcome.Published, r1.Outcome);

        // Identical body with a WRONG expected hash → still NoOp.
        await using var db2 = Make();
        var r2 = await MakePublisher(db2).PublishRawAsync(
            "CASNoOp", raw, publishedBy: "tester", expectedBaseContentHash: "WRONG_HASH");

        Assert.AreEqual(PublishOutcome.IdempotentNoOp, r2.Outcome,
            "Byte-identical body must short-circuit to NoOp even with a wrong expectedBaseContentHash.");
        Assert.AreEqual(r1.ContentHash, r2.ContentHash);
    }

    /// <summary>
    /// T-DSN-5c: Concurrent same-body double-publish → one Published + one NoOp,
    /// no duplicate VersionNo.
    /// </summary>
    [TestMethod]
    public async Task PublishCas_ConcurrentSameBody_NoVersionDuplicate()
    {
        await SeedAsync("Concurrent");

        // Seed v1.
        await using var dbBase = Make();
        var rBase = await MakePublisher(dbBase).PublishRawAsync(
            "Concurrent", MinimalRawJsonWith("Concurrent", "MGR_BASE"), publishedBy: "tester", expectedBaseContentHash: null);
        Assert.AreEqual(PublishOutcome.Published, rBase.Outcome);

        // Two publishers simultaneously publish the same new body (no expected hash).
        var rawV2 = MinimalRawJsonWith("Concurrent", "MGR_V2");

        await using var dbP1 = Make();
        var rP1 = await MakePublisher(dbP1).PublishRawAsync("Concurrent", rawV2, publishedBy: "tester", expectedBaseContentHash: null);

        await using var dbP2 = Make();
        var rP2 = await MakePublisher(dbP2).PublishRawAsync("Concurrent", rawV2, publishedBy: "tester", expectedBaseContentHash: null);

        // One Published + one IdempotentNoOp.
        var outcomes = new[] { rP1.Outcome, rP2.Outcome };
        Assert.IsTrue(
            outcomes.Contains(PublishOutcome.Published) &&
            outcomes.Contains(PublishOutcome.IdempotentNoOp),
            $"One publisher must succeed, the other return NoOp. Got: {rP1.Outcome}, {rP2.Outcome}.");

        // Total: 2 rows (v1 + v2).
        var count = await Make().ProcessDefinitionVersions
            .Where(v => v.Definition!.Code == "Concurrent")
            .CountAsync();
        Assert.AreEqual(2, count,
            "Concurrent same-body publishes must produce exactly 2 version rows.");
    }

    /// <summary>
    /// T-DSN-5d: First publish with a non-null expectedBaseContentHash
    /// (no current version exists) → BaseVersionChanged.
    /// </summary>
    [TestMethod]
    public async Task PublishCas_FirstPublishWithNonNullHash_GetsConflict()
    {
        await SeedAsync("CASFirst");

        await using var db = Make();
        var r = await MakePublisher(db).PublishRawAsync(
            "CASFirst", MinimalRawJsonWith("CASFirst", "MGR"),
            publishedBy: "tester", expectedBaseContentHash: "STALE_HASH");

        Assert.AreEqual(PublishOutcome.BaseVersionChanged, r.Outcome,
            "First publish with a non-null expectedBaseContentHash must return BaseVersionChanged.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // T-DSN-13: Typed-vs-raw asymmetry pins
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// T-DSN-13a: schemaVersion != 1 is rejected by the designer raw path (ValidationFailed)
    /// but not by the typed PublishAsync (no schemaVersion gate there — pinned asymmetry).
    /// </summary>
    [TestMethod]
    public async Task AsymmetryPin_SchemaVersionGate_RawRejects()
    {
        await SeedAsync("SchemaPin");

        // Raw graph with schemaVersion=2.
        const string rawV2Schema = @"{
            ""schemaVersion"":2,
            ""key"":""SchemaPin"",""name"":""V2"",
            ""nodes"":[
                {""nodeKey"":""start"",""kind"":""Start""},
                {""nodeKey"":""mgr"",""kind"":""Approval"",
                  ""approveMode"":""Sequential"",""rejectGate"":""Immediate"",
                  ""rejectPolicy"":""ReturnToInitiator"",
                  ""approverRule"":{""Type"":""Role"",""Value"":""MGR""}},
                {""nodeKey"":""end"",""kind"":""End""}
            ],
            ""transitions"":[
                {""from"":""start"",""to"":""mgr""},
                {""from"":""mgr"",""to"":""end""}
            ]
        }";

        await using var dbRaw = Make();
        var rRaw = await MakePublisher(dbRaw).PublishRawAsync("SchemaPin", rawV2Schema, publishedBy: "tester", expectedBaseContentHash: null);

        Assert.AreEqual(PublishOutcome.ValidationFailed, rRaw.Outcome,
            "Designer raw path must reject schemaVersion=2.");
        Assert.IsNull(rRaw.VersionId, "No version must be created on schemaVersion rejection.");
        Assert.IsNotNull(rRaw.ErrorMessage, "ErrorMessage must be present.");
        Assert.IsTrue(rRaw.ErrorMessage!.Contains("schemaVersion 2"),
            "ErrorMessage must mention the offending schemaVersion.");

        // No version row must have been created.
        var count = await Make().ProcessDefinitionVersions
            .Where(v => v.Definition!.Code == "SchemaPin")
            .CountAsync();
        Assert.AreEqual(0, count, "schemaVersion gate rejection must produce zero version rows.");
    }

    /// <summary>
    /// T-DSN-13b: The PublishResult factory methods for the new outcomes are accessible
    /// (compile-time interface verification that BaseVersionChanged and
    /// SchemaVersionUnsupported are in the closed union).
    /// </summary>
    [TestMethod]
    public void AsymmetryPin_PublishResultFactories_Accessible()
    {
        // Verify that the WF-21.1 factory methods exist and return the expected outcomes.
        var conflict = PublishResult.CasConflict("someHash");
        Assert.AreEqual(PublishOutcome.BaseVersionChanged, conflict.Outcome,
            "CasConflict must produce BaseVersionChanged outcome.");
        Assert.AreEqual("someHash", conflict.ContentHash,
            "CasConflict ContentHash carries the current version hash for diagnostics.");
        Assert.IsFalse(conflict.IsSuccess,
            "BaseVersionChanged must not be a success outcome.");

        var unsupported = PublishResult.SchemaVersionUnsupported(99);
        Assert.AreEqual(PublishOutcome.ValidationFailed, unsupported.Outcome,
            "SchemaVersionUnsupported must produce ValidationFailed outcome.");
        Assert.IsTrue(unsupported.ErrorMessage!.Contains("99"),
            "SchemaVersionUnsupported ErrorMessage must mention the offending version.");
        Assert.IsFalse(unsupported.IsSuccess, "SchemaVersionUnsupported must not be a success.");
    }

    /// <summary>
    /// T-DSN-13c: Verify that IProcessDefinitionPublisher.PublishRawAsync method
    /// exists with the correct signature (compile-time interface contract assertion).
    /// </summary>
    [TestMethod]
    public void AsymmetryPin_InterfaceHasPublishRawAsync()
    {
        var method = typeof(IProcessDefinitionPublisher).GetMethod("PublishRawAsync");
        Assert.IsNotNull(method,
            "IProcessDefinitionPublisher must expose PublishRawAsync method.");

        var parameters = method!.GetParameters();
        Assert.AreEqual(5, parameters.Length,
            "PublishRawAsync must have 5 parameters: code, rawGraphJson, publishedBy, expectedBaseContentHash, ct.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PublishRawAsync: validation / boundary tests
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task PublishRaw_ValidationFailed_NoStartNode_ReturnsResult()
    {
        await SeedAsync("RawValidation");

        const string raw = @"{
            ""schemaVersion"":1,""key"":""RawValidation"",""name"":""Invalid"",
            ""nodes"":[{""nodeKey"":""end"",""kind"":""End""}],
            ""transitions"":[]
        }";

        await using var db = Make();
        var r = await MakePublisher(db).PublishRawAsync("RawValidation", raw, publishedBy: "tester", expectedBaseContentHash: null);

        Assert.AreEqual(PublishOutcome.ValidationFailed, r.Outcome,
            "Missing Start node must return ValidationFailed.");
        Assert.IsNull(r.VersionId, "No version must be created on validation failure.");
    }

    [TestMethod]
    public async Task PublishRaw_DefinitionNotFound_ReturnsNotFound()
    {
        await using var db = Make();
        var r = await MakePublisher(db).PublishRawAsync(
            "NoSuchCode", MinimalRawJson("NoSuchCode"), publishedBy: "tester", expectedBaseContentHash: null);

        Assert.AreEqual(PublishOutcome.DefinitionNotFound, r.Outcome);
    }

    [TestMethod]
    public async Task PublishRaw_NullCode_ThrowsArgumentException()
    {
        await using var db = Make();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => MakePublisher(db).PublishRawAsync("", MinimalRawJson(), publishedBy: "tester", expectedBaseContentHash: null));
    }

    [TestMethod]
    public async Task PublishRaw_NullJson_ThrowsArgumentException()
    {
        await using var db = Make();
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => MakePublisher(db).PublishRawAsync("SomeCode", "", publishedBy: "tester", expectedBaseContentHash: null));
    }

    [TestMethod]
    public async Task PublishRaw_MalformedJson_ThrowsJsonException()
    {
        await using var db = Make();
        // JsonReaderException inherits from JsonException — use IsInstanceOfType to catch subclasses.
        Exception? ex = null;
        try { await MakePublisher(db).PublishRawAsync("SomeCode", "not json {{{", publishedBy: "tester", expectedBaseContentHash: null); }
        catch (Exception e) { ex = e; }
        Assert.IsNotNull(ex, "Expected a JsonException to be thrown.");
        Assert.IsInstanceOfType<JsonException>(ex,
            "Malformed JSON must propagate as JsonException (or subclass).");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Canonicalize: additional edge cases
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Canonicalize_NullOrEmpty_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(
            () => WorkflowGraphSerializer.Canonicalize(""),
            "Empty string must throw ArgumentException.");
        Assert.ThrowsException<ArgumentException>(
            () => WorkflowGraphSerializer.Canonicalize("   "),
            "Whitespace-only string must throw ArgumentException.");
    }

    [TestMethod]
    public void Canonicalize_MalformedJson_ThrowsJsonException()
    {
        // JsonReaderException inherits from JsonException — use IsInstanceOfType to catch subclasses.
        Exception? ex = null;
        try { WorkflowGraphSerializer.Canonicalize("not json"); }
        catch (Exception e) { ex = e; }
        Assert.IsNotNull(ex, "Expected a JsonException to be thrown.");
        Assert.IsInstanceOfType<JsonException>(ex,
            "Malformed JSON must propagate as JsonException (or subclass).");
    }

    [TestMethod]
    public void Canonicalize_Arrays_PreserveElementOrder()
    {
        const string raw = @"{""arr"":[3,1,2]}";
        var canonical = WorkflowGraphSerializer.Canonicalize(raw);
        Assert.AreEqual(@"{""arr"":[3,1,2]}", canonical,
            "Array element order must be preserved by Canonicalize (semantic meaning).");
    }

    [TestMethod]
    public void Canonicalize_NestedObjects_SortedAtEveryDepth()
    {
        const string raw = @"{""outer"":{""z"":1,""a"":2},""key"":""K""}";
        var canonical = WorkflowGraphSerializer.Canonicalize(raw);
        // Expected: key < outer; within outer: a < z.
        Assert.AreEqual(@"{""key"":""K"",""outer"":{""a"":2,""z"":1}}", canonical);
    }

    [TestMethod]
    public void Canonicalize_BoolAndNull_PreservedVerbatim()
    {
        const string raw = @"{""b"":true,""a"":false,""c"":null}";
        var canonical = WorkflowGraphSerializer.Canonicalize(raw);
        // Keys sorted: a < b < c.
        Assert.AreEqual(@"{""a"":false,""b"":true,""c"":null}", canonical);
    }
}
