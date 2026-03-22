using Microsoft.Data.SqlClient;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Integration.Test;

public abstract class IntegrationTestBase
{
    private static readonly string BaseConnectionString =
        Environment.GetEnvironmentVariable("WTM_TEST_MSSQL")
        ?? "Server=localhost,1433;Database=WtmIntegrationTest;User Id=sa;Password=YourStr0ng!Pass;TrustServerCertificate=True";

    protected IntegrationDataContext DC { get; private set; } = null!;
    protected WTMContext Wtm { get; private set; } = null!;

    /// <summary>
    /// Returns a connection string with a unique DB name based on the test class name.
    /// </summary>
    protected string GetUniqueConnectionString()
    {
        return GetConnectionStringForDb($"WtmIntTest_{GetType().Name}");
    }

    /// <summary>
    /// Static helper: returns a connection string with the specified DB name.
    /// Used by FrameworkContextTests and other tests that don't extend this base.
    /// </summary>
    public static string GetConnectionStringForDb(string dbName)
    {
        var builder = new SqlConnectionStringBuilder(BaseConnectionString)
        {
            InitialCatalog = dbName
        };
        return builder.ConnectionString;
    }

    protected void InitializeDatabase(string? userCode = null)
    {
        var cs = GetUniqueConnectionString();
        DC = new IntegrationDataContext(cs, DBTypeEnum.SqlServer);
        DC.Database.EnsureDeleted();
        DC.Database.EnsureCreated();
        Wtm = MockWtmContext.CreateWtmContext(DC, userCode ?? "testuser");
    }

    protected void CleanupDatabase()
    {
        DC?.Database.EnsureDeleted();
        DC?.Dispose();
    }
}
