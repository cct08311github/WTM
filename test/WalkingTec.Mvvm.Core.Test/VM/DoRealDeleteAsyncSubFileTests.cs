#nullable enable
// Regression test for Issue #504:
//   DoRealDeleteAsync did not re-fetch ISubFile navigation collections when
//   they were null (not eager-loaded), causing physical files to be orphaned.
//   This test calls DoRealDeleteAsync with an entity whose Attachments nav
//   is intentionally NOT loaded and asserts that the FileAttachment row is
//   deleted — which only happens when the sub-file FileIds were collected
//   via the re-fetch path.
//
//   SQLite shared-memory is used because the InMemory provider cannot translate
//   the Include() + AsNoTracking() re-fetch inside DoRealDeleteAsync.
//   A minimal ProductSubFileContext (only Product + ProductAttachment DbSets)
//   is used so EnsureCreated() does not trip over unrelated conflicting FKs
//   from the full DataContext schema.
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    // ── Minimal context used by this test class ───────────────────────────────────
    // Declares only Product and ProductAttachment so EnsureCreated() generates a
    // clean schema without collisions from the full DataContext entity graph.

    // Extends FrameworkContext directly (not the full test DataContext) to avoid
    // conflicting FK/column names from the unrelated test entity graph.
    internal class ProductSubFileContext : FrameworkContext
    {
        public DbSet<Product> Products { get; set; } = null!;
        public DbSet<ProductAttachment> ProductAttachments { get; set; } = null!;

        private readonly IInterceptor[] _interceptors;

        public ProductSubFileContext(string cs, DBTypeEnum dbType) : base(cs, dbType)
        {
            _interceptors = Array.Empty<IInterceptor>();
        }

        /// <summary>
        /// Issue #828: accepts optional EF Core interceptors for test-seam fault injection —
        /// e.g. simulating a FileAttachment resolution query failure (provider parameter cap,
        /// timeout, transient connection loss, ...) that has nothing to do with whether the
        /// candidate ids are legitimate. Additive only: every existing call site using the
        /// two-arg constructor above is unaffected.
        /// </summary>
        public ProductSubFileContext(string cs, DBTypeEnum dbType, params IInterceptor[] interceptors) : base(cs, dbType)
        {
            _interceptors = interceptors;
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            base.OnConfiguring(optionsBuilder);
            if (_interceptors.Length > 0)
            {
                optionsBuilder.AddInterceptors(_interceptors);
            }
        }
    }

    /// <summary>
    /// Regression tests for Issue #504 — DoRealDeleteAsync orphaned physical files
    /// when the ISubFile navigation property was not eager-loaded.
    /// </summary>
    [TestClass]
    public class DoRealDeleteAsyncSubFileTests
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"subfile_{Guid.NewGuid():N}";
            _keepAlive = new SqliteConnection(ConnectionString);
            _keepAlive.Open();

            using var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite);
            ctx.Database.EnsureCreated();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive.Close();
            _keepAlive.Dispose();
        }

        /// <summary>
        /// DoRealDeleteAsync must collect FileIds from an ISubFile collection
        /// even when the navigation property is null (not eager-loaded) on the entity.
        ///
        /// Before the fix: subs == null was not re-fetched, so no FileIds were
        /// collected and the FileAttachment row was never deleted.
        /// After the fix: the async re-fetch path loads the sub-files and the
        /// FileAttachment row is removed from the database (WtmFileProvider.DeleteFile).
        /// </summary>
        [TestMethod]
        [Description("#504 DoRealDeleteAsync must re-fetch unloaded ISubFile nav to avoid orphaned physical files")]
        public async Task DoRealDeleteAsync_UnloadedSubFileNav_CollectsFileIdsAndDeletesAttachment()
        {
            // ── Arrange: seed a Product with one ProductAttachment + FileAttachment ──

            Guid productId;
            Guid fileId;

            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var fa = new FileAttachment
                {
                    FileName = "test.pdf",
                    FileExt = ".pdf",
                    SaveMode = "local",
                    Path = "/uploads/test.pdf",
                    UploadTime = DateTime.Now
                };
                ctx.Set<FileAttachment>().Add(fa);
                ctx.SaveChanges();
                fileId = fa.ID;

                var product = new Product { Name = "Widget" };
                ctx.Set<Product>().Add(product);
                ctx.SaveChanges();
                productId = product.ID;

                var attachment = new ProductAttachment
                {
                    ProductId = productId,
                    FileId = fileId,
                    Order = 1
                };
                ctx.Set<ProductAttachment>().Add(attachment);
                ctx.SaveChanges();
            }

            // ── Arrange: load entity WITHOUT the Attachments navigation ──
            Product entityWithoutNav;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                // Deliberately NOT including Attachments — this is the orphan scenario.
                entityWithoutNav = ctx.Set<Product>()
                    .AsNoTracking()
                    .First(x => x.ID == productId);
            }

            // Sanity: confirm the nav is null (not loaded).
            Assert.IsNull(entityWithoutNav.Attachments,
                "Precondition failed: Attachments should be null (not eager-loaded).");

            // ── Act: call DoRealDeleteAsync with the nav-unloaded entity ──
            var vm = new BaseCRUDVM<Product>
            {
                Wtm = MockWtmContext.CreateWtmContext(
                    new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite), "testuser")
            };
            vm.Entity = entityWithoutNav;
            await vm.DoRealDeleteAsync();

            // ── Assert: FileAttachment row was deleted, proving FileId was collected ──
            // If the re-fetch was missing (pre-fix), subs would be null, no FileId
            // collected, and the FileAttachment record would still exist.
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var remainingFile = ctx.Set<FileAttachment>()
                    .IgnoreQueryFilters()
                    .FirstOrDefault(x => x.ID == fileId);

                Assert.IsNull(remainingFile,
                    "FileAttachment must be deleted when DoRealDeleteAsync re-fetches the " +
                    "unloaded ISubFile navigation. If it still exists, the re-fetch fix " +
                    "(Issue #504) was not applied.");

                // Also confirm the product itself is gone.
                var remainingProduct = ctx.Set<Product>()
                    .IgnoreQueryFilters()
                    .FirstOrDefault(x => x.ID == productId);
                Assert.IsNull(remainingProduct, "Product entity must be physically deleted.");
            }
        }

        /// <summary>
        /// DoRealDeleteAsync with an EAGER-LOADED ISubFile collection must still
        /// work correctly — no regression on the happy path.
        /// </summary>
        [TestMethod]
        [Description("#504 DoRealDeleteAsync still works correctly when sub-file nav IS eager-loaded")]
        public async Task DoRealDeleteAsync_LoadedSubFileNav_CollectsFileIds()
        {
            // ── Arrange ──
            Guid productId;
            Guid fileId;

            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var fa = new FileAttachment
                {
                    FileName = "eager.pdf",
                    FileExt = ".pdf",
                    SaveMode = "local",
                    Path = "/uploads/eager.pdf",
                    UploadTime = DateTime.Now
                };
                ctx.Set<FileAttachment>().Add(fa);
                ctx.SaveChanges();
                fileId = fa.ID;

                var product = new Product { Name = "EagerProduct" };
                ctx.Set<Product>().Add(product);
                ctx.SaveChanges();
                productId = product.ID;

                var attachment = new ProductAttachment
                {
                    ProductId = productId,
                    FileId = fileId,
                    Order = 1
                };
                ctx.Set<ProductAttachment>().Add(attachment);
                ctx.SaveChanges();
            }

            // Load WITH the Attachments nav (eager-load path).
            Product entityWithNav;
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                entityWithNav = ctx.Set<Product>()
                    .Include(x => x.Attachments)
                    .AsNoTracking()
                    .First(x => x.ID == productId);
            }

            Assert.IsNotNull(entityWithNav.Attachments, "Precondition: Attachments must be loaded.");
            Assert.AreEqual(1, entityWithNav.Attachments!.Count, "Precondition: exactly one attachment.");

            // ── Act ──
            var vm = new BaseCRUDVM<Product>
            {
                Wtm = MockWtmContext.CreateWtmContext(
                    new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite), "testuser")
            };
            vm.Entity = entityWithNav;
            await vm.DoRealDeleteAsync();

            // ── Assert ──
            using (var ctx = new ProductSubFileContext(ConnectionString, DBTypeEnum.SQLite))
            {
                var remainingFile = ctx.Set<FileAttachment>()
                    .IgnoreQueryFilters()
                    .FirstOrDefault(x => x.ID == fileId);
                Assert.IsNull(remainingFile, "FileAttachment must be deleted on the eager-load path.");

                var remainingProduct = ctx.Set<Product>()
                    .IgnoreQueryFilters()
                    .FirstOrDefault(x => x.ID == productId);
                Assert.IsNull(remainingProduct, "Product entity must be physically deleted.");
            }
        }
    }
}
