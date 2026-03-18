#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WalkingTec.Mvvm.Core
{
    [Table("FrameworkWorkflows")]
    [Obsolete("WTM's built-in Elsa workflow integration has been removed. This member will be deleted in the next major version. See CHANGELOG.md for migration guidance.")]
    public class FrameworkWorkflow : TopBasePoco, ITenant
    {
        [Required]
        [StringLength(50)]
        public string UserCode { get; set; } = "";
        [StringLength(50)]
        public string? Tag { get; set; }
        [StringLength(450)]
        public string? WorkflowName { get; set; }
        [StringLength(450)]
        public string? ModelType { get; set; }
        [StringLength(100)]
        public string? ModelID { get; set; }
        [StringLength(50)]
        public string? Submitter { get; set; }

        [Required]
        [StringLength(50)]
        public string WorkflowId { get; set; } = "";
        [Required]
        [StringLength(50)]
        public string ActivityId { get; set; } = "";
        [Display(Name = "_Admin.Tenant")]
        [StringLength(50)]
        public string? TenantCode { get; set; }

        public DateTime? StartTime { get; set; }
    }

}
