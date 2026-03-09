#nullable enable
using System;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 不執行任何快取的空實作，用於停用快取時。
    /// </summary>
    public class NullAnalysisCache : IAnalysisCache
    {
        public bool TryGet(string queryHash, out AnalysisQueryResponse? cached)
        {
            cached = null;
            return false;
        }

        public void Set(string queryHash, AnalysisQueryResponse response, TimeSpan? ttl = null) { }
        public void Invalidate(string queryHash) { }
        public void InvalidateAll() { }
    }
}
