#nullable enable
// Issue #1024 (Phase 1, reduced) — adjudication established that the storage-identity attack
// (FileAttachment.Path is a forgeable storage locator; the tenant filter protects the metadata
// row, not the blob it points at) needs a writer that can set FileAttachment PERSISTED FIELDS —
// precondition (a). production-readiness.md's #987 section concludes (a) does not hold in the
// current tree, and this file is the missing test coverage FOR WHY: the whole thing rests on
// BaseCRUDVM.cs's own de-duplication loop —
//
//   //将所有TopBasePoco的属性赋空值，防止添加关联的重复内容
//   if (typeof(TModel) != typeof(FileAttachment))
//   {
//       foreach (var pro in pros)
//           if (pro.PropertyType.GetTypeInfo().IsSubclassOf(typeof(TopBasePoco)))
//               pro.SetValue(Entity, null);
//
// — whose own comment says its purpose is preventing duplicate related content on save, not
// confidentiality. Before this file, ZERO tests exercised it: a harmless-looking refactor could
// delete it and nothing would go red.
//
// What actually stops the attack, and where it genuinely differs between Add and Edit (empirically
// determined below, NOT assumed — the two paths turned out NOT to be symmetric):
//
// ADD is the real, mutation-provable case. DoAddPrepare's terminal step is a literal
// `DC.Set<TModel>().Add(Entity)` (`DbSet<T>.Add`) — EF Core's `Add()` performs a FULL recursive
// graph walk (`ChangeTracker.TrackGraph`) that discovers and tracks (as `Added`) EVERY reachable
// object, including a scalar `FileAttachment`-typed navigation property and, one level deeper,
// each `List<T>` sub-item's own such property. DoAddPrepareCore's nulling loop (~554-575, the one
// quoted above) runs BEFORE that `Add()` call and is what keeps a caller-supplied nested
// `Photo: { ID, Path, FileName, ... }` off the graph entirely. Proven by mutation below: deleting
// `pro.SetValue(Entity, null);` (~line 563) turns DoAdd_.../DoAddAsync_... red — the forged
// FileAttachment row is actually INSERTed, with the attacker's own Path, and EF Core's own FK
// fixup even wires the parent's PhotoId to point at it, entirely outside WtmFileProvider/the
// upload endpoint/any [AllRights] check.
//
// EDIT is NOT the same mechanism, and the equivalent line there is NOT mutation-provable — this
// was the original (wrong) assumption going into this file, corrected after an empirical
// diagnostic (`ChangeTracker.Entries()` inspected directly after `Entry(entity).State = Modified;
// ChangeTracker.DetectChanges();` on an entity with an unnulled nested `Photo`) showed the nested
// object is NEVER added to the change tracker at all — only the root entity appears. DoEdit's
// (and DoAdd's `AddEntity`-based sub-item path's) primitives — `EmptyContext.AddEntity`/
// `UpdateEntity`/`DeleteEntity`, all literally `this.Entry(entity).State = <state>;` — do NOT do
// the graph walk `DbSet.Add()`/`Attach()`/`Update()` do; they only touch the ONE entity instance
// passed in. So a nested `Photo` object hanging off `Entity` when `DC.UpdateEntity(Entity)` runs
// simply stays an inert, never-tracked CLR object — EF Core never looks at it, whether or not
// DoEditPreparePart1's `pro.SetValue(Entity, null);` (~line 902) ran first. Deleting that line was
// tested (see this file's own history/report) and does NOT turn the Edit tests below red. They
// are kept as regression/outcome pins — the invariant they assert is real and worth protecting
// against a FUTURE change (e.g. if `UpdateEntity` were ever reimplemented via `context.Update()`,
// which DOES cascade) — but they are not, today, evidence that DoEditPreparePart1's nulling loop
// is what holds the line; nothing in this codebase's Edit path currently needs it to.
// FileAttachmentSaveChangesGuard.cs (the #824/#985 SaveChanges-level backstop) does not change
// either conclusion: it only validates OTHER entities' FK-SCALAR properties that point AT a
// FileAttachment principal, never the CONTENT of a FileAttachment entity that is itself
// Added/Modified.
//
// Test model/fixture: reuses ProductWithOptionalPhoto + RequiredFkGateContext, declared in
// FkWriteGateSeventhRoundSqliteTests815.cs (same assembly/namespace) — an intentionally minimal
// TopBasePoco with an OPTIONAL scalar `FileAttachment? Photo` nav property and matching `Guid?
// PhotoId` FK, so the separate #815/#824/#828 FK-SCALAR resolution machinery (which this file is
// not testing) stays a complete no-op: every test below leaves PhotoId null and only ever posts
// the NAV property, isolating the nulling loop's own contribution from that machinery.
//
// See .claude/rules/testing.md's "EF InMemory limits" section for why this needs the
// FK-enforcing SQLite fixture, not EF InMemory: InMemory does not enforce the FileAttachments
// primary key, so a duplicate-id insert that should throw a real DbUpdateException would
// silently succeed there and hide exactly the failure mode this file exists to catch.

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    [TestClass]
    public class FileAttachmentNavPropertyPersistedFieldInvariantTests1024
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"fanavprop1024_{Guid.NewGuid():N}";
            _keepAlive = new SqliteConnection(ConnectionString);
            _keepAlive.Open();

            using var ctx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            ctx.Database.EnsureCreated();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive.Close();
            _keepAlive.Dispose();
        }

        private static FileAttachment ForgedNestedPhoto(Guid id, string tenantCode) => new FileAttachment
        {
            ID = id,
            FileName = "pwned.txt",
            FileExt = "txt",
            Path = "ATTACKER_CONTROLLED_PATH/../../etc/passwd",
            SaveMode = "local",
            UploadTime = DateTime.UtcNow,
            Length = 999,
            TenantCode = tenantCode
        };

        // ── Add path ────────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#1024 Phase 1: DoAdd on a model with a scalar FileAttachment nav property must never let a caller-supplied nested FileAttachment object (with its own attacker-chosen Path/FileName/etc.) reach the database, regardless of the entity's own (unrelated) scalar fields saving successfully. Named line: BaseCRUDVM.cs DoAddPrepareCore, `pro.SetValue(Entity, null);` (~line 563, inside `if (typeof(TModel) != typeof(FileAttachment))`).")]
        public void DoAdd_NestedFileAttachmentNavProperty_AttackerControlledPath_NeverPersisted()
        {
            var attackerDc = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<ProductWithOptionalPhoto>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            var newId = Guid.NewGuid();
            var forgedFileId = Guid.NewGuid();
            vm.Entity = new ProductWithOptionalPhoto
            {
                ID = newId,
                Name = "Widget",
                // PhotoId is deliberately left null — this test isolates the NAV property
                // nulling loop from the separate FK-scalar resolution gate (#815/#824/#828),
                // which is a no-op here and tested elsewhere.
                Photo = ForgedNestedPhoto(forgedFileId, "TENANT_ATTACKER")
            };

            vm.DoAdd();

            Assert.IsTrue(vm.MSD == null || vm.MSD.Count == 0,
                "#1024 Phase 1: the request itself must succeed (the entity's own fields are fine) — this is a silent content-injection risk, not a request-rejection one");

            using var checkCtx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);

            // Checked FIRST (before the weaker PhotoId signal below): this is precondition (a)
            // itself. If this fails, the attacker's forged Path just landed in the database with
            // zero involvement of WtmFileProvider/the upload endpoint.
            var persistedForgedFile = checkCtx.Set<FileAttachment>().IgnoreQueryFilters().FirstOrDefault(x => x.ID == forgedFileId);
            Assert.IsNull(persistedForgedFile,
                "#1024 Phase 1 (precondition (a)): a caller-supplied nested FileAttachment object must never reach the FileAttachments table — not even under the poster's own tenant, not even with a brand-new id.");

            var reloadedProduct = checkCtx.Set<ProductWithOptionalPhoto>().FirstOrDefault(x => x.ID == newId);
            Assert.IsNotNull(reloadedProduct, "#1024 Phase 1: the entity's own legitimate save must still succeed");
            // Secondary signal: without the nulling, EF Core's own navigation/FK fixup also wires
            // PhotoId to the forged Photo.ID automatically once both are tracked together — a
            // second, independent symptom of the same missing guard, not a separate mechanism.
            Assert.IsNull(reloadedProduct!.PhotoId, "#1024 Phase 1: no FK should end up pointing at the forged file either");
        }

        [TestMethod]
        [Description("#1024 Phase 1 async: same attack as the sync DoAdd version above, through DoAddAsync — shares DoAddPrepareCore, so the same named line applies.")]
        public async Task DoAddAsync_NestedFileAttachmentNavProperty_AttackerControlledPath_NeverPersisted()
        {
            var attackerDc = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var vm = new BaseCRUDVM<ProductWithOptionalPhoto>
            {
                Wtm = MockWtmContext.CreateWtmContext(attackerDc, "attacker")
            };
            var newId = Guid.NewGuid();
            var forgedFileId = Guid.NewGuid();
            vm.Entity = new ProductWithOptionalPhoto
            {
                ID = newId,
                Name = "Widget",
                Photo = ForgedNestedPhoto(forgedFileId, "TENANT_ATTACKER")
            };

            await vm.DoAddAsync();

            Assert.IsTrue(vm.MSD == null || vm.MSD.Count == 0,
                "#1024 Phase 1 async: the request itself must succeed");

            using var checkCtx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            var persistedForgedFile = checkCtx.Set<FileAttachment>().IgnoreQueryFilters().FirstOrDefault(x => x.ID == forgedFileId);
            Assert.IsNull(persistedForgedFile,
                "#1024 Phase 1 async (precondition (a)): a caller-supplied nested FileAttachment object must never reach the FileAttachments table via DoAddAsync either");
        }

        // ── Edit path ───────────────────────────────────────────────────────────────────────

        [TestMethod]
        [Description("#1024 Phase 1: DoEdit on a model with a scalar FileAttachment nav property must never let a caller-supplied nested FileAttachment object reach the database. NOT mutation-provable against a single named line (see this file's header comment): deleting DoEditPreparePart1's `pro.SetValue(Entity, null);` (~line 902) does not turn this red — the outcome holds because EmptyContext.UpdateEntity is `Entry(entity).State = Modified;`, which never cascade-tracks a reachable, previously-untracked navigation property the way DbSet.Add() does on the Add path. Kept as a regression/outcome pin, not a claim about that specific line.")]
        public void DoEdit_NestedFileAttachmentNavProperty_AttackerControlledPath_NeverPersisted()
        {
            Guid productId;
            using (var ctx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite))
            {
                ctx.SetTenantCode("TENANT_EDITOR");
                var product = new ProductWithOptionalPhoto { Name = "Widget" };
                ctx.Set<ProductWithOptionalPhoto>().Add(product);
                ctx.SaveChanges();
                productId = product.ID;
            }

            var editorDc = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            editorDc.SetTenantCode("TENANT_EDITOR");
            var vm = new BaseCRUDVM<ProductWithOptionalPhoto>
            {
                Wtm = MockWtmContext.CreateWtmContext(editorDc, "editor")
            };
            var forgedFileId = Guid.NewGuid();
            vm.Entity = new ProductWithOptionalPhoto
            {
                ID = productId,
                Name = "Renamed",
                Photo = ForgedNestedPhoto(forgedFileId, "TENANT_EDITOR")
            };

            vm.DoEdit(updateAllFields: true);

            Assert.IsFalse(vm.IsConcurrencyConflict, "#1024 Phase 1: must not surface as a concurrency conflict");
            Assert.IsTrue(vm.MSD == null || vm.MSD.Count == 0,
                "#1024 Phase 1: the request itself must succeed");

            using var checkCtx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);

            // Checked FIRST — see the DoAdd test above for why this ordering matters.
            var persistedForgedFile = checkCtx.Set<FileAttachment>().IgnoreQueryFilters().FirstOrDefault(x => x.ID == forgedFileId);
            Assert.IsNull(persistedForgedFile,
                "#1024 Phase 1 (precondition (a)): a caller-supplied nested FileAttachment object must never reach the FileAttachments table via DoEdit, even under the editor's own tenant");

            var reloadedProduct = checkCtx.Set<ProductWithOptionalPhoto>().FirstOrDefault(x => x.ID == productId);
            Assert.IsNotNull(reloadedProduct);
            Assert.AreEqual("Renamed", reloadedProduct!.Name, "#1024 Phase 1: the legitimate scalar rename in the same request must still be saved");
            Assert.IsNull(reloadedProduct.PhotoId, "#1024 Phase 1: no FK should end up pointing at the forged file either");
        }

        [TestMethod]
        [Description("#1024 Phase 1 async: same attack as the sync DoEdit version above, through DoEditAsync — shares DoEditPreparePart1, so the same 'not mutation-provable against a single line' finding applies (see the file header comment and the sync DoEdit test's Description).")]
        public async Task DoEditAsync_NestedFileAttachmentNavProperty_AttackerControlledPath_NeverPersisted()
        {
            Guid productId;
            using (var ctx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite))
            {
                ctx.SetTenantCode("TENANT_EDITOR");
                var product = new ProductWithOptionalPhoto { Name = "Widget" };
                ctx.Set<ProductWithOptionalPhoto>().Add(product);
                await ctx.SaveChangesAsync();
                productId = product.ID;
            }

            var editorDc = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            editorDc.SetTenantCode("TENANT_EDITOR");
            var vm = new BaseCRUDVM<ProductWithOptionalPhoto>
            {
                Wtm = MockWtmContext.CreateWtmContext(editorDc, "editor")
            };
            var forgedFileId = Guid.NewGuid();
            vm.Entity = new ProductWithOptionalPhoto
            {
                ID = productId,
                Name = "Renamed",
                Photo = ForgedNestedPhoto(forgedFileId, "TENANT_EDITOR")
            };

            await vm.DoEditAsync(updateAllFields: true);

            Assert.IsFalse(vm.IsConcurrencyConflict, "#1024 Phase 1 async: must not surface as a concurrency conflict");
            Assert.IsTrue(vm.MSD == null || vm.MSD.Count == 0,
                "#1024 Phase 1 async: the request itself must succeed");

            using var checkCtx = new RequiredFkGateContext(ConnectionString, DBTypeEnum.SQLite);
            var persistedForgedFile = checkCtx.Set<FileAttachment>().IgnoreQueryFilters().FirstOrDefault(x => x.ID == forgedFileId);
            Assert.IsNull(persistedForgedFile,
                "#1024 Phase 1 async (precondition (a)): a caller-supplied nested FileAttachment object must never reach the FileAttachments table via DoEditAsync either");
        }
    }
}
