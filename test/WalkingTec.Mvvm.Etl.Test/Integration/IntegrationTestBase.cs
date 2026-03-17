#nullable enable
using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Etl.Test.Integration;

/// <summary>
/// 整合測試基底類別 — 提供 MSSQL/Oracle 連線字串、共用的表建立/清理方法。
/// 所有整合測試都標記 [TestCategory("Integration")]，CI 可選擇性跳過。
///
/// 使用方式：
///   docker compose -f test/docker-compose.etl-test.yml up -d
///   dotnet test --filter "TestCategory=Integration"
/// </summary>
[TestCategory("Integration")]
public abstract class IntegrationTestBase
{
    protected static string MssqlConnectionString =>
        Environment.GetEnvironmentVariable("ETL_TEST_MSSQL")
        ?? "Server=localhost,1433;Database=EtlTest;User Id=sa;Password=EtlTest@2026!;TrustServerCertificate=true";

    protected static string OracleConnectionString =>
        Environment.GetEnvironmentVariable("ETL_TEST_ORACLE")
        ?? "Data Source=localhost:1521/XEPDB1;User Id=system;Password=EtlTest2026;";

    /// <summary>
    /// 在 MSSQL 中建立測試用來源表（含 OrderNo, Amount, UpdatedAt）
    /// </summary>
    protected static async Task CreateMssqlSourceTableAsync(string tableName, int rowCount = 0)
    {
        await using var conn = new SqlConnection(MssqlConnectionString);
        await conn.OpenAsync();

        // 如果存在則先刪除
        await ExecuteMssqlAsync(conn, $@"
            IF OBJECT_ID('{tableName}', 'U') IS NOT NULL
                DROP TABLE [{tableName}]");

        await ExecuteMssqlAsync(conn, $@"
            CREATE TABLE [{tableName}] (
                [OrderNo]    NVARCHAR(50) NOT NULL,
                [Amount]     DECIMAL(18,2) NOT NULL,
                [UpdatedAt]  DATETIME2 NOT NULL,
                CONSTRAINT [PK_{tableName}] PRIMARY KEY ([OrderNo])
            )");

        if (rowCount > 0)
        {
            for (int i = 0; i < rowCount; i++)
            {
                await ExecuteMssqlAsync(conn, $@"
                    INSERT INTO [{tableName}] ([OrderNo], [Amount], [UpdatedAt])
                    VALUES ('ORD-{i:D8}', {100m + (i % 1000)}, '{new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i):yyyy-MM-dd HH:mm:ss}')");
            }
        }
    }

    /// <summary>
    /// 在 MSSQL 中建立測試用目標表（結構與來源相同）
    /// </summary>
    protected static async Task CreateMssqlTargetTableAsync(string tableName)
    {
        await using var conn = new SqlConnection(MssqlConnectionString);
        await conn.OpenAsync();

        await ExecuteMssqlAsync(conn, $@"
            IF OBJECT_ID('{tableName}', 'U') IS NOT NULL
                DROP TABLE [{tableName}]");

        await ExecuteMssqlAsync(conn, $@"
            CREATE TABLE [{tableName}] (
                [OrderNo]    NVARCHAR(50) NOT NULL,
                [Amount]     DECIMAL(18,2) NOT NULL,
                [UpdatedAt]  DATETIME2 NOT NULL,
                CONSTRAINT [PK_{tableName}] PRIMARY KEY ([OrderNo])
            )");
    }

    /// <summary>
    /// 確保 MSSQL 中的 EtlTest 資料庫存在
    /// </summary>
    protected static async Task EnsureMssqlDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(MssqlConnectionString)
        {
            InitialCatalog = "master"
        };
        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync();

        await ExecuteMssqlAsync(conn, @"
            IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = 'EtlTest')
                CREATE DATABASE [EtlTest]");
    }

    /// <summary>
    /// 刪除 MSSQL 測試表
    /// </summary>
    protected static async Task DropMssqlTableAsync(string tableName)
    {
        await using var conn = new SqlConnection(MssqlConnectionString);
        await conn.OpenAsync();
        await ExecuteMssqlAsync(conn, $@"
            IF OBJECT_ID('{tableName}', 'U') IS NOT NULL
                DROP TABLE [{tableName}]");
    }

    /// <summary>
    /// 查詢 MSSQL 表的行數
    /// </summary>
    protected static async Task<int> CountMssqlRowsAsync(string tableName)
    {
        await using var conn = new SqlConnection(MssqlConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM [{tableName}]";
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// 查詢 MSSQL 表是否存在
    /// </summary>
    protected static async Task<bool> MssqlTableExistsAsync(string tableName)
    {
        await using var conn = new SqlConnection(MssqlConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @t";
        cmd.Parameters.AddWithValue("@t", tableName);
        return (int)(await cmd.ExecuteScalarAsync())! > 0;
    }

    /// <summary>
    /// 產生測試用 DataTable（與來源表結構相同）
    /// </summary>
    protected static DataTable GenerateTestData(int rowCount, DateTime? baseDate = null, int startIndex = 0)
    {
        var dt = new DataTable();
        dt.Columns.Add("OrderNo", typeof(string));
        dt.Columns.Add("Amount", typeof(decimal));
        dt.Columns.Add("UpdatedAt", typeof(DateTime));

        var start = baseDate ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = startIndex; i < startIndex + rowCount; i++)
        {
            var row = dt.NewRow();
            row["OrderNo"] = $"ORD-{i:D8}";
            row["Amount"] = 100m + (i % 1000);
            row["UpdatedAt"] = start.AddSeconds(i);
            dt.Rows.Add(row);
        }

        return dt;
    }

    /// <summary>
    /// 探測 MSSQL 是否可連線。無法連線時測試應呼叫 Assert.Inconclusive()。
    /// </summary>
    protected static bool IsMssqlAvailable()
    {
        try
        {
            using var conn = new SqlConnection(MssqlConnectionString);
            conn.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 探測 Oracle 是否可連線。無法連線時測試應呼叫 Assert.Inconclusive()。
    /// </summary>
    protected static bool IsOracleAvailable()
    {
        try
        {
            using var conn = new Oracle.ManagedDataAccess.Client.OracleConnection(OracleConnectionString);
            conn.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task ExecuteMssqlAsync(SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
