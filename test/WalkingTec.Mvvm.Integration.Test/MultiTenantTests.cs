using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Integration.Test.Models;

namespace WalkingTec.Mvvm.Integration.Test;

[TestClass]
[TestCategory("Integration")]
public class MultiTenantTests
{
    private TenantFrameworkContext? _seedDC;
    private string _cs = null!;

    [TestInitialize]
    public void Setup()
    {
        _cs = IntegrationTestBase.GetConnectionStringForDb("WtmIntTest_MultiTenant");
        _seedDC = new TenantFrameworkContext(_cs, DBTypeEnum.SqlServer);
        _seedDC.Database.EnsureDeleted();
        _seedDC.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _seedDC?.Database.EnsureDeleted();
        _seedDC?.Dispose();
    }

    [TestMethod]
    public void MultiTenant_FilterApplied()
    {
        // Seed data for two tenants
        _seedDC!.TenantSchools.Add(new TenantSchool { SchoolName = "Tenant A School", TenantCode = "A" });
        _seedDC!.TenantSchools.Add(new TenantSchool { SchoolName = "Tenant B School", TenantCode = "B" });
        _seedDC!.SaveChanges();

        // Verify both records exist without filter
        var allSchools = _seedDC!.TenantSchools.IgnoreQueryFilters().ToList();
        Assert.AreEqual(2, allSchools.Count, "Both tenant records should exist");

        // Create a new context with TenantCode = "A" — global query filter should apply
        using var tenantA_DC = new TenantFrameworkContext(_cs, DBTypeEnum.SqlServer);
        tenantA_DC.SetTenantCode("A");
        var tenantASchools = tenantA_DC.TenantSchools.ToList();
        Assert.AreEqual(1, tenantASchools.Count, "Only tenant A's record should be visible");
        Assert.AreEqual("Tenant A School", tenantASchools[0].SchoolName);

        // Create a context with TenantCode = "B"
        using var tenantB_DC = new TenantFrameworkContext(_cs, DBTypeEnum.SqlServer);
        tenantB_DC.SetTenantCode("B");
        var tenantBSchools = tenantB_DC.TenantSchools.ToList();
        Assert.AreEqual(1, tenantBSchools.Count, "Only tenant B's record should be visible");
        Assert.AreEqual("Tenant B School", tenantBSchools[0].SchoolName);
    }
}
