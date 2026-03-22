using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Integration.Test;

[TestClass]
[TestCategory("Integration")]
public class DataContextTests : IntegrationTestBase
{
    [TestInitialize]
    public void Setup() => InitializeDatabase();

    [TestCleanup]
    public void Cleanup() => CleanupDatabase();

    [TestMethod]
    public void DataContext_ConnectAndMigrate()
    {
        Assert.IsNotNull(DC);
        Assert.AreEqual(DBTypeEnum.SqlServer, DC.DBType);
        Assert.IsTrue(DC.Database.CanConnect());
    }
}
