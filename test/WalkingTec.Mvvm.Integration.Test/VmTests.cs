using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Integration.Test.Models;

namespace WalkingTec.Mvvm.Integration.Test;

[TestClass]
[TestCategory("Integration")]
public class VmTests : IntegrationTestBase
{
    [TestInitialize]
    public void Setup() => InitializeDatabase();

    [TestCleanup]
    public void Cleanup() => CleanupDatabase();

    [TestMethod]
    public void BaseCRUDVM_AddEditDelete()
    {
        var vm = Wtm.CreateVM<TenantSchoolCrudVM>();
        vm.Entity = new TenantSchool { SchoolName = "VM School" };
        vm.DoAdd();

        var id = vm.Entity.ID;
        vm = Wtm.CreateVM<TenantSchoolCrudVM>(id);
        Assert.AreEqual("VM School", vm.Entity.SchoolName);

        vm.Entity.SchoolName = "Updated School";
        vm.DoEdit(updateAllFields: true);

        vm = Wtm.CreateVM<TenantSchoolCrudVM>(id);
        Assert.AreEqual("Updated School", vm.Entity.SchoolName);

        vm = Wtm.CreateVM<TenantSchoolCrudVM>(id);
        vm.DoDelete();

        var check = DC.TenantSchools.Find(id);
        Assert.IsNull(check);
    }

    [TestMethod]
    public void BasePagedListVM_Paging()
    {
        for (int i = 1; i <= 15; i++)
        {
            DC.TenantSchools.Add(new TenantSchool { SchoolName = $"School {i}" });
        }
        DC.SaveChanges();

        var listVm = Wtm.CreateVM<TenantSchoolListVM>();
        listVm.Searcher.Limit = 10;
        listVm.DoSearch();

        Assert.AreEqual(15, listVm.Searcher.Count);
        Assert.AreEqual(10, listVm.EntityList.Count);
    }
}

public class TenantSchoolCrudVM : BaseCRUDVM<TenantSchool>
{
}

public class TenantSchoolListVM : BasePagedListVM<TenantSchool, TenantSchoolSearcher>
{
    protected override IEnumerable<IGridColumn<TenantSchool>> InitGridHeader()
    {
        return [];
    }
}

public class TenantSchoolSearcher : BaseSearcher
{
}
