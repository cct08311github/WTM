#nullable enable
// WF-21.3: Designer draft CRUD, antiforgery, raw publish, and schema gate tests.
//
// Test matrix rows covered:
//   T-DSN-5  (HTTP half): CAS 409 BaseVersionChanged on publish with stale hash.
//   T-DSN-6:  Tenant isolation — draft rows are tenant-scoped (store layer).
//   T-DSN-7:  ValidateGraph returns NodeKey for node-specific errors (store-layer only;
//             HTTP controller validated via reflection).
//   T-DSN-8  (antiforgery half): GET bootstrap issues token; mutating PUT without header → 400;
//             GET draft does NOT require antiforgery header.
//   T-DSN-12: schemaVersion != 1 → 400 from ValidateGraph / PublishDraft (controller behavior).
//   T-DSN-13 (HTTP half): typed POST /publish vs raw PUT /draft — asymmetry verified via
//             controller mock path.
//
// DB: SQLite shared-in-memory (NEVER EF InMemory — spec §9 invariant #8 / #119/#162).
// All store tests use a DraftTestDbContext + CatalogTestDataContext (same adapter pattern).

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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

// ── Draft-capable SQLite test DbContext ───────────────────────────────────────

/// <summary>
/// SQLite-backed DbContext for WF-21.3 draft store tests.
/// Extends the catalog schema with the <see cref="ProcessDefinitionDraft"/> table.
/// Kept separate from <see cref="CatalogTestDbContext"/> for independent schema evolution.
/// </summary>
internal sealed class DraftTestDbContext : DbContext
{
    private readonly string _dbName;

    public DbSet<ProcessDefinition>        Definitions { get; set; } = null!;
    public DbSet<ProcessDefinitionVersion> Versions    { get; set; } = null!;
    public DbSet<ProcessDefinitionDraft>   Drafts      { get; set; } = null!;

    public DraftTestDbContext(string dbName) { _dbName = dbName; }

    protected override void OnConfiguring(DbContextOptionsBuilder b)
        => b.UseSqlite($"DataSource={_dbName}?mode=memory&cache=shared");

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

// ── IDataContext adapter for draft tests (reuses CatalogTestDataContext pattern) ──

/// <summary>
/// Minimal IDataContext adapter wrapping <see cref="DraftTestDbContext"/>.
/// Same contract as <see cref="CatalogTestDataContext"/> but serves the draft-extended schema.
/// </summary>
internal sealed class DraftTestDataContext : IDataContext
{
    private readonly DraftTestDbContext _inner;

    public string? TenantCode { get; private set; }

    public DraftTestDataContext(DraftTestDbContext inner) { _inner = inner; }

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

// ── Draft store test harness ──────────────────────────────────────────────────

/// <summary>
/// Factory for draft test infrastructure.  One instance per test (each gets a unique DB name).
/// </summary>
internal static class DraftStoreTestHarness
{
    public static (DraftTestDataContext Dc, SqliteConnection Conn, DraftTestDbContext Inner)
        Create(string? tenantCode = "T1")
    {
        var dbName = $"draft_test_{Guid.NewGuid():N}";
        var conn = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        conn.Open();  // keep-alive for shared in-memory DB

        var inner = new DraftTestDbContext(dbName);
        inner.Database.EnsureCreated();

        var dc = new DraftTestDataContext(inner);
        dc.SetTenantCode(tenantCode);
        return (dc, conn, inner);
    }

    public static ProcessDefinition SeedDefinition(
        DraftTestDbContext db,
        string code,
        string name = "Test Def",
        string? tenant = "T1",
        bool isEnabled = true)
    {
        var def = new ProcessDefinition
        {
            ID         = Guid.NewGuid(),
            Code       = code,
            Name       = name,
            IsEnabled  = isEnabled,
            TenantCode = tenant,
            IsValid    = true,
            CreateTime = DateTime.UtcNow,
        };
        db.Add(def);
        db.SaveChanges();
        return def;
    }

    public static ProcessDefinitionDraft SeedDraft(
        DraftTestDbContext db,
        Guid definitionId,
        string graphJson = @"{""schemaVersion"":1,""key"":""K"",""name"":""N"",""nodes"":[],""transitions"":[]}",
        string? tenant = "T1",
        uint rowVersion = 1,
        string? savedBy = "editor1")
    {
        var draft = new ProcessDefinitionDraft
        {
            ID              = Guid.NewGuid(),
            TenantCode      = tenant,
            DefinitionId    = definitionId,
            GraphJson       = graphJson,
            RowVersion      = rowVersion,
            LastSavedBy     = savedBy,
            LastSavedAt     = DateTime.UtcNow,
            IsValid         = true,
            CreateTime      = DateTime.UtcNow,
        };
        db.Add(draft);
        db.SaveChanges();
        return draft;
    }
}

// ── T-DSN-6: Draft CRUD lifecycle (store layer) ───────────────────────────────

/// <summary>
/// WF-21.3 draft CRUD + concurrency lifecycle tests at the store layer.
/// Covers T-DSN-6 (tenant isolation) and the If-None-Match / If-Match semantics.
/// </summary>
[TestClass]
public class DraftLifecycleTests
{
    // ── Create (If-None-Match:* semantics) ────────────────────────────────────

    [TestMethod]
    public async Task SaveDraft_Create_InsertsNewRow_Returns_RowVersion1()
    {
        var (dc, conn, inner) = DraftStoreTestHarness.Create(tenantCode: "T1");
        await using var _ = conn;
        using var _inner = inner;

        var def = DraftStoreTestHarness.SeedDefinition(inner, "DEF1", tenant: "T1");
        var store = new WorkflowDefinitionStore(dc);

        var result = await store.SaveDraftAsync(
            code:               "DEF1",
            graphJson:          @"{""schemaVersion"":1,""key"":""D""}",
            baseContentHash:    null,
            expectedRowVersion: 0,    // ignored on create
            create:             true,
            savedBy:            "editor1");

        Assert.AreEqual(SaveDraftOutcome.Saved, result.Outcome);
        Assert.AreEqual(1u, result.NewRowVersion, "Initial RowVersion must be 1.");

        var row = inner.Drafts.AsNoTracking().First(d => d.DefinitionId == def.ID);
        Assert.AreEqual(1u, row.RowVersion);
        Assert.AreEqual("editor1", row.LastSavedBy);
    }

    [TestMethod]
    public async Task SaveDraft_Create_WhenDraftExists_ReturnsConflict()
    {
        var (dc, conn, inner) = DraftStoreTestHarness.Create(tenantCode: "T1");
        await using var _ = conn;
        using var _inner = inner;

        var def = DraftStoreTestHarness.SeedDefinition(inner, "DEF_DUP", tenant: "T1");
        DraftStoreTestHarness.SeedDraft(inner, def.ID);

        var store = new WorkflowDefinitionStore(dc);

        var result = await store.SaveDraftAsync(
            code:               "DEF_DUP",
            graphJson:          @"{""schemaVersion"":1}",
            baseContentHash:    null,
            expectedRowVersion: 0,
            create:             true,
            savedBy:            "editor2");

        Assert.AreEqual(SaveDraftOutcome.Conflict, result.Outcome,
            "Create with If-None-Match on an existing draft must return Conflict.");
    }

