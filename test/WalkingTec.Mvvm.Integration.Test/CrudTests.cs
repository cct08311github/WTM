using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Integration.Test.Models;

namespace WalkingTec.Mvvm.Integration.Test;

[TestClass]
[TestCategory("Integration")]
public class CrudTests : IntegrationTestBase
{
    [TestInitialize]
    public void Setup() => InitializeDatabase();

    [TestCleanup]
    public void Cleanup() => CleanupDatabase();

    [TestMethod]
    public void CRUD_Create_Read()
    {
        var school = new TenantSchool { SchoolName = "Test School" };
        DC.TenantSchools.Add(school);
        DC.SaveChanges();

        var loaded = DC.TenantSchools.Find(school.ID);
        Assert.IsNotNull(loaded);
        Assert.AreEqual("Test School", loaded.SchoolName);
    }

    [TestMethod]
    public void CRUD_Update()
    {
        var school = new TenantSchool { SchoolName = "Before" };
        DC.TenantSchools.Add(school);
        DC.SaveChanges();

        school.SchoolName = "After";
        DC.SaveChanges();

        using var fresh = new IntegrationDataContext(GetUniqueConnectionString(), DBTypeEnum.SqlServer);
        var loaded = fresh.TenantSchools.Find(school.ID);
        Assert.IsNotNull(loaded);
        Assert.AreEqual("After", loaded.SchoolName);
    }

    [TestMethod]
    public void CRUD_Delete()
    {
        var school = new TenantSchool { SchoolName = "ToDelete" };
        DC.TenantSchools.Add(school);
        DC.SaveChanges();
        var id = school.ID;

        DC.TenantSchools.Remove(school);
        DC.SaveChanges();

        var loaded = DC.TenantSchools.Find(id);
        Assert.IsNull(loaded);
    }
}
