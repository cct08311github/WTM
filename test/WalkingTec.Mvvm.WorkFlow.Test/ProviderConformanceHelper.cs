#nullable enable
// WF-3 / #270 — Env-var-gated live-provider CAS conformance harness.
//
// Gate:   WTM_WF_LIVE_PROVIDERS  — when unset: all T_PROV_* tests are Inconclusive (CI-safe).
//                                 — when set:   live conformance mode active.
// Per-provider connection strings (required when gate is set):
//   WTM_WF_CONN_SQLSERVER   — SQL Server / Azure SQL
//   WTM_WF_CONN_POSTGRES    — PostgreSQL
//   WTM_WF_CONN_MYSQL       — MySQL / MariaDB
//   WTM_WF_CONN_ORACLE      — Oracle
//   WTM_WF_CONN_DAMENG      — DaMeng 达梦
//
// CAS conformance check (T_PROV_0 shape):
//   1. Create a temp table with (Id CHAR(36), RowVer INT) columns.
//   2. Insert one row (Id=uuid, RowVer=0).
//   3. Fire two concurrent raw-SQL guarded-CAS UPDATEs:
//        UPDATE t SET RowVer=1 WHERE Id=@id AND RowVer=0
//   4. Assert exactly one UPDATE returned rows-affected=1 and the other returned 0.
//   5. Assert final RowVer=1.
//   6. Drop the temp table.
//
// Raw SQL is used deliberately (no EF Core provider packages needed in this test project).
// Driver assemblies are loaded via reflection so missing drivers produce a clear Assert.Fail.

using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.WorkFlow.Test;

/// <summary>
/// Shared harness for live-provider CAS conformance tests.
/// </summary>
internal static class ProviderConformanceHelper
{
    // ── Gate check ────────────────────────────────────────────────────────────

    /// <summary>
    /// Name of the environment variable that activates live-provider conformance mode.
    /// When this variable is not set, T_PROV_* tests call <see cref="Assert.Inconclusive"/>
    /// so the normal CI suite stays green.
    /// </summary>
    public const string GateEnvVar = "WTM_WF_LIVE_PROVIDERS";

    // ── Per-provider connection-string env vars ────────────────────────────────

    public const string SqlServerConnVar = "WTM_WF_CONN_SQLSERVER";
    public const string PostgresConnVar  = "WTM_WF_CONN_POSTGRES";
    public const string MySqlConnVar     = "WTM_WF_CONN_MYSQL";
    public const string OracleConnVar    = "WTM_WF_CONN_ORACLE";
    public const string DaMengConnVar    = "WTM_WF_CONN_DAMENG";

    // ── Provider factory type names (fully-qualified, resolved by reflection) ──

    private const string SqlServerFactoryType = "Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient";
    private const string PostgresFactoryType  = "Npgsql.NpgsqlFactory, Npgsql";
    private const string MySqlFactoryType     = "MySql.Data.MySqlClient.MySqlClientFactory, MySql.Data";
    private const string OracleFactoryType    = "Oracle.ManagedDataAccess.Client.OracleClientFactory, Oracle.ManagedDataAccess.Client";
    private const string DaMengFactoryType    = "Dm.DmClientFactory, DmProvider";

    // ── Public entry points ───────────────────────────────────────────────────

    /// <summary>
    /// Run the standard guarded-CAS conformance check for SQL Server.
    /// Returns immediately (Inconclusive) when <see cref="GateEnvVar"/> is not set.
    /// </summary>
    public static Task RunSqlServerAsync()
        => RunAsync("SQL Server", SqlServerConnVar, SqlServerFactoryType, SqlServerTempTableSql);

    /// <summary>Run the CAS conformance check for PostgreSQL.</summary>
    public static Task RunPostgresAsync()
        => RunAsync("PostgreSQL", PostgresConnVar, PostgresFactoryType, PostgresTempTableSql);

