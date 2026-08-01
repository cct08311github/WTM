#nullable enable
// Issue #824 cross-vendor review Finding 2 (MEDIUM-HIGH): FileAttachmentSaveChangesGuard.Guard
// runs BEFORE base.SaveChanges()/base.SaveChangesAsync() is ever entered, so its "same unit of
// work" exception (an id whose FileAttachment entry is state Added is trusted without a DB round
// trip — see FileAttachmentSaveChangesGuard's class doc comment) reads each entry's state as the
// CALLER left it. EF Core's own SavingChanges/SavingChangesAsync interceptor pipeline runs LATER,
// inside base.SaveChanges, and is explicitly designed to let a registered interceptor inspect and
// MUTATE the change tracker one more time before the actual SQL is built. A downstream
// interceptor that flips a FileAttachment entry from Added to Unchanged AFTER the guard already
// trusted it skips that entry's INSERT — and therefore its PK constraint check — while the
// DEPENDENT entity's own FK write still proceeds and succeeds, because the id genuinely already
// exists as a real row (just not the one this "Added" shell object claims to represent).
//
// This test constructs that exact shape and PROVES it is reachable — not merely theoretical: a
// SaveChangesInterceptor is a first-class, publicly documented EF Core extension point, requires
// no code changes to FileAttachmentSaveChangesGuard itself, and this repo's own test suite already
// demonstrates the identical technique for an unrelated purpose (see
// DoRealDeleteAsyncSubFileTests.cs's ProductSubFileContext(string, DBTypeEnum, params
// IInterceptor[])).
//
// IMPORTANT — what this test does NOT claim: no PRODUCTION interceptor anywhere in this
// repository does what FlipFileAttachmentToUnchangedInterceptor below does. This is a
// demonstration that the INVARIANT FileAttachmentSaveChangesGuard's class doc comment states —
// "a SaveChanges hook must not mutate a FileAttachment entry's state or a FileAttachment-typed FK
// scalar after the guard runs" — is load-bearing, not decorative: violating it is reachable by
// design (EF Core interceptors are meant to be able to do exactly this), and this test is the
// proof. It is not evidence of a live, exploitable hole in this codebase as shipped.

