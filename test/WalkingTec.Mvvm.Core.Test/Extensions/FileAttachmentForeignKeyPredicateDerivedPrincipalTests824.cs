#nullable enable
// Issue #824 cross-vendor review Finding 1: DCExtension.IsFileAttachmentForeignKeyProperty used
// to compare fk.PrincipalEntityType.ClrType against FileAttachment with EXACT-type equality, so a
// relationship whose PRINCIPAL is a class DERIVED from FileAttachment (a downstream TPH/TPT
// subclass) was invisible to it -- the map this predicate backs omitted the FK entirely, so the
// shared predicate's consumers (starting with _FrameworkController.UpdateModelProperty's already-
// shipped #824 Part 1 gate) let a caller set their own FK to a DIFFERENT tenant's derived-
// attachment row: the DB's own FK constraint is satisfied by the shared base-table
// FileAttachment.ID underneath any tenant's derived row.
//
// These tests build a minimal SQLite-backed context with a scalar FK whose principal is a
// FileAttachment-derived type, under BOTH EF Core inheritance mapping strategies -- TPH (the
// subclass shares FileAttachment's own table) and TPT (the subclass gets its own table, FK'd back
// to the shared base table) -- and assert the predicate now recognizes both as attachment FKs.
// Bisected against the pre-fix predicate (git stash of the DCExtension.Schema.cs change) both
// FAILED with Assert.IsTrue failed before the fix and PASS after it -- see the commit message for
// the exact captured output.

using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Test.VM;

namespace WalkingTec.Mvvm.Core.Test.Extensions
{
    // ── TPH shape: SignedFileTph declares no [Table] of its own, so by EF Core convention it
    // shares FileAttachment's own table (single-table-per-hierarchy). UseTphMappingStrategy() is
    // called explicitly in the context below anyway, so this shape stays TPH even if EF's
    // Table-attribute-based strategy inference ever changes.
    public class SignedFileTph824 : FileAttachment
    {
        public string? SignerName { get; set; }
    }

    [Table("zz_invoice_tph_824")]
    public class InvoiceTph824 : TopBasePoco
    {
        public Guid SignedFileId { get; set; }
        public SignedFileTph824? SignedFile { get; set; }
    }

    // Minimal context, extending FrameworkContext directly (not the full test DataContext) so
    // EnsureCreated() does not trip over unrelated conflicting FKs -- same pattern as
    // DoRealDeleteAsyncSubFileTests.cs's ProductSubFileContext and FkWriteGateSeventhRoundSqliteTests815.cs's
    // RequiredFkGateContext.
    internal class DerivedPrincipalTphContext824 : FrameworkContext
    {
        public DbSet<InvoiceTph824> Invoices { get; set; } = null!;
        public DbSet<SignedFileTph824> SignedFiles { get; set; } = null!;

