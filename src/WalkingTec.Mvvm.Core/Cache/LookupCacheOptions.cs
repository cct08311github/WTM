#nullable enable
using System;

namespace WalkingTec.Mvvm.Core.Cache
{
    /// <summary>
    /// Lookup Cache 全域設定。透過 DI 注入，個別 <see cref="CacheLookupAttribute"/> 可覆蓋。
    /// </summary>
    public class LookupCacheOptions
    {
        /// <summary>全域預設 TenantIsolation 值（預設 true）。個別 Attribute 可覆蓋。</summary>
        public bool DefaultTenantIsolation { get; set; } = true;

        /// <summary>
        /// Stampede 保護的 per-key semaphore 逾時時間（預設 10 秒）。
        /// <para>
        /// Cache miss 時只有一個執行緒實際查 DB，其餘執行緒在此逾時內等待該執行緒填入快取。逾時後的
        /// 行為依呼叫路徑而異：<c>GetAll</c>/<c>GetAllAsync</c> 回退為直接查 DB、不寫入快取（見
        /// <see cref="LookupCacheService"/> 對應方法上的 Bug #112 (4) / M10 fix 說明）；
        /// <c>RefreshAsync</c> 拋出 <see cref="System.TimeoutException"/>，因為它是呼叫端明確要求
        /// 「立刻生效」的動作，靜默視為成功等同於假象（Bug #804）。
        /// </para>
        /// </summary>
        public TimeSpan StampedeTimeout { get; set; } = TimeSpan.FromSeconds(10);
    }
}
