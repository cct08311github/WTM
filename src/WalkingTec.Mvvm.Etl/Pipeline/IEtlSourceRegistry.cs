#nullable enable
using System;
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// 可插拔 ETL Source 登記表 — 將 source-kind key 對應到 IEtlSource 工廠委派。
/// 透過此介面可在不修改 EtlSourceFactory 的前提下擴充新的 IEtlSource 實作。
/// </summary>
public interface IEtlSourceRegistry
{
    /// <summary>
    /// 登記一個 source-kind key 及其工廠委派。
    /// 同一 key 重複登記時，後者覆蓋前者（最後登記者勝）。
    /// </summary>
    /// <param name="sourceKind">source 種類識別字串（大小寫不敏感）</param>
    /// <param name="factory">建立 IEtlSource 的委派</param>
    void Register(string sourceKind, Func<IEtlSource> factory);

    /// <summary>
    /// 嘗試以 source-kind key 建立對應的 IEtlSource。
    /// </summary>
    /// <param name="sourceKind">source 種類識別字串（大小寫不敏感）</param>
    /// <param name="source">成功時輸出 IEtlSource 實例；失敗時 null</param>
    /// <returns>key 存在且工廠成功建立時 true，否則 false</returns>
    bool TryCreate(string sourceKind, out IEtlSource? source);

    /// <summary>
    /// 以 source-kind key 建立對應的 IEtlSource。
    /// key 不存在時拋 <see cref="NotSupportedException"/>。
    /// </summary>
    /// <param name="sourceKind">source 種類識別字串（大小寫不敏感）</param>
    IEtlSource Create(string sourceKind);

    /// <summary>
    /// 已登記的 source-kind key 列表（皆已正規化為小寫）。
    /// </summary>
    IReadOnlyCollection<string> RegisteredKinds { get; }
}