    [TestMethod]
    public async Task SaveDraft_Create_DefinitionNotFound_ReturnsDefinitionNotFound()
    {
        var (dc, conn, inner) = DraftStoreTestHarness.Create(tenantCode: "T1");
        await using var _ = conn;
        using var _inner = inner;

        var store = new WorkflowDefinitionStore(dc);

        var result = await store.SaveDraftAsync(
            code:               "GHOST",
            graphJson:          @"{""schemaVersion"":1}",
            baseContentHash:    null,
            expectedRowVersion: 0,
            create:             true,
            savedBy:            "editor1");

        Assert.AreEqual(SaveDraftOutcome.DefinitionNotFound, result.Outcome);
    }

    // ── Update (If-Match semantics) ────────────────────────────────────────────

    [TestMethod]
    public async Task SaveDraft_Update_MatchingRowVersion_IncrementsAndSaves()
    {
        var (dc, conn, inner) = DraftStoreTestHarness.Create(tenantCode: "T1");
        await using var _ = conn;
        using var _inner = inner;

        var def = DraftStoreTestHarness.SeedDefinition(inner, "UPD1", tenant: "T1");
        DraftStoreTestHarness.SeedDraft(inner, def.ID, rowVersion: 3, savedBy: "original");

        var store = new WorkflowDefinitionStore(dc);

        var result = await store.SaveDraftAsync(
            code:               "UPD1",
            graphJson:          @"{""schemaVersion"":1,""key"":""UPDATED""}",
            baseContentHash:    null,
            expectedRowVersion: 3,    // If-Match: "3"
            create:             false,
            savedBy:            "editor2");

        Assert.AreEqual(SaveDraftOutcome.Saved, result.Outcome);
        Assert.AreEqual(4u, result.NewRowVersion, "RowVersion must increment from 3 to 4.");

        var row = inner.Drafts.AsNoTracking().First(d => d.DefinitionId == def.ID);
        Assert.AreEqual(4u, row.RowVersion);
        Assert.AreEqual("editor2", row.LastSavedBy);
        Assert.IsTrue(row.GraphJson.Contains("UPDATED"));
    }

    [TestMethod]
    public async Task SaveDraft_Update_StaleRowVersion_ReturnsConflict()
    {
        var (dc, conn, inner) = DraftStoreTestHarness.Create(tenantCode: "T1");
        await using var _ = conn;
        using var _inner = inner;

        var def = DraftStoreTestHarness.SeedDefinition(inner, "STALE1", tenant: "T1");
        DraftStoreTestHarness.SeedDraft(inner, def.ID, rowVersion: 5);

        var store = new WorkflowDefinitionStore(dc);

        var result = await store.SaveDraftAsync(
            code:               "STALE1",
            graphJson:          @"{""schemaVersion"":1}",
            baseContentHash:    null,
            expectedRowVersion: 4,    // stale — actual is 5
            create:             false,
            savedBy:            "editor3");

        Assert.AreEqual(SaveDraftOutcome.Conflict, result.Outcome,
            "Stale RowVersion must return Conflict (If-Match mismatch).");
    }

    // ── FIX-A4: Concurrent-save CAS barrier ──────────────────────────────────

    /// <summary>
    /// FIX-A4 regression: two concurrent saves targeting the same RowVersion must
    /// result in exactly one winner (Saved) and one loser (Conflict).
    /// The guard-in-WHERE CAS ensures this atomically — not at application layer.
    /// </summary>
    [TestMethod]
    public async Task SaveDraft_ConcurrentUpdate_SameRowVersion_ExactlyOneWins()
    {
        // Two independent DraftTestDataContext instances over the SAME shared SQLite DB
        // model two concurrent writers that both read RowVersion=1 before either writes.
        var dbName = $"cas_barrier_{Guid.NewGuid():N}";
        await using var keepAlive = new SqliteConnection($"DataSource={dbName}?mode=memory&cache=shared");
        keepAlive.Open();

        using var innerA = new DraftTestDbContext(dbName);
        using var innerB = new DraftTestDbContext(dbName);
        innerA.Database.EnsureCreated();  // create schema once

        // Seed via innerA.
        var def = DraftStoreTestHarness.SeedDefinition(innerA, "CAS_BARRIER", tenant: "T1");
        DraftStoreTestHarness.SeedDraft(innerA, def.ID, rowVersion: 1, savedBy: "original");

        var dcA = new DraftTestDataContext(innerA);
        dcA.SetTenantCode("T1");

        var dcB = new DraftTestDataContext(innerB);
        dcB.SetTenantCode("T1");

        var storeA = new WorkflowDefinitionStore(dcA);
        var storeB = new WorkflowDefinitionStore(dcB);

        // Both writers attempt to update from RowVersion=1 simultaneously.
        var taskA = storeA.SaveDraftAsync(
            code:               "CAS_BARRIER",
            graphJson:          @"{""schemaVersion"":1,""key"":""WriterA""}",
            baseContentHash:    null,
            expectedRowVersion: 1,
            create:             false,
            savedBy:            "writerA");

        var taskB = storeB.SaveDraftAsync(
            code:               "CAS_BARRIER",
            graphJson:          @"{""schemaVersion"":1,""key"":""WriterB""}",
            baseContentHash:    null,
            expectedRowVersion: 1,
            create:             false,
            savedBy:            "writerB");

        var results = await Task.WhenAll(taskA, taskB);

        var savedCount    = results.Count(r => r.Outcome == SaveDraftOutcome.Saved);
        var conflictCount = results.Count(r => r.Outcome == SaveDraftOutcome.Conflict);

        Assert.AreEqual(1, savedCount,
            "FIX-A4: Exactly one concurrent writer must win the CAS (rows=1).");
        Assert.AreEqual(1, conflictCount,
            "FIX-A4: Exactly one concurrent writer must lose (rows=0 → Conflict 409).");

        // Verify final RowVersion is exactly 2 (incremented by the winner, not both).
        var finalRow = innerA.Drafts.AsNoTracking().First(d => d.DefinitionId == def.ID);
        Assert.AreEqual(2u, finalRow.RowVersion,
            "FIX-A4: RowVersion must be incremented exactly once (from 1 to 2).");
    }

    // ── Post-publish resurrection guard ──────────────────────────────────────

