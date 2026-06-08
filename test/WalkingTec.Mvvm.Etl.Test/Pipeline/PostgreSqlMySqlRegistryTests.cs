#nullable enable
// Tests for PostgreSQL + MySQL ETL source + bulk loader (Issue #223)
// All tests are pure unit tests — no live DB required.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;
using WalkingTec.Mvvm.Etl.Pipeline.Sources;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

/// <summary>
/// Unit tests for PostgreSQL + MySQL ETL source and bulk loader registration.
///
/// Coverage:
///   1. DefaultRegistry contains "postgresql"/"pgsql"/"mysql" keys.
///   2. DefaultRegistry creates the correct source type for each key.
///   3. EtlSourceFactory.CreateSource(DBTypeEnum.PgSql) resolves PostgreSqlSource.
///   4. EtlSourceFactory.CreateSource(DBTypeEnum.MySql) resolves MySqlEtlSource.
///   5. EtlSourceFactory.CreateLoader(DBTypeEnum.PgSql) resolves PostgreSqlBulkLoader.
///   6. EtlSourceFactory.CreateLoader(DBTypeEnum.MySql) resolves MySqlBulkLoader.
///   7. Back-compat: Oracle/Mssql source + loader still resolve.
///   8. PostgreSqlBulkLoader.BuildUpsertSql — single key produces correct ON CONFLICT.
///   9. PostgreSqlBulkLoader.BuildUpsertSql — composite key produces AND-free syntax
///      (PG ON CONFLICT takes a column list, not AND — this test validates the column list).
///  10. PostgreSqlBulkLoader.BuildUpsertSql — update columns exclude key columns.
///  11. PostgreSqlBulkLoader.BuildUpsertSql — all-key table produces DO NOTHING.
///  12. PostgreSqlBulkLoader.QuoteIdentifier handles simple and schema-qualified names.
///  13. MySqlBulkLoader.BuildUpsertSql — single key produces correct ON DUPLICATE KEY UPDATE.
///  14. MySqlBulkLoader.BuildUpsertSql — composite key produced for all non-key columns.
///  15. MySqlBulkLoader.BuildUpsertSql — all-key table produces INSERT IGNORE.
///  16. MySqlBulkLoader.QuoteIdentifier handles simple and schema-qualified names.
///  17. PostgreSqlBulkLoader ReplaceAsync guard fires before opening DB connection.
///  18. MySqlBulkLoader ReplaceAsync guard fires before opening DB connection.
///  19. PostgreSqlBulkLoader.ParseSchemaAndTable handles schema-prefixed and bare names.
/// </summary>
[TestClass]
public class PostgreSqlMySqlRegistryTests
{
    // ─── 1–2: DefaultRegistry contains PG / MySQL keys ───────────────────

    [TestMethod]
    public void DefaultRegistry_contains_postgresql_pgsql_mysql()
    {
        var reg = EtlSourceFactory.DefaultRegistry;

        reg.RegisteredKinds.Should().Contain("postgresql");
        reg.RegisteredKinds.Should().Contain("pgsql");
        reg.RegisteredKinds.Should().Contain("mysql");
    }

    [TestMethod]
    public void DefaultRegistry_creates_postgresql_source()
    {
        using var src = EtlSourceFactory.DefaultRegistry.Create("postgresql");
        src.Should().BeOfType<PostgreSqlSource>();
    }

    [TestMethod]
    public void DefaultRegistry_creates_pgsql_source_as_alias()
    {
        using var src = EtlSourceFactory.DefaultRegistry.Create("pgsql");
        src.Should().BeOfType<PostgreSqlSource>();
    }

    [TestMethod]
    public void DefaultRegistry_creates_mysql_source()
    {
        using var src = EtlSourceFactory.DefaultRegistry.Create("mysql");
        src.Should().BeOfType<MySqlEtlSource>();
    }

    // ─── 3–6: CreateSource / CreateLoader via DBTypeEnum ──────────────────

    [TestMethod]
    public void CreateSource_PgSql_resolves_PostgreSqlSource()
    {
        using var src = EtlSourceFactory.CreateSource(DBTypeEnum.PgSql);
        src.Should().BeOfType<PostgreSqlSource>();
    }

