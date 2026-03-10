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
    /// 失效時機：透過 <see cref="FrameworkContext.SaveChanges()"/> 在 SaveChanges 時自動失效。
    /// 只要透過 WTM 的 DC（FrameworkContext 子類別）寫入資料，快取即自動失效。
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class CacheLookupAttribute : Attribute
    {
        /// <summary>快取存活時間（分鐘），預設 30 分鐘</summary>
        public int TtlMinutes { get; set; } = 30;

        // Attribute 參數不支援 bool?，使用 sentinel 模式模擬三態
        private const int Unset = -1;
        private int _tenantIsolation = Unset;

        /// <summary>
        /// 是否依租戶隔離快取鍵。未設定時使用全域預設值（見 <see cref="LookupCacheOptions.DefaultTenantIsolation"/>）。
        /// 明確設為 true/false 會覆蓋全域預設。
        /// 實作 ITenant 的 Model 建議保持預設（或明確設為 true），避免租戶資料互串。
        /// </summary>
        public bool TenantIsolation
        {
            get => _tenantIsolation != Unset && _tenantIsolation != 0;
            set => _tenantIsolation = value ? 1 : 0;
        }

        /// <summary>
        /// 取得 TenantIsolation 的三態值：true、false 或 null（未設定，使用全域預設）。
        /// 此屬性供框架內部使用，不在 Attribute 語法中顯示。
        /// </summary>
        internal bool? TenantIsolationOrNull =>
            _tenantIsolation == Unset ? null : _tenantIsolation != 0;

        /// <summary>
        /// 是否在應用啟動時預熱快取（BackgroundService 背景執行，不阻擋啟動）。
        /// 預設 true。
        /// </summary>
        public bool WarmOnStartup { get; set; } = true;

        /// <summary>
        /// 指定此 Model 所在的資料庫連線鍵（對應 appsettings.json 中 Connections 的 Key）。
        /// 為 null 時使用預設的 Wtm.DC。
        /// </summary>
        public string? ConnectionKey { get; set; }
    }
}
