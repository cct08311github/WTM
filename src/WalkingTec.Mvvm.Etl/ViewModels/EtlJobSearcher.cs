#nullable enable
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.ViewModels;

public class EtlJobSearcher : BaseSearcher
{
    [Display(Name = "Job 名稱")]
    public string? Name { get; set; }

    [Display(Name = "狀態")]
    public EtlJobStatus? Status { get; set; }

    [Display(Name = "來源 DB 類型")]
    public DBTypeEnum? SourceDbType { get; set; }
}
