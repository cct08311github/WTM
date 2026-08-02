#nullable enable
using System;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// Minimal <see cref="FrameworkContext"/> subclass used only to get a SQLite-backed
    /// <see cref="FileAttachment"/> table with the real production <c>ITenant</c> global query
    /// filter applied (via <c>FrameworkContext.OnModelCreating</c>'s <c>Utils.GetAllModels()</c>
    /// scan) — deliberately NOT <c>WalkingTec.Mvvm.Core.Test.DataContext</c>, whose large set of
    /// unrelated fixture entities (<c>Major</c>/<c>School</c>/<c>Student</c>/...) throws
    /// <c>SQLite Error 1: 'duplicate column name: MajorId'</c> under SQLite's migration/schema
    /// creation (a conflict InMemory silently tolerates, which is why
    /// <c>TenantIsolationFixTests.FileAttachmentTestContext</c> stayed on InMemory). This file
    /// needs SQLite specifically (see <see cref="WtmFileProviderTenantScopedReadTests1011"/>'s
    /// own doc comment), so it needs a context whose model SQLite can actually create.
    /// </summary>
    internal sealed class FileAttachmentSqliteContext : FrameworkContext
    {
        public FileAttachmentSqliteContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    /// <summary>
    /// Issue #1011 — <c>WtmFileProvider.GetFileTenantScoped</c>/<c>GetFileNameTenantScoped</c>.
    ///
    /// <para>
    /// A prior design pass argued for adding nothing new here, on the theory that cross-tenant
    /// READ has a legitimate deployment shape (a documented tenant-agnostic public file store,
    /// <c>FileUploadOptions.cs</c>'s own doc comment on <c>EnforceTenantFileScope</c>) so a
    /// deployment-wide flag is the right layer. Cross-vendor review overturned that with an
    /// actual call path: <c>BaseImportVM</c>'s two <c>GetFile</c> sites resolve
    /// <c>UploadFileId</c> — a value that is model-bound the SAME way
    /// <see cref="BaseVM.DeletedFileIds"/> is (per <c>WtmFileProvider.DeleteFileTenantScoped</c>'s
    /// own doc comment) — and <c>UploadFileId</c> is the user's just-uploaded workbook, not a
    /// shared template (<c>BaseImportVM.cs:311-317</c>). The real distinction is not read-vs-delete;
    /// it is that a caller-controlled ID sink must be immune to the global flag. This file proves
    /// the new scoped API actually delivers that immunity, on SQLite (not EF InMemory — the
    /// assertion is about the global <c>ITenant</c> filter's SQL translation, which InMemory's
    /// LINQ-to-objects evaluation never performs; a green there would prove nothing about the
    /// real SQL Server/SQLite/Oracle behaviour).
    /// </para>
    ///
    /// <para>
    /// Every test here runs with <c>EnforceTenantFileScope = false</c> — the deployment has
    /// explicitly opted OUT of the deployment-wide flag — specifically to prove the scoped API's
    /// immunity to that flag, not merely its behaviour when the flag happens to already be on
    /// (<c>TenantIsolationFixTests.GetFile_EnforceTenantScope_CrossTenantGuid_ReturnsNull</c>
    /// already covers the flag-on case for the plain method).
    /// </para>
    /// </summary>
    [TestClass]
    public class WtmFileProviderTenantScopedReadTests1011
    {
        /// <summary>
        /// Seeds one <see cref="FileAttachment"/> (SaveMode "database", so
        /// <see cref="WtmDataBaseFileHandler.GetFileData"/> reads its bytes back with no
        /// filesystem dependency) belonging to <paramref name="tenantCode"/>, on a fresh SQLite
        /// shared-memory database, and returns its id plus the marker bytes it was seeded with.
        /// The caller is responsible for keeping a <see cref="SqliteConnection"/> to
        /// <paramref name="cs"/> open for the DB's lifetime (shared-cache in-memory SQLite dies
        /// once every connection to it closes).
        /// </summary>
        private static (Guid Id, string Marker) SeedFile(string cs, string? tenantCode, string label)
        {
            var marker = $"WTM-1011-{label}-{Guid.NewGuid():N}";
            using var seedCtx = new FileAttachmentSqliteContext(cs, DBTypeEnum.SQLite);
            seedCtx.Database.EnsureCreated();
            var fa = new FileAttachment
            {
                ID = Guid.NewGuid(),
                FileName = $"file-{label}.txt",
                FileExt = "txt",
                TenantCode = tenantCode,
                SaveMode = "database",
                UploadTime = DateTime.UtcNow,
                FileData = Encoding.UTF8.GetBytes(marker),
                Length = marker.Length,
            };
            seedCtx.Add(fa);
            seedCtx.SaveChanges();
            return (fa.ID, marker);
        }

        private static WTMContext NewWtm(string? currentTenant)
        {
            // The DC passed here is never used by the assertions below — every call under test
            // passes its own `dc:` argument explicitly, mirroring how BaseImportVM's two sites
            // call WtmFileProvider (see .claude/rules/testing.md: "Wtm.CreateDC('default')
            // cannot be mocked" — passing dc: explicitly sidesteps needing it to be).
            var wtm = MockWtmContext.CreateWtmContext(null, "user1011");
            wtm.ConfigInfo!.EnableTenant = true;
            wtm.ConfigInfo!.FileUploadOptions.EnforceTenantFileScope = false;
            wtm.LoginUserInfo!.CurrentTenant = currentTenant;
            return wtm;
        }

        // ═══════════════════════════════════════════════════════════════════
        // Pin the documented opt-out: the PLAIN method still resolves cross-tenant when
        // EnforceTenantFileScope=false. Do NOT "fix" this — it is FileUploadOptions.cs's own
        // documented behaviour for a tenant-agnostic public file store.
        // ═══════════════════════════════════════════════════════════════════

        [TestMethod]
        [Description("#1011: pin — GetFile (plain, flag-driven) still resolves cross-tenant when EnforceTenantFileScope=false")]
        public void GetFile_EnforceTenantFileScopeFalse_CrossTenantGuid_StillResolves()
        {
            var cs = $"DataSource=wtmfp1011_plain_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            using var keepAlive = new SqliteConnection(cs);
            keepAlive.Open();

            var (fileId, marker) = SeedFile(cs, "TENANT_B", "plain");

            var wtm = NewWtm("TENANT_A");
            using var tenantADc = new FileAttachmentSqliteContext(cs, DBTypeEnum.SQLite);
            tenantADc.SetTenantCode("TENANT_A");

            var fp = new WtmFileProvider(wtm);
            var result = fp.GetFile(fileId.ToString(), withData: true, dc: tenantADc);

            Assert.IsNotNull(result,
                "#1011: the plain GetFile method must still resolve a cross-tenant file by GUID " +
                "when EnforceTenantFileScope=false — this is the documented opt-out for a " +
                "tenant-agnostic public file store (FileUploadOptions.cs), not a bug to fix here.");
            Assert.IsNotNull(result!.DataStream);
            using var reader = new System.IO.StreamReader(result.DataStream!);
            Assert.AreEqual(marker, reader.ReadToEnd(),
                "the resolved file's content must be TenantB's marker, proving this is a real " +
                "cross-tenant read and not a coincidental non-null result.");
        }

        // ═══════════════════════════════════════════════════════════════════
        // The SCOPED method blocks cross-tenant resolution unconditionally — immune to the flag.
        // ═══════════════════════════════════════════════════════════════════

        [TestMethod]
        [Description("#1011: GetFileTenantScoped blocks cross-tenant resolution even when EnforceTenantFileScope=false")]
        public void GetFileTenantScoped_EnforceTenantFileScopeFalse_CrossTenantGuid_ReturnsNull()
        {
            var cs = $"DataSource=wtmfp1011_scopedblock_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            using var keepAlive = new SqliteConnection(cs);
            keepAlive.Open();

            var (fileId, _) = SeedFile(cs, "TENANT_B", "scopedblock");

            var wtm = NewWtm("TENANT_A");
            using var tenantADc = new FileAttachmentSqliteContext(cs, DBTypeEnum.SQLite);
            tenantADc.SetTenantCode("TENANT_A");

            var fp = new WtmFileProvider(wtm);
            var result = fp.GetFileTenantScoped(fileId.ToString(), withData: true, dc: tenantADc);

            Assert.IsNull(result,
                "#1011: GetFileTenantScoped must NOT resolve a file belonging to a different " +
                "tenant, even though EnforceTenantFileScope=false — the scoped overload keeps " +
                "the global ITenant query filter ON unconditionally, so a caller-controlled id " +
                "(e.g. BaseImportVM.UploadFileId) cannot read across tenants regardless of the " +
                "deployment-wide flag.");
        }

        [TestMethod]
        [Description("#1011: positive control — GetFileTenantScoped still resolves a SAME-tenant file")]
        public void GetFileTenantScoped_EnforceTenantFileScopeFalse_SameTenantGuid_ReturnsFile()
        {
            var cs = $"DataSource=wtmfp1011_scopedok_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            using var keepAlive = new SqliteConnection(cs);
            keepAlive.Open();

            var (fileId, marker) = SeedFile(cs, "TENANT_A", "scopedok");

            var wtm = NewWtm("TENANT_A");
            using var tenantADc = new FileAttachmentSqliteContext(cs, DBTypeEnum.SQLite);
            tenantADc.SetTenantCode("TENANT_A");

            var fp = new WtmFileProvider(wtm);
            var result = fp.GetFileTenantScoped(fileId.ToString(), withData: true, dc: tenantADc);

            Assert.IsNotNull(result,
                "#1011 positive control: GetFileTenantScoped must still resolve the CALLER'S OWN " +
                "tenant's file — otherwise the cross-tenant-blocked assertion above would be " +
                "meaningless (the method could simply be broken for every id, not specifically " +
                "scoped to the caller's tenant).");
            Assert.IsNotNull(result!.DataStream);
            using var reader = new System.IO.StreamReader(result.DataStream!);
            Assert.AreEqual(marker, reader.ReadToEnd());
        }

        // ═══════════════════════════════════════════════════════════════════
        // GetFileNameTenantScoped mirrors the same contract for the filename-only projection.
        // ═══════════════════════════════════════════════════════════════════

        [TestMethod]
        [Description("#1011: GetFileNameTenantScoped blocks cross-tenant resolution even when EnforceTenantFileScope=false")]
        public void GetFileNameTenantScoped_EnforceTenantFileScopeFalse_CrossTenantGuid_ReturnsUnknown()
        {
            var cs = $"DataSource=wtmfp1011_nameblock_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            using var keepAlive = new SqliteConnection(cs);
            keepAlive.Open();

            var (fileId, _) = SeedFile(cs, "TENANT_B", "nameblock");

            var wtm = NewWtm("TENANT_A");
            using var tenantADc = new FileAttachmentSqliteContext(cs, DBTypeEnum.SQLite);
            tenantADc.SetTenantCode("TENANT_A");

            var fp = new WtmFileProvider(wtm);
            var result = fp.GetFileNameTenantScoped(fileId.ToString(), dc: tenantADc);

            Assert.AreEqual("unknown", result,
                "#1011: GetFileNameTenantScoped must fall back to the 'unknown' sentinel for a " +
                "cross-tenant id — it must not honour EnforceTenantFileScope=false and leak the " +
                "other tenant's real filename.");
        }

        [TestMethod]
        [Description("#1011: positive control — GetFileNameTenantScoped still resolves a SAME-tenant filename")]
        public void GetFileNameTenantScoped_EnforceTenantFileScopeFalse_SameTenantGuid_ReturnsRealName()
        {
            var cs = $"DataSource=wtmfp1011_nameok_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            using var keepAlive = new SqliteConnection(cs);
            keepAlive.Open();

            var (fileId, _) = SeedFile(cs, "TENANT_A", "nameok");

            var wtm = NewWtm("TENANT_A");
            using var tenantADc = new FileAttachmentSqliteContext(cs, DBTypeEnum.SQLite);
            tenantADc.SetTenantCode("TENANT_A");

            var fp = new WtmFileProvider(wtm);
            var result = fp.GetFileNameTenantScoped(fileId.ToString(), dc: tenantADc);

            Assert.AreEqual("file-nameok.txt", result,
                "#1011 positive control: the caller's own tenant's filename must still resolve — " +
                "otherwise the cross-tenant 'unknown' assertion above would be meaningless.");
        }
    }
}