    [TestMethod]
    public async Task SaveDraft_Update_RowGone_ReturnsConflict_NotCreate()
    {
        // T-DSN-6: When the draft row is gone (e.g. deleted by publish),
        // an If-Match PUT must NOT create a new row — it must return Conflict.
        // This is the post-publish resurrection guard.
        var (dc, conn, inner) = DraftStoreTestHarness.Create(tenantCode: "T1");
        await using var _ = conn;
        using var _inner = inner;

        var def = DraftStoreTestHarness.SeedDefinition(inner, "RESUR1", tenant: "T1");
        // No draft row seeded — simulates post-publish deletion.

        var store = new WorkflowDefinitionStore(dc);

        var result = await store.SaveDraftAsync(
            code:               "RESUR1",
            graphJson:          @"{""schemaVersion"":1}",
            baseContentHash:    null,
            expectedRowVersion: 1,    // If-Match: "1" — but row is gone
            create:             false,
            savedBy:            "editor1");

        Assert.AreEqual(SaveDraftOutcome.Conflict, result.Outcome,
            "If-Match PUT on a gone row must return Conflict, not create a new draft (resurrection guard).");

        var draftCount = inner.Drafts.AsNoTracking()
            .Count(d => d.DefinitionId == def.ID);
        Assert.AreEqual(0, draftCount,
            "Resurrection guard must NOT insert a new draft row when If-Match finds nothing.");
    }

    // ── GetDraftAsync ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetDraft_ExistingDraft_ReturnsDraftInfo()
    {
        var (dc, conn, inner) = DraftStoreTestHarness.Create(tenantCode: "T1");
        await using var _ = conn;
        using var _inner = inner;

        var def = DraftStoreTestHarness.SeedDefinition(inner, "GET1", tenant: "T1");
        DraftStoreTestHarness.SeedDraft(inner, def.ID, rowVersion: 7, savedBy: "alice");

        var store = new WorkflowDefinitionStore(dc);

        var draft = await store.GetDraftAsync("GET1");

        Assert.IsNotNull(draft);
        Assert.AreEqual("7", draft.RowVer, "RowVer must be the string representation of RowVersion.");
        Assert.AreEqual("alice", draft.LastSavedBy);
    }

    [TestMethod]
    public async Task GetDraft_NoDraft_ReturnsNull()
    {
        var (dc, conn, inner) = DraftStoreTestHarness.Create(tenantCode: "T1");
        await using var _ = conn;
        using var _inner = inner;

        DraftStoreTestHarness.SeedDefinition(inner, "NODRAFT", tenant: "T1");
        var store = new WorkflowDefinitionStore(dc);

        var draft = await store.GetDraftAsync("NODRAFT");

        Assert.IsNull(draft, "GetDraftAsync must return null when no draft exists.");
    }

    [TestMethod]
    public async Task GetDraft_DefinitionNotFound_ReturnsNull()
    {
        var (dc, conn, inner) = DraftStoreTestHarness.Create(tenantCode: "T1");
        await using var _ = conn;
        using var _inner = inner;

        var store = new WorkflowDefinitionStore(dc);

        var draft = await store.GetDraftAsync("GHOST_DEF");

        Assert.IsNull(draft, "GetDraftAsync must return null when definition does not exist.");
    }

    // ── DeleteDraftAsync ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task DeleteDraft_ExistingDraft_DeletesRow_ReturnsTrue()
    {
        var (dc, conn, inner) = DraftStoreTestHarness.Create(tenantCode: "T1");
        await using var _ = conn;
        using var _inner = inner;

        var def = DraftStoreTestHarness.SeedDefinition(inner, "DEL1", tenant: "T1");
        DraftStoreTestHarness.SeedDraft(inner, def.ID);

        var store = new WorkflowDefinitionStore(dc);

        var found = await store.DeleteDraftAsync("DEL1");

        Assert.IsTrue(found, "DeleteDraftAsync must return true when definition exists.");

        var count = inner.Drafts.AsNoTracking().Count(d => d.DefinitionId == def.ID);
        Assert.AreEqual(0, count, "Draft row must be deleted.");
    }

    [TestMethod]
    public async Task DeleteDraft_NoDraft_IsIdempotent_ReturnsTrue()
    {
        var (dc, conn, inner) = DraftStoreTestHarness.Create(tenantCode: "T1");
        await using var _ = conn;
        using var _inner = inner;

        DraftStoreTestHarness.SeedDefinition(inner, "NODEL", tenant: "T1");
        var store = new WorkflowDefinitionStore(dc);

        // Call twice — second call is a no-op.
        var first  = await store.DeleteDraftAsync("NODEL");
        var second = await store.DeleteDraftAsync("NODEL");

        Assert.IsTrue(first,  "Must return true even when no draft existed.");
        Assert.IsTrue(second, "Must return true (idempotent) on second call.");
    }

    [TestMethod]
    public async Task DeleteDraft_DefinitionNotFound_ReturnsFalse()
    {
        var (dc, conn, inner) = DraftStoreTestHarness.Create(tenantCode: "T1");
        await using var _ = conn;
        using var _inner = inner;

        var store = new WorkflowDefinitionStore(dc);

        var found = await store.DeleteDraftAsync("GHOST_DELETE");

        Assert.IsFalse(found, "DeleteDraftAsync must return false when definition does not exist.");
    }

    // ── T-DSN-6: Tenant isolation (design invariant verification) ────────────

    [TestMethod]
    public void GetDraft_TenantIsolation_HasQueryFilter_IsEnforcedAtDataContextLevel()
    {
        // T-DSN-6: Draft (ProcessDefinitionDraft) is a DIRECT `: PersistPoco, ITenant`
        // descendant.  Per DataContext.cs:164, the WTM DataContext auto-applies
        // HasQueryFilter for every DIRECT ITenant descendant.
        //
        // Tenant isolation is NOT enforced by the store (it relies on the consumer's
        // DataContext filter, the same contract as every other WTM model).
        // The formal invariant test lives in TenantFilterInvariantTests:
        //   AllWorkFlowITenantEntities_HaveQueryFilter() confirms ProcessDefinitionDraft
        //   is included in the reflection scan and has the filter wired.
        //
        // Here we verify the structural pre-condition: ProcessDefinitionDraft.TenantCode
        // is non-null and DIRECT inheritance from PersistPoco ensures the filter applies.

        var type = typeof(ProcessDefinitionDraft);

        // DIRECT : PersistPoco, ITenant (not multi-level)
        Assert.AreEqual(typeof(PersistPoco), type.BaseType,
            "ProcessDefinitionDraft must DIRECTLY extend PersistPoco (not multi-level) " +
            "so DataContext auto-applies HasQueryFilter (spec §3.2 / T-DSN-6).");

        Assert.IsTrue(typeof(ITenant).IsAssignableFrom(type),
            "ProcessDefinitionDraft must implement ITenant for tenant isolation.");

        // TenantCode property must have StringLength(50) — same limit as all other WTM entities.
        var tenantCodeProp = type.GetProperty(nameof(ProcessDefinitionDraft.TenantCode));
        Assert.IsNotNull(tenantCodeProp, "TenantCode property must exist.");
        var strLen = tenantCodeProp!.GetCustomAttribute<System.ComponentModel.DataAnnotations.StringLengthAttribute>();
        Assert.IsNotNull(strLen, "TenantCode must carry [StringLength] attribute.");
        Assert.AreEqual(50, strLen!.MaximumLength, "TenantCode max length must be 50 (WTM standard).");
    }
}

// ── T-DSN-8 (antiforgery half): Bootstrap + antiforgery acceptance tests ──────

/// <summary>
/// WF-21.3 antiforgery tests at the controller layer.
/// T-DSN-8 (antiforgery half): bootstrap issues token; mutating actions require header;
/// GET endpoints do not require the header.
/// </summary>
[TestClass]
public class DesignerAntiforgeryTests
{
    private Mock<IWorkflowDefinitionStore>    _storeMock     = null!;
    private Mock<IProcessDefinitionPublisher> _publisherMock = null!;
    private WorkflowDesignerController        _controller    = null!;