    [TestMethod]
    public void CreateSource_MySql_resolves_MySqlEtlSource()
    {
        using var src = EtlSourceFactory.CreateSource(DBTypeEnum.MySql);
        src.Should().BeOfType<MySqlEtlSource>();
    }

    [TestMethod]
    public void CreateLoader_PgSql_resolves_PostgreSqlBulkLoader()
    {
        var loader = EtlSourceFactory.CreateLoader(DBTypeEnum.PgSql);
        loader.Should().BeOfType<PostgreSqlBulkLoader>();
    }

    [TestMethod]
    public void CreateLoader_MySql_resolves_MySqlBulkLoader()
    {
        var loader = EtlSourceFactory.CreateLoader(DBTypeEnum.MySql);
        loader.Should().BeOfType<MySqlBulkLoader>();
    }

    // ─── 7: Back-compat: Oracle / Mssql still resolve ─────────────────────

    [TestMethod]
    public void BackCompat_Oracle_source_still_resolves()
    {
        using var src = EtlSourceFactory.CreateSource(DBTypeEnum.Oracle);
        src.Should().BeOfType<OracleSource>();
    }

    [TestMethod]
    public void BackCompat_Mssql_source_still_resolves()
    {
        using var src = EtlSourceFactory.CreateSource(DBTypeEnum.SqlServer);
        src.Should().BeOfType<MssqlSource>();
    }

    [TestMethod]
    public void BackCompat_Oracle_loader_still_resolves()
    {
        var loader = EtlSourceFactory.CreateLoader(DBTypeEnum.Oracle);
        loader.Should().BeOfType<OracleBulkLoader>();
    }

    [TestMethod]
    public void BackCompat_Mssql_loader_still_resolves()
    {
        var loader = EtlSourceFactory.CreateLoader(DBTypeEnum.SqlServer);
        loader.Should().BeOfType<MssqlBulkLoader>();
    }

    // ─── 8–11: PostgreSqlBulkLoader.BuildUpsertSql ────────────────────────

    [TestMethod]
    public void PgLoader_BuildUpsertSql_single_key_produces_on_conflict_with_key()
    {
        var sql = PostgreSqlBulkLoader.BuildUpsertSql(
            "stg_orders", "orders",
            new[] { "id" },
            new[] { "id", "name", "value" });

        sql.Should().Contain("ON CONFLICT (\"id\")",
            "single key must appear in the ON CONFLICT column list");
        sql.Should().Contain("DO UPDATE SET",
            "non-key columns must get DO UPDATE SET clause");
        sql.Should().Contain("\"name\" = EXCLUDED.\"name\"",
            "non-key column 'name' must be set from EXCLUDED");
        sql.Should().Contain("\"value\" = EXCLUDED.\"value\"",
            "non-key column 'value' must be set from EXCLUDED");
        // key column must NOT appear in the UPDATE SET
        sql.Should().NotMatchRegex(@"SET.*""id""",
            "key column must not appear in the UPDATE SET assignment");
    }

    [TestMethod]
    public void PgLoader_BuildUpsertSql_composite_key_lists_all_keys_in_conflict()
    {
        var keyColumns = new[] { "tenant_id", "order_no" };
        var allColumns = new[] { "tenant_id", "order_no", "amount", "status" };

        var sql = PostgreSqlBulkLoader.BuildUpsertSql(
            "stg", "orders", keyColumns, allColumns);

        sql.Should().Contain("ON CONFLICT (\"tenant_id\", \"order_no\")",
            "both key columns must appear in the ON CONFLICT list");
        sql.Should().Contain("DO UPDATE SET",
            "non-key columns should get an update clause");
        sql.Should().Contain("\"amount\" = EXCLUDED.\"amount\"");
        sql.Should().Contain("\"status\" = EXCLUDED.\"status\"");
        // key columns must NOT be in the UPDATE SET
        sql.Should().NotContain("EXCLUDED.\"tenant_id\"");
        sql.Should().NotContain("EXCLUDED.\"order_no\"");
    }

