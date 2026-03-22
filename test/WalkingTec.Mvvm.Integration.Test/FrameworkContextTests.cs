using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Integration.Test;

[TestClass]
[TestCategory("Integration")]
public class FrameworkContextTests
{
    private TestFrameworkContext? _dc;

    [TestInitialize]
    public void Setup()
    {
        var cs = IntegrationTestBase.GetConnectionStringForDb("WtmIntTest_FrameworkCtx");
        _dc = new TestFrameworkContext(cs, DBTypeEnum.SqlServer);
        _dc.Database.EnsureDeleted();
        _dc.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dc?.Database.EnsureDeleted();
        _dc?.Dispose();
    }

    [TestMethod]
    public void FrameworkContext_ModelBuilding_Succeeds()
    {
        Assert.IsNotNull(_dc);
        Assert.IsTrue(_dc.Database.CanConnect());

        // Verify framework tables exist by querying FrameworkRole
        var roles = _dc.Set<FrameworkRole>().Take(1).ToList();
        Assert.IsNotNull(roles);
    }
}
