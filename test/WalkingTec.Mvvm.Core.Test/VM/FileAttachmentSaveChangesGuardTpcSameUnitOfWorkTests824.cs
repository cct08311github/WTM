#nullable enable
// Issue #824, adversarial review of PR #978, Finding 6 (narrower, fixed): CollectCandidates'
// "same unit of work" trust exception used to admit any Added FileAttachment-derived entry keyed
// by id ALONE (a flat HashSet<Guid>). That argument -- "it will be INSERTed, therefore PK-checked,
// in this same transaction" -- holds for TPH and TPT, which share (or link) ONE physical table
// across the whole inheritance hierarchy, so an Added shell object carrying a REAL, EXISTING
// derived row's id collides on INSERT (a PK violation). It does NOT hold for TPC
// (Table-Per-Concrete-Type): each concrete type, including the base, gets its OWN separate table
// and PK space. An Added base-typed FileAttachment "shell" with ID = <a real SignedFile's id>
// inserts into the UNRELATED base table with no collision at all, while the actual FK constraint
// on a dependent whose relationship principal is the DERIVED type (SignedFile) references the
// DERIVED type's own table -- already satisfied by the victim's pre-existing row there, regardless
// of the shell's own insert outcome. The old trust check could not tell "an Added SignedFile" from
// "an Added FileAttachment shell claiming a SignedFile's id" apart -- both satisfy
// `entry.Entity is FileAttachment`.
//
// Fix: the trust exception now also requires the FK's OWN declared PrincipalEntityType.ClrType to
// accept the Added entry's ACTUAL runtime type (Type.IsAssignableFrom) -- a base-typed shell is
// not assignable to a derived-typed principal requirement, so it no longer qualifies, and falls
// through to the same tenant-scoped resolution query every other candidate gets.

