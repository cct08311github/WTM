using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Integration.Test.Models;

public class TenantSchool : BasePoco, ITenant
{
    [Required]
    [StringLength(50)]
    public string SchoolName { get; set; } = "";

    [StringLength(50)]
    public string? TenantCode { get; set; }
}
