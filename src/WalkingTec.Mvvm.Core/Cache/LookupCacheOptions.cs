#nullable enable
namespace WalkingTec.Mvvm.Core.Cache
{
    /// <summary>
    /// Lookup Cache 全域設定。透過 DI 注入，個別 <see cref="CacheLookupAttribute"/> 可覆蓋。
    /// </summary>
    public class LookupCacheOptions
    {
        /// <summary>全域預設 TenantIsolation 值（預設 true）。個別 Attribute 可覆蓋。</summary>
        public bool DefaultTenantIsolation { get; set; } = true;
    }
}