        public DerivedPrincipalTphContext824(string cs, DBTypeEnum dbType) : base(cs, dbType) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // Configured on the ROOT entity type -- same requirement as the TPT context below.
            modelBuilder.Entity<FileAttachment>().UseTphMappingStrategy();
        }
    }

    // ── TPT shape: SignedFileTpt gets its OWN table, explicitly forced to TPT. ──
    [Table("zz_signed_file_tpt_824")]
    public class SignedFileTpt824 : FileAttachment
    {
        public string? SignerName { get; set; }
    }

    [Table("zz_invoice_tpt_824")]
    public class InvoiceTpt824 : TopBasePoco
    {
        public Guid SignedFileId { get; set; }
        public SignedFileTpt824? SignedFile { get; set; }
    }

    internal class DerivedPrincipalTptContext824 : FrameworkContext
    {
        public DbSet<InvoiceTpt824> Invoices { get; set; } = null!;
        public DbSet<SignedFileTpt824> SignedFiles { get; set; } = null!;

        public DerivedPrincipalTptContext824(string cs, DBTypeEnum dbType) : base(cs, dbType) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // EF Core requires the mapping strategy to be configured on the ROOT entity type
            // (FileAttachment), not the derived one -- see
            // https://go.microsoft.com/fwlink/?linkid=2130430. This only affects this test
            // context's own local model, not the shipped FileAttachment mapping.
            modelBuilder.Entity<FileAttachment>().UseTptMappingStrategy();
        }
    }

    [TestClass]
    public class FileAttachmentForeignKeyPredicateDerivedPrincipalTests824
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"fkpred824_{Guid.NewGuid():N}";
            _keepAlive = new SqliteConnection(ConnectionString);
            _keepAlive.Open();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive.Close();
            _keepAlive.Dispose();
        }

        [TestMethod]
        [Description("#824 Finding 1 (TPH, SQLite): IsFileAttachmentForeignKeyProperty must return true for a FK whose principal is a TPH-derived FileAttachment subclass")]
        public void IsFileAttachmentForeignKeyProperty_TphDerivedPrincipal_ReturnsTrue()
        {
            using var ctx = new DerivedPrincipalTphContext824(ConnectionString, DBTypeEnum.SQLite);
            ctx.Database.EnsureCreated();

            var result = ((IDataContext)ctx).IsFileAttachmentForeignKeyProperty(typeof(InvoiceTph824), nameof(InvoiceTph824.SignedFileId));

            Assert.IsTrue(result,
                "#824 Finding 1: a FK whose principal is a TPH-derived FileAttachment subclass (SignedFileTph824) must be recognized as an attachment FK");
        }

        [TestMethod]
        [Description("#824 Finding 1 (TPT, SQLite): IsFileAttachmentForeignKeyProperty must return true for a FK whose principal is a TPT-derived FileAttachment subclass")]
        public void IsFileAttachmentForeignKeyProperty_TptDerivedPrincipal_ReturnsTrue()
        {
            using var ctx = new DerivedPrincipalTptContext824(ConnectionString, DBTypeEnum.SQLite);
            ctx.Database.EnsureCreated();

            var result = ((IDataContext)ctx).IsFileAttachmentForeignKeyProperty(typeof(InvoiceTpt824), nameof(InvoiceTpt824.SignedFileId));

            Assert.IsTrue(result,
                "#824 Finding 1: a FK whose principal is a TPT-derived FileAttachment subclass (SignedFileTpt824) must be recognized as an attachment FK");
        }

        [TestMethod]
        [Description("#824 Finding 1 non-regression: an unrelated scalar property must still be reported false")]
        public void IsFileAttachmentForeignKeyProperty_UnrelatedProperty_ReturnsFalse()
        {
            using var ctx = new DerivedPrincipalTphContext824(ConnectionString, DBTypeEnum.SQLite);
            ctx.Database.EnsureCreated();

            var result = ((IDataContext)ctx).IsFileAttachmentForeignKeyProperty(typeof(InvoiceTph824), nameof(InvoiceTph824.ID));

            Assert.IsFalse(result, "#824 Finding 1 non-regression: ID is not a FileAttachment FK and must not be flagged as one");
        }

        /// <summary>
        /// #986: replaces the mutant's former positive control (the test above,
        /// <see cref="IsFileAttachmentForeignKeyProperty_UnrelatedProperty_ReturnsFalse"/>). That
        /// test reused THIS class's own <see cref="DerivedPrincipalTphContext824"/>/
        /// <see cref="InvoiceTph824"/> fixture — the SAME derived-principal FK
        /// (<c>InvoiceTph824.SignedFileId</c> → <c>SignedFileTph824</c>, a TPH subclass of
        /// <see cref="FileAttachment"/>) the dcext824-derived-principal-neutralize mutant targets.
        /// Production (<c>DCExtension.Schema.cs</c>'s <c>IsFileAttachmentForeignKeyProperty</c>)
        /// calls the mutated <c>IsFileAttachmentPrincipal(fk.PrincipalEntityType?.ClrType)</c>
        /// FIRST, inside the per-FK loop, and <c>continue</c>s past the FK entirely when it
        /// returns false — only a FK that survives that gate ever reaches the property-name
        /// comparison below it. Querying <c>InvoiceTph824.ID</c> (not an FK property) returns
        /// <see langword="false"/> either way, but for two DIFFERENT reasons: unmutated, the gate
        /// passes (true) and the property-name loop correctly finds no match; mutated, the gate
        /// itself now returns false (exact-type comparison rejects the TPH subclass) and the FK is
        /// skipped before any property name is ever compared. Same boolean, different control
        /// path — not a control at all.
        /// <para>
        /// This test avoids that coupling by using <see cref="BypassGuardContext824"/>'s
        /// <see cref="ProductWithOptionalPhoto"/> instead: its only FK
        /// (<see cref="ProductWithOptionalPhoto.PhotoId"/>) targets <see cref="FileAttachment"/>
        /// ITSELF, not a derived subclass. <c>IsFileAttachmentPrincipal(typeof(FileAttachment))</c>
        /// evaluates to <see langword="true"/> under BOTH the real
        /// <c>typeof(FileAttachment).IsAssignableFrom(principalClrType)</c> comparison and the
        /// mutant's reverted <c>principalClrType == typeof(FileAttachment)</c> comparison — an
        /// exact type match satisfies either test identically, so the mutation cannot change this
        /// invocation's return value. The FK therefore always reaches the SAME property-name loop
        /// below the gate, which correctly finds no match for <c>ID</c> against <c>PhotoId</c> and
        /// returns false regardless of whether the mutant is applied. The assertion's outcome is
        /// provably invariant to this mutation, which is what makes it a genuine positive control.
        /// </para>
        /// </summary>
        [TestMethod]
        [Description("#824/#986 positive control: an unrelated scalar property on an entity whose ONLY FK principal is FileAttachment itself (not a derived TPH/TPT subclass) must still be reported false, via the unchanged property-name-comparison path — decoupled from the derived-principal gate the dcext824 mutant targets")]
        public void IsFileAttachmentForeignKeyProperty_UnrelatedProperty_NonDerivedPrincipal_ReturnsFalse()
        {
            using var ctx = new BypassGuardContext824(ConnectionString, DBTypeEnum.SQLite);
            ctx.Database.EnsureCreated();

            var result = ((IDataContext)ctx).IsFileAttachmentForeignKeyProperty(typeof(ProductWithOptionalPhoto), nameof(ProductWithOptionalPhoto.ID));

            Assert.IsFalse(result,
                "#824/#986: ID is not a FileAttachment FK and must not be flagged as one -- this " +
                "fixture's only FK (PhotoId) targets FileAttachment itself (non-derived), so " +
                "IsFileAttachmentPrincipal returns true identically whether the derived-principal " +
                "comparison is IsAssignableFrom or exact-type equality, and this assertion's " +
                "outcome does not depend on the #824 Finding 1 derived-principal comparison at all");
        }
    }

    /// <summary>
    /// Issue #824 Finding 1, cross-vendor review follow-up (PR #978): the tests above only ever
    /// call <c>IsFileAttachmentForeignKeyProperty</c> directly — they prove the SHARED PREDICATE
    /// recognizes a derived-principal FK, but they never exercise
    /// <see cref="FileAttachmentSaveChangesGuard"/>'s own model-wide FK map
    /// (<c>BuildMap</c>), which — until this same PR's refactor to share
    /// <c>DCExtension.IsFileAttachmentPrincipal</c> — had an independent, unproven copy of the
    /// same comparison. These tests go through the REAL <c>SaveChanges</c> path end to end, so a
    /// regression in the guard's OWN consumption of the shared predicate (as opposed to the
    /// predicate itself) is caught here, not just in isolation.
    /// </summary>
    [TestClass]
    public class FileAttachmentSaveChangesGuardDerivedPrincipalTests824
    {
        private string _dbName = null!;
        private SqliteConnection _keepAlive = null!;

        private string ConnectionString => $"DataSource={_dbName}?mode=memory&cache=shared";

        [TestInitialize]
        public void Initialize()
        {
            _dbName = $"guardderivedprincipal824_{Guid.NewGuid():N}";
            _keepAlive = new SqliteConnection(ConnectionString);
            _keepAlive.Open();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive.Close();
            _keepAlive.Dispose();
        }

        [TestMethod]
        [Description("#824 Finding 1 (guard path, TPH, SQLite): the SaveChanges guard itself — not just the isolated predicate — must reject a forged cross-tenant FK whose principal is a TPH-derived FileAttachment subclass")]
        public void SaveChanges_ForgedCrossTenantDerivedPrincipalFK_Tph_RejectedByGuard_NotPersisted()
        {
            Guid victimFileId;
            using (var seedCtx = new DerivedPrincipalTphContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.Database.EnsureCreated();
                seedCtx.SetTenantCode("TENANT_VICTIM");
                var victim = new SignedFileTph824
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
                seedCtx.Set<SignedFileTph824>().Add(victim);
                seedCtx.SaveChanges();
                victimFileId = victim.ID;
            }

            var attackerDc = new DerivedPrincipalTphContext824(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var newInvoiceId = Guid.NewGuid();
            attackerDc.Set<InvoiceTph824>().Add(new InvoiceTph824
            {
                ID = newInvoiceId,
                SignedFileId = victimFileId,
            });

            // SaveChanges has no try/catch around it here — the exception propagates.
            Assert.ThrowsException<WalkingTec.Mvvm.Core.Exceptions.UnresolvableFileAttachmentReferenceException>(
                () => attackerDc.SaveChanges());

            using var checkCtx = new DerivedPrincipalTphContext824(ConnectionString, DBTypeEnum.SQLite);
            Assert.IsFalse(checkCtx.Set<InvoiceTph824>().Any(x => x.ID == newInvoiceId),
                "#824 Finding 1 (guard path): a forged FK whose principal is a TPH-derived " +
                "FileAttachment subclass must not have landed — the guard's OWN FK map must " +
                "recognize the derived principal, not just the isolated predicate");
        }

        [TestMethod]
        [Description("#824 Finding 1 (guard path, TPT, SQLite): same as the TPH version above, for a TPT-derived principal")]
        public void SaveChanges_ForgedCrossTenantDerivedPrincipalFK_Tpt_RejectedByGuard_NotPersisted()
        {
            Guid victimFileId;
            using (var seedCtx = new DerivedPrincipalTptContext824(ConnectionString, DBTypeEnum.SQLite))
            {
                seedCtx.Database.EnsureCreated();
                seedCtx.SetTenantCode("TENANT_VICTIM");
                var victim = new SignedFileTpt824
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
                seedCtx.Set<SignedFileTpt824>().Add(victim);
                seedCtx.SaveChanges();
                victimFileId = victim.ID;
            }

            var attackerDc = new DerivedPrincipalTptContext824(ConnectionString, DBTypeEnum.SQLite);
            attackerDc.SetTenantCode("TENANT_ATTACKER");
            var newInvoiceId = Guid.NewGuid();
            attackerDc.Set<InvoiceTpt824>().Add(new InvoiceTpt824
            {
                ID = newInvoiceId,
                SignedFileId = victimFileId,
            });

            Assert.ThrowsException<WalkingTec.Mvvm.Core.Exceptions.UnresolvableFileAttachmentReferenceException>(
                () => attackerDc.SaveChanges());

            using var checkCtx = new DerivedPrincipalTptContext824(ConnectionString, DBTypeEnum.SQLite);
            Assert.IsFalse(checkCtx.Set<InvoiceTpt824>().Any(x => x.ID == newInvoiceId),
                "#824 Finding 1 (guard path): a forged FK whose principal is a TPT-derived " +
                "FileAttachment subclass must not have landed");
        }
    }
}
