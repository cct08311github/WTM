#nullable enable
using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Implement;
using WalkingTec.Mvvm.Core.Support.FileHandlers;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    /// <summary>
    /// Minimal <see cref="FrameworkContext"/> subclass giving a SQLite-backed
    /// <see cref="FileAttachment"/> table with the real production <c>ITenant</c> global query
    /// filter applied — see
    /// <c>WalkingTec.Mvvm.Core.Test.Security.FileAttachmentSqliteContext</c>'s doc comment for
    /// why this can't just be <c>WalkingTec.Mvvm.Core.Test.DataContext</c> (SQLite schema
    /// conflict among its unrelated fixture entities).
    /// </summary>
    internal sealed class ImportFileAttachmentSqliteContext : FrameworkContext
    {
        public ImportFileAttachmentSqliteContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    /// <summary>
    /// A <see cref="WTMContext"/> whose <see cref="CreateDC"/> returns an already-built,
    /// already-tenant-scoped <see cref="IDataContext"/> directly, bypassing the
    /// connection-string/tenant-routing lookup that requires config
    /// <c>WalkingTec.Mvvm.Test.Mock.MockWtmContext</c> does not supply (see
    /// <c>.claude/rules/testing.md</c>: "Wtm.CreateDC('default') cannot be mocked"). Same pattern
    /// as <c>FrameworkControllerFileAccessTest.SingleConnectionFileAccessWtmContext</c> — needed
    /// here because <c>BaseImportVM.SetTemplateData</c>'s real (non-overridden) implementation
    /// calls <c>Wtm!.CreateDC(false)</c> directly, and this test exercises that real
    /// implementation rather than a bytes-injected override.
    /// </summary>
    internal sealed class SingleConnectionImportWtmContext : WTMContext
    {
        private readonly IDataContext _dc;

        public SingleConnectionImportWtmContext(IDataContext dc)
            : base(null, new GlobalData(), null, new DefaultUIService(), null, dc, null)
        {
            _dc = dc;
        }

        public override IDataContext? CreateDC(bool isLog = false, string? cskey = null, bool logerror = true) => _dc;
    }

    /// <summary>
    /// Exposes <see cref="BaseImportVM{T, P}.TemplateData"/> (protected) so the tests below can
    /// assert on it directly. Does NOT override <c>SetTemplateData</c>, <c>InitVM</c>, or
    /// anything else that would touch the read path under test — the whole point of this class
    /// is to exercise the REAL, unmodified <c>SetTemplateData()</c> (unlike
    /// <c>BytesInjectedImportVM</c>/<c>NormalImportDataInjectionVM</c> elsewhere in this project,
    /// which override it specifically to bypass <see cref="WtmFileProvider"/>).
    /// </summary>
    internal sealed class RealFileReadImportVM : BaseImportVM<ImportTestTemplateVM, ImportTestItem>
    {
        public System.Collections.Generic.List<ImportTestTemplateVM>? PublicTemplateData => TemplateData;
    }

    /// <summary>
    /// Issue #1011 — <c>BaseImportVM.SetTemplateData</c>'s real <c>WtmFileProvider.GetFile</c>
    /// call site (<c>BaseImportVM.cs:322</c>) moved to <c>GetFileTenantScoped</c>. This is the
    /// call path that overturned the "add nothing" design pass: <c>UploadFileId</c> is
    /// model-bound the same way <see cref="BaseVM.DeletedFileIds"/> is, and it names the user's
    /// just-uploaded workbook, not a shared template (<c>SetTemplateData</c> returns "please
    /// upload template" when it is missing, then hands it straight to NPOI) — so it is exactly
    /// the caller-controlled ID sink <c>DeleteFileTenantScoped</c>'s own doc comment says must
    /// use the scoped overload.
    ///
    /// <para>
    /// Runs with <c>EnforceTenantFileScope = false</c> throughout, on SQLite (see
    /// <c>WtmFileProviderTenantScopedReadTests1011</c>'s class doc comment for why SQLite, not
    /// EF InMemory), to prove the fix narrows behaviour even for a deployment that has opted OUT
    /// of the deployment-wide flag — the whole point of routing this call through the
    /// unconditionally-scoped overload instead of the flag-driven one.
    /// </para>
    /// </summary>
    [TestClass]
    public class BaseImportVMTenantScopedReadTests1011
    {
        /// <summary>
        /// A workbook <c>SetTemplateData()</c>'s real, unmodified row-parsing logic accepts with
        /// zero validation errors and exactly one parsed row — reusing the exact layout
        /// <see cref="MediumFixWorkbookBuilder.BuildWithBlankRows"/> already proves works end to
        /// end via <c>BytesInjectedImportVM</c> (which reproduces the same control flow) in
        /// <c>ImportMediumFixTests</c>. Used as the seeded <see cref="FileAttachment"/>'s content
        /// so a resolved-vs-blocked read produces a stark, unambiguous before/after difference:
        /// resolved → 1 parsed row, 0 errors; blocked → 0 parsed rows, 1 "WrongTemplate" error.
        /// </summary>
        private static byte[] BuildValidWorkbook() =>
            MediumFixWorkbookBuilder.BuildWithBlankRows(
                "ImportTestTemplateVM",
                new[] { "Name", "Value" },
                new object?[]?[] { new object?[] { "Alpha", 42 } });

        private static Guid SeedFile(string cs, string? tenantCode)
        {
            using var seedCtx = new ImportFileAttachmentSqliteContext(cs, DBTypeEnum.SQLite);
            seedCtx.Database.EnsureCreated();
            var fa = new FileAttachment
            {
                ID = Guid.NewGuid(),
                FileName = "template.xlsx",
                FileExt = "xlsx",
                TenantCode = tenantCode,
                SaveMode = "database",
                UploadTime = DateTime.UtcNow,
                FileData = BuildValidWorkbook(),
            };
            fa.Length = fa.FileData.Length;
            seedCtx.Add(fa);
            seedCtx.SaveChanges();
            return fa.ID;
        }

        private static RealFileReadImportVM BuildVm(string cs, string uploadFileId, string callerTenant)
        {
            var tenantDc = new ImportFileAttachmentSqliteContext(cs, DBTypeEnum.SQLite);
            tenantDc.SetTenantCode(callerTenant);

            var wtm = new SingleConnectionImportWtmContext(tenantDc)
            {
                LoginUserInfo = new LoginUserInfo { ITCode = "user1011", CurrentTenant = callerTenant },
            };
            wtm.ConfigInfo!.EnableTenant = true;
            wtm.ConfigInfo!.FileUploadOptions.EnforceTenantFileScope = false;

            var fp = new WtmFileProvider(wtm);
            var mockSp = new Mock<IServiceProvider>();
            mockSp.Setup(x => x.GetService(typeof(WtmFileProvider))).Returns(fp);
            wtm.SetServiceProvider(mockSp.Object);

            return new RealFileReadImportVM
            {
                Wtm = wtm,
                UploadFileId = uploadFileId,
                ValidityTemplateType = true,
            };
        }

        [TestMethod]
        [Description("#1011: SetTemplateData's real GetFile* call site blocks a cross-tenant UploadFileId even when EnforceTenantFileScope=false")]
        public void SetTemplateData_EnforceTenantFileScopeFalse_CrossTenantUploadFileId_Blocked()
        {
            var cs = $"DataSource=baseimport1011_cross_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            using var keepAlive = new SqliteConnection(cs);
            keepAlive.Open();

            var fileId = SeedFile(cs, tenantCode: "TENANT_B");
            var vm = BuildVm(cs, fileId.ToString(), callerTenant: "TENANT_A");

            vm.SetTemplateData();

            Assert.AreEqual(0, vm.PublicTemplateData?.Count ?? 0,
                "#1011: a cross-tenant UploadFileId must not be parsed into any template rows — " +
                $"got {vm.PublicTemplateData?.Count ?? 0}. Errors: " +
                string.Join("; ", vm.ErrorListVM.EntityList.Select(e => e.Message)));
            Assert.AreEqual(1, vm.ErrorListVM.EntityList.Count,
                "#1011: a cross-tenant UploadFileId must produce exactly the 'file did not " +
                "resolve' (WrongTemplate) error — not proceed to parse the workbook.");
        }

        [TestMethod]
        [Description("#1011 positive control: SetTemplateData still succeeds for the caller's own tenant's UploadFileId")]
        public void SetTemplateData_EnforceTenantFileScopeFalse_SameTenantUploadFileId_Succeeds()
        {
            var cs = $"DataSource=baseimport1011_same_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            using var keepAlive = new SqliteConnection(cs);
            keepAlive.Open();

            var fileId = SeedFile(cs, tenantCode: "TENANT_A");
            var vm = BuildVm(cs, fileId.ToString(), callerTenant: "TENANT_A");

            vm.SetTemplateData();

            Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count,
                "#1011 positive control: the caller's own tenant's UploadFileId must still parse " +
                "with zero errors — otherwise the cross-tenant-blocked assertion above would be " +
                "meaningless (the read path could simply be broken for every id). Errors: " +
                string.Join("; ", vm.ErrorListVM.EntityList.Select(e => e.Message)));
            Assert.AreEqual(1, vm.PublicTemplateData?.Count,
                $"#1011 positive control: exactly one row ('Alpha', 42) should have been parsed " +
                $"— got {vm.PublicTemplateData?.Count ?? 0}.");
            Assert.AreEqual("Alpha", vm.PublicTemplateData![0].Name_Excel.Value);
        }
    }
}