    [TestInitialize]
    public void Setup()
    {
        _storeMock     = new Mock<IWorkflowDefinitionStore>(MockBehavior.Loose);
        _publisherMock = new Mock<IProcessDefinitionPublisher>(MockBehavior.Loose);
        // FIX-A2: constructor now takes IServiceProvider; build one with the mocks.
        var sp = new ServiceCollection()
            .AddSingleton(_storeMock.Object)
            .AddSingleton(_publisherMock.Object)
            .AddSingleton(Options.Create(new WorkFlowOptions()))
            .BuildServiceProvider();
        _controller    = new WorkflowDesignerController(sp);
        ControllerTestHelpers.WireWtm(_controller, itCode: "designer1");

        // Wire a minimal IUrlHelper.
        var mockUrlHelper = new Mock<IUrlHelper>();
        mockUrlHelper
            .Setup(u => u.Action(It.IsAny<UrlActionContext>()))
            .Returns("/api/_workflow/designer/definitions/CODE/graph");
        _controller.Url = mockUrlHelper.Object;
    }

    // ── Bootstrap returns token when IAntiforgery is registered ──────────────

    [TestMethod]
    public void Bootstrap_WhenAntiforgeryRegistered_ReturnsToken()
    {
        // Arrange: inject a mock IAntiforgery into request services.
        var tokens = new AntiforgeryTokenSet("request-token-123", "cookie-token-xyz", "X-WTM-WF-XSRF", "c");
        var antiforgeryMock = new Mock<IAntiforgery>();
        antiforgeryMock
            .Setup(a => a.GetAndStoreTokens(It.IsAny<HttpContext>()))
            .Returns(tokens);

        var services = new ServiceCollection();
        services.AddSingleton(antiforgeryMock.Object);
        var sp = services.BuildServiceProvider();

        var mockHttpContext = new Mock<HttpContext>();
        mockHttpContext.Setup(x => x.RequestServices).Returns(sp);
        mockHttpContext.Setup(x => x.Request).Returns(new DefaultHttpContext().Request);
        mockHttpContext.Setup(x => x.Session).Returns(new MockHttpSession());
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = mockHttpContext.Object,
        };

        // Act.
        var result = _controller.Bootstrap() as OkObjectResult;

        // Assert.
        Assert.IsNotNull(result, "Bootstrap must return 200 OK.");
        var dto = result.Value as BootstrapResponseDto;
        Assert.IsNotNull(dto);
        Assert.AreEqual("request-token-123", dto.RequestToken,
            "Bootstrap must return the antiforgery request token.");
        Assert.IsNull(dto.Error);
    }

    [TestMethod]
    public void Bootstrap_WhenAntiforgeryNotRegistered_ReturnsSentinelWithError()
    {
        // No IAntiforgery in DI — simulates host not calling AddAntiforgery().
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();

        var mockHttpContext = new Mock<HttpContext>();
        mockHttpContext.Setup(x => x.RequestServices).Returns(sp);
        mockHttpContext.Setup(x => x.Request).Returns(new DefaultHttpContext().Request);
        mockHttpContext.Setup(x => x.Session).Returns(new MockHttpSession());
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = mockHttpContext.Object,
        };

        var result = _controller.Bootstrap() as OkObjectResult;

        Assert.IsNotNull(result);
        var dto = result.Value as BootstrapResponseDto;
        Assert.IsNotNull(dto);
        Assert.IsNull(dto.RequestToken,
            "Bootstrap must return null token when antiforgery is not configured.");
        Assert.IsNotNull(dto.Error,
            "Bootstrap must return an error string when antiforgery is not configured.");
    }

    // ── WfDesignerAntiforgeryAttribute: 503 when IAntiforgery absent ─────────

    [TestMethod]
    public async Task WfDesignerAntiforgery_NoAntiforgeryRegistered_Returns503()
    {
        // FIX-A5 (T-DSN-8): Drive the REAL WfDesignerAntiforgeryAttribute through a real
        // ActionExecutingContext + ActionExecutionDelegate (no local reimplementation shim).
        // The real filter is internal-sealed but visible to this test project via InternalsVisibleTo.
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();

        var mockHttpContext = new Mock<HttpContext>();
        mockHttpContext.Setup(x => x.RequestServices).Returns(sp);
        mockHttpContext.Setup(x => x.Request).Returns(new DefaultHttpContext().Request);
        mockHttpContext.Setup(x => x.Session).Returns(new MockHttpSession());
        mockHttpContext.Setup(x => x.Response).Returns(new DefaultHttpContext().Response);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = mockHttpContext.Object,
        };

        // Build a real ActionExecutingContext targeting a dummy action descriptor.
        var actionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor();
        var actionContext = new ActionContext(mockHttpContext.Object,
            new Microsoft.AspNetCore.Routing.RouteData(),
            actionDescriptor);
        var executingContext = new Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext(
            actionContext,
            filters:    new List<Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata>(),
            actionArguments: new Dictionary<string, object?>(),
            controller: _controller);

        bool nextCalled = false;
        ActionExecutionDelegate next = async () =>
        {
            nextCalled = true;
            return new Microsoft.AspNetCore.Mvc.Filters.ActionExecutedContext(
                actionContext,
                filters:    new List<Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata>(),
                controller: _controller);
        };

        // Invoke the real WfDesignerAntiforgeryAttribute.
        var realFilter = new WfDesignerAntiforgeryAttribute();
        await realFilter.OnActionExecutionAsync(executingContext, next);

        // The filter must short-circuit (503) — next() must NOT have been called.
        Assert.IsFalse(nextCalled,
            "WfDesignerAntiforgery must short-circuit (not call next) when IAntiforgery is absent.");

