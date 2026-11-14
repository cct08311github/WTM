using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>A record with a tenant, a validity flag, the audit members and a child list.</summary>
    public class GeDoc : PersistPoco, ITenant
    {
        public string Title { get; set; }
        [CanNotEdit]
        public string Note { get; set; }
        public string TenantCode { get; set; }
        public List<GeLine> Lines { get; set; }
    }

    public class GeLine : PersistPoco, ITenant
    {
        public string Text { get; set; }
        public string TenantCode { get; set; }
        public Guid GeDocId { get; set; }
        public GeDoc GeDoc { get; set; }
    }

    /// <summary>
    /// Has audit members (IBasePoco) but declares its own IsValid and TenantCode without the interfaces, like FrameworkUserBase.IsValid:
    /// those two stay editable.
    /// </summary>
    public class GePlain : BasePoco
    {
        public string Title { get; set; }
        public bool IsValid { get; set; } = true;
        public string TenantCode { get; set; }
    }

    public class GeImportDoc : PersistPoco, ITenant
    {
        public string Code { get; set; }
        public string Title { get; set; }
        public string TenantCode { get; set; }
    }

    public class GeDocBatch : BaseVM
    {
        public Guid ID { get; set; }
        public string Title { get; set; }
        public string Note { get; set; }
        public string TenantCode { get; set; }
        public bool IsValid { get; set; }
        public string CreateBy { get; set; }
        public DateTime? CreateTime { get; set; }
        public string UpdateBy { get; set; }
    }

    public class GeImportTemplate : BaseTemplateVM
    {
        public ExcelPropety Code = ExcelPropety.CreateProperty<GeImportDoc>(x => x.Code);
        public ExcelPropety Title = ExcelPropety.CreateProperty<GeImportDoc>(x => x.Title);
        public ExcelPropety TenantCode = ExcelPropety.CreateProperty<GeImportDoc>(x => x.TenantCode);
        public ExcelPropety IsValid = ExcelPropety.CreateProperty<GeImportDoc>(x => x.IsValid);
        public ExcelPropety CreateBy = ExcelPropety.CreateProperty<GeImportDoc>(x => x.CreateBy);
    }

    public class GeImportVM : BaseImportVM<GeImportTemplate, GeImportDoc>
    {
    }

    /// <summary>ITenant is implemented by a base class, the entity derives from it.</summary>
    public abstract class GeTenantBase : PersistPoco, ITenant
    {
        public string TenantCode { get; set; }
    }

    public class GeDerived : GeTenantBase
    {
        public string Title { get; set; }
    }

    public class GeShadowBase : PersistPoco, ITenant
    {
        public string TenantCode { get; set; }
    }

    /// <summary>Hides the property of the base class with <c>new</c>.</summary>
    public class GeShadow : GeShadowBase
    {
        public string Title { get; set; }
        public new string TenantCode { get; set; }
    }

    /// <summary>
    /// The generic edit paths (form edit, list edit, batch edit, the overwrite of an import) do not write the members the framework owns
    /// (tenant code, validity flag, create time and creator) when a request carries them, and the framework replaces the update stamps.
    /// <c>GenericEdit:LegacyProtectedMemberWrites</c> restores the earlier member writes.
    /// </summary>
    [TestClass]
    public class GenericEditProtectedMembersTest
    {
        private const string Tenant = "t1";
        private static readonly DateTime SeedTime = new DateTime(2020, 1, 2, 3, 4, 5);
        private static readonly DateTime EvilTime = new DateTime(1999, 9, 9, 9, 9, 9);

        private string _seed;
        private Guid _docId;
        private Guid _lineId;
        private Guid _plainId;

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString();
            using var dc = Dc();
            var doc = new GeDoc { ID = Guid.NewGuid(), Title = "stored", Note = "keep", TenantCode = Tenant, CreateBy = "alice", CreateTime = SeedTime };
            var line = new GeLine { ID = Guid.NewGuid(), Text = "line", TenantCode = Tenant, GeDocId = doc.ID, CreateBy = "alice", CreateTime = SeedTime };
            var plain = new GePlain { ID = Guid.NewGuid(), Title = "stored", TenantCode = "x", CreateBy = "alice", CreateTime = SeedTime };
            dc.Set<GeDoc>().Add(doc);
            dc.Set<GeLine>().Add(line);
            dc.Set<GePlain>().Add(plain);
            dc.SaveChanges();
            _docId = doc.ID;
            _lineId = line.ID;
            _plainId = plain.ID;
        }

        private const string OtherTenant = "t2";

        // the tenant is set the way the framework does it for a request (IDataContext.SetTenantCode); null is a host user
        private DataContext Dc(string tenant = Tenant)
        {
            var dc = new DataContext(_seed, DBTypeEnum.Memory);
            dc.SetTenantCode(tenant);
            return dc;
        }

        private static Configs Legacy(bool on) => new Configs { GenericEdit = new GenericEditOptions { LegacyProtectedMemberWrites = on } };

        private WTMContext Wtm(bool legacy = false, string tenant = Tenant)
        {
            var wtm = MockWtmContext.CreateWtmContext(Dc(tenant), "bob", configs: legacy ? Legacy(true) : null);
            wtm.LoginUserInfo.CurrentTenant = tenant;
            return wtm;
        }

        private GeDoc StoredDoc()
        {
            using var dc = Dc();
            return dc.Set<GeDoc>().IgnoreQueryFilters().AsNoTracking().Single(x => x.ID == _docId);
        }

        private GeLine StoredLine()
        {
            using var dc = Dc();
            return dc.Set<GeLine>().IgnoreQueryFilters().AsNoTracking().Single(x => x.ID == _lineId);
        }

        private GePlain StoredPlain()
        {
            using var dc = Dc();
            return dc.Set<GePlain>().IgnoreQueryFilters().AsNoTracking().Single(x => x.ID == _plainId);
        }

        private static void AssertUntouched(GeDoc doc)
        {
            Assert.AreEqual(Tenant, doc.TenantCode);
            Assert.IsTrue(doc.IsValid);
            Assert.AreEqual("alice", doc.CreateBy);
            Assert.AreEqual(SeedTime, doc.CreateTime);
        }

        private static GeDoc EvilDoc(Guid id) => new GeDoc { ID = id, Title = "edited", Note = "mallory", TenantCode = "evil", IsValid = false, CreateBy = "mallory", CreateTime = EvilTime, UpdateBy = "mallory", UpdateTime = EvilTime };

        private static readonly string[] AllFields = { "Title", "TenantCode", "IsValid", "CreateBy", "CreateTime", "UpdateBy", "UpdateTime" };

        // ---- the predicate -------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Predicate_MatchesByInterfaceAndExactName()
        {
            Assert.IsTrue(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GeDoc), "TenantCode"));
            Assert.IsTrue(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GeDoc), "IsValid"));
            Assert.IsTrue(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GeDoc), "CreateBy"));
            Assert.IsTrue(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GeDoc), "CreateTime"));
            Assert.IsFalse(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GeDoc), "UpdateBy"), "the update stamps are written by the framework, not refused");
            Assert.IsFalse(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GeDoc), "UpdateTime"));
            Assert.IsFalse(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GeDoc), "Title"));
            Assert.IsFalse(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GeDoc), "tenantcode"), "the name is the resolved property name, compared exactly");
            Assert.IsFalse(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GeDoc), "isvalid"));
            Assert.IsFalse(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GeDoc), "ID"), "the key is skipped by name elsewhere");
            Assert.IsTrue(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GePlain), "CreateBy"));
            Assert.IsFalse(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GePlain), "IsValid"), "an IsValid that is not IPersistPoco stays editable");
            Assert.IsFalse(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GePlain), "TenantCode"), "a TenantCode that is not ITenant stays editable");
            Assert.IsFalse(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(Album), "CreateBy"));
            Assert.IsFalse(ModelPropertyUpdateRules.IsProtectedEditMember(null, "TenantCode"));
            Assert.IsFalse(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GeDoc), "IsValid", exemptIsValid: true));
            Assert.IsTrue(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GeDoc), "TenantCode", exemptIsValid: true), "only IsValid is exempted");
        }

        [TestMethod]
        public void ImportPredicate_RefusesOnlyTenantCodeAndIsValid()
        {
            Assert.IsTrue(ModelPropertyUpdateRules.IsProtectedImportMember(typeof(GeImportDoc), "TenantCode"));
            Assert.IsTrue(ModelPropertyUpdateRules.IsProtectedImportMember(typeof(GeImportDoc), "IsValid"));
            Assert.IsFalse(ModelPropertyUpdateRules.IsProtectedImportMember(typeof(GeImportDoc), "CreateBy"));
            Assert.IsFalse(ModelPropertyUpdateRules.IsProtectedImportMember(typeof(GeImportDoc), "CreateTime"));
            Assert.IsFalse(ModelPropertyUpdateRules.IsProtectedImportMember(typeof(GePlain), "IsValid"));
            Assert.IsFalse(ModelPropertyUpdateRules.IsProtectedImportMember(typeof(GePlain), "TenantCode"));
        }

        // ---- form edit -----------------------------------------------------------------------------------------------------------------

        private BaseCRUDVM<GeDoc> FormVm(bool legacy, params string[] fields)
        {
            var vm = new BaseCRUDVM<GeDoc> { Wtm = Wtm(legacy) };
            vm.Entity = EvilDoc(_docId);
            vm.FC = fields.ToDictionary(x => "Entity." + x, x => (object)"");
            return vm;
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task FormEdit_PostedFields_RefusedMembersKept_OthersSaved(bool useAsync)
        {
            var vm = FormVm(false, AllFields);
            if (useAsync) await vm.DoEditAsync(false); else vm.DoEdit(false);
            Assert.IsTrue(vm.MSD.IsValid);
            var doc = StoredDoc();
            Assert.AreEqual("edited", doc.Title);
            AssertUntouched(doc);
            Assert.AreEqual("bob", doc.UpdateBy, "the update stamp is the framework's");
            Assert.AreNotEqual(EvilTime, doc.UpdateTime);
        }

        [TestMethod]
        public void FormEdit_PostedFields_KeyCaseDoesNotMatter()
        {
            var vm = new BaseCRUDVM<GeDoc> { Wtm = Wtm() };
            vm.Entity = EvilDoc(_docId);
            vm.FC = new Dictionary<string, object> { ["entity.title"] = "", ["ENTITY.TENANTCODE"] = "", ["Entity.isvalid"] = "", ["entity.createby"] = "", ["entity.createtime"] = "" };
            vm.DoEdit(false);
            Assert.AreEqual("edited", StoredDoc().Title);
            AssertUntouched(StoredDoc());
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task FormEdit_AllFields_BodyWithoutTheMembers_DoesNotNullThem(bool useAsync)
        {
            var vm = new BaseCRUDVM<GeDoc> { Wtm = Wtm() };
            vm.Entity = new GeDoc { ID = _docId, Title = "edited" };
            vm.FC = new Dictionary<string, object>();
            if (useAsync) await vm.DoEditAsync(true); else vm.DoEdit(true);
            var doc = StoredDoc();
            Assert.AreEqual("edited", doc.Title);
            AssertUntouched(doc);
            Assert.AreEqual("keep", doc.Note, "CanNotEdit is still kept");
        }

        [TestMethod]
        public void FormEdit_AllFields_PostedValuesAreNotWritten()
        {
            var vm = FormVm(false);
            vm.DoEdit(true);
            var doc = StoredDoc();
            Assert.AreEqual("edited", doc.Title);
            AssertUntouched(doc);
            Assert.AreEqual("bob", doc.UpdateBy);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task FormEdit_NonRefusedMembersOfOtherTypes_StillEditable(bool useAsync)
        {
            var vm = new BaseCRUDVM<GePlain> { Wtm = Wtm() };
            vm.Entity = new GePlain { ID = _plainId, Title = "edited", IsValid = false, TenantCode = "other", CreateBy = "mallory", CreateTime = EvilTime };
            vm.FC = new[] { "Title", "IsValid", "TenantCode", "CreateBy", "CreateTime" }.ToDictionary(x => "Entity." + x, x => (object)"");
            if (useAsync) await vm.DoEditAsync(false); else vm.DoEdit(false);
            var plain = StoredPlain();
            Assert.AreEqual("edited", plain.Title);
            Assert.IsFalse(plain.IsValid, "an IsValid that is not IPersistPoco is still written");
            Assert.AreEqual("other", plain.TenantCode, "a TenantCode that is not ITenant is still written");
            Assert.AreEqual("alice", plain.CreateBy, "the audit members of an IBasePoco are refused");
            Assert.AreEqual(SeedTime, plain.CreateTime);
        }

        [TestMethod]
        public void FormEdit_Legacy_WritesTheMembersAgain()
        {
            var vm = FormVm(true, AllFields);
            vm.DoEdit(false);
            var doc = StoredDoc();
            using var dc = Dc();
            var raw = dc.Set<GeDoc>().IgnoreQueryFilters().AsNoTracking().Single(x => x.ID == _docId);
            Assert.AreEqual("evil", raw.TenantCode);
            Assert.IsFalse(raw.IsValid);
            Assert.AreEqual("mallory", raw.CreateBy);
            Assert.AreEqual(EvilTime, raw.CreateTime);
            Assert.AreEqual("edited", doc.Title);
            Assert.AreEqual("bob", raw.UpdateBy, "the update stamp does not depend on the setting");
        }

        [TestMethod]
        public void FormEdit_AllFields_Legacy_WritesTheWholeRecordAgain()
        {
            var vm = new BaseCRUDVM<GeDoc> { Wtm = Wtm(true) };
            vm.Entity = new GeDoc { ID = _docId, Title = "edited", TenantCode = Tenant };
            vm.FC = new Dictionary<string, object>();
            vm.DoEdit(true);
            var doc = StoredDoc();
            Assert.IsNull(doc.CreateBy, "with the setting on, a body without CreateBy nulls it as before");
            Assert.IsNull(doc.CreateTime);
        }

        // ---- soft delete ---------------------------------------------------------------------------------------------------------------

        private BaseCRUDVM<GeDoc> DeleteVm(bool legacy, string postedIsValid)
        {
            var vm = new BaseCRUDVM<GeDoc> { Wtm = Wtm(legacy) };
            vm.Entity = new GeDoc { ID = _docId, Title = "stored", TenantCode = Tenant, CreateBy = "alice", CreateTime = SeedTime };
            vm.FC = new Dictionary<string, object>();
            if (postedIsValid != null)
            {
                vm.FC["Entity.IsValid"] = postedIsValid;
            }
            return vm;
        }

        [TestMethod]
        [DataRow(false, null, false)]
        [DataRow(false, "true", false)]
        [DataRow(false, null, true)]
        [DataRow(false, "true", true)]
        [DataRow(true, null, false)]
        [DataRow(true, "true", true)]
        public async Task SoftDelete_StillSetsIsValidFalse(bool legacy, string postedIsValid, bool useAsync)
        {
            var vm = DeleteVm(legacy, postedIsValid);
            if (useAsync) await vm.DoDeleteAsync(); else vm.DoDelete();
            Assert.IsTrue(vm.MSD.IsValid);
            var doc = StoredDoc();
            Assert.IsFalse(doc.IsValid);
            Assert.AreEqual("bob", doc.UpdateBy);
            Assert.AreEqual(Tenant, doc.TenantCode);
            Assert.IsFalse(StoredLine().IsValid, "the children are marked invalid too");
        }

        [TestMethod]
        public void SoftDelete_DoesNotExemptOtherMembers()
        {
            var vm = DeleteVm(false, "true");
            vm.FC["Entity.TenantCode"] = "";
            vm.FC["Entity.CreateBy"] = "";
            vm.Entity.TenantCode = "evil";
            vm.Entity.CreateBy = "mallory";
            vm.DoDelete();
            var doc = StoredDoc();
            Assert.IsFalse(doc.IsValid);
            Assert.AreEqual(Tenant, doc.TenantCode);
            Assert.AreEqual("alice", doc.CreateBy);
        }

        [TestMethod]
        public void EditAfterSoftDeleteFlag_DoesNotLeakTheExemption()
        {
            DeleteVm(false, null).DoDelete();
            // a later form edit on another record: IsValid is refused again (the exemption is per call)
            var doc2 = new BaseCRUDVM<GeDoc> { Wtm = Wtm() };
            using (var dc = Dc())
            {
                var other = new GeDoc { ID = Guid.NewGuid(), Title = "other", TenantCode = Tenant };
                dc.Set<GeDoc>().Add(other);
                dc.SaveChanges();
                doc2.Entity = new GeDoc { ID = other.ID, Title = "x", IsValid = false };
                doc2.FC = new Dictionary<string, object> { ["Entity.IsValid"] = "", ["Entity.Title"] = "" };
                doc2.DoEdit(false);
                var stored = Dc().Set<GeDoc>().IgnoreQueryFilters().AsNoTracking().Single(x => x.ID == other.ID);
                Assert.IsTrue(stored.IsValid);
                Assert.AreEqual("x", stored.Title);
            }
        }

        // ---- child rows ----------------------------------------------------------------------------------------------------------------

        private BaseCRUDVM<GeDoc> ChildVm(bool legacy, params string[] fields)
        {
            var vm = new BaseCRUDVM<GeDoc> { Wtm = Wtm(legacy) };
            vm.Entity = new GeDoc
            {
                ID = _docId,
                Title = "edited",
                Lines = new List<GeLine>
                {
                    new GeLine { ID = _lineId, Text = "changed", TenantCode = "evil", IsValid = false, CreateBy = "mallory", CreateTime = EvilTime, UpdateBy = "mallory", UpdateTime = EvilTime, GeDocId = _docId }
                }
            };
            vm.FC = fields.ToDictionary(x => "Entity.Lines[0]." + x, x => (object)"");
            vm.FC["Entity.Title"] = "";
            return vm;
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ChildEdit_PostedFields_RefusedMembersKept_UpdateStampReplaced(bool useAsync)
        {
            var vm = ChildVm(false, "Text", "TenantCode", "IsValid", "CreateBy", "CreateTime", "UpdateBy", "UpdateTime");
            if (useAsync) await vm.DoEditAsync(false); else vm.DoEdit(false);
            Assert.IsTrue(vm.MSD.IsValid);
            var line = StoredLine();
            Assert.AreEqual("changed", line.Text);
            Assert.AreEqual(Tenant, line.TenantCode);
            Assert.IsTrue(line.IsValid);
            Assert.AreEqual("alice", line.CreateBy);
            Assert.AreEqual(SeedTime, line.CreateTime);
            Assert.AreEqual("bob", line.UpdateBy, "a posted UpdateBy is replaced by the framework stamp");
            Assert.AreNotEqual(EvilTime, line.UpdateTime);
        }

        [TestMethod]
        public void ChildEdit_AllFields_DoesNotWriteTheMembers()
        {
            var vm = ChildVm(false, "Text");
            vm.DoEdit(true);
            var line = StoredLine();
            Assert.AreEqual("changed", line.Text);
            Assert.AreEqual(Tenant, line.TenantCode);
            Assert.IsTrue(line.IsValid);
            Assert.AreEqual("alice", line.CreateBy);
            Assert.AreEqual("bob", line.UpdateBy);
        }

        [TestMethod]
        public void ChildEdit_Legacy_WritesTheMembers_ButTheUpdateStampStaysTheFrameworks()
        {
            var vm = ChildVm(true, "Text", "TenantCode", "IsValid", "CreateBy", "CreateTime", "UpdateBy");
            vm.DoEdit(false);
            using var dc = Dc();
            var line = dc.Set<GeLine>().IgnoreQueryFilters().AsNoTracking().Single(x => x.ID == _lineId);
            Assert.AreEqual("evil", line.TenantCode);
            Assert.IsFalse(line.IsValid);
            Assert.AreEqual("mallory", line.CreateBy);
            Assert.AreEqual("bob", line.UpdateBy);
        }

        // ---- list edit -----------------------------------------------------------------------------------------------------------------

        private BasePagedListVM<GeDoc, BaseSearcher> ListVm(bool legacy, params string[] fields)
        {
            var vm = new BasePagedListVM<GeDoc, BaseSearcher> { Wtm = Wtm(legacy) };
            vm.EntityList = new List<GeDoc> { EvilDoc(_docId) };
            vm.FC = fields.ToDictionary(x => "EntityList[0]." + x, x => (object)"");
            return vm;
        }

        [TestMethod]
        public void ListEdit_PostedFields_RefusedMembersKept_UpdateStampReplaced()
        {
            var vm = ListVm(false, AllFields);
            vm.UpdateEntityList(false);
            var doc = StoredDoc();
            Assert.AreEqual("edited", doc.Title);
            AssertUntouched(doc);
            Assert.AreEqual("bob", doc.UpdateBy);
            Assert.AreNotEqual(EvilTime, doc.UpdateTime);
        }

        [TestMethod]
        public void ListEdit_AllFields_PostedValuesAreNotWritten()
        {
            var vm = ListVm(false, "Title");
            vm.UpdateEntityList(true);
            var doc = StoredDoc();
            Assert.AreEqual("edited", doc.Title);
            AssertUntouched(doc);
            Assert.AreEqual("keep", doc.Note);
            Assert.AreEqual("bob", doc.UpdateBy);
        }

        [TestMethod]
        public void ListEdit_Legacy_WritesTheMembers_ButTheUpdateStampStaysTheFrameworks()
        {
            var vm = ListVm(true, AllFields);
            vm.UpdateEntityList(false);
            using var dc = Dc();
            var doc = dc.Set<GeDoc>().IgnoreQueryFilters().AsNoTracking().Single(x => x.ID == _docId);
            Assert.AreEqual("evil", doc.TenantCode);
            Assert.IsFalse(doc.IsValid);
            Assert.AreEqual("mallory", doc.CreateBy);
            Assert.AreEqual("bob", doc.UpdateBy);
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ListEdit_AddedRow_GetsTheTenantOfTheUser_AndTheCreateStamp(bool legacy)
        {
            var newId = Guid.NewGuid();
            var vm = new BasePagedListVM<GeDoc, BaseSearcher> { Wtm = Wtm(legacy) };
            vm.EntityList = new List<GeDoc>
            {
                new GeDoc { ID = _docId, Title = "stored" },
                new GeDoc { ID = newId, Title = "added", TenantCode = "evil", UpdateBy = "mallory" },
            };
            vm.FC = new Dictionary<string, object> { ["EntityList[0].Title"] = "" };
            vm.UpdateEntityList(false);
            using var dc = Dc();
            var added = dc.Set<GeDoc>().IgnoreQueryFilters().AsNoTracking().Single(x => x.ID == newId);
            Assert.AreEqual(Tenant, added.TenantCode, "the tenant of a row added by the list edit is the tenant of the signed-in user");
            Assert.AreEqual("bob", added.CreateBy);
            Assert.AreEqual("bob", added.UpdateBy);
        }

        // ---- list edit: the stored rows are read in the tenant of the user ----------------------------------------------------------------

        // the posted id is not found in the rows of the tenant, so the row is an add and meets the primary key: the save fails
        // (an ArgumentException from the in memory store, a DbUpdateException from a relational one)
        private static void ExpectFailure(Action action)
        {
            Exception caught = null;
            try { action(); } catch (Exception ex) { caught = ex; }
            Assert.IsNotNull(caught, "the save of the batch must fail");
        }

        private Guid Seed(string tenant, string title = "other")
        {
            using var dc = Dc(tenant);
            var doc = new GeDoc { ID = Guid.NewGuid(), Title = title, TenantCode = tenant, CreateBy = "alice", CreateTime = SeedTime };
            dc.Set<GeDoc>().Add(doc);
            dc.SaveChanges();
            return doc.ID;
        }

        private GeDoc Raw(Guid id)
        {
            using var dc = Dc();
            return dc.Set<GeDoc>().IgnoreQueryFilters().AsNoTracking().Single(x => x.ID == id);
        }

        private BasePagedListVM<GeDoc, BaseSearcher> PostedRows(WTMContext wtm, params Guid[] ids)
        {
            var vm = new BasePagedListVM<GeDoc, BaseSearcher> { Wtm = wtm };
            vm.EntityList = ids.Select(x => new GeDoc { ID = x, Title = "edited" }).ToList();
            vm.FC = new Dictionary<string, object> { ["EntityList[0].Title"] = "" };
            return vm;
        }

        [TestMethod]
        public void ListEdit_TenantUser_EditsOwnRow()
        {
            PostedRows(Wtm(), _docId).UpdateEntityList(false);
            var doc = StoredDoc();
            Assert.AreEqual("edited", doc.Title);
            Assert.AreEqual("bob", doc.UpdateBy);
            Assert.AreEqual(Tenant, doc.TenantCode);
            Assert.AreEqual("alice", doc.CreateBy, "an update is not an add");
        }

        [TestMethod]
        public void ListEdit_PostedIdOfAnotherTenantsRow_IsNotUpdated_WholeBatchFails()
        {
            var foreign = Seed(OtherTenant);
            var vm = PostedRows(Wtm(), _docId, foreign);
            vm.FC["EntityList[1].Title"] = "";
            ExpectFailure(() => vm.UpdateEntityList(false));
            Assert.AreEqual("other", Raw(foreign).Title);
            Assert.AreEqual(OtherTenant, Raw(foreign).TenantCode);
            Assert.AreEqual("alice", Raw(foreign).CreateBy);
            Assert.AreEqual("stored", StoredDoc().Title, "the own row of the same batch is not written either");
        }

        [TestMethod]
        public void ListEdit_PostedIdOfARowWithoutTenant_IsNotUpdated()
        {
            var hostRow = Seed(null);
            var vm = PostedRows(Wtm(), hostRow);
            ExpectFailure(() => vm.UpdateEntityList(false));
            var raw = Raw(hostRow);
            Assert.AreEqual("other", raw.Title);
            Assert.IsNull(raw.TenantCode);
            Assert.AreEqual("alice", raw.CreateBy);
            Assert.AreEqual(1, Count(hostRow), "no second row was written");
        }

        private int Count(Guid id)
        {
            using var dc = Dc();
            return dc.Set<GeDoc>().IgnoreQueryFilters().Count(x => x.ID == id);
        }

        [TestMethod]
        public void ListEdit_HostUser_EditsARowWithoutTenant()
        {
            var hostRow = Seed(null);
            PostedRows(Wtm(tenant: null), hostRow).UpdateEntityList(false);
            var raw = Raw(hostRow);
            Assert.AreEqual("edited", raw.Title);
            Assert.IsNull(raw.TenantCode);
            Assert.AreEqual("bob", raw.UpdateBy);
        }

        [TestMethod]
        public void ListEdit_HostUser_DoesNotMatchARowOfATenant()
        {
            var vm = PostedRows(Wtm(tenant: null), _docId);
            ExpectFailure(() => vm.UpdateEntityList(false));
            Assert.AreEqual("stored", StoredDoc().Title);
        }

        // ---- added rows: the members the framework owns ----------------------------------------------------------------------------------

        private static GeDoc EvilNew(Guid id) => new GeDoc { ID = id, Title = "added", TenantCode = "evil", IsValid = false, CreateBy = "mallory", CreateTime = EvilTime };

        [TestMethod]
        public void ListEdit_AddedRow_FrameworkOwnedMembersAreSetByTheFramework()
        {
            var newId = Guid.NewGuid();
            var vm = new BasePagedListVM<GeDoc, BaseSearcher> { Wtm = Wtm() };
            vm.EntityList = new List<GeDoc> { EvilNew(newId) };
            vm.FC = new Dictionary<string, object> { ["EntityList[0].Title"] = "" };
            vm.UpdateEntityList(false);
            var added = Raw(newId);
            Assert.AreEqual(Tenant, added.TenantCode);
            Assert.IsTrue(added.IsValid);
            Assert.AreEqual("bob", added.CreateBy);
            Assert.IsTrue(added.CreateTime.HasValue && added.CreateTime.Value > SeedTime && added.CreateTime.Value != EvilTime);
        }

        [TestMethod]
        public void ListEdit_AddedRow_Legacy_KeepsTheEarlierBehavior()
        {
            var newId = Guid.NewGuid();
            var emptyId = Guid.NewGuid();
            var vm = new BasePagedListVM<GeDoc, BaseSearcher> { Wtm = Wtm(true) };
            vm.EntityList = new List<GeDoc> { EvilNew(newId), new GeDoc { ID = emptyId, Title = "empty" } };
            vm.FC = new Dictionary<string, object> { ["EntityList[0].Title"] = "" };
            vm.UpdateEntityList(false);
            var added = Raw(newId);
            Assert.AreEqual(Tenant, added.TenantCode, "the tenant of a new row does not depend on the setting");
            Assert.AreEqual("mallory", added.CreateBy);
            Assert.AreEqual(EvilTime, added.CreateTime);
            Assert.IsFalse(added.IsValid, "the validity flag is left as posted");
            var empty = Raw(emptyId);
            Assert.AreEqual("bob", empty.CreateBy, "an empty creator is still filled");
            Assert.IsNotNull(empty.CreateTime);
        }

        private BaseCRUDVM<GeDoc> ChildAddVm(bool legacy, Guid newLineId)
        {
            var vm = new BaseCRUDVM<GeDoc> { Wtm = Wtm(legacy) };
            vm.Entity = new GeDoc
            {
                ID = _docId,
                Title = "edited",
                Lines = new List<GeLine>
                {
                    new GeLine { ID = _lineId, Text = "line", GeDocId = _docId },
                    new GeLine { ID = newLineId, Text = "new", TenantCode = "evil", IsValid = false, CreateBy = "mallory", CreateTime = EvilTime, GeDocId = _docId },
                }
            };
            vm.FC = new Dictionary<string, object> { ["Entity.Title"] = "", ["Entity.Lines[0].Text"] = "" };
            return vm;
        }

        private GeLine RawLine(Guid id)
        {
            using var dc = Dc();
            return dc.Set<GeLine>().IgnoreQueryFilters().AsNoTracking().Single(x => x.ID == id);
        }

        [TestMethod]
        public void ChildAdd_FrameworkOwnedMembersAreSetByTheFramework()
        {
            var id = Guid.NewGuid();
            var vm = ChildAddVm(false, id);
            vm.DoEdit(false);
            Assert.IsTrue(vm.MSD.IsValid);
            var line = RawLine(id);
            Assert.AreEqual(Tenant, line.TenantCode);
            Assert.IsTrue(line.IsValid);
            Assert.AreEqual("bob", line.CreateBy);
            Assert.IsTrue(line.CreateTime.HasValue && line.CreateTime.Value > SeedTime && line.CreateTime.Value != EvilTime);
            Assert.AreEqual("new", line.Text);
        }

        [TestMethod]
        public void ChildAdd_Legacy_KeepsTheEarlierBehavior()
        {
            var id = Guid.NewGuid();
            var vm = ChildAddVm(true, id);
            vm.DoEdit(false);
            var line = RawLine(id);
            Assert.AreEqual(Tenant, line.TenantCode);
            Assert.AreEqual("mallory", line.CreateBy);
            Assert.AreEqual(EvilTime, line.CreateTime);
            Assert.IsFalse(line.IsValid);
        }

        // ---- other shapes of the entity type ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void SoftDelete_PostedTenantCode_IsDeleted_TenantCodeUnchanged()
        {
            var vm = new BaseCRUDVM<GeDoc> { Wtm = Wtm() };
            vm.Entity = new GeDoc { ID = _docId, Title = "stored", TenantCode = "evil" };
            vm.FC = new Dictionary<string, object> { ["Entity.TenantCode"] = "" };
            vm.DoDelete();
            Assert.IsTrue(vm.MSD.IsValid);
            var doc = StoredDoc();
            Assert.IsFalse(doc.IsValid);
            Assert.AreEqual(Tenant, doc.TenantCode);
        }

        [TestMethod]
        public void Predicate_TenantImplementedOnABaseClass_AndHiddenWithNew()
        {
            Assert.IsTrue(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GeDerived), "TenantCode"));
            Assert.IsTrue(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GeDerived), "IsValid"));
            Assert.IsTrue(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GeShadow), "TenantCode"));
            Assert.IsFalse(ModelPropertyUpdateRules.IsProtectedEditMember(typeof(GeShadow), "Title"));
        }

        [TestMethod]
        public void FormEdit_TenantImplementedOnABaseClass_PostedTenantCodeIsNotWritten()
        {
            Guid id;
            using (var dc = Dc())
            {
                var row = new GeDerived { ID = Guid.NewGuid(), Title = "stored", TenantCode = Tenant, CreateBy = "alice", CreateTime = SeedTime };
                dc.Set<GeDerived>().Add(row);
                dc.SaveChanges();
                id = row.ID;
            }
            var vm = new BaseCRUDVM<GeDerived> { Wtm = Wtm() };
            vm.Entity = new GeDerived { ID = id, Title = "edited", TenantCode = "evil", IsValid = false, CreateBy = "mallory" };
            vm.FC = new[] { "Title", "TenantCode", "IsValid", "CreateBy" }.ToDictionary(x => "Entity." + x, x => (object)"");
            vm.DoEdit(false);
            using var read = Dc();
            var stored = read.Set<GeDerived>().IgnoreQueryFilters().AsNoTracking().Single(x => x.ID == id);
            Assert.AreEqual("edited", stored.Title);
            Assert.AreEqual(Tenant, stored.TenantCode);
            Assert.IsTrue(stored.IsValid);
            Assert.AreEqual("alice", stored.CreateBy);
        }

        [TestMethod]
        public void FormEdit_TenantHiddenWithNew_PostedTenantCodeIsNotWritten()
        {
            Guid id;
            using (var dc = Dc())
            {
                var row = new GeShadow { ID = Guid.NewGuid(), Title = "stored", TenantCode = Tenant };
                dc.Set<GeShadow>().Add(row);
                dc.SaveChanges();
                id = row.ID;
            }
            var vm = new BaseCRUDVM<GeShadow> { Wtm = Wtm() };
            vm.Entity = new GeShadow { ID = id, Title = "edited", TenantCode = "evil" };
            vm.FC = new[] { "Title", "TenantCode" }.ToDictionary(x => "Entity." + x, x => (object)"");
            vm.DoEdit(false);
            using var read = Dc();
            var stored = read.Set<GeShadow>().IgnoreQueryFilters().AsNoTracking().Single(x => x.ID == id);
            Assert.AreEqual("edited", stored.Title);
            Assert.AreEqual(Tenant, stored.TenantCode);
        }

        // ---- batch edit ----------------------------------------------------------------------------------------------------------------

        private BaseBatchVM<GeDoc, GeDocBatch> BatchVm(bool legacy)
        {
            var vm = new BaseBatchVM<GeDoc, GeDocBatch> { Wtm = Wtm(legacy) };
            vm.LinkedVM = new GeDocBatch { ID = Guid.NewGuid(), Title = "batch", Note = "mallory", TenantCode = "evil", IsValid = false, CreateBy = "mallory", CreateTime = EvilTime, UpdateBy = "mallory" };
            foreach (var name in new[] { "Title", "Note", "TenantCode", "IsValid", "CreateBy", "CreateTime", "UpdateBy" })
            {
                vm.FC.Add("LinkedVM." + name, 0);
            }
            vm.Ids = new[] { _docId.ToString() };
            return vm;
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task BatchEdit_RefusedMembersKept_OthersSaved_UpdateStampReplaced(bool useAsync)
        {
            var vm = BatchVm(false);
            vm.FC.Add("LinkedVM.ID", 0);
            var ok = useAsync ? await vm.DoBatchEditAsync() : vm.DoBatchEdit();
            Assert.IsTrue(ok, string.Join(";", vm.ErrorMessage.Values));
            var doc = StoredDoc();
            Assert.AreEqual("batch", doc.Title);
            Assert.AreEqual("keep", doc.Note, "CanNotEdit is honored by the batch edit");
            AssertUntouched(doc);
            Assert.AreEqual("bob", doc.UpdateBy, "a posted UpdateBy is replaced by the framework stamp");
        }

        [TestMethod]
        public void BatchEdit_Legacy_WritesTheMembersAgain()
        {
            var vm = BatchVm(true);
            var ok = vm.DoBatchEdit();
            Assert.IsTrue(ok, string.Join(";", vm.ErrorMessage.Values));
            using var dc = Dc();
            var doc = dc.Set<GeDoc>().IgnoreQueryFilters().AsNoTracking().Single(x => x.ID == _docId);
            Assert.AreEqual("evil", doc.TenantCode);
            Assert.IsFalse(doc.IsValid);
            Assert.AreEqual("mallory", doc.CreateBy);
            Assert.AreEqual("mallory", doc.Note);
            Assert.AreEqual("bob", doc.UpdateBy);
        }

        // ---- import overwrite ----------------------------------------------------------------------------------------------------------

        private (GeImportVM vm, Guid id) ImportVm(bool legacy)
        {
            var id = Guid.NewGuid();
            using (var dc = Dc())
            {
                dc.Set<GeImportDoc>().Add(new GeImportDoc { ID = id, Code = "c1", Title = "old", TenantCode = Tenant, CreateBy = "alice", CreateTime = SeedTime });
                dc.SaveChanges();
            }
            return (new GeImportVM { Wtm = Wtm(legacy) }, id);
        }

        private static void Overwrite(GeImportVM vm, GeImportDoc item, GeImportDoc exist)
        {
            var method = typeof(BaseImportVM<GeImportTemplate, GeImportDoc>).GetMethod("PrepareItemForSave", BindingFlags.NonPublic | BindingFlags.Instance);
            method.Invoke(vm, new object[] { item, exist });
            vm.DC.SaveChanges();
        }

        private GeImportDoc StoredImport(Guid id)
        {
            using var dc = Dc();
            return dc.Set<GeImportDoc>().IgnoreQueryFilters().AsNoTracking().Single(x => x.ID == id);
        }

        [TestMethod]
        public void ImportOverwrite_RefusesTenantCodeAndIsValid_KeepsCreateMembersWritable()
        {
            var (vm, id) = ImportVm(false);
            var exist = vm.DC.Set<GeImportDoc>().Single(x => x.ID == id);
            var item = new GeImportDoc { Code = "c1", Title = "new", TenantCode = "evil", IsValid = false, CreateBy = "importer" };
            Overwrite(vm, item, exist);
            var doc = StoredImport(id);
            Assert.AreEqual("new", doc.Title);
            Assert.AreEqual(Tenant, doc.TenantCode);
            Assert.IsTrue(doc.IsValid);
            Assert.AreEqual("importer", doc.CreateBy, "CreateBy stays writable on an import whose template maps it");
            Assert.AreEqual("bob", doc.UpdateBy);
        }

        [TestMethod]
        public void ImportOverwrite_Legacy_WritesTenantCodeAndIsValid()
        {
            var (vm, id) = ImportVm(true);
            var exist = vm.DC.Set<GeImportDoc>().Single(x => x.ID == id);
            var item = new GeImportDoc { Code = "c1", Title = "new", TenantCode = "evil", IsValid = false, CreateBy = "importer" };
            Overwrite(vm, item, exist);
            var doc = StoredImport(id);
            Assert.AreEqual("evil", doc.TenantCode);
            Assert.IsFalse(doc.IsValid);
        }

        // ---- the setting ---------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Setting_DefaultIsProtected_AndBindsStrictly()
        {
            Assert.IsFalse(new Configs().GenericEdit.LegacyProtectedMemberWrites);
            var empty = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build().Get<Configs>();
            Assert.IsTrue(empty == null || empty.GenericEdit.LegacyProtectedMemberWrites == false);
            var on = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["GenericEdit:LegacyProtectedMemberWrites"] = "true" }).Build().Get<Configs>();
            Assert.IsTrue(on.GenericEdit.LegacyProtectedMemberWrites);
            var off = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["GenericEdit:LegacyProtectedMemberWrites"] = "false" }).Build().Get<Configs>();
            Assert.IsFalse(off.GenericEdit.LegacyProtectedMemberWrites);
        }

        [TestMethod]
        public void Setting_RejectsNonBooleanValues()
        {
            foreach (var value in new[] { "0", "1", "", "abc", "yes" })
            {
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["GenericEdit:LegacyProtectedMemberWrites"] = value }).Build();
                Assert.ThrowsException<InvalidOperationException>(() => config.Get<Configs>(), $"value '{value}' must be rejected");
            }
        }

        [TestMethod]
        public void Setting_IsReportedAtStartUpOnlyWhenOn()
        {
            var off = new CapturingLoggerFactory();
            GenericEditGuard.LogRelaxedWarning(new Configs(), off.CreateLogger("t"));
            Assert.IsFalse(off.Entries.Any(x => x.Message.Contains("LegacyProtectedMemberWrites")));

            var on = new CapturingLoggerFactory();
            GenericEditGuard.LogRelaxedWarning(Legacy(true), on.CreateLogger("t"));
            Assert.IsTrue(on.Entries.Any(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Warning && x.Message.Contains("GenericEdit:LegacyProtectedMemberWrites")));
            GenericEditGuard.LogRelaxedWarning(null, on.CreateLogger("t"));
        }
    }
}