    /// <summary>Run the CAS conformance check for MySQL / MariaDB.</summary>
    public static Task RunMySqlAsync()
        => RunAsync("MySQL", MySqlConnVar, MySqlFactoryType, MySqlTempTableSql);

    /// <summary>Run the CAS conformance check for Oracle.</summary>
    public static Task RunOracleAsync()
        => RunAsync("Oracle", OracleConnVar, OracleFactoryType, OracleTempTableSql);

    /// <summary>Run the CAS conformance check for DaMeng (达梦).</summary>
    public static Task RunDaMengAsync()
        => RunAsync("DaMeng", DaMengConnVar, DaMengFactoryType, DaMengTempTableSql);

    // ── Core implementation ───────────────────────────────────────────────────

    private static async Task RunAsync(
        string providerName,
        string connEnvVar,
        string factoryTypeName,
        ProviderSqlDialect dialect)
    {
        // Step 1 — Gate check. Inconclusive when gate is absent (CI-safe).
        var gate = Environment.GetEnvironmentVariable(GateEnvVar);
        if (string.IsNullOrWhiteSpace(gate))
        {
            Assert.Inconclusive(
                $"T_PROV ({providerName}): set {GateEnvVar}=1 to run live-provider CAS conformance. " +
                "Test skipped in normal CI.");
            return; // unreachable; suppresses compiler flow warnings
        }

        // Step 2 — Connection string check.
        var connString = Environment.GetEnvironmentVariable(connEnvVar);
        if (string.IsNullOrWhiteSpace(connString))
        {
            Assert.Fail(
                $"T_PROV ({providerName}): {GateEnvVar} is set but {connEnvVar} is not. " +
                $"Set {connEnvVar} to a valid {providerName} connection string.");
            return;
        }

        // Step 3 — Resolve the DbProviderFactory via reflection (no compile-time package dep).
        DbProviderFactory factory;
        try
        {
            var type = Type.GetType(factoryTypeName, throwOnError: true)!;
            var instance = type.GetField("Instance")?.GetValue(null)
                        ?? type.GetProperty("Instance")?.GetValue(null);
            if (instance is not DbProviderFactory f)
            {
                Assert.Fail(
                    $"T_PROV ({providerName}): could not resolve DbProviderFactory from '{factoryTypeName}'. " +
                    "Ensure the provider NuGet package is referenced in the test project.");
                return;
            }
            factory = f;
        }
        catch (Exception ex) when (
            ex is TypeLoadException
            or System.IO.FileNotFoundException
            or System.IO.FileLoadException
            or BadImageFormatException)
        {
            Assert.Fail(
                $"T_PROV ({providerName}): driver assembly not available — '{factoryTypeName}'. " +
                $"Add the {providerName} EF provider NuGet package to the test project to run live conformance. " +
                $"Inner: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // Step 4 — Open a connection.
        await using DbConnection conn = factory.CreateConnection()
            ?? throw new InvalidOperationException($"Factory.CreateConnection() returned null for {providerName}.");
        conn.ConnectionString = connString;

        try
        {
            await conn.OpenAsync();
        }
        catch (Exception ex)
        {
            Assert.Fail(
                $"T_PROV ({providerName}): cannot connect using {connEnvVar}. " +
                $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        // Step 5 — Run the guarded-CAS conformance check.
        await RunCasConformanceAsync(conn, factory, providerName, dialect);
    }

    // ── Guarded-CAS conformance body ─────────────────────────────────────────

    /// <summary>
    /// T_PROV_0 guarded-CAS body (provider-agnostic raw SQL):
    ///   - Create a temporary table.
    ///   - Insert one row with RowVer=0.
    ///   - Fire two concurrent guarded-CAS UPDATEs on the same row.
    ///   - Assert exactly one winner (rows-affected==1) and one loser (rows-affected==0).
    ///   - Assert final RowVer==1.
    ///   - Drop the temporary table.
    /// </summary>
    private static async Task RunCasConformanceAsync(
        DbConnection conn,
        DbProviderFactory factory,
        string providerName,
        ProviderSqlDialect dialect)
    {
        const int Rounds = 5;
        var tableName = $"Wf_ProvConf_{Guid.NewGuid():N}"[..32];

        // Create temp table.
        await ExecuteNonQueryAsync(conn, dialect.CreateTableSql(tableName));

        try
        {
            for (int round = 0; round < Rounds; round++)
            {
                var rowId = Guid.NewGuid().ToString("N")[..32];

                // Insert row with RowVer=0.
                await ExecuteNonQueryAsync(conn, dialect.InsertSql(tableName, rowId));

                // Two concurrent guarded-CAS UPDATEs.
                var barrier = new SemaphoreSlim(0, 2);

                Task<int> MakeRacer()
                {
                    return Task.Run(async () =>
                    {
                        await barrier.WaitAsync();
                        // Each racer opens its own connection for true concurrency.
                        await using var c2 = factory.CreateConnection()
                            ?? throw new InvalidOperationException("Factory.CreateConnection() null");
                        c2.ConnectionString = conn.ConnectionString;
                        await c2.OpenAsync();
                        return await ExecuteNonQueryAsync(c2, dialect.UpdateSql(tableName, rowId));
                    });
                }

                var t1 = MakeRacer();
                var t2 = MakeRacer();
                barrier.Release(2);

                int[] results = await Task.WhenAll(t1, t2);
                int winners = 0;
                int losers  = 0;
                foreach (var r in results)
                {
                    if (r == 1) winners++;
                    else if (r == 0) losers++;
                }

                Assert.AreEqual(1, winners,
                    $"T_PROV ({providerName}) round {round}: expected exactly 1 CAS winner, " +
                    $"got [{results[0]},{results[1]}]. Provider CAS semantics are non-conformant.");
                Assert.AreEqual(1, losers,
                    $"T_PROV ({providerName}) round {round}: expected exactly 1 CAS loser, " +
                    $"got [{results[0]},{results[1]}].");

                // Verify final RowVer.
                var finalRowVer = await ExecuteScalarAsync(conn, dialect.SelectRowVerSql(tableName, rowId));
                Assert.AreEqual(1L, Convert.ToInt64(finalRowVer),
                    $"T_PROV ({providerName}) round {round}: final RowVer must be 1 after exactly one winner.");
            }
        }
        finally
        {
            // Best-effort cleanup.
            try { await ExecuteNonQueryAsync(conn, dialect.DropTableSql(tableName)); }
            catch { /* ignore cleanup failures */ }
        }
    }

    // ── Raw SQL execution helpers ─────────────────────────────────────────────

    private static async Task<int> ExecuteNonQueryAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ExecuteScalarAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    // ── SQL dialect helpers ───────────────────────────────────────────────────

    private sealed class ProviderSqlDialect
    {
        private readonly Func<string, string> _createTable;
        private readonly Func<string, string, string> _insert;
        private readonly Func<string, string, string> _update;
        private readonly Func<string, string, string> _selectRowVer;
        private readonly Func<string, string> _dropTable;

        public ProviderSqlDialect(
            Func<string, string> createTable,
            Func<string, string, string> insert,
            Func<string, string, string> update,
            Func<string, string, string> selectRowVer,
            Func<string, string> dropTable)
        {
            _createTable  = createTable;
            _insert       = insert;
            _update       = update;
            _selectRowVer = selectRowVer;
            _dropTable    = dropTable;
        }

        public string CreateTableSql(string tableName)      => _createTable(tableName);
        public string InsertSql(string tableName, string id) => _insert(tableName, id);
        public string UpdateSql(string tableName, string id) => _update(tableName, id);
        public string SelectRowVerSql(string tableName, string id) => _selectRowVer(tableName, id);
        public string DropTableSql(string tableName)        => _dropTable(tableName);
    }

    // SQL Server — uses a regular table with a unique name per test run (temp tables can't cross connections).
    private static readonly ProviderSqlDialect SqlServerTempTableSql = new(
        t  => $"CREATE TABLE [{t}] (Id NVARCHAR(32) NOT NULL PRIMARY KEY, RowVer INT NOT NULL DEFAULT 0);",
        (t, id) => $"INSERT INTO [{t}] (Id, RowVer) VALUES ('{id}', 0);",
        (t, id) => $"UPDATE [{t}] SET RowVer = 1 WHERE Id = '{id}' AND RowVer = 0;",
        (t, id) => $"SELECT RowVer FROM [{t}] WHERE Id = '{id}';",
        t  => $"DROP TABLE IF EXISTS [{t}];"
    );

    // PostgreSQL
    private static readonly ProviderSqlDialect PostgresTempTableSql = new(
        t  => $"CREATE TABLE \"{t}\" (\"Id\" VARCHAR(32) NOT NULL PRIMARY KEY, \"RowVer\" INTEGER NOT NULL DEFAULT 0);",
        (t, id) => $"INSERT INTO \"{t}\" (\"Id\", \"RowVer\") VALUES ('{id}', 0);",
        (t, id) => $"UPDATE \"{t}\" SET \"RowVer\" = 1 WHERE \"Id\" = '{id}' AND \"RowVer\" = 0;",
        (t, id) => $"SELECT \"RowVer\" FROM \"{t}\" WHERE \"Id\" = '{id}';",
        t  => $"DROP TABLE IF EXISTS \"{t}\";"
    );

    // MySQL / MariaDB — backtick identifiers, no square brackets
    private static readonly ProviderSqlDialect MySqlTempTableSql = new(
        t  => $"CREATE TABLE `{t}` (`Id` VARCHAR(32) NOT NULL PRIMARY KEY, `RowVer` INT NOT NULL DEFAULT 0);",
        (t, id) => $"INSERT INTO `{t}` (`Id`, `RowVer`) VALUES ('{id}', 0);",
        (t, id) => $"UPDATE `{t}` SET `RowVer` = 1 WHERE `Id` = '{id}' AND `RowVer` = 0;",
        (t, id) => $"SELECT `RowVer` FROM `{t}` WHERE `Id` = '{id}';",
        t  => $"DROP TABLE IF EXISTS `{t}`;"
    );

    // Oracle — no backticks, uses double-quote identifiers; no IF EXISTS for DROP
    private static readonly ProviderSqlDialect OracleTempTableSql = new(
        t  => $"CREATE TABLE \"{t}\" (\"Id\" VARCHAR2(32) NOT NULL PRIMARY KEY, \"RowVer\" NUMBER(10) DEFAULT 0 NOT NULL)",
        (t, id) => $"INSERT INTO \"{t}\" (\"Id\", \"RowVer\") VALUES ('{id}', 0)",
        (t, id) => $"UPDATE \"{t}\" SET \"RowVer\" = 1 WHERE \"Id\" = '{id}' AND \"RowVer\" = 0",
        (t, id) => $"SELECT \"RowVer\" FROM \"{t}\" WHERE \"Id\" = '{id}'",
        t  => $"DROP TABLE \"{t}\""
    );

    // DaMeng (达梦) — syntax close to Oracle; uses double-quote identifiers
    private static readonly ProviderSqlDialect DaMengTempTableSql = new(
        t  => $"CREATE TABLE \"{t}\" (\"Id\" VARCHAR(32) NOT NULL PRIMARY KEY, \"RowVer\" INT DEFAULT 0 NOT NULL)",
        (t, id) => $"INSERT INTO \"{t}\" (\"Id\", \"RowVer\") VALUES ('{id}', 0)",
        (t, id) => $"UPDATE \"{t}\" SET \"RowVer\" = 1 WHERE \"Id\" = '{id}' AND \"RowVer\" = 0",
        (t, id) => $"SELECT \"RowVer\" FROM \"{t}\" WHERE \"Id\" = '{id}'",
        t  => $"DROP TABLE \"{t}\""
    );
}