using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    /// <summary>
    /// Issue #824 Finding 2: fires on every SavingChanges(Async) call and flips any tracked
    /// <see cref="FileAttachment"/> entry that is currently <see cref="EntityState.Added"/> back
    /// to <see cref="EntityState.Unchanged"/> — simulating a hypothetical downstream interceptor
    /// that (deliberately or accidentally) neutralizes an in-flight insert AFTER
    /// FileAttachmentSaveChangesGuard already trusted it as "will be INSERTed, therefore
    /// PK-checked, in this same unit of work". No such interceptor exists in this repository's
    /// production code — see this file's header comment.
    /// </summary>
    internal sealed class FlipFileAttachmentToUnchangedInterceptor : SaveChangesInterceptor
    {
        public int FlippedCount { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Flip(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Flip(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        private void Flip(DbContext? context)
        {
            if (context == null)
            {
                return;
            }
            foreach (var entry in context.ChangeTracker.Entries<FileAttachment>().ToList())
            {
                if (entry.State == EntityState.Added)
                {
                    entry.State = EntityState.Unchanged;
                    FlippedCount++;
                }
            }
        }
    }

    /// <summary>Minimal context accepting EF Core interceptors — same shape as this test
    /// assembly's existing ProductSubFileContext (DoRealDeleteAsyncSubFileTests.cs).</summary>
    internal class InterceptorMutationContext824 : FrameworkContext
    {
        public DbSet<ProductWithOptionalPhoto> OptionalPhotoOwners { get; set; } = null!;

        private readonly IInterceptor[] _interceptors;

        public InterceptorMutationContext824(string cs, DBTypeEnum dbType) : base(cs, dbType)
        {
            _interceptors = Array.Empty<IInterceptor>();
        }

        public InterceptorMutationContext824(string cs, DBTypeEnum dbType, params IInterceptor[] interceptors) : base(cs, dbType)
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

    [TestClass]
    public class FileAttachmentSaveChangesGuardInterceptorMutationTests824
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"interceptormutation824_{Guid.NewGuid():N}";
            _keepAlive = new SqliteConnection(ConnectionString);
            _keepAlive.Open();

            using var ctx = new InterceptorMutationContext824(ConnectionString, DBTypeEnum.SQLite);
            ctx.Database.EnsureCreated();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive.Close();
            _keepAlive.Dispose();
        }

        [TestMethod]
        [Description("#824 Finding 2: a SaveChanges hook that flips a same-unit-of-work FileAttachment entry from Added to Unchanged AFTER the guard already trusted it lets the dependent's FK write land — reachable by design (a documented EF Core interceptor capability), not a claim about any in-tree production interceptor")]
        public void SavingChangesInterceptor_FlipsSameUnitOfWorkAttachmentToUnchanged_DependentFkStillLands()
        {
            // ── Arrange: a REAL victim FileAttachment, seeded normally (no interceptor). ──
            Guid victimFileId;
            using (var seedCtx = new InterceptorMutationContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_VICTIM");
                var victim = new FileAttachment
                {
                    ID = Guid.NewGuid(),
                    FileName = "victim.txt",
                    FileExt = "txt",
                    SaveMode = "database",
                    TenantCode = "TENANT_VICTIM",
                    UploadTime = DateTime.UtcNow,
                    Length = 4
                };
                seedCtx.Set<FileAttachment>().Add(victim);
                seedCtx.SaveChanges();
                victimFileId = victim.ID;
            }

            // Sanity: confirm the guard alone (no interceptor) still rejects the plain forge —
            // this isolates Finding 2 from a regression in the guard's own basic behaviour.
            using (var sanityDc = new InterceptorMutationContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                sanityDc.SetTenantCode("TENANT_ATTACKER");
                sanityDc.Set<ProductWithOptionalPhoto>().Add(new ProductWithOptionalPhoto
                {
                    ID = Guid.NewGuid(),
                    Name = "PlainForge",
                    PhotoId = victimFileId
                });
                Assert.ThrowsException<WalkingTec.Mvvm.Core.Exceptions.UnresolvableFileAttachmentReferenceException>(
                    () => sanityDc.SaveChanges(),
                    "sanity: without the interceptor, the guard must still reject a plain cross-tenant forge");
            }

            // ── Act: the interceptor-armed attacker context. Adds a SHELL FileAttachment object
            // carrying the VICTIM's real id (State=Added — never actually re-fetched, never
            // legitimately owned) purely to satisfy the guard's same-unit-of-work check, plus the
            // dependent row whose FK points at that same id. ──
            var interceptor = new FlipFileAttachmentToUnchangedInterceptor();
            var attackerDc = new InterceptorMutationContext824(ConnectionString, DBTypeEnum.SQLite, interceptor);
            attackerDc.SetTenantCode("TENANT_ATTACKER");

            attackerDc.Set<FileAttachment>().Add(new FileAttachment
            {
                ID = victimFileId, // the VICTIM's real id, not a new upload
                FileName = "shell.txt",
                FileExt = "txt",
                SaveMode = "database",
                TenantCode = "TENANT_ATTACKER",
                UploadTime = DateTime.UtcNow,
                Length = 4
            });
            var ownerId = Guid.NewGuid();
            attackerDc.Set<ProductWithOptionalPhoto>().Add(new ProductWithOptionalPhoto
            {
                ID = ownerId,
                Name = "InterceptorBypass",
                PhotoId = victimFileId
            });

            // The guard runs first (Guard() inside EmptyContext.SaveChanges, before base.SaveChanges),
            // sees the shell FileAttachment as Added, and trusts victimFileId via the same-unit-of-work
            // exception WITHOUT a DB round trip -- it never learns the id already belongs to a
            // different tenant. base.SaveChanges() then fires the SavingChanges interceptor pipeline,
            // where FlipFileAttachmentToUnchangedInterceptor flips the shell back to Unchanged --
            // skipping its INSERT (and the PK-violation that insert would otherwise have hit, since
            // victimFileId already exists) -- while the ProductWithOptionalPhoto INSERT still proceeds
            // and succeeds, because the REAL victim row satisfies the FK constraint.
            attackerDc.SaveChanges();

            Assert.AreEqual(1, interceptor.FlippedCount,
                "the interceptor must actually have flipped the shell FileAttachment entry for this test to prove anything");

            using var checkCtx = new InterceptorMutationContext824(ConnectionString, DBTypeEnum.SQLite);
            var reloadedOwner = checkCtx.Set<ProductWithOptionalPhoto>().First(x => x.ID == ownerId);
            Assert.AreEqual(victimFileId, reloadedOwner.PhotoId,
                "#824 Finding 2 (reachability proof, not an in-tree production hole): a SaveChanges " +
                "hook that mutates a same-unit-of-work FileAttachment entry AFTER the guard runs " +
                "defeats the guard's trust decision -- the dependent's cross-tenant FK lands anyway. " +
                "See FileAttachmentSaveChangesGuard's documented invariant: no hook may do this.");

            // The shell insert itself must NOT have created a duplicate/second FileAttachment row —
            // it was flipped to Unchanged, so no INSERT for it was ever sent; only the ORIGINAL
            // victim row exists.
            var attachmentRows = checkCtx.Set<FileAttachment>().IgnoreQueryFilters().Where(x => x.ID == victimFileId).ToList();
            Assert.AreEqual(1, attachmentRows.Count,
                "exactly one FileAttachment row must exist for this id — the shell's INSERT was skipped by the flip to Unchanged");
            Assert.AreEqual("TENANT_VICTIM", attachmentRows[0].TenantCode,
                "the surviving row must be the ORIGINAL victim row (its TenantCode is untouched) — the attacker's shell never actually wrote anything for this id");
        }
    }
}
