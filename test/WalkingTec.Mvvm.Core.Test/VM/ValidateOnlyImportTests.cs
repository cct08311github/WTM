#nullable enable
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM;

/// <summary>
/// Tests for <see cref="BaseImportVM{T,P}.ValidateOnly"/> dry-run mode.
/// </summary>
[TestClass]
public class ValidateOnlyImportTests
{
    private string _seed = null!;

    [TestInitialize]
    public void Init() => _seed = System.Guid.NewGuid().ToString();

    private IDataContext CreateDb() => new ImportTestDataContext(_seed, DBTypeEnum.Memory);

    // ── helpers ─────────────────────────────────────────────────────────────

    private TestImportVM MakeVm(List<ImportTestItem> entities, bool validateOnly = false)
    {
        var vm = new TestImportVM(entities) { ValidateOnly = validateOnly };
        vm.Wtm = MockWtmContext.CreateWtmContext(CreateDb(), "testuser");
        return vm;
    }

    // ── ValidateOnly = false (normal path unchanged) ─────────────────────────

    [TestMethod]
    public void ValidateOnly_False_ByDefault()
    {
        var vm = new TestImportVM();
        Assert.IsFalse(vm.ValidateOnly);
    }

    [TestMethod]
    public void ValidateOnly_False_PersistsData()
    {
        var entities = new List<ImportTestItem>
        {
            new ImportTestItem { Name = "Alpha", Value = 1 }
        };
        var vm = MakeVm(entities, validateOnly: false);

        var result = vm.BatchSaveData();

        Assert.IsTrue(result);
        using var db = CreateDb();
        Assert.AreEqual(1, db.Set<ImportTestItem>().Count(), "Row should be persisted");
    }

    // ── ValidateOnly = true, no errors ───────────────────────────────────────

    [TestMethod]
    public void ValidateOnly_True_ValidEntities_ReturnsTrue()
    {
        var entities = new List<ImportTestItem>
        {
            new ImportTestItem { Name = "Preview1", Value = 10 },
            new ImportTestItem { Name = "Preview2", Value = 20 }
        };
        var vm = MakeVm(entities, validateOnly: true);

        var result = vm.BatchSaveData();

        Assert.IsTrue(result, "ValidateOnly should return true when validation passes");
    }

    [TestMethod]
    public void ValidateOnly_True_ValidEntities_DoesNotPersist()
    {
        var entities = new List<ImportTestItem>
        {
            new ImportTestItem { Name = "DryRun", Value = 99 }
        };
        var vm = MakeVm(entities, validateOnly: true);
        vm.BatchSaveData();

        using var db = CreateDb();
        Assert.AreEqual(0, db.Set<ImportTestItem>().Count(), "ValidateOnly must not write to DB");
    }

    [TestMethod]
    public void ValidateOnly_True_EntityListPopulated_ForPreview()
    {
        var entities = new List<ImportTestItem>
        {
            new ImportTestItem { Name = "Row1", Value = 1 },
            new ImportTestItem { Name = "Row2", Value = 2 },
            new ImportTestItem { Name = "Row3", Value = 3 }
        };
        var vm = MakeVm(entities, validateOnly: true);

        vm.BatchSaveData();

        Assert.AreEqual(3, vm.EntityList.Count, "EntityList exposes rows for preview");
        Assert.AreEqual(0, vm.ErrorListVM.EntityList.Count, "No validation errors expected");
    }

    // ── ValidateOnly = true, with validation errors ───────────────────────────

    [TestMethod]
    public void ValidateOnly_True_ValidationErrors_ReturnsFalse()
    {
        // Name exceeds StringLength(50)
        var entities = new List<ImportTestItem>
        {
            new ImportTestItem { Name = new string('X', 60), Value = 1 }
        };
        var vm = MakeVm(entities, validateOnly: true);

        var result = vm.BatchSaveData();

        Assert.IsFalse(result, "ValidateOnly should return false when validation fails");
        Assert.IsTrue(vm.ErrorListVM.EntityList.Count > 0, "Errors should be reported");
    }

    [TestMethod]
    public void ValidateOnly_True_ValidationErrors_DoesNotPersist()
    {
        var entities = new List<ImportTestItem>
        {
            new ImportTestItem { Name = "GoodRow", Value = 1 },
            new ImportTestItem { Name = new string('Y', 60), Value = 2 }  // invalid
        };
        var vm = MakeVm(entities, validateOnly: true);
        vm.BatchSaveData();

        using var db = CreateDb();
        Assert.AreEqual(0, db.Set<ImportTestItem>().Count(),
            "Nothing should be persisted even when some rows are valid");
    }
}
