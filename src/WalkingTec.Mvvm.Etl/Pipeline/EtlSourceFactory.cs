#nullable enable
using System;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Pipeline.Loaders;
using WalkingTec.Mvvm.Etl.Pipeline.Sources;

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// 根據 DBTypeEnum 建立對應的 IEtlSource 和 IBulkLoader
/// </summary>
public static class EtlSourceFactory
{
    public static IEtlSource CreateSource(DBTypeEnum dbType) => dbType switch
    {
        DBTypeEnum.SqlServer => new MssqlSource(),
        // DBTypeEnum.Oracle => new OracleSource(),  // PR4
        _ => throw new NotSupportedException($"ETL source not supported for {dbType}")
    };

    public static IBulkLoader CreateLoader(DBTypeEnum targetDbType) => targetDbType switch
    {
        DBTypeEnum.SqlServer => new MssqlBulkLoader(),
        // DBTypeEnum.Oracle => new OracleBulkLoader(),  // PR4
        _ => throw new NotSupportedException($"ETL loader not supported for {targetDbType}")
    };
}
