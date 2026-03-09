#nullable enable
using System;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 分析查詢快取介面，用於快取 QueryHash 對應的查詢結果。
    /// </summary>
    public interface IAnalysisCache
    {
        bool TryGet(string queryHash, out AnalysisQueryResponse? cached);
        void Set(string queryHash, AnalysisQueryResponse response, TimeSpan? ttl = null);
        void Invalidate(string queryHash);
        void InvalidateAll();
    }
}
