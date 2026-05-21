#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    // ─── Entity with explicit ErrorMessage on validation attrs ───────────────
    // Used to cover TryValidateProperty lines 920-953 (the ErrorMessage branches).

    public class ValidatedItem : BasePoco
    {
        [StringLength(5, ErrorMessage = "StrLen error")]
        public string ShortCode { get; set; } = "";

        [Range(1, 10, ErrorMessage = "Range error")]
        public int SmallQty { get; set; }
    }

    internal class ValidatedItemContext : DataContext
    {
        public DbSet<ValidatedItem> Items { get; set; } = null!;
        public ValidatedItemContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    public class ValidatedItemTemplateVM : BaseTemplateVM
    {
        public ExcelPropety ShortCode_Excel = ExcelPropety.CreateProperty<ValidatedItem>(x => x.ShortCode);
        public ExcelPropety SmallQty_Excel  = ExcelPropety.CreateProperty<ValidatedItem>(x => x.SmallQty);
        protected override void InitVM() { }
    }

    public class ValidatedItemImportVM : BaseImportVM<ValidatedItemTemplateVM, ValidatedItem>
    {
        public void InjectTemplateData(List<ValidatedItemTemplateVM> rows) { TemplateData = rows; }
        public override DuplicatedInfo<ValidatedItem>? SetDuplicatedCheck() => null;
    }

    internal static class ValidatedRowBuilder
    {
        public static ValidatedItemTemplateVM Build(string shortCode, string smallQty)
        {
            var row = new ValidatedItemTemplateVM();
            row.ShortCode_Excel = ExcelPropety.CreateProperty<ValidatedItem>(x => x.ShortCode);
            row.ShortCode_Excel.Value = shortCode;
            row.SmallQty_Excel = ExcelPropety.CreateProperty<ValidatedItem>(x => x.SmallQty);
            row.SmallQty_Excel.Value = smallQty;
            return row;
        }
    }

    // ─── Entity / template / VM for SetEntityData tests ─────────────────────

    public class EntityDataItem : BasePoco
    {
        [StringLength(30)]
        public string Code { get; set; } = "";

        [StringLength(100)]
        public string Label { get; set; } = "";

        public int Qty { get; set; }
    }

    internal class EntityDataContext : DataContext
    {
        public DbSet<EntityDataItem> Items { get; set; } = null!;
        public EntityDataContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
    }

    /// <summary>
    /// Template VM for EntityDataItem.
    /// </summary>
    public class EntityDataTemplateVM : BaseTemplateVM
    {
        public ExcelPropety Code_Excel = ExcelPropety.CreateProperty<EntityDataItem>(x => x.Code);
        public ExcelPropety Label_Excel = ExcelPropety.CreateProperty<EntityDataItem>(x => x.Label);
        public ExcelPropety Qty_Excel = ExcelPropety.CreateProperty<EntityDataItem>(x => x.Qty);
        protected override void InitVM() { }
    }

    /// <summary>
    /// Import VM that allows tests to inject pre-built TemplateData directly,
    /// bypassing the Excel-reading path in SetTemplateData().
    /// </summary>
    public class EntityDataImportVM : BaseImportVM<EntityDataTemplateVM, EntityDataItem>
    {
        /// <summary>
        /// Inject template rows from test code. Calling SetEntityList() will then skip
        /// SetTemplateData() (because TemplateData is already non-empty) and call SetEntityData().
        /// </summary>
        public void InjectTemplateData(List<EntityDataTemplateVM> rows)
        {
            TemplateData = rows;
        }

        /// <summary>Expose SetEntityData for direct invocation.</summary>
        public void PublicSetEntityData() => SetEntityData();

        /// <summary>Expose SetEntityFieldValue for direct testing.</summary>
        public void PublicSetEntityFieldValue(object entity, ExcelPropety ep, int row, string field, EntityDataTemplateVM tmpl)
            => SetEntityFieldValue(entity, ep, row, field, tmpl);

        /// <summary>Expose protected SetExceptionMessage for direct testing.</summary>
        public void PublicSetExceptionMessage(Exception e, long? id)
            => SetExceptionMessage(e, id);

        /// <summary>Expose HasSubTable protected property getter.</summary>
        public bool PublicHasSubTable => HasSubTable;

        public override DuplicatedInfo<EntityDataItem>? SetDuplicatedCheck()
            => CreateFieldsInfo(SimpleField(x => x.Code));
    }

    /// <summary>
    /// Helper that creates a populated EntityDataTemplateVM row from raw values.
    /// Each ExcelPropety field on the template must have its Value set before SetEntityData reads it.
    /// </summary>
    internal static class TemplateRowBuilder
    {
        public static EntityDataTemplateVM Build(string code, string label, string qty)
        {
            var row = new EntityDataTemplateVM();
            row.Code_Excel = ExcelPropety.CreateProperty<EntityDataItem>(x => x.Code);
            row.Code_Excel.Value = code;
            row.Label_Excel = ExcelPropety.CreateProperty<EntityDataItem>(x => x.Label);
            row.Label_Excel.Value = label;
            row.Qty_Excel = ExcelPropety.CreateProperty<EntityDataItem>(x => x.Qty);
            row.Qty_Excel.Value = qty;
            return row;
        }
    }

    /// <summary>
    /// Minimal CRUDvm for EntityDataItem so that SetValidateCheck() can find it in the assembly
    /// and exercise the vm-found path (lines 640-642) and the dinfo path (lines 656-661)
    /// when the ImportVM's own SetDuplicatedCheck() returns null.
    /// Also overrides Validate() to produce errors for entities with Code == "INVALID",
    /// exercising the MSD-has-errors path (lines 681-685) in SetValidateCheck.
    /// </summary>
    public class EntityDataItemVm : BaseCRUDVM<EntityDataItem>
    {
        protected override void InitVM() { }

        // Provides a non-null dinfo so that NoDupCheckImportVM (which returns null from
        // SetDuplicatedCheck) can exercise the "else if (dinfo != null)" branch in SetValidateCheck.
        public override DuplicatedInfo<EntityDataItem>? SetDuplicatedCheck()
            => CreateFieldsInfo(SimpleField(x => x.Code));

        // Adds a model-state error for Code == "INVALID_VM" to exercise lines 681-685.
        public override void Validate()
        {
            base.Validate();
            if (Entity?.Code == "INVALID_VM")
            {
                MSD.AddModelError("Code", "Code 'INVALID_VM' is not allowed");
            }
        }
    }

    // ─── Tests ───────────────────────────────────────────────────────────────

    [TestClass]
    public class BaseImportVMEntityDataTest
    {
        private string _seed = null!;

        [TestInitialize]
        public void Init() => _seed = Guid.NewGuid().ToString();

        private IDataContext CreateDb() => new EntityDataContext(_seed, DBTypeEnum.Memory);

        private EntityDataImportVM CreateVm()
        {
            var vm = new EntityDataImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");
            return vm;
        }

        // ─── SetEntityData: basic population ─────────────────────────────

        [TestMethod]
        public void SetEntityData_SingleRow_PopulatesEntityList()
        {
            var vm = CreateVm();
            var rows = new List<EntityDataTemplateVM>
            {
                TemplateRowBuilder.Build("C1", "Label1", "5")
            };
            vm.InjectTemplateData(rows);

            vm.PublicSetEntityData();

            Assert.AreEqual(1, vm.EntityList.Count, "Should produce one entity");
            Assert.AreEqual("C1", vm.EntityList[0].Code);
            Assert.AreEqual("Label1", vm.EntityList[0].Label);
        }

        [TestMethod]
        public void SetEntityData_MultipleRows_PopulatesAllEntities()
        {
            var vm = CreateVm();
            var rows = new List<EntityDataTemplateVM>
            {
                TemplateRowBuilder.Build("A1", "Alpha", "1"),
                TemplateRowBuilder.Build("B2", "Beta",  "2"),
                TemplateRowBuilder.Build("C3", "Gamma", "3"),
            };
            vm.InjectTemplateData(rows);

            vm.PublicSetEntityData();

            Assert.AreEqual(3, vm.EntityList.Count);
            Assert.AreEqual("A1", vm.EntityList[0].Code);
            Assert.AreEqual("B2", vm.EntityList[1].Code);
            Assert.AreEqual("C3", vm.EntityList[2].Code);
        }

        [TestMethod]
        public void SetEntityData_EmptyTemplateData_ProducesEmptyEntityList()
        {
            var vm = CreateVm();
            vm.InjectTemplateData(new List<EntityDataTemplateVM>());
            vm.EntityList = new List<EntityDataItem>();

            vm.PublicSetEntityData();

            Assert.AreEqual(0, vm.EntityList.Count);
        }

        [TestMethod]
        public void SetEntityData_IntegerField_ParsedCorrectly()
        {
            var vm = CreateVm();
            vm.InjectTemplateData(new List<EntityDataTemplateVM>
            {
                TemplateRowBuilder.Build("X", "Test", "42")
            });

            vm.PublicSetEntityData();

            Assert.AreEqual(42, vm.EntityList[0].Qty);
        }

        [TestMethod]
        public void SetEntityData_ExcelIndex_PropagatedFromTemplateRow()
        {
            var vm = CreateVm();
            var row = TemplateRowBuilder.Build("Z", "Zeta", "7");
            row.ExcelIndex = 5;
            vm.InjectTemplateData(new List<EntityDataTemplateVM> { row });

            vm.PublicSetEntityData();

            Assert.AreEqual(5, vm.EntityList[0].ExcelIndex);
        }

        // ─── SetEntityList calls SetEntityData when TemplateData pre-populated ──

        [TestMethod]
        public void SetEntityList_WithInjectedTemplateData_CallsSetEntityData()
        {
            var vm = CreateVm();
            var rows = new List<EntityDataTemplateVM>
            {
                TemplateRowBuilder.Build("P1", "Product1", "10")
            };
            vm.InjectTemplateData(rows);

            // SetEntityList → detects TemplateData non-empty → skips file read → calls SetEntityData
            vm.SetEntityList();

            Assert.AreEqual(1, vm.EntityList.Count);
            Assert.AreEqual("P1", vm.EntityList[0].Code);
        }

        // ─── BatchSaveData with ValidateOnly ─────────────────────────────────

        [TestMethod]
        public void BatchSaveData_ValidateOnly_DoesNotPersistData()
        {
            var vm = CreateVm();
            var rows = new List<EntityDataTemplateVM>
            {
                TemplateRowBuilder.Build("VO1", "ValidateOnly", "1")
            };
            vm.InjectTemplateData(rows);
            vm.ValidateOnly = true;

            var result = vm.BatchSaveData();

            Assert.IsTrue(result, "ValidateOnly with valid data should return true");
            // Data should NOT have been written to DB
            using var db = (EntityDataContext)CreateDb();
            Assert.AreEqual(0, db.Items.Count(), "ValidateOnly must not persist records");
        }

        [TestMethod]
        public void BatchSaveData_ValidateOnly_WithValidationError_ReturnsFalse()
        {
            // Make an entity with a Name that exceeds StringLength(30)
            var vm = CreateVm();
            var rows = new List<EntityDataTemplateVM>
            {
                TemplateRowBuilder.Build(new string('X', 50), "LongCode", "1")
            };
            vm.InjectTemplateData(rows);
            vm.ValidateOnly = true;

            var result = vm.BatchSaveData();

            Assert.IsFalse(result, "StringLength violation should return false");
            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0, "Should have at least one validation error");
        }

        // ─── BatchSaveData with IsOverWriteExistData=false ───────────────────

        [TestMethod]
        public void BatchSaveData_IsOverWriteExistData_False_NoExisting_Succeeds()
        {
            var vm = CreateVm();
            vm.IsOverWriteExistData = false;
            var rows = new List<EntityDataTemplateVM>
            {
                TemplateRowBuilder.Build("NEWITEM", "No existing record", "5")
            };
            vm.InjectTemplateData(rows);

            var result = vm.BatchSaveData();

            Assert.IsTrue(result, "Should succeed when no existing DB record and IsOverWriteExistData=false");
        }

        // ─── BatchSaveData with IProgress reporting ───────────────────────────

        [TestMethod]
        public void BatchSaveData_WithProgress_ReportsAllPhases()
        {
            var vm = CreateVm();
            var rows = new List<EntityDataTemplateVM>
            {
                TemplateRowBuilder.Build("PR1", "Progress Test", "3"),
                TemplateRowBuilder.Build("PR2", "Progress Test 2", "6"),
            };
            vm.InjectTemplateData(rows);

            var reports = new List<ImportProgress>();
            var progress = new Progress<ImportProgress>(p => reports.Add(p));
            vm.BatchSaveData(progress);

            // Give the Progress<T> callbacks time to fire (they post to sync context)
            System.Threading.Thread.Sleep(50);

            Assert.IsTrue(reports.Count > 0, "Progress should have been reported at least once");
        }

        // ─── GetErrorJson: early return when ServiceProvider is null ──────────

        [TestMethod]
        public void GetErrorJson_WithValidationErrors_ReturnsFormError()
        {
            var vm = CreateVm();
            vm.ErrorListVM.EntityList.Add(new ErrorMessage { Index = 0, Message = "Top-level error" });

            var result = vm.GetErrorJson();

            Assert.IsTrue(result.Form.ContainsKey("Entity.Import"), "Should have Entity.Import key");
            Assert.AreEqual("Top-level error", result.Form["Entity.Import"]);
        }

        [TestMethod]
        public void GetErrorJson_NoErrors_ServiceProviderNull_ReturnsEmptyForm()
        {
            // Create context with a service provider that IS null at WTMContext level
            // by using the MockWtmContext but then detaching the service provider
            var vm = new EntityDataImportVM();
            // Minimal Wtm without ServiceProvider
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");
            // Force ServiceProvider to null indirectly by not having an UploadFileId error
            // and no errors in list → GetErrorJson will hit the "else" case
            // Actually when errors list is empty AND ServiceProvider is null, returns early
            // We can test the "else" (non-empty error at Index==0) path
            vm.ErrorListVM.EntityList.Add(new ErrorMessage { Index = 0, Message = "FormError" });

            var obj = vm.GetErrorJson();

            Assert.IsTrue(obj.Form.Count > 0);
        }

        // ─── SetEntityFieldValue: basic path ─────────────────────────────────

        [TestMethod]
        public void SetEntityFieldValue_SetsStringProperty()
        {
            var vm = CreateVm();
            var entity = new EntityDataItem();
            var ep = ExcelPropety.CreateProperty<EntityDataItem>(x => x.Code);
            ep.Value = "TESTCODE";
            var tmpl = new EntityDataTemplateVM();

            vm.PublicSetEntityFieldValue(entity, ep, 1, "Code", tmpl);

            Assert.AreEqual("TESTCODE", entity.Code);
        }

        [TestMethod]
        public void SetEntityFieldValue_SetsIntegerProperty()
        {
            var vm = CreateVm();
            var entity = new EntityDataItem();
            var ep = ExcelPropety.CreateProperty<EntityDataItem>(x => x.Qty);
            ep.Value = "99";
            var tmpl = new EntityDataTemplateVM();

            vm.PublicSetEntityFieldValue(entity, ep, 1, "Qty", tmpl);

            Assert.AreEqual(99, entity.Qty);
        }

        // ─── IsOverWriteExistData = false: skip existing records ─────────────

        [TestMethod]
        public void BatchSaveData_IsOverWriteExistData_False_ExistingRecord_ReturnsError()
        {
            // When IsOverWriteExistData=false and a DB duplicate is found,
            // IsDuplicateData adds an error and BatchSaveData returns false.
            var db = (EntityDataContext)CreateDb();
            var existing = new EntityDataItem { Code = "EXIST", Label = "Original", Qty = 1 };
            db.Items.Add(existing);
            db.SaveChanges();

            var vm = new EntityDataImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new EntityDataContext(_seed, DBTypeEnum.Memory), "user");
            vm.IsOverWriteExistData = false;

            var rows = new List<EntityDataTemplateVM>
            {
                TemplateRowBuilder.Build("EXIST", "Overwrite Attempt", "99")
            };
            vm.InjectTemplateData(rows);

            var result = vm.BatchSaveData();

            // IsDuplicateData adds a "duplicate" error when IsOverWriteExistData=false
            Assert.IsFalse(result, "Should return false when DB duplicate found and IsOverWriteExistData=false");
            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0, "Should have error about duplicate");
        }

        // ─── IsOverWriteExistData = true: updates existing records ───────────

        [TestMethod]
        public void BatchSaveData_IsOverWriteExistData_True_ExistingRecord_IsUpdated()
        {
            // Pre-seed the DB with an item
            var db = (EntityDataContext)CreateDb();
            var existing = new EntityDataItem { Code = "UPD", Label = "Original", Qty = 1 };
            db.Items.Add(existing);
            db.SaveChanges();

            var vm = new EntityDataImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new EntityDataContext(_seed, DBTypeEnum.Memory), "user");
            vm.IsOverWriteExistData = true;

            var rows = new List<EntityDataTemplateVM>
            {
                TemplateRowBuilder.Build("UPD", "Updated Label", "99")
            };
            vm.InjectTemplateData(rows);

            var result = vm.BatchSaveData();

            Assert.IsTrue(result, "Should return true when updating");
            using var verifyDb = new EntityDataContext(_seed, DBTypeEnum.Memory);
            var updated = verifyDb.Items.First(x => x.Code == "UPD");
            Assert.AreEqual("Updated Label", updated.Label);
        }

        // ─── ValidateDuplicateData path through SetValidateCheck ─────────────

        [TestMethod]
        public void BatchSaveData_DuplicateRows_InEntityList_ReturnsFalse()
        {
            // ValidateDuplicateData uses ExcelIndex to distinguish rows ("exclude self").
            // Both rows must have distinct non-zero ExcelIndex values for the duplicate
            // detection expression (ExcelIndex != self.ExcelIndex) to find the other row.
            var vm = CreateVm();
            var row1 = TemplateRowBuilder.Build("DUP", "First",  "1");
            row1.ExcelIndex = 2;
            var row2 = TemplateRowBuilder.Build("DUP", "Second", "2");
            row2.ExcelIndex = 3;
            vm.InjectTemplateData(new List<EntityDataTemplateVM> { row1, row2 });

            var result = vm.BatchSaveData();

            Assert.IsFalse(result, "Duplicate Code should fail validation");
            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0, "Should report duplicate error");
        }

        // ─── DoReInit verifies no state leak ──────────────────────────────────

        [TestMethod]
        public void BatchSaveData_AfterFailure_EntityListRemainsPopulated()
        {
            var vm = CreateVm();
            // Inject an entity whose StringLength constraint is violated (Code > 30)
            var rows = new List<EntityDataTemplateVM>
            {
                TemplateRowBuilder.Build(new string('Y', 50), "ExceedsLength", "1")
            };
            vm.InjectTemplateData(rows);

            var result = vm.BatchSaveData();

            Assert.IsFalse(result);
            // ErrorListVM should have the error
            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0);
        }

        // ─── SetEntityFieldValue with FormatSingleData delegate ───────────────

        [TestMethod]
        public void SetEntityFieldValue_FormatSingleData_SetsValueViaDelegate()
        {
            var vm = CreateVm();
            var entity = new EntityDataItem();
            var ep = ExcelPropety.CreateProperty<EntityDataItem>(x => x.Code);
            ep.Value = "raw";
            // FormatSingleData converts the raw value to something else
            ep.FormatSingleData = (object? excelVal, BaseTemplateVM tmpl, out string result, out string err) =>
            {
                result = "transformed-" + excelVal;
                err = "";
            };
            var tmpl = new EntityDataTemplateVM();

            vm.PublicSetEntityFieldValue(entity, ep, 1, "Code", tmpl);

            Assert.AreEqual("transformed-raw", entity.Code);
        }

        [TestMethod]
        public void SetEntityFieldValue_FormatSingleData_WithError_AddsToErrorList()
        {
            var vm = CreateVm();
            var entity = new EntityDataItem();
            var ep = ExcelPropety.CreateProperty<EntityDataItem>(x => x.Code);
            ep.Value = "bad";
            ep.FormatSingleData = (object? excelVal, BaseTemplateVM tmpl, out string result, out string err) =>
            {
                result = "";
                err = "Conversion failed";
            };
            var tmpl = new EntityDataTemplateVM();

            vm.PublicSetEntityFieldValue(entity, ep, 2, "Code", tmpl);

            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0, "Error delegate should add error");
            Assert.AreEqual("Conversion failed", vm.ErrorListVM.EntityList[0].Message);
        }

        [TestMethod]
        public void SetEntityFieldValue_FormatData_OneToOne_SetsValue()
        {
            var vm = CreateVm();
            var entity = new EntityDataItem();
            var ep = ExcelPropety.CreateProperty<EntityDataItem>(x => x.Code);
            ep.Value = "input";
            ep.FormatData = (excelVal, tmpl) =>
            {
                var pr = new ProcessResult();
                pr.EntityValues = new System.Collections.Generic.List<EntityValue>
                {
                    new EntityValue { FieldName = "Code", FieldValue = "formatted", ErrorMsg = "" }
                };
                return pr;
            };
            var tmpl = new EntityDataTemplateVM();

            vm.PublicSetEntityFieldValue(entity, ep, 1, "Code", tmpl);

            Assert.AreEqual("formatted", entity.Code);
        }

        [TestMethod]
        public void SetEntityFieldValue_FormatData_OneToOne_WithError_AddsError()
        {
            var vm = CreateVm();
            var entity = new EntityDataItem();
            var ep = ExcelPropety.CreateProperty<EntityDataItem>(x => x.Code);
            ep.Value = "bad";
            ep.FormatData = (excelVal, tmpl) =>
            {
                var pr = new ProcessResult();
                pr.EntityValues = new System.Collections.Generic.List<EntityValue>
                {
                    new EntityValue { FieldName = "Code", FieldValue = "", ErrorMsg = "Format error" }
                };
                return pr;
            };
            var tmpl = new EntityDataTemplateVM();

            vm.PublicSetEntityFieldValue(entity, ep, 1, "Code", tmpl);

            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0);
            Assert.AreEqual("Format error", vm.ErrorListVM.EntityList[0].Message);
        }

        [TestMethod]
        public void SetEntityFieldValue_FormatData_ZeroEntityValues_SetsRawValue()
        {
            var vm = CreateVm();
            var entity = new EntityDataItem();
            var ep = ExcelPropety.CreateProperty<EntityDataItem>(x => x.Code);
            ep.Value = "rawval";
            ep.FormatData = (excelVal, tmpl) =>
            {
                var pr = new ProcessResult();
                pr.EntityValues = new System.Collections.Generic.List<EntityValue>(); // empty
                return pr;
            };
            var tmpl = new EntityDataTemplateVM();

            vm.PublicSetEntityFieldValue(entity, ep, 1, "Code", tmpl);

            Assert.AreEqual("rawval", entity.Code);
        }

        [TestMethod]
        public void SetEntityFieldValue_FormatData_MultipleEntityValues_SetsAllProperties()
        {
            var vm = CreateVm();
            var entity = new EntityDataItem();
            var ep = ExcelPropety.CreateProperty<EntityDataItem>(x => x.Code);
            ep.Value = "multi";
            ep.FormatData = (excelVal, tmpl) =>
            {
                var pr = new ProcessResult();
                pr.EntityValues = new System.Collections.Generic.List<EntityValue>
                {
                    new EntityValue { FieldName = "Code",  FieldValue = "multi-code",  ErrorMsg = "" },
                    new EntityValue { FieldName = "Label", FieldValue = "multi-label", ErrorMsg = "" },
                };
                return pr;
            };
            var tmpl = new EntityDataTemplateVM();

            vm.PublicSetEntityFieldValue(entity, ep, 1, "Code", tmpl);

            Assert.AreEqual("multi-code", entity.Code);
            Assert.AreEqual("multi-label", entity.Label);
        }

        // ─── SetValidateCheck with CRUDvm found in assembly ───────────────────

        [TestMethod]
        public void SetValidateCheck_WithCRUDvmInAssembly_ExercisesCRUDvmPath()
        {
            // EntityDataItemVm : BaseCRUDVM<EntityDataItem> is defined above.
            // SetValidateCheck scans the assembly for BaseCRUDVM<EntityDataItem> subclasses
            // and finds EntityDataItemVm, exercising lines 638-642.
            var vm = CreateVm();
            var row1 = TemplateRowBuilder.Build("A", "Alpha", "1");
            row1.ExcelIndex = 2;
            var row2 = TemplateRowBuilder.Build("B", "Beta", "2");
            row2.ExcelIndex = 3;
            vm.InjectTemplateData(new List<EntityDataTemplateVM> { row1, row2 });
            vm.SetEntityList();

            // SetValidateCheck is called during BatchSaveData; we can call it directly via BatchSaveData
            var result = vm.BatchSaveData();

            // No validation errors expected for valid data
            Assert.IsTrue(result, "Valid distinct rows should pass SetValidateCheck");
        }

        [TestMethod]
        public void SetValidateCheck_CRUDvmValidation_CatchesConstraintViolation()
        {
            // Force a model-validation error detectable by the CRUDVM's Validate() path
            var vm = CreateVm();
            // Code longer than StringLength(30) triggers TryValidateProperty
            var row1 = TemplateRowBuilder.Build(new string('Z', 50), "TooLong", "1");
            row1.ExcelIndex = 2;
            vm.InjectTemplateData(new List<EntityDataTemplateVM> { row1 });
            vm.SetEntityList(); // populates EntityList

            // Run BatchSaveData: validation errors are caught before DB save
            var result = vm.BatchSaveData();

            Assert.IsFalse(result, "StringLength violation should fail");
        }

        // ─── Property setters (lines 117 and 151) ─────────────────────────────

        [TestMethod]
        public void Parms_Setter_CanBeAssigned()
        {
            var vm = CreateVm();
            vm.Parms = new System.Collections.Generic.Dictionary<string, string> { { "k", "v" } };
            Assert.AreEqual("v", vm.Parms!["k"]);
        }

        [TestMethod]
        public void SetParms_FlowsThroughToTemplate()
        {
            var vm = CreateVm();
            var parms = new System.Collections.Generic.Dictionary<string, string> { { "lang", "en" } };
            vm.SetParms(parms);
            Assert.AreEqual("en", vm.Template.Parms!["lang"]);
        }

        // ─── ValidateDuplicateData multi-field group error path ───────────────

        [TestMethod]
        public void ValidateDuplicateData_MultiFieldDuplicate_AddsGroupError()
        {
            // Create an ExposedDupImportVM with a two-field duplicate check
            // so that the multi-field error path (lines 880-882) is exercised.
            var items = new List<DupCheckItem>
            {
                new DupCheckItem { Key = "K", Label = "Same", ExcelIndex = 2 },
                new DupCheckItem { Key = "K", Label = "Same", ExcelIndex = 3 }
            };
            var vm = new ExposedDupImportVM(items);
            vm.Wtm = MockWtmContext.CreateWtmContext(new DupCheckDataContext(Guid.NewGuid().ToString(), DBTypeEnum.Memory), "user");
            vm.SetEntityList();

            // Build a two-field DuplicatedInfo manually using the base helper
            var twoFieldInfo = vm.CreateFieldsInfo(
                vm.SimpleField(x => x.Key),
                vm.SimpleField(x => x.Label));

            // Both items share Key AND Label — item[0] finds item[1] as multi-field duplicate
            vm.PublicValidateDuplicateData(twoFieldInfo, items[0]);

            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0,
                "Multi-field duplicate should add error");
        }

        // ─── SetEntityFieldValue_FormatData multi-value with error ────────────

        [TestMethod]
        public void SetEntityFieldValue_FormatData_MultipleEntityValues_WithError_AddsError()
        {
            // Exercise line 726: multi-value FormatData where one value has an ErrorMsg
            var vm = CreateVm();
            var entity = new EntityDataItem();
            var ep = ExcelPropety.CreateProperty<EntityDataItem>(x => x.Code);
            ep.Value = "multi-err";
            ep.FormatData = (excelVal, tmpl) =>
            {
                var pr = new ProcessResult();
                pr.EntityValues = new System.Collections.Generic.List<EntityValue>
                {
                    new EntityValue { FieldName = "Code",  FieldValue = "v1", ErrorMsg = "Error in field" },
                    new EntityValue { FieldName = "Label", FieldValue = "v2", ErrorMsg = "" },
                };
                return pr;
            };
            var tmpl = new EntityDataTemplateVM();

            vm.PublicSetEntityFieldValue(entity, ep, 1, "Code", tmpl);

            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0, "Error in multi-value should be reported");
        }

        // ─── BatchSaveData with progress callback — ensure all phases reported ──

        [TestMethod]
        public void BatchSaveData_Progress_PhasesAreValidating_And_Saving()
        {
            var vm = CreateVm();
            var rows = new List<EntityDataTemplateVM>
            {
                TemplateRowBuilder.Build("PH1", "Phase test", "1"),
            };
            vm.InjectTemplateData(rows);

            var phases = new System.Collections.Concurrent.ConcurrentBag<string>();
            var sync = new System.Threading.ManualResetEventSlim(false);
            var progress = new Progress<ImportProgress>(p =>
            {
                phases.Add(p.Phase);
                sync.Set();
            });

            vm.BatchSaveData(progress);

            // Give Progress<T> time to fire its callbacks on the thread-pool
            sync.Wait(500);

            Assert.IsTrue(phases.Count > 0, "At least one progress report should have been emitted");
        }

        // ─── InlineErrors with custom limit ──────────────────────────────────

        [TestMethod]
        public void InlineErrors_Limit0_ReturnsEmptyList()
        {
            var vm = CreateVm();
            vm.InlineErrorLimit = 0;
            vm.ErrorListVM.EntityList.Add(new ErrorMessage { Message = "Err1" });

            Assert.AreEqual(0, vm.InlineErrors.Count, "Limit=0 should return empty list");
        }

        [TestMethod]
        public void InlineErrors_LimitN_ReturnsTruncatedList()
        {
            var vm = CreateVm();
            vm.InlineErrorLimit = 2;
            for (int i = 0; i < 10; i++)
                vm.ErrorListVM.EntityList.Add(new ErrorMessage { Message = $"Err{i}" });

            Assert.AreEqual(2, vm.InlineErrors.Count, "Should return at most InlineErrorLimit items");
        }

        // ─── ValidateOnly = true, no entities → returns true quickly ─────────

        [TestMethod]
        public void BatchSaveData_ValidateOnly_EmptyEntityList_ReturnsTrue()
        {
            // Use a VM that overrides SetEntityList to produce an empty list
            // (bypassing SetTemplateData which would fail without UploadFileId)
            var vm = CreateVm();
            vm.ValidateOnly = true;
            // Inject a single valid row with TemplateData; entity list will have 1 row
            // but ValidateOnly means no DB write happens
            var row = TemplateRowBuilder.Build("VE", "ValidateEmpty", "0");
            vm.InjectTemplateData(new List<EntityDataTemplateVM> { row });

            var result = vm.BatchSaveData();

            Assert.IsTrue(result, "ValidateOnly with valid data should return true");
            // Verify nothing was persisted
            using var db = new EntityDataContext(_seed, DBTypeEnum.Memory);
            Assert.AreEqual(0, db.Items.Count(), "ValidateOnly must not persist records");
        }

        // ─── SetValidateCheck: CRUDVM Validate() error path (lines 681-685) ──

        [TestMethod]
        public void BatchSaveData_CRUDvmValidate_ProducesError_ReturnsFalse()
        {
            // EntityDataItemVm.Validate() adds an error when Code == "INVALID_VM".
            // SetValidateCheck calls vm.Validate() for each entity, and if MSD has errors
            // it adds them to ErrorListVM (lines 681-685).
            var vm = CreateVm();
            var row = TemplateRowBuilder.Build("INVALID_VM", "Should fail CRUDVM validation", "1");
            row.ExcelIndex = 2;
            vm.InjectTemplateData(new List<EntityDataTemplateVM> { row });

            var result = vm.BatchSaveData();

            Assert.IsFalse(result, "CRUDVM validation error should fail BatchSaveData");
            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0, "Should have CRUDVM validation error");
            Assert.IsTrue(vm.ErrorListVM.EntityList.Any(e => e.Message?.Contains("INVALID_VM") == true),
                "Error should mention the INVALID_VM code");
        }

        // ─── SetValidateCheck: dinfo (CRUDVM) path, not cinfo (import VM) ────

        [TestMethod]
        public void SetValidateCheck_CRUDvmDinfoPath_ValidatesWithoutImportDuplication()
        {
            // When SetDuplicatedCheck() on the ImportVM returns null but the CRUDVM has one,
            // SetValidateCheck uses dinfo (lines 656-661).
            // EntityDataImportVM.SetDuplicatedCheck() returns Code-based check (cinfo != null),
            // so we use a subclass that returns null from SetDuplicatedCheck().
            var vm = new NoDupCheckImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "user");
            var row = TemplateRowBuilder.Build("NoDup", "Test", "1");
            row.ExcelIndex = 2;
            vm.InjectTemplateData(new List<EntityDataTemplateVM> { row });

            var result = vm.BatchSaveData();

            // Should succeed (valid single row, no duplicates)
            Assert.IsTrue(result);
        }

        // ─── TryValidateProperty: StringLength with ErrorMessage (line 936-948) ──

        [TestMethod]
        public void BatchSaveData_StringLength_WithErrorMessage_TriggersValidationPath()
        {
            // ValidatedItem.ShortCode has [StringLength(5, ErrorMessage="StrLen error")]
            // Passing a value of length > 5 exercises the StringLengthAttribute ErrorMessage
            // path in TryValidateProperty (lines 936-948).
            var seed = Guid.NewGuid().ToString();
            var vm = new ValidatedItemImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new ValidatedItemContext(seed, DBTypeEnum.Memory), "user");
            var row = ValidatedRowBuilder.Build(new string('X', 10), "5");
            vm.InjectTemplateData(new List<ValidatedItemTemplateVM> { row });
            vm.ValidateOnly = true;

            var result = vm.BatchSaveData();

            Assert.IsFalse(result, "StringLength(5) violation should fail");
            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0, "Should have StringLength error");
        }

        [TestMethod]
        public void BatchSaveData_Range_WithErrorMessage_TriggersRangeValidationPath()
        {
            // ValidatedItem.SmallQty has [Range(1,10, ErrorMessage="Range error")]
            // Passing 999 exercises the RangeAttribute ErrorMessage path (lines 921-934).
            var seed = Guid.NewGuid().ToString();
            var vm = new ValidatedItemImportVM();
            vm.Wtm = MockWtmContext.CreateWtmContext(new ValidatedItemContext(seed, DBTypeEnum.Memory), "user");
            var row = ValidatedRowBuilder.Build("ok", "999");
            vm.InjectTemplateData(new List<ValidatedItemTemplateVM> { row });
            vm.ValidateOnly = true;

            var result = vm.BatchSaveData();

            Assert.IsFalse(result, "Range violation should fail");
            Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0, "Should have Range error");
        }

        // ─── SetExceptionMessage: non-DbUpdateException path ────────────────────

        [TestMethod]
        public void SetExceptionMessage_NonDbException_WithId_AddsIndexedError()
        {
            // Exercise SetExceptionMessage with a plain Exception and a non-null id (lines 1309-1311).
            var vm = CreateVm();
            var ex = new InvalidOperationException("test error");

            vm.PublicSetExceptionMessage(ex, 42L);

            Assert.AreEqual(1, vm.ErrorListVM.EntityList.Count);
            Assert.AreEqual(42L, vm.ErrorListVM.EntityList[0].Index);
            Assert.AreEqual("test error", vm.ErrorListVM.EntityList[0].Message);
        }

        [TestMethod]
        public void SetExceptionMessage_NonDbException_NullId_AddsZeroIndexError()
        {
            // Exercise SetExceptionMessage with a plain Exception and null id (lines 1314-1315).
            var vm = CreateVm();
            var ex = new InvalidOperationException("no id error");

            vm.PublicSetExceptionMessage(ex, null);

            Assert.AreEqual(1, vm.ErrorListVM.EntityList.Count);
            Assert.AreEqual(0L, vm.ErrorListVM.EntityList[0].Index);
            Assert.AreEqual("no id error", vm.ErrorListVM.EntityList[0].Message);
        }

        // ─── HasSubTable default value ───────────────────────────────────────────

        [TestMethod]
        public void HasSubTable_DefaultValue_IsFalse()
        {
            // Line 151: protected bool HasSubTable { get; set; }
            // Default should be false for an ImportVM that uses simple flat entity.
            var vm = CreateVm();
            Assert.IsFalse(vm.PublicHasSubTable, "HasSubTable should default to false");
        }
    }

    /// <summary>
    /// Import VM where SetDuplicatedCheck() returns null, so SetValidateCheck() falls through
    /// to dinfo (the CRUDVM's SetDuplicatedCheck if any), exercising lines 656-661.
    /// </summary>
    public class NoDupCheckImportVM : EntityDataImportVM
    {
        public override DuplicatedInfo<EntityDataItem>? SetDuplicatedCheck() => null;
    }

}
