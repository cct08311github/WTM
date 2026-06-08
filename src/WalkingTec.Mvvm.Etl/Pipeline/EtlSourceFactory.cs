#nullable enable
using System;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;
using WalkingTec.Mvvm.Etl.Pipeline.Sources;

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// 根據 DBTypeEnum 建立對應的 IEtlSource 和 IBulkLoader。
///
/// <para>
/// 自 10.6+ 起，內建的 Oracle/Mssql source 已透過
/// <see cref="EtlSourceRegistry"/> 登記；<see cref="CreateSource(DBTypeEnum)"/>
/// 維持原有 API 不變（back-compat），底層改為呼叫 registry。
/// 需要自訂 source 種類時，請直接使用 <see cref="IEtlSourceRegistry"/>。
/// </para>
/// </summary>
public static class EtlSourceFactory
{
    // Shared default registry — holds the built-in Oracle/Mssql registrations.
    // Application code can also inject IEtlSourceRegistry via DI for richer scenarios.
    internal static readonly EtlSourceRegistry DefaultRegistry = BuildDefaultRegistry();

    private static EtlSourceRegistry BuildDefaultRegistry()
    {
        var reg = new EtlSourceRegistry();
        reg.Register("sqlserver",  () => new MssqlSource());
        reg.Register("oracle",     () => new OracleSource());
        reg.Register("postgresql", () => new PostgreSqlSource());
        reg.Register("pgsql",      () => new PostgreSqlSource());
        reg.Register("mysql",      () => new MySqlEtlSource());
        reg.Register("csv",        () => new CsvEtlSource());
        reg.Register("excel",      () => new ExcelEtlSource());
        return reg;
    }

    /// <summary>
    /// 根據 DBTypeEnum 建立對應的 IEtlSource（back-compat API）。
    /// Oracle → <see cref="OracleSource"/>，SqlServer → <see cref="MssqlSource"/>，
    /// PgSql → <see cref="PostgreSqlSource"/>，MySql → <see cref="MySqlEtlSource"/>。
    /// </summary>
    public static IEtlSource CreateSource(DBTypeEnum dbType) => dbType switch
    {
        DBTypeEnum.SqlServer => DefaultRegistry.Create("sqlserver"),
        DBTypeEnum.Oracle    => DefaultRegistry.Create("oracle"),
        DBTypeEnum.PgSql     => DefaultRegistry.Create("postgresql"),
        DBTypeEnum.MySql     => DefaultRegistry.Create("mysql"),
        _ => throw new NotSupportedException($"ETL source not supported for {dbType}")
    };

    /// <summary>
    /// 根據 source-kind key 從預設 registry 建立 IEtlSource。
    /// 支援：<c>"sqlserver"</c>、<c>"oracle"</c>、<c>"csv"</c>、<c>"excel"</c>，
    /// 以及任何透過 <see cref="IEtlSourceRegistry.Register"/> 額外登記的 key（大小寫不敏感）。
    /// </summary>
    /// <param name="sourceKind">source 種類識別字串（大小寫不敏感）</param>
    public static IEtlSource CreateSource(string sourceKind) =>
        DefaultRegistry.Create(sourceKind);

    /// <summary>
    /// 根據 DBTypeEnum 建立對應的 IBulkLoader（back-compat API）。
    /// SqlServer → <see cref="MssqlBulkLoader"/>，Oracle → <see cref="OracleBulkLoader"/>，
    /// PgSql → <see cref="PostgreSqlBulkLoader"/>，MySql → <see cref="MySqlBulkLoader"/>。
    /// </summary>
    public static IBulkLoader CreateLoader(DBTypeEnum targetDbType) => targetDbType switch
    {
        DBTypeEnum.SqlServer => new MssqlBulkLoader(),
        DBTypeEnum.Oracle    => new OracleBulkLoader(),
        DBTypeEnum.PgSql     => new PostgreSqlBulkLoader(),
        DBTypeEnum.MySql     => new MySqlBulkLoader(),
        _ => throw new NotSupportedException($"ETL loader not supported for {targetDbType}")
    };
}
