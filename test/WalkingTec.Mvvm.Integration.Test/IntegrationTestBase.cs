using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Integration.Test;

public abstract class IntegrationTestBase
{
    private static readonly string BaseConnectionString =
        Environment.GetEnvironmentVariable("WTM_TEST_MSSQL")
        ?? "Server=localhost,1433;Database=WtmIntegrationTest;User Id=sa;Password=YourStr0ng!Pass;TrustServerCertificate=True";

    // Issue #811: probe SQL Server once per assembly and cache the outcome.
    // When the probe fails, tests report Inconclusive (yellow) with a setup
    // guide rather than Failed (red) with a cryptic connection error —
    // matches the actual semantic (env not ready, not code broken).
    private static readonly Lazy<string?> s_connectionError = new(() =>
    {
        var builder = new SqlConnectionStringBuilder(BaseConnectionString)
        {
            // Short probe timeout: integration tests want a fast "is SQL
            // here?" signal, not the default 15-second negotiate.
            ConnectTimeout = 2,
            // Target master so the probe does not depend on the
            // per-test DB existing yet.
            InitialCatalog = "master"
        };
        try
        {
            using var conn = new SqlConnection(builder.ConnectionString);
            conn.Open();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    });

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

    /// <summary>
    /// Call from <c>[TestInitialize]</c> (via <see cref="InitializeDatabase"/>
    /// or directly in tests that don't extend this base). When SQL Server is
    /// not reachable, throws <see cref="AssertInconclusiveException"/> with a
    /// setup guide — test appears as yellow Inconclusive, not red Failed.
    /// Issue #811.
    /// </summary>
    public static void EnsureSqlServerAvailable()
    {
        var error = s_connectionError.Value;
        if (error == null) { return; }

        // Sanitize the connection string shown in the message: strip the
        // password so it does not leak into CI logs if the env var holds a
        // real credential.
        var builder = new SqlConnectionStringBuilder(BaseConnectionString);
        builder.Password = string.Empty;
        var sanitized = builder.ConnectionString;

        Assert.Inconclusive(
            "[WtmIntegrationTest] SQL Server not reachable — skipping." + Environment.NewLine +
            "  Tried: " + sanitized + Environment.NewLine +
            "  Error: " + error + Environment.NewLine +
            "" + Environment.NewLine +
            "  To run these tests locally:" + Environment.NewLine +
            "  1. Start SQL Server via Docker:" + Environment.NewLine +
            "       docker compose up -d mssql   (see docker-compose.yml at repo root)" + Environment.NewLine +
            "     or ad-hoc:" + Environment.NewLine +
            "       docker run -d -p 1433:1433 -e ACCEPT_EULA=Y \\" + Environment.NewLine +
            "         -e MSSQL_SA_PASSWORD='YourStr0ng!Pass' \\" + Environment.NewLine +
            "         mcr.microsoft.com/mssql/server:2022-latest" + Environment.NewLine +
            "  2. Or point to an existing SQL Server:" + Environment.NewLine +
            "       export WTM_TEST_MSSQL='Server=host;Database=WtmInt;...'" + Environment.NewLine +
            "  See test/WalkingTec.Mvvm.Integration.Test/README.md for details.");
    }

    protected void InitializeDatabase(string? userCode = null)
    {
        EnsureSqlServerAvailable();

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
