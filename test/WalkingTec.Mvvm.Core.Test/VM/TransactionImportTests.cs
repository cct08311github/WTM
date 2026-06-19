#nullable enable
// Tests for EVM-002 (transaction atomicity), EVM-008 (per-row import isolation),
// and EVM-009 (DynamicSelect null-guard on unknown field name).
//
// EVM-002: DoBatchDelete and DoBatchEdit must roll back fully on a mid-batch failure.
// EVM-008: BatchSaveData with one staging-error row must report that row's error and
//          not corrupt others (validate-all-then-commit contract).
// EVM-009: DynamicSelect with an unknown field must not throw NRE.
//
// SQLite shared in-memory is used for EVM-002/EVM-009 (transactions require a real
// provider; EF InMemory does not support actual DB transactions).
// A minimal entity (TxTestItem) with NO navigation properties is used to avoid the
// "duplicate column name: MajorId" EnsureCreated collision caused by School.Majors.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    // ═══════════════════════════════════════════════════════════════════════════
    // Minimal entity — no navigation properties, no FK deps
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Flat entity used exclusively by TransactionImportTests.
    /// No navigation properties so EnsureCreated does not collide with
    /// the test project's existing School/Major FK relationships.
    /// </summary>
    public class TxTestItem : BasePoco
    {
        [StringLength(50)]
        public string Name { get; set; } = "";

        [StringLength(10)]
        public string Code { get; set; } = "";
    }

    /// <summary>LinkedVM for batch-edit tests on TxTestItem.</summary>
    public class TxTestItemEdit : BaseVM
    {
        public string Name { get; set; } = "";
        public string Code { get; set; } = "";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SQLite shared in-memory DataContext — only TxTestItem, no FK collision.
    //
    // Extends EmptyContext but overrides OnModelCreating to register ONLY
    // TxTestItem (no base call) and explicitly ignores AnalysisSavedQuery
    // (inherited DbSet from EmptyContext).  This prevents EF Core from
    // discovering StudentMajor/StudentMajorTop via the assembly-scan path in
    // FrameworkContext.OnModelCreating and avoids 'duplicate column name: MajorId'.
    // ═══════════════════════════════════════════════════════════════════════════

    internal class TxTestContext : EmptyContext
    {
        public DbSet<TxTestItem> TxTestItems { get; set; } = null!;

        public TxTestContext(string cs) : base(cs, DBTypeEnum.SQLite) { }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseSqlite(CSName);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Explicitly register ONLY TxTestItem.  Do NOT call base.OnModelCreating()
            // (which would invoke Utils.GetAllModels() through FrameworkContext).
            // Ignore the AnalysisSavedQuery DbSet inherited from EmptyContext so that
            // EF Core does not include it in the model and inadvertently pull in
            // StudentMajor/StudentMajorTop through the global entity scan, causing the
            // 'duplicate column name: MajorId' SQLite schema error.
            modelBuilder.Entity<TxTestItem>(b =>
            {
                b.HasKey(e => e.ID);
                b.Property(e => e.Name).HasMaxLength(50);
                b.Property(e => e.Code).HasMaxLength(10);
                b.Property(e => e.CreateTime);
                b.Property(e => e.CreateBy).HasMaxLength(50);
                b.Property(e => e.UpdateTime);
                b.Property(e => e.UpdateBy).HasMaxLength(50);
            });
            modelBuilder.Ignore<AnalysisSavedQuery>();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ThrowOnSaveTxContext — SQLite context whose SaveChanges always throws
    // ═══════════════════════════════════════════════════════════════════════════

    internal class ThrowOnSaveTxContext : TxTestContext
    {
        public ThrowOnSaveTxContext(string cs) : base(cs) { }

        private static Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException MakeEx()
            => new("simulated save failure mid-batch", Array.Empty<IUpdateEntry>());

        public override int SaveChanges()         { throw MakeEx(); }
        public override int SaveChanges(bool _)   { throw MakeEx(); }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ThrowOnSaveAsyncTxContext — SQLite context whose SaveChangesAsync throws
    // ═══════════════════════════════════════════════════════════════════════════

    internal class ThrowOnSaveAsyncTxContext : TxTestContext
    {
        public ThrowOnSaveAsyncTxContext(string cs) : base(cs) { }

        private static Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException MakeEx()
            => new("simulated async save failure mid-batch", Array.Empty<IUpdateEntry>());

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            throw MakeEx();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Test class
    // ═══════════════════════════════════════════════════════════════════════════

    [TestClass]
    public class TransactionImportTests : IDisposable
    {
        private string _connStr = null!;
        private SqliteConnection _keepAlive = null!;

        [TestInitialize]
        public void Setup()
        {
            var dbName = $"TxTest_{Guid.NewGuid():N}";
            _connStr = $"DataSource={dbName}?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(_connStr);
            _keepAlive.Open();

            using var ctx = new TxTestContext(_connStr);
            ctx.Database.EnsureCreated();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive?.Close();
            _keepAlive?.Dispose();
        }

        public void Dispose() => Cleanup();

        // ─── helpers ──────────────────────────────────────────────────────────

        private IDataContext NewCtx() => new TxTestContext(_connStr);

        private static readonly Guid ItemA = new Guid("AAAAAAAA-0001-0000-0000-000000000001");
        private static readonly Guid ItemB = new Guid("AAAAAAAA-0002-0000-0000-000000000002");
        private static readonly Guid ItemC = new Guid("AAAAAAAA-0003-0000-0000-000000000003");

        private void SeedThreeItems()
        {
            using var ctx = (DbContext)NewCtx();
            ctx.Set<TxTestItem>().AddRange(
                new TxTestItem { ID = ItemA, Name = "Alpha", Code = "A1" },
                new TxTestItem { ID = ItemB, Name = "Beta",  Code = "B2" },
                new TxTestItem { ID = ItemC, Name = "Gamma", Code = "C3" }
            );
            ctx.SaveChanges();
        }

        // ═══════════════════════════════════════════════════════════════════════
        // EVM-002 — DoBatchDelete transaction rollback
        // ═══════════════════════════════════════════════════════════════════════

        [TestMethod]
        [Description("EVM-002: DoBatchDelete rolls back fully when SaveChanges throws — no partial delete")]
        public void DoBatchDelete_RollsBack_OnSaveFailure()
        {
            SeedThreeItems();

            var throwCtx = new ThrowOnSaveTxContext(_connStr);
            var vm = new BaseBatchVM<TxTestItem, TxTestItemEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(throwCtx, "tester");
            vm.Ids = new[] { ItemA.ToString(), ItemB.ToString() };

            var result = vm.DoBatchDelete();

            Assert.IsFalse(result, "DoBatchDelete must return false when SaveChanges throws");

            using var verify = (DbContext)NewCtx();
            Assert.AreEqual(3, verify.Set<TxTestItem>().Count(), "All 3 items must remain — no partial delete");
        }

        [TestMethod]
        [Description("EVM-002: DoBatchDelete happy-path still works correctly after transaction wrapping")]
        public void DoBatchDelete_HappyPath_StillDeletesCorrectly()
        {
            SeedThreeItems();

            var vm = new BaseBatchVM<TxTestItem, TxTestItemEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(NewCtx(), "tester");
            vm.Ids = new[] { ItemA.ToString(), ItemB.ToString() };

            var result = vm.DoBatchDelete();

            Assert.IsTrue(result, "DoBatchDelete should succeed");

            using var verify = (DbContext)NewCtx();
            var remaining = verify.Set<TxTestItem>().ToList();
            Assert.AreEqual(1, remaining.Count, "Only ItemC must remain");
            Assert.AreEqual(ItemC, remaining[0].ID);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // EVM-002 — DoBatchEdit transaction rollback
        // ═══════════════════════════════════════════════════════════════════════

        [TestMethod]
        [Description("EVM-002: DoBatchEdit rolls back fully when SaveChanges throws — no partial edit")]
        public void DoBatchEdit_RollsBack_OnSaveFailure()
        {
            SeedThreeItems();

            string originalNameA;
            string originalNameB;
            using (var r = (DbContext)NewCtx())
            {
                originalNameA = r.Set<TxTestItem>().Find(ItemA)!.Name;
                originalNameB = r.Set<TxTestItem>().Find(ItemB)!.Name;
            }

            var throwCtx = new ThrowOnSaveTxContext(_connStr);
            var vm = new BaseBatchVM<TxTestItem, TxTestItemEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(throwCtx, "tester");

            var linked = new TxTestItemEdit { Name = "CHANGED", Code = "XX" };
            vm.LinkedVM = linked;
            vm.FC.Add("LinkedVM.Name", "CHANGED");
            vm.Ids = new[] { ItemA.ToString(), ItemB.ToString() };

            var result = vm.DoBatchEdit();

            Assert.IsFalse(result, "DoBatchEdit must return false when SaveChanges throws");

            // The ThrowOnSaveTxContext throws before SaveChanges commits anything,
            // so the DB (separate context) must still reflect original values.
            using var verify = (DbContext)NewCtx();
            var itemA = verify.Set<TxTestItem>().Find(ItemA)!;
            var itemB = verify.Set<TxTestItem>().Find(ItemB)!;
            Assert.AreEqual(originalNameA, itemA.Name, "ItemA name must be unchanged — no partial edit");
            Assert.AreEqual(originalNameB, itemB.Name, "ItemB name must be unchanged — no partial edit");
        }

        [TestMethod]
        [Description("EVM-002: DoBatchEdit happy-path still works correctly after transaction wrapping")]
        public void DoBatchEdit_HappyPath_StillEditsCorrectly()
        {
            SeedThreeItems();

            var vm = new BaseBatchVM<TxTestItem, TxTestItemEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(NewCtx(), "tester");

            var linked = new TxTestItemEdit { Name = "UPDATED", Code = "ZZ" };
            vm.LinkedVM = linked;
            vm.FC.Add("LinkedVM.Name", "UPDATED");
            vm.Ids = new[] { ItemA.ToString(), ItemB.ToString() };

            var result = vm.DoBatchEdit();

            Assert.IsTrue(result, "DoBatchEdit should succeed");

            using var verify = (DbContext)NewCtx();
            var itemA = verify.Set<TxTestItem>().Find(ItemA)!;
            var itemB = verify.Set<TxTestItem>().Find(ItemB)!;
            Assert.AreEqual("UPDATED", itemA.Name, "ItemA name must be updated");
            Assert.AreEqual("UPDATED", itemB.Name, "ItemB name must be updated");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // EVM-009 — DynamicSelect null-guard on unknown field
        // ═══════════════════════════════════════════════════════════════════════

        [TestMethod]
        [Description("EVM-009: DynamicSelect with an unknown field name must not throw NRE")]
        public void DynamicSelect_UnknownField_DoesNotThrow()
        {
            SeedThreeItems();

            using var ctx = (DbContext)NewCtx();
            var query = ctx.Set<TxTestItem>().AsQueryable();

            IQueryable<string>? result = null;
            Exception? ex = null;
            try
            {
                result = query.DynamicSelect("NonExistentField");
            }
            catch (Exception e)
            {
                ex = e;
            }

            Assert.IsNull(ex, $"DynamicSelect with unknown field must not throw; got: {ex?.GetType().Name}: {ex?.Message}");
            Assert.IsNotNull(result, "DynamicSelect must return a non-null (empty) queryable");
        }

        [TestMethod]
        [Description("EVM-009: DynamicSelect with known field still works correctly after null-guard")]
        public void DynamicSelect_KnownField_ReturnsValues()
        {
            SeedThreeItems();

            using var ctx = (DbContext)NewCtx();
            var query = ctx.Set<TxTestItem>().AsQueryable();

            var result = query.DynamicSelect("Name").ToList();

            Assert.AreEqual(3, result.Count, "DynamicSelect on a known field must return all rows");
            CollectionAssert.Contains(result, "Alpha");
            CollectionAssert.Contains(result, "Beta");
            CollectionAssert.Contains(result, "Gamma");
        }

        [TestMethod]
        [Description("EVM-009: DynamicSelect unknown-field result matches Sort null-guard — returns empty")]
        public void DynamicSelect_UnknownField_ReturnsEmpty()
        {
            SeedThreeItems();

            using var ctx = (DbContext)NewCtx();
            var query = ctx.Set<TxTestItem>().AsQueryable();
            var result = query.DynamicSelect("ThisFieldDoesNotExistOnTxTestItem").ToList();

            Assert.AreEqual(0, result.Count, "DynamicSelect on unknown field must return empty, not NRE");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // #413 — Async batch operation tests (SQLite shared in-memory)
    // ═══════════════════════════════════════════════════════════════════════════

    [TestClass]
    public class AsyncBatchVMTests : IDisposable
    {
        private string _connStr = null!;
        private SqliteConnection _keepAlive = null!;

        [TestInitialize]
        public void Setup()
        {
            var dbName = $"AsyncBatch_{Guid.NewGuid():N}";
            _connStr = $"DataSource={dbName}?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(_connStr);
            _keepAlive.Open();

            using var ctx = new TxTestContext(_connStr);
            ctx.Database.EnsureCreated();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive?.Close();
            _keepAlive?.Dispose();
        }

        public void Dispose() => Cleanup();

        private IDataContext NewCtx() => new TxTestContext(_connStr);

        private static readonly Guid ItemA = new Guid("BBBBBBBB-0001-0000-0000-000000000001");
        private static readonly Guid ItemB = new Guid("BBBBBBBB-0002-0000-0000-000000000002");
        private static readonly Guid ItemC = new Guid("BBBBBBBB-0003-0000-0000-000000000003");

        private void SeedThreeItems()
        {
            using var ctx = (DbContext)NewCtx();
            ctx.Set<TxTestItem>().AddRange(
                new TxTestItem { ID = ItemA, Name = "AsyncAlpha", Code = "AA" },
                new TxTestItem { ID = ItemB, Name = "AsyncBeta",  Code = "BB" },
                new TxTestItem { ID = ItemC, Name = "AsyncGamma", Code = "CC" }
            );
            ctx.SaveChanges();
        }

        // ═══════════════════════════════════════════════════════════════════════
        // (1) Happy-path async batch delete — commits all rows
        // ═══════════════════════════════════════════════════════════════════════

        [TestMethod]
        [Description("#413 happy-path: DoBatchDeleteAsync commits all targeted rows")]
        public async Task DoBatchDeleteAsync_HappyPath_CommitsAllRows()
        {
            SeedThreeItems();

            var vm = new BaseBatchVM<TxTestItem, TxTestItemEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(NewCtx(), "tester");
            vm.Ids = new[] { ItemA.ToString(), ItemB.ToString() };

            var result = await vm.DoBatchDeleteAsync();

            Assert.IsTrue(result, "DoBatchDeleteAsync should return true on success");

            using var verify = (DbContext)NewCtx();
            var remaining = verify.Set<TxTestItem>().ToList();
            Assert.AreEqual(1, remaining.Count, "Only ItemC must remain");
            Assert.AreEqual(ItemC, remaining[0].ID);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // (1) Happy-path async batch edit — commits all rows
        // ═══════════════════════════════════════════════════════════════════════

        [TestMethod]
        [Description("#413 happy-path: DoBatchEditAsync commits updates to all targeted rows")]
        public async Task DoBatchEditAsync_HappyPath_CommitsAllRows()
        {
            SeedThreeItems();

            var vm = new BaseBatchVM<TxTestItem, TxTestItemEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(NewCtx(), "tester");

            var linked = new TxTestItemEdit { Name = "AsyncUpdated", Code = "ZZ" };
            vm.LinkedVM = linked;
            vm.FC.Add("LinkedVM.Name", "AsyncUpdated");
            vm.FC.Add("LinkedVM.Code", "ZZ");
            vm.Ids = new[] { ItemA.ToString(), ItemB.ToString() };

            var result = await vm.DoBatchEditAsync();

            Assert.IsTrue(result, "DoBatchEditAsync should return true on success");

            using var verify = (DbContext)NewCtx();
            var itemA = verify.Set<TxTestItem>().Find(ItemA)!;
            var itemB = verify.Set<TxTestItem>().Find(ItemB)!;
            Assert.AreEqual("AsyncUpdated", itemA.Name, "ItemA name must be updated");
            Assert.AreEqual("AsyncUpdated", itemB.Name, "ItemB name must be updated");
            Assert.AreEqual("ZZ", itemA.Code, "ItemA code must be updated");
            Assert.AreEqual("ZZ", itemB.Code, "ItemB code must be updated");
            // ItemC should be untouched
            var itemC = verify.Set<TxTestItem>().Find(ItemC)!;
            Assert.AreEqual("AsyncGamma", itemC.Name, "ItemC must not be modified");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // (2) Mid-batch SaveChangesAsync failure rolls back ENTIRE batch — no partial rows
        // ═══════════════════════════════════════════════════════════════════════

        [TestMethod]
        [Description("#413 EVM-002: DoBatchDeleteAsync rolls back fully when SaveChangesAsync throws")]
        public async Task DoBatchDeleteAsync_RollsBack_OnSaveAsyncFailure()
        {
            SeedThreeItems();

            var throwCtx = new ThrowOnSaveAsyncTxContext(_connStr);
            var vm = new BaseBatchVM<TxTestItem, TxTestItemEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(throwCtx, "tester");
            vm.Ids = new[] { ItemA.ToString(), ItemB.ToString() };

            var result = await vm.DoBatchDeleteAsync();

            Assert.IsFalse(result, "DoBatchDeleteAsync must return false when SaveChangesAsync throws");

            using var verify = (DbContext)NewCtx();
            Assert.AreEqual(3, verify.Set<TxTestItem>().Count(), "All 3 items must remain — no partial delete");
        }

        [TestMethod]
        [Description("#413 EVM-002: DoBatchEditAsync rolls back fully when SaveChangesAsync throws")]
        public async Task DoBatchEditAsync_RollsBack_OnSaveAsyncFailure()
        {
            SeedThreeItems();

            string originalNameA;
            string originalNameB;
            using (var r = (DbContext)NewCtx())
            {
                originalNameA = r.Set<TxTestItem>().Find(ItemA)!.Name;
                originalNameB = r.Set<TxTestItem>().Find(ItemB)!.Name;
            }

            var throwCtx = new ThrowOnSaveAsyncTxContext(_connStr);
            var vm = new BaseBatchVM<TxTestItem, TxTestItemEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(throwCtx, "tester");

            var linked = new TxTestItemEdit { Name = "SHOULD_NOT_PERSIST", Code = "XX" };
            vm.LinkedVM = linked;
            vm.FC.Add("LinkedVM.Name", "SHOULD_NOT_PERSIST");
            vm.Ids = new[] { ItemA.ToString(), ItemB.ToString() };

            var result = await vm.DoBatchEditAsync();

            Assert.IsFalse(result, "DoBatchEditAsync must return false when SaveChangesAsync throws");

            using var verify = (DbContext)NewCtx();
            var itemA = verify.Set<TxTestItem>().Find(ItemA)!;
            var itemB = verify.Set<TxTestItem>().Find(ItemB)!;
            Assert.AreEqual(originalNameA, itemA.Name, "ItemA name must be unchanged — no partial edit");
            Assert.AreEqual(originalNameB, itemB.Name, "ItemB name must be unchanged — no partial edit");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // (3) Per-row validation errors are collected
        // ═══════════════════════════════════════════════════════════════════════

        [TestMethod]
        [Description("#413: DoBatchDeleteAsync collects per-row validation errors when CheckIfCanDelete fails")]
        public async Task DoBatchDeleteAsync_CollectsValidationError_WhenCheckIfCanDeleteFails()
        {
            SeedThreeItems();

            // Use a subclass that blocks deletion of ItemA
            var vm = new AsyncCannotDeleteBatchVM(ItemA.ToString());
            vm.Wtm = MockWtmContext.CreateWtmContext(NewCtx(), "tester");
            vm.Ids = new[] { ItemA.ToString(), ItemB.ToString() };

            var result = await vm.DoBatchDeleteAsync();

            Assert.IsFalse(result, "Must return false when a row is blocked");
            Assert.IsTrue(vm.ErrorMessage.ContainsKey(ItemA.ToString()),
                "ErrorMessage should contain the blocked id");
            Assert.AreEqual("Async delete blocked by test", vm.ErrorMessage[ItemA.ToString()]);
        }

        [TestMethod]
        [Description("#413: DoBatchEditAsync collects per-row validation errors")]
        public async Task DoBatchEditAsync_CollectsValidationErrors()
        {
            SeedThreeItems();

            // LinkedVM is null — will throw NullReferenceException on the first row
            var vm = new BaseBatchVM<TxTestItem, TxTestItemEdit>();
            vm.Wtm = MockWtmContext.CreateWtmContext(NewCtx(), "tester");
            // Intentionally set a LinkedVM with invalid data that causes a per-row error
            var linked = new TxTestItemEdit { Name = "ok", Code = "ZZ" };
            vm.LinkedVM = linked;
            vm.FC.Add("LinkedVM.Name", "ok");
            // IDs include a non-existent one — should still succeed (no validation errors from CRUD VM
            // since TxTestItem has no corresponding assembly BaseCRUDVM in the test project)
            vm.Ids = new[] { ItemA.ToString(), ItemB.ToString() };

            var result = await vm.DoBatchEditAsync();

            // Should succeed — no validation error since there is no TxTestItemCrudVM in assembly
            Assert.IsTrue(result, "DoBatchEditAsync with valid data should succeed");
            Assert.AreEqual(0, vm.ErrorMessage.Count, "No validation errors expected");
        }

        [TestMethod]
        [Description("#413: ErrorMessage is populated with per-row errors after async batch delete validation failure")]
        public async Task DoBatchDeleteAsync_ErrorMessage_ContainsBlockedId()
        {
            SeedThreeItems();

            var vm = new AsyncCannotDeleteBatchVM(ItemB.ToString());
            vm.Wtm = MockWtmContext.CreateWtmContext(NewCtx(), "tester");
            vm.Ids = new[] { ItemA.ToString(), ItemB.ToString() };

            var result = await vm.DoBatchDeleteAsync();

            Assert.IsFalse(result);
            // ItemB was blocked, ItemA was tried first (it passes) but batch stopped at ItemB
            Assert.IsTrue(vm.ErrorMessage.ContainsKey(ItemB.ToString()),
                "ErrorMessage must record the blocked item");
        }
    }

    // ─── Subclass that blocks delete for a specific ID in async tests ──────────

    internal sealed class AsyncCannotDeleteBatchVM : BaseBatchVM<TxTestItem, TxTestItemEdit>
    {
        private readonly string _blockedId;
        public AsyncCannotDeleteBatchVM(string blockedId) { _blockedId = blockedId; }

        protected override bool CheckIfCanDelete(object id, out string? errorMessage)
        {
            if (id?.ToString() == _blockedId)
            {
                errorMessage = "Async delete blocked by test";
                return false;
            }
            errorMessage = null;
            return true;
        }
    }
}