    [TestMethod]
    public void PgLoader_BuildUpsertSql_update_cols_exclude_key_columns()
    {
        var keys = new[] { "id" };
        var all  = new[] { "id", "a", "b" };

        var sql = PostgreSqlBulkLoader.BuildUpsertSql("stg", "tgt", keys, all);

        sql.Should().Contain("\"a\" = EXCLUDED.\"a\"");
        sql.Should().Contain("\"b\" = EXCLUDED.\"b\"");
        sql.Should().NotMatchRegex(@"SET.*""id""");
    }

    [TestMethod]
    public void PgLoader_BuildUpsertSql_all_key_columns_produces_do_nothing()
    {
        var keys = new[] { "id" };
        var all  = new[] { "id" }; // only the key column

        var sql = PostgreSqlBulkLoader.BuildUpsertSql("stg", "tgt", keys, all);

        sql.Should().Contain("DO NOTHING",
            "when no update columns exist, DO NOTHING must be used");
        sql.Should().NotContain("DO UPDATE SET");
    }

    // ─── 12: PostgreSqlBulkLoader.QuoteIdentifier ─────────────────────────

    [TestMethod]
    public void PgLoader_QuoteIdentifier_wraps_simple_name_in_double_quotes()
    {
        PostgreSqlBulkLoader.QuoteIdentifier("myTable")
            .Should().Be("\"myTable\"");
    }

    [TestMethod]
    public void PgLoader_QuoteIdentifier_handles_schema_qualified_name()
    {
        PostgreSqlBulkLoader.QuoteIdentifier("public.stg_orders")
            .Should().Be("\"public\".\"stg_orders\"");
    }

    [TestMethod]
    public void PgLoader_QuoteIdentifier_escapes_embedded_double_quotes()
    {
        PostgreSqlBulkLoader.QuoteIdentifier("my\"table")
            .Should().Be("\"my\"\"table\"");
    }

    // ─── 13–16: MySqlBulkLoader.BuildUpsertSql ────────────────────────────

    [TestMethod]
    public void MySqlLoader_BuildUpsertSql_single_key_produces_on_duplicate_key_update()
    {
        var sql = MySqlBulkLoader.BuildUpsertSql(
            "stg_orders", "orders",
            new[] { "id" },
            new[] { "id", "name", "value" });

        sql.Should().Contain("ON DUPLICATE KEY UPDATE",
            "MySQL upsert uses ON DUPLICATE KEY UPDATE");
        sql.Should().Contain("`name` = VALUES(`name`)",
            "non-key column 'name' must be assigned via VALUES()");
        sql.Should().Contain("`value` = VALUES(`value`)",
            "non-key column 'value' must be assigned via VALUES()");
        // key column must NOT appear in the UPDATE list
        sql.Should().NotMatchRegex(@"UPDATE.*`id`",
            "key column must not appear in ON DUPLICATE KEY UPDATE assignment");
    }

    [TestMethod]
    public void MySqlLoader_BuildUpsertSql_composite_key_excludes_all_keys_from_update()
    {
        var keyColumns = new[] { "tenant_id", "order_no" };
        var allColumns = new[] { "tenant_id", "order_no", "amount", "status" };

        var sql = MySqlBulkLoader.BuildUpsertSql("stg", "orders", keyColumns, allColumns);

        sql.Should().Contain("ON DUPLICATE KEY UPDATE");
        sql.Should().Contain("`amount` = VALUES(`amount`)");
        sql.Should().Contain("`status` = VALUES(`status`)");
        sql.Should().NotContain("VALUES(`tenant_id`)");
        sql.Should().NotContain("VALUES(`order_no`)");
    }

    [TestMethod]
    public void MySqlLoader_BuildUpsertSql_all_key_columns_produces_insert_ignore()
    {
        var keys = new[] { "id" };
        var all  = new[] { "id" };

        var sql = MySqlBulkLoader.BuildUpsertSql("stg", "tgt", keys, all);

        sql.Should().Contain("INSERT IGNORE",
            "when no update columns exist, INSERT IGNORE must be used");
        sql.Should().NotContain("ON DUPLICATE KEY UPDATE");
    }

