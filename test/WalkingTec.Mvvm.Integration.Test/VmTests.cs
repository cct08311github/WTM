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
    public void BaseCRUDVM_AddAndVerify()
    {
        var vm = Wtm.CreateVM<TenantSchoolCrudVM>();
        vm.Entity = new TenantSchool { SchoolName = "VM School" };
        vm.DoAdd();

        var id = vm.Entity.ID;

        // Verify via fresh context to avoid change tracker conflicts
        using var freshDC = new IntegrationDataContext(GetUniqueConnectionString(), WalkingTec.Mvvm.Core.DBTypeEnum.SqlServer);
        var loaded = freshDC.TenantSchools.Find(id);
        Assert.IsNotNull(loaded);
        Assert.AreEqual("VM School", loaded.SchoolName);
    }

    [TestMethod]
    public void BaseCRUDVM_Delete()
    {
        // Seed via direct DC
        var school = new TenantSchool { SchoolName = "ToDelete via VM" };
        DC.TenantSchools.Add(school);
        DC.SaveChanges();
        var id = school.ID;

        // Detach so VM can track it
        DC.Entry(school).State = Microsoft.EntityFrameworkCore.EntityState.Detached;

        var vm = Wtm.CreateVM<TenantSchoolCrudVM>(id);
        vm.DoDelete();

        // Verify via fresh context
        using var freshDC = new IntegrationDataContext(GetUniqueConnectionString(), WalkingTec.Mvvm.Core.DBTypeEnum.SqlServer);
        var check = freshDC.TenantSchools.Find(id);
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