using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Exceptions;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    // TPC: SignedFileTpc824 gets its OWN table (like TPT), but the mapping strategy itself --
    // UseTpcMappingStrategy(), forced on the ROOT FileAttachment type below -- is what makes the
    // base type's table INDEPENDENT of the derived type's table (no shared/linking PK space),
    // unlike TPT where the tables are linked by a shared primary key.
    [Table("zz_signed_file_tpc_824")]
    public class SignedFileTpc824 : FileAttachment
    {
        public string? SignerName { get; set; }
    }

    [Table("zz_invoice_tpc_824")]
    public class InvoiceTpc824 : TopBasePoco
    {
        public Guid SignedFileId { get; set; }
        public SignedFileTpc824? SignedFile { get; set; }
    }

    internal class DerivedPrincipalTpcContext824 : FrameworkContext
    {
        public DbSet<InvoiceTpc824> Invoices { get; set; } = null!;
        public DbSet<SignedFileTpc824> SignedFiles { get; set; } = null!;

        public DerivedPrincipalTpcContext824(string cs, DBTypeEnum dbType) : base(cs, dbType) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // Configured on the ROOT entity type, same EF Core requirement as TPH/TPT (see
            // FileAttachmentForeignKeyPredicateDerivedPrincipalTests824.cs's matching comment).
            modelBuilder.Entity<FileAttachment>().UseTpcMappingStrategy();
        }
    }

    [TestClass]
    public class FileAttachmentSaveChangesGuardTpcSameUnitOfWorkTests824
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"tpcsameunitofwork824_{Guid.NewGuid():N}";
            _keepAlive = new SqliteConnection(ConnectionString);
            _keepAlive.Open();

            using var ctx = new DerivedPrincipalTpcContext824(ConnectionString, DBTypeEnum.SQLite);
            ctx.Database.EnsureCreated();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive.Close();
            _keepAlive.Dispose();
        }

        [TestMethod]
        [Description("#824 Finding 6: an Added base-typed FileAttachment 'shell' carrying a REAL SignedFileTpc824's id must NOT be trusted as a same-unit-of-work attachment for a dependent whose FK principal is specifically SignedFileTpc824 -- under TPC, the shell's own INSERT does not collide with (or say anything about) the derived type's separate table, so the same-unit-of-work exception must not apply and the write must still be rejected")]
        public void SaveChanges_TpcBaseShellAddedInSameUnitOfWork_DoesNotBypassResolution_RejectedByGuard()
        {
            Guid victimSignedFileId;
            using (var seedCtx = new DerivedPrincipalTpcContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.SetTenantCode("TENANT_VICTIM");
                var victim = new SignedFileTpc824
                {
                    ID = Guid.NewGuid(),
                    FileName = "victim.txt",
                    FileExt = "txt",
                    SaveMode = "database",
                    TenantCode = "TENANT_VICTIM",
                    UploadTime = DateTime.UtcNow,
                    Length = 4,
                    SignerName = "Victim",
                };
                seedCtx.Set<SignedFileTpc824>().Add(victim);
                seedCtx.SaveChanges();
                victimSignedFileId = victim.ID;
            }

            var attackerDc = new DerivedPrincipalTpcContext824(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");

            // The attack: a base-TYPED FileAttachment shell claiming the VICTIM's real
            // SignedFileTpc824 id, Added in the SAME unit of work as the dependent that
            // references it. Under TPC this shell inserts into the (separate) base
            // FileAttachments table -- no PK collision with SignedFileTpc824's own table at all.
            attackerDc.Set<FileAttachment>().Add(new FileAttachment
            {
                ID = victimSignedFileId,
                FileName = "shell.txt",
                FileExt = "txt",
                SaveMode = "database",
                TenantCode = "TENANT_ATTACKER",
                UploadTime = DateTime.UtcNow,
                Length = 4,
            });
            var newInvoiceId = Guid.NewGuid();
            attackerDc.Set<InvoiceTpc824>().Add(new InvoiceTpc824
            {
                ID = newInvoiceId,
                SignedFileId = victimSignedFileId,
            });

            // Pre-fix: the flat HashSet<Guid> trust check saw "an Added FileAttachment-derived
            // entry with this id exists" (the shell) and skipped resolution for BOTH the shell's
            // own id-match AND the dependent's identical candidate id -- neither was ever
            // tenant-scope-checked. Post-fix: the shell's runtime type (FileAttachment) is not
            // assignable to the FK's declared principal type (SignedFileTpc824), so it does not
            // qualify for the exception, and the dependent's FK still goes through normal
            // resolution -- which correctly rejects it (TENANT_ATTACKER cannot resolve
            // TENANT_VICTIM's SignedFileTpc824).
            Assert.ThrowsException<UnresolvableFileAttachmentReferenceException>(() => attackerDc.SaveChanges());

            using var checkCtx = new DerivedPrincipalTpcContext824(ConnectionString, DBTypeEnum.SQLite);
            Assert.IsFalse(checkCtx.Set<InvoiceTpc824>().Any(x => x.ID == newInvoiceId),
                "#824 Finding 6: the dependent's FK, trusted only because of a base-typed shell " +
                "insert under TPC, must not have landed");
        }

        [TestMethod]
        [Description("#824 Finding 6 must-not-reject: a REAL SignedFileTpc824 Added in the SAME unit of work as its dependent (the LEGITIMATE case the same-unit-of-work exception exists for) must still be trusted without a DB round trip")]
        public void SaveChanges_TpcRealDerivedAttachmentAddedInSameUnitOfWork_Persists()
        {
            var dc = new DerivedPrincipalTpcContext824(ConnectionString, DBTypeEnum.SQLite);
            dc.SetTenantCode("TENANT_A");

            var newFileId = Guid.NewGuid();
            dc.Set<SignedFileTpc824>().Add(new SignedFileTpc824
            {
                ID = newFileId,
                FileName = "new.txt",
                FileExt = "txt",
                SaveMode = "database",
                TenantCode = "TENANT_A",
                UploadTime = DateTime.UtcNow,
                Length = 4,
                SignerName = "A",
            });
            var newInvoiceId = Guid.NewGuid();
            dc.Set<InvoiceTpc824>().Add(new InvoiceTpc824
            {
                ID = newInvoiceId,
                SignedFileId = newFileId,
            });

            dc.SaveChanges(); // both genuinely Added in the same unit of work -- must NOT throw

            using var checkCtx = new DerivedPrincipalTpcContext824(ConnectionString, DBTypeEnum.SQLite);
            Assert.IsTrue(checkCtx.Set<SignedFileTpc824>().IgnoreQueryFilters().Any(x => x.ID == newFileId),
                "the newly-created SignedFileTpc824 itself must exist");
            var reloadedInvoice = checkCtx.Set<InvoiceTpc824>().First(x => x.ID == newInvoiceId);
            Assert.AreEqual(newFileId, reloadedInvoice.SignedFileId,
                "#824 Finding 6 must-not-reject: a genuinely same-unit-of-work derived attachment " +
                "must still be trusted -- Finding 6's narrowing must not over-reject the legitimate case");
        }
    }
}
