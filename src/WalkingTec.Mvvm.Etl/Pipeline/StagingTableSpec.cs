#nullable enable
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// Staging table 結構定義
/// </summary>
public class StagingTableSpec
{
    public string TableName { get; }
    public IReadOnlyList<StagingColumn> Columns { get; }

    public StagingTableSpec(string tableName, params StagingColumn[] columns)
    {
        TableName = tableName;
        Columns = columns;
    }
}

/// <summary>
/// Staging table 欄位定義（名稱 + 原生 SQL 類型）
/// </summary>
public record StagingColumn(string Name, string SqlType);
