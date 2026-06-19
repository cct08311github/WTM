#nullable enable
using System.ComponentModel.DataAnnotations;

namespace WalkingTec.Mvvm.Core.Analysis
{
    /// <summary>
    /// 儲存的 Analysis 查詢設定（維度、度量、篩選條件），支援 private/public 共享。
    /// </summary>
    /// <remarks>
    /// Issue #380: implements <see cref="ITenant"/> so that the DataContext global query filter
    /// scopes saved queries to the current tenant when multi-tenancy is enabled.
    /// In single-tenant deployments (EnableTenant = false) the <see cref="TenantCode"/>
    /// column is nullable and the global filter is never applied — behaviour is unchanged.
    /// </remarks>
    public class AnalysisSavedQuery : BasePoco, ITenant
    {
        // ─── ITenant (Issue #380) ───

        /// <summary>
        /// Tenant discriminator for multi-tenant isolation (Issue #380).
        /// Stamped from <c>Wtm.LoginUserInfo.CurrentTenant</c> on creation.
        /// Null in single-tenant deployments — no filter applied, behaviour unchanged.
        /// </summary>
        [StringLength(50)]
        public string? TenantCode { get; set; }

        [Required]
        [StringLength(100, ErrorMessage = "Validate.{0}stringmax{1}")]
        public string Name { get; set; } = string.Empty;

        /// <summary>對應的 ListVM FullName（在 AnalysisVmRegistry 白名單中查找）。</summary>
        [Required]
        [StringLength(500)]
        public string ListVmType { get; set; } = string.Empty;

        /// <summary>JSON-serialized AnalysisQueryRequest（dims、msrs、filters、hierarchies）。</summary>
        /// <remarks>
        /// L20: [StringLength(65536)] bounds the column to 64 KB.  Tightens validation for
        /// new input only; existing DB column widths are unaffected.
        /// </remarks>
        [Required]
        [StringLength(65536)]
        public string ConfigJson { get; set; } = string.Empty;

        /// <summary>擁有者的 ITCode（登入代碼）。</summary>
        [StringLength(50)]
        public string? OwnerCode { get; set; }

        /// <summary>是否公開（其他使用者可載入，但不能刪除）。</summary>
        public bool IsPublic { get; set; } = false;
    }
}