    // ─── 16: MySqlBulkLoader.QuoteIdentifier ──────────────────────────────

    [TestMethod]
    public void MySqlLoader_QuoteIdentifier_wraps_simple_name_in_backticks()
    {
        MySqlBulkLoader.QuoteIdentifier("myTable")
            .Should().Be("`myTable`");
    }

    [TestMethod]
    public void MySqlLoader_QuoteIdentifier_handles_schema_qualified_name()
    {
        MySqlBulkLoader.QuoteIdentifier("mydb.stg_orders")
            .Should().Be("`mydb`.`stg_orders`");
    }

    [TestMethod]
    public void MySqlLoader_QuoteIdentifier_escapes_embedded_backticks()
    {
        MySqlBulkLoader.QuoteIdentifier("my`table")
            .Should().Be("`my``table`");
    }

    // ─── 17–18: ReplaceAsync guard fires before DB connection ─────────────

    [TestMethod]
    public async Task PgLoader_ReplaceAsync_throws_ArgumentException_for_semicolon_whereClause()
    {
        var loader = new PostgreSqlBulkLoader();

        var ex = await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await loader.ReplaceAsync(
                "Host=fake;Database=fake;",
                "stg_orders",
                "orders",
                "1=1; DROP TABLE orders",
                default));

        ex.Message.Should().Contain("whereClause");
    }

    [TestMethod]
    public async Task PgLoader_ReplaceAsync_throws_ArgumentException_for_comment_whereClause()
    {
        var loader = new PostgreSqlBulkLoader();

        await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await loader.ReplaceAsync(
                "Host=fake;Database=fake;",
                "stg",
                "tgt",
                "1=1 -- injected",
                default));
    }

    [TestMethod]
    public async Task MySqlLoader_ReplaceAsync_throws_ArgumentException_for_semicolon_whereClause()
    {
        var loader = new MySqlBulkLoader();

        var ex = await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await loader.ReplaceAsync(
                "Server=fake;Database=fake;",
                "stg_orders",
                "orders",
                "1=1; DROP TABLE orders",
                default));

        ex.Message.Should().Contain("whereClause");
    }

    [TestMethod]
    public async Task MySqlLoader_ReplaceAsync_throws_ArgumentException_for_comment_whereClause()
    {
        var loader = new MySqlBulkLoader();

        await Assert.ThrowsExceptionAsync<ArgumentException>(async () =>
            await loader.ReplaceAsync(
                "Server=fake;Database=fake;",
                "stg",
                "tgt",
                "1=1 -- injected",
                default));
    }

    // ─── 19: PostgreSqlBulkLoader.ParseSchemaAndTable ─────────────────────

    [TestMethod]
    public void PgLoader_ParseSchemaAndTable_schema_qualified_name()
    {
        var (schema, table) = PostgreSqlBulkLoader.ParseSchemaAndTable("public.stg_orders");
        schema.Should().Be("public");
        table.Should().Be("stg_orders");
    }

    [TestMethod]
    public void PgLoader_ParseSchemaAndTable_bare_table_defaults_to_public()
    {
        var (schema, table) = PostgreSqlBulkLoader.ParseSchemaAndTable("stg_orders");
        schema.Should().Be("public");
        table.Should().Be("stg_orders");
    }

    // ─── Case-insensitive registry lookups ────────────────────────────────

    [TestMethod]
    public void DefaultRegistry_postgresql_key_is_case_insensitive()
    {
        using var src1 = EtlSourceFactory.DefaultRegistry.Create("POSTGRESQL");
        using var src2 = EtlSourceFactory.DefaultRegistry.Create("PostgreSQL");
        src1.Should().BeOfType<PostgreSqlSource>();
        src2.Should().BeOfType<PostgreSqlSource>();
    }

    [TestMethod]
    public void DefaultRegistry_mysql_key_is_case_insensitive()
    {
        using var src1 = EtlSourceFactory.DefaultRegistry.Create("MySQL");
        using var src2 = EtlSourceFactory.DefaultRegistry.Create("MYSQL");
        src1.Should().BeOfType<MySqlEtlSource>();
        src2.Should().BeOfType<MySqlEtlSource>();
    }
}
