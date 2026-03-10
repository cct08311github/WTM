#nullable enable
using System;

namespace WalkingTec.Mvvm.Core.Cache
{
    /// <summary>
    /// 標記此 Model 為靜態/參數表，框架啟動後自動快取全表資料並在寫入時失效。
    /// 適用於低異動頻率的參照資料（字典表、城市代碼、品類等）。
    /// </summary>
    /// <remarks>
    /// 使用方式：
    /// <code>
    /// [CacheLookup(TtlMinutes = 60)]
    /// public class CityCode : BasePoco { ... }
    ///
    /// // 任意 VM 或 Controller 內
    /// var cities = Wtm.GetLookup&lt;CityCode&gt;();
    /// var active = Wtm.GetLookup&lt;CityCode&gt;(x =&gt; x.IsActive);
    /// </code>
    ///
    /// 失效時機：透過 <see cref="LookupInvalidationInterceptor"/> 在 SaveChanges 時自動失效。
    /// 應用需在 DbContextOptions 加入該 Interceptor 才能觸發自動失效（見 docs/lookup-cache.md）。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class CacheLookupAttribute : Attribute
    {
        /// <summary>快取存活時間（分鐘），預設 30 分鐘</summary>
        public int TtlMinutes { get; set; } = 30;

        /// <summary>
        /// 是否依租戶隔離快取鍵。
        /// 實作 ITenant 的 Model 建議保持預設值 true，避免租戶資料互串。
        /// </summary>
        public bool TenantIsolation { get; set; } = true;

        /// <summary>
        /// 是否在應用啟動時預熱快取（BackgroundService 背景執行，不阻擋啟動）。
        /// 預設 true。
        /// </summary>
        public bool WarmOnStartup { get; set; } = true;
    }
}