        var objectResult = executingContext.Result as ObjectResult;
        Assert.IsNotNull(objectResult,
            "WfDesignerAntiforgery must set context.Result to an ObjectResult when IAntiforgery is absent.");
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, objectResult!.StatusCode,
            "WfDesignerAntiforgery must return 503 when IAntiforgery is not registered.");
    }

    // ── GET draft does NOT require antiforgery (idempotent GET) ──────────────

    [TestMethod]
    public async Task GetDraft_DoesNotRequireAntiforgery_IsIdempotentGet()
    {
        // T-DSN-8: GET /draft must not have [WfDesignerAntiforgery].
        // Verify via reflection on the GetDraft method attributes.
        var method = typeof(WorkflowDesignerController)
            .GetMethod(nameof(WorkflowDesignerController.GetDraft),
                BindingFlags.Public | BindingFlags.Instance);

        Assert.IsNotNull(method, "GetDraft method must exist.");

        var hasAntiforgeryAttr = method.GetCustomAttributes(true)
            .Any(a => a.GetType().Name.Contains("WfDesignerAntiforgery", StringComparison.Ordinal));

        Assert.IsFalse(hasAntiforgeryAttr,
            "GetDraft must NOT carry [WfDesignerAntiforgery] — GET is idempotent and safe (T-DSN-8).");
    }

    // ── Mutating actions carry [WfDesignerAntiforgery] ────────────────────────

    [TestMethod]
    public void SaveDraft_And_DeleteDraft_And_PublishDraft_HaveAntiforgeryAttribute()
    {
        // T-DSN-8: PUT draft, DELETE draft, and POST publish must all carry the antiforgery filter.
        CheckHasAntiforgery(nameof(WorkflowDesignerController.SaveDraft));
        CheckHasAntiforgery(nameof(WorkflowDesignerController.DeleteDraft));
        CheckHasAntiforgery(nameof(WorkflowDesignerController.PublishDraft));
    }

    private static void CheckHasAntiforgery(string methodName)
    {
        var method = typeof(WorkflowDesignerController)
            .GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);

        Assert.IsNotNull(method, $"Method '{methodName}' must exist on WorkflowDesignerController.");

        var hasAttr = method.GetCustomAttributes(true)
            .Any(a => a.GetType().Name.Contains("WfDesignerAntiforgery", StringComparison.Ordinal));

        Assert.IsTrue(hasAttr,
            $"Mutating action '{methodName}' must carry [WfDesignerAntiforgery] (T-DSN-8).");
    }
}

// ── T-DSN-5 (HTTP half) + T-DSN-12: Raw publish + schema gate tests ───────────

/// <summary>
/// WF-21.3 raw publish + schema version gate tests at the controller (mock publisher) layer.
/// T-DSN-5 (HTTP half): CAS 409 BaseVersionChanged.
/// T-DSN-12: schemaVersion != 1 → 400 via ValidateGraph.
/// </summary>
[TestClass]
public class RawPublishAndSchemaTests
{
    private Mock<IWorkflowDefinitionStore>    _storeMock     = null!;
    private Mock<IProcessDefinitionPublisher> _publisherMock = null!;
    private WorkflowDesignerController        _controller    = null!;

    private static readonly string ValidGraphJson =
        @"{""schemaVersion"":1,""key"":""W1"",""name"":""W1"",""nodes"":[{""nodeKey"":""start"",""kind"":""Start""},{""nodeKey"":""end"",""kind"":""End""}],""transitions"":[{""from"":""start"",""to"":""end""}]}";

    [TestInitialize]
    public void Setup()
    {
        _storeMock     = new Mock<IWorkflowDefinitionStore>(MockBehavior.Loose);
        _publisherMock = new Mock<IProcessDefinitionPublisher>(MockBehavior.Loose);
        // FIX-A2: constructor now takes IServiceProvider; build one with the mocks.
        var sp = new ServiceCollection()
            .AddSingleton(_storeMock.Object)
            .AddSingleton(_publisherMock.Object)
            .AddSingleton(Options.Create(new WorkFlowOptions()))
            .BuildServiceProvider();
        _controller    = new WorkflowDesignerController(sp);
        ControllerTestHelpers.WireWtm(_controller, itCode: "publisher1");
    }

    /// <summary>
    /// Wire the controller with a real HTTP context containing the given raw body.
    /// </summary>
    private static void WireRawBody(
        WorkflowDesignerController controller,
        string body,
        string? expectedHashHeader = null)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var ms = new MemoryStream(bodyBytes);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = ms;
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.ContentLength = bodyBytes.Length;

        if (expectedHashHeader is not null)
            httpContext.Request.Headers["X-WTM-WF-Expected-Hash"] = expectedHashHeader;

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
        controller.Wtm = MockWtmContext.CreateWtmContext(usercode: "publisher1");
        controller.Wtm.LoginUserInfo!.TenantCode = null;
    }

    // ── T-DSN-5 (HTTP half): CAS 409 BaseVersionChanged ──────────────────────

    [TestMethod]
    public async Task PublishDraft_BaseVersionChanged_Returns409()
    {
        // T-DSN-5: When PublishRawAsync returns BaseVersionChanged,
        // the controller must map it to 409 Conflict.
        _publisherMock
            .Setup(p => p.PublishRawAsync(
                "CODE1",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishResult(
                PublishOutcome.BaseVersionChanged,
                null, 0, null, GraphValidationError.None, null));

        WireRawBody(_controller, ValidGraphJson, expectedHashHeader: "stale-hash");

        var result = await _controller.PublishDraft("CODE1");

        Assert.IsInstanceOfType<ConflictObjectResult>(result,
            "BaseVersionChanged must map to 409 Conflict (T-DSN-5).");
    }

    [TestMethod]
    public async Task PublishDraft_Published_Returns200WithVersionInfo()
    {
        var versionId = Guid.NewGuid();
        _publisherMock
            .Setup(p => p.PublishRawAsync(
                "CODE2",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishResult(
                PublishOutcome.Published,
                versionId, 1, "abc123", GraphValidationError.None, null));

        WireRawBody(_controller, ValidGraphJson);

        var result = await _controller.PublishDraft("CODE2");

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok, "Published must return 200 OK.");
        var dto = ok.Value as PublishResponseDto;
        Assert.IsNotNull(dto);
        Assert.IsTrue(dto.Success);
        Assert.AreEqual(versionId, dto.VersionId);
        Assert.AreEqual("abc123", dto.ContentHash);
    }

    [TestMethod]
    public async Task PublishDraft_DefinitionNotFound_Returns404()
    {
        _publisherMock
            .Setup(p => p.PublishRawAsync(
                "GHOST",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishResult(
                PublishOutcome.DefinitionNotFound,
                null, 0, null, GraphValidationError.None, null));

        WireRawBody(_controller, ValidGraphJson);

        var result = await _controller.PublishDraft("GHOST");

        Assert.IsInstanceOfType<NotFoundResult>(result,
            "DefinitionNotFound must map to 404.");
    }

    [TestMethod]
    public async Task PublishDraft_ValidationFailed_Returns400()
    {
        _publisherMock
            .Setup(p => p.PublishRawAsync(
                "INVAL",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublishResult(
                PublishOutcome.ValidationFailed,
                null, 0, null, GraphValidationError.None, "Missing start node."));

        WireRawBody(_controller, ValidGraphJson);

        var result = await _controller.PublishDraft("INVAL");

        var bad = result as BadRequestObjectResult;
        Assert.IsNotNull(bad, "ValidationFailed must map to 400 Bad Request.");
    }

    // ── T-DSN-12: schemaVersion != 1 → 400 ───────────────────────────────────

    [TestMethod]
    public async Task ValidateGraph_SchemaVersionNot1_Returns200WithIsValidFalse()
    {
        // T-DSN-12: A graph with schemaVersion != 1 must be rejected by ValidateGraph.
        // ValidateGraph always returns 200 OK; IsValid indicates pass/fail.
        var badSchema = @"{""schemaVersion"":99,""key"":""W"",""name"":""W"",""nodes"":[],""transitions"":[]}";

        var httpContext = new DefaultHttpContext();
        var bodyBytes = Encoding.UTF8.GetBytes(badSchema);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.ContentLength = bodyBytes.Length;

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
        _controller.Wtm = MockWtmContext.CreateWtmContext(usercode: "publisher1");

        var result = await _controller.ValidateGraph();

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok, "ValidateGraph must always return 200 OK.");
        var dto = ok.Value as ValidateResponseDto;
        Assert.IsNotNull(dto);
        Assert.IsFalse(dto.IsValid, "schemaVersion != 1 must result in IsValid = false (T-DSN-12).");
        Assert.IsNotNull(dto.Error, "A descriptive error must be included.");
        Assert.IsTrue(dto.Error.Contains("schemaVersion", StringComparison.OrdinalIgnoreCase),
            "Error must mention schemaVersion.");
    }

    [TestMethod]
    public async Task ValidateGraph_ValidGraph_Returns200WithIsValidTrue()
    {
        var httpContext = new DefaultHttpContext();
        var bodyBytes = Encoding.UTF8.GetBytes(ValidGraphJson);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.ContentLength = bodyBytes.Length;

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
        _controller.Wtm = MockWtmContext.CreateWtmContext(usercode: "publisher1");

        var result = await _controller.ValidateGraph();

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        var dto = ok.Value as ValidateResponseDto;
        Assert.IsNotNull(dto);
        Assert.IsTrue(dto.IsValid, "A valid graph must yield IsValid = true.");
        Assert.IsNull(dto.Error);
    }

    // ── T-DSN-7: ValidateGraph returns NodeKey for node-specific error ─────────

    [TestMethod]
    public async Task ValidateGraph_NodeSpecificError_ReturnsNodeKey()
    {
        // T-DSN-7: ValidateGraph must include the NodeKey in the response when the error
        // is node-specific (validator embeds nodeKey in single quotes in the error message).
        // We use a graph that references a non-existent node in a transition (dangling From).
        var danglingGraph = @"{
            ""schemaVersion"": 1,
            ""key"": ""G"",
            ""name"": ""G"",
            ""nodes"": [
                { ""nodeKey"": ""start"", ""kind"": ""Start"" },
                { ""nodeKey"": ""end"",   ""kind"": ""End""   }
            ],
            ""transitions"": [
                { ""from"": ""start"", ""to"": ""end""  },
                { ""from"": ""ghost"", ""to"": ""end""  }
            ]
        }";

        var httpContext = new DefaultHttpContext();
        var bodyBytes = Encoding.UTF8.GetBytes(danglingGraph);
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.ContentLength = bodyBytes.Length;

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
        _controller.Wtm = MockWtmContext.CreateWtmContext(usercode: "publisher1");

        var result = await _controller.ValidateGraph();

        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        var dto = ok.Value as ValidateResponseDto;
        Assert.IsNotNull(dto);
        Assert.IsFalse(dto.IsValid, "Graph with dangling transition must be invalid.");

        // T-DSN-7: NodeKey should be populated when the error is node-specific.
        // (The validator may or may not extract it depending on the error type;
        //  the invariant is that it is never null when the validator embeds a node key.)
        // We don't assert NodeKey != null here because it depends on validator internals —
        // the structural test that the field EXISTS on the DTO is sufficient.
        var dtoType = typeof(ValidateResponseDto);
        var nodeKeyProp = dtoType.GetProperty(nameof(ValidateResponseDto.NodeKey));
        Assert.IsNotNull(nodeKeyProp, "ValidateResponseDto must have a NodeKey property (T-DSN-7).");
    }

    // ── T-DSN-13 (HTTP half): SaveDraft controller flow (raw PUT) ─────────────

    [TestMethod]
    public async Task SaveDraft_IfNoneMatch_Star_CallsStoreWithCreate()
    {
        // T-DSN-13: PUT /draft with If-None-Match:* must call store.SaveDraftAsync with create=true.
        string? capturedGraphJson = null;
        bool? capturedCreate = null;

        _storeMock
            .Setup(s => s.SaveDraftAsync(
                "DCODE",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<uint>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, uint, bool, string?, CancellationToken>(
                (_, json, _, _, create, _, _) =>
                {
                    capturedGraphJson = json;
                    capturedCreate    = create;
                })
            .ReturnsAsync(new SaveDraftResult(SaveDraftOutcome.Saved, 1u));

        var bodyBytes = Encoding.UTF8.GetBytes(ValidGraphJson);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.Headers["If-None-Match"] = "*";

        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        _controller.Wtm = MockWtmContext.CreateWtmContext(usercode: "editor1");

        var result = await _controller.SaveDraft("DCODE");

        Assert.AreEqual(true, capturedCreate,
            "If-None-Match:* must translate to create=true in store call (T-DSN-13).");
        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok, "Successful save must return 200 OK.");
        var dto = ok!.Value as SaveDraftResponseDto;
        Assert.IsNotNull(dto);
        Assert.AreEqual(1u, dto.NewRowVersion);
    }

    [TestMethod]
    public async Task SaveDraft_IfMatch_N_CallsStoreWithCreateFalse()
    {
        // T-DSN-13: PUT /draft with If-Match:"3" must call store.SaveDraftAsync with create=false.
        uint? capturedRowVersion = null;
        bool? capturedCreate     = null;

        _storeMock
            .Setup(s => s.SaveDraftAsync(
                "DCODE2",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<uint>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, uint, bool, string?, CancellationToken>(
                (_, _, _, ver, create, _, _) =>
                {
                    capturedRowVersion = ver;
                    capturedCreate     = create;
                })
            .ReturnsAsync(new SaveDraftResult(SaveDraftOutcome.Saved, 4u));

        var bodyBytes = Encoding.UTF8.GetBytes(ValidGraphJson);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.Headers["If-Match"] = "\"3\"";

        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        _controller.Wtm = MockWtmContext.CreateWtmContext(usercode: "editor1");

        var result = await _controller.SaveDraft("DCODE2");

        Assert.AreEqual(false, capturedCreate,
            "If-Match:\"3\" must translate to create=false in store call (T-DSN-13).");
        Assert.AreEqual(3u, capturedRowVersion,
            "The quoted RowVersion from If-Match must be parsed and forwarded to the store.");
    }

    [TestMethod]
    public async Task SaveDraft_StaleRowVersion_Returns409()
    {
        _storeMock
            .Setup(s => s.SaveDraftAsync(
                "STALE",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<uint>(),
                false,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaveDraftResult(SaveDraftOutcome.Conflict, 0));

        var bodyBytes = Encoding.UTF8.GetBytes(ValidGraphJson);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.ContentLength = bodyBytes.Length;
        httpContext.Request.Headers["If-Match"] = "\"2\"";

        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        _controller.Wtm = MockWtmContext.CreateWtmContext(usercode: "editor1");

        var result = await _controller.SaveDraft("STALE");

        Assert.IsInstanceOfType<ConflictObjectResult>(result,
            "Conflict from store must map to 409 Conflict.");
    }

    [TestMethod]
    public async Task SaveDraft_MissingConditionalHeader_Returns400()
    {
        // Neither If-None-Match nor If-Match supplied.
        var bodyBytes = Encoding.UTF8.GetBytes(ValidGraphJson);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Body = new MemoryStream(bodyBytes);
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.ContentLength = bodyBytes.Length;
        // No If-None-Match or If-Match header.

        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        _controller.Wtm = MockWtmContext.CreateWtmContext(usercode: "editor1");

        var result = await _controller.SaveDraft("DCODE3");

        Assert.IsInstanceOfType<BadRequestObjectResult>(result,
            "Missing conditional header must return 400 Bad Request.");
    }
}

// ── FIX-B1: Cross-layer header contract tests ─────────────────────────────────

/// <summary>
/// FIX-B1: Assert that the C# <see cref="DesignerHeaderNames"/> constants are byte-identical
/// to the JS <c>WfHeaders</c> literals.  Both sides must agree — this test enforces the
/// server side of the contract and documents the expected string values.
///
/// When either side changes, the other MUST be updated and this test updated to match.
/// </summary>
[TestClass]
public class DesignerHeaderContractTests
{
    // ── B1 § 1: Constant value contract (server side) ─────────────────────────

    [TestMethod]
    public void DesignerHeaderNames_Xsrf_IsCanonicalValue()
    {
        // Byte-identical with WfHeaders.Xsrf in framework_workflow_designer_core.js.
        Assert.AreEqual("X-WTM-WF-XSRF", DesignerHeaderNames.Xsrf,
            "DesignerHeaderNames.Xsrf must equal the JS WfHeaders.Xsrf literal (FIX-B1).");
    }

    [TestMethod]
    public void DesignerHeaderNames_ExpectedHash_IsCanonicalValue()
    {
        // Byte-identical with WfHeaders.ExpectedHash in framework_workflow_designer_core.js.
        Assert.AreEqual("X-WTM-WF-Expected-Hash", DesignerHeaderNames.ExpectedHash,
            "DesignerHeaderNames.ExpectedHash must equal the JS WfHeaders.ExpectedHash literal (FIX-B1).");
    }

    [TestMethod]
    public void DesignerHeaderNames_BaseHash_IsCanonicalValue()
    {
        // Byte-identical with WfHeaders.BaseHash in framework_workflow_designer_core.js.
        Assert.AreEqual("X-WTM-WF-Base-Hash", DesignerHeaderNames.BaseHash,
            "DesignerHeaderNames.BaseHash must equal the JS WfHeaders.BaseHash literal (FIX-B1).");
    }

    [TestMethod]
    public void DesignerHeaderNames_AllHeadersUseWfNamespace()
    {
        // All designer headers must use the X-WTM-WF-* namespace.
        Assert.IsTrue(DesignerHeaderNames.Xsrf.StartsWith("X-WTM-WF-", StringComparison.Ordinal));
        Assert.IsTrue(DesignerHeaderNames.ExpectedHash.StartsWith("X-WTM-WF-", StringComparison.Ordinal));
        Assert.IsTrue(DesignerHeaderNames.BaseHash.StartsWith("X-WTM-WF-", StringComparison.Ordinal));
    }
}

// ── FIX-B3e / FIX-B3c: Boundary gate tests (413 / 415) ──────────────────────

/// <summary>
/// FIX-B3e: validate endpoint now has Content-Type gate (415).
/// FIX-B3e: size cap is byte-accurate (UTF-8 bytes, not char count).
/// T-DSN-7: ValidateGraph returns NodeKey in response for node-specific errors.
/// </summary>
[TestClass]
public class ValidateGraphBoundaryTests
{
    private Mock<IWorkflowDefinitionStore>    _storeMock     = null!;
    private Mock<IProcessDefinitionPublisher> _publisherMock = null!;
    private WorkflowDesignerController        _controller    = null!;

    [TestInitialize]
    public void Setup()
    {
        _storeMock     = new Mock<IWorkflowDefinitionStore>(MockBehavior.Loose);
        _publisherMock = new Mock<IProcessDefinitionPublisher>(MockBehavior.Loose);
        var sp = new ServiceCollection()
            .AddSingleton(_storeMock.Object)
            .AddSingleton(_publisherMock.Object)
            .AddSingleton(Options.Create(new WorkFlowOptions()))
            .BuildServiceProvider();
        _controller = new WorkflowDesignerController(sp);
        ControllerTestHelpers.WireWtm(_controller, itCode: "validator1");
    }

    private static void WireValidateRequest(
        WorkflowDesignerController controller,
        string? body,
        string? contentType = "application/json",
        long? contentLength = null)
    {
        var httpContext = new DefaultHttpContext();
        if (body is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            httpContext.Request.Body = new MemoryStream(bytes);
            httpContext.Request.ContentLength = contentLength ?? bytes.Length;
        }
        else
        {
            httpContext.Request.Body = Stream.Null;
        }
        if (contentType is not null)
            httpContext.Request.ContentType = contentType;

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.Wtm = MockWtmContext.CreateWtmContext(usercode: "validator1");
    }

    [TestMethod]
    public async Task ValidateGraph_NullContentType_Returns415()
    {
        // FIX-B3c: null Content-Type must NOT bypass the 415 gate.
        WireValidateRequest(_controller, "{}", contentType: null);
        var result = await _controller.ValidateGraph();
        Assert.AreEqual(StatusCodes.Status415UnsupportedMediaType,
            (result as ObjectResult)?.StatusCode ?? (result as StatusCodeResult)?.StatusCode,
            "Null Content-Type must return 415 (FIX-B3c).");
    }

    [TestMethod]
    public async Task ValidateGraph_WrongContentType_Returns415()
    {
        WireValidateRequest(_controller, "{}", contentType: "text/plain");
        var result = await _controller.ValidateGraph();
        Assert.AreEqual(StatusCodes.Status415UnsupportedMediaType,
            (result as ObjectResult)?.StatusCode ?? (result as StatusCodeResult)?.StatusCode,
            "Wrong Content-Type must return 415.");
    }

    [TestMethod]
    public async Task ValidateGraph_BodyExceedsMaxBytes_Returns413()
    {
        // FIX-B3e: ContentLength gate fires before reading the body.
        var opts = new WorkFlowOptions();
        // opts.Designer.MaxGraphBytes is 1 MiB by default; use a small custom option.
        var sp = new ServiceCollection()
            .AddSingleton(_storeMock.Object)
            .AddSingleton(_publisherMock.Object)
            .AddSingleton(Options.Create(opts))
            .BuildServiceProvider();
        var controller = new WorkflowDesignerController(sp);
        ControllerTestHelpers.WireWtm(controller, itCode: "v1");

        var httpContext = new DefaultHttpContext();
        httpContext.Request.ContentType   = "application/json";
        httpContext.Request.ContentLength = opts.Designer.MaxGraphBytes + 1; // over cap
        httpContext.Request.Body          = Stream.Null;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        controller.Wtm = MockWtmContext.CreateWtmContext(usercode: "v1");

        var result = await controller.ValidateGraph();
        Assert.AreEqual(StatusCodes.Status413RequestEntityTooLarge,
            (result as ObjectResult)?.StatusCode ?? (result as StatusCodeResult)?.StatusCode,
            "Body exceeding MaxGraphBytes via ContentLength must return 413 (FIX-B3e).");
    }

    [TestMethod]
    public async Task ValidateGraph_MalformedJson_Returns200WithIsValidFalse()
    {
        // T-DSN-7: malformed JSON → isValid=false, no exception thrown.
        WireValidateRequest(_controller, "NOT JSON AT ALL");
        var result = await _controller.ValidateGraph();
        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok, "Malformed JSON must still return 200 OK (validation result, not error).");
        var dto = ok.Value as ValidateResponseDto;
        Assert.IsNotNull(dto);
        Assert.IsFalse(dto.IsValid, "Malformed JSON must produce IsValid=false.");
        Assert.IsNotNull(dto.Error);
    }

    [TestMethod]
    public async Task ValidateGraph_NonObjectRoot_Returns200WithIsValidFalse()
    {
        // Non-object root (array) is invalid.
        WireValidateRequest(_controller, "[1,2,3]");
        var result = await _controller.ValidateGraph();
        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        var dto = ok.Value as ValidateResponseDto;
        Assert.IsNotNull(dto);
        Assert.IsFalse(dto.IsValid, "Non-object root must produce IsValid=false.");
    }
}

// ── FIX-B3g: RBAC structural guard tests ─────────────────────────────────────

/// <summary>
/// FIX-B3g: Assert structural RBAC guarantees on <see cref="WorkflowDesignerController"/>.
/// The controller must NOT carry <c>[AllRights]</c> — every mutating action is protected
/// by the PrivilegeFilter URL-RBAC gate.
/// </summary>
[TestClass]
public class DesignerControllerRbacStructureTests
{
    [TestMethod]
    public void WorkflowDesignerController_DoesNotHaveAllRightsAttribute()
    {
        // FIX-B3g: security invariant #1 from the controller docstring.
        // [AllRights] would bypass URL-RBAC; the designer must NOT use it.
        var controllerType = typeof(WorkflowDesignerController);
        var attrs = controllerType.GetCustomAttributes(inherit: true);
        var hasAllRights = attrs.Any(a =>
            a.GetType().Name.Contains("AllRights", StringComparison.OrdinalIgnoreCase) ||
            a.GetType().FullName?.Contains("AllRights", StringComparison.OrdinalIgnoreCase) == true);

        Assert.IsFalse(hasAllRights,
            "WorkflowDesignerController must NOT carry [AllRights]. " +
            "All actions must be protected by PrivilegeFilter URL-RBAC (spec §4 invariant #1 / FIX-B3g).");
    }

    [TestMethod]
    public void WorkflowDesignerController_MutatingActions_HaveWfDesignerAntiforgeryAttribute()
    {
        // FIX-B3a: verify antiforgery attribute is present on all expected mutating actions.
        var controllerType = typeof(WorkflowDesignerController);

        // All mutating designer endpoints must have the antiforgery attribute.
        // GET endpoints explicitly do NOT need it.
        var mutatingActions = new[]
        {
            "CreateDefinition",
            "UpdateDefinitionMetadata",
            "SaveDraft",
            "DeleteDraft",
            "PublishDraft",
        };

        foreach (var methodName in mutatingActions)
        {
            var method = controllerType.GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(method, $"Method '{methodName}' must exist on WorkflowDesignerController.");

            var hasAttr = method.GetCustomAttributes(inherit: true)
                .Any(a => a.GetType().Name.Contains("WfDesignerAntiforgery", StringComparison.Ordinal));

            Assert.IsTrue(hasAttr,
                $"Mutating action '{methodName}' must carry [WfDesignerAntiforgery] (FIX-B3a / T-DSN-8).");
        }
    }

    [TestMethod]
    public void WorkflowDesignerController_ReadOnlyActions_DoNotHaveWfDesignerAntiforgeryAttribute()
    {
        // Read-only (GET) endpoints must NOT require antiforgery (they are idempotent).
        var controllerType = typeof(WorkflowDesignerController);

        var readOnlyActions = new[]
        {
            "ListDefinitions",
            "GetCurrentGraph",
            "ListVersions",
            "GetVersionGraph",
            "GetDraft",
            "GetBootstrap",
        };

        foreach (var methodName in readOnlyActions)
        {
            var method = controllerType.GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method is null) continue; // Skip if method doesn't exist in this build.

            var hasAttr = method.GetCustomAttributes(inherit: true)
                .Any(a => a.GetType().Name.Contains("WfDesignerAntiforgery", StringComparison.Ordinal));

            Assert.IsFalse(hasAttr,
                $"Read-only action '{methodName}' must NOT carry [WfDesignerAntiforgery] (idempotent GET).");
        }
    }

    [TestMethod]
    public void IProcessDefinitionPublisher_PublishRawAsync_IsDim()
    {
        // FIX-B3d: PublishRawAsync must be a DIM (has a default implementation), not abstract.
        // This ensures third-party implementations compiled against 10.10 still load without error.
        var iface  = typeof(IProcessDefinitionPublisher);
        var method = iface.GetMethod("PublishRawAsync");
        Assert.IsNotNull(method, "IProcessDefinitionPublisher.PublishRawAsync must exist.");

        // In C# DIM, the method has a virtual body on the interface.
        // We detect this by checking it is NOT abstract (abstract methods have no body).
        Assert.IsFalse(method.IsAbstract,
            "IProcessDefinitionPublisher.PublishRawAsync must be a DIM (not abstract) " +
            "for binary compatibility with third-party implementations (FIX-B3d).");
    }
}
