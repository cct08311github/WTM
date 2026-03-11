#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Models;

namespace WalkingTec.Mvvm.Etl.ViewModels;

public class EtlRunLogSearcher : BaseSearcher
{
    [Display(Name = "Job")]
    public Guid? JobId { get; set; }

    [Display(Name = "結果")]
    public EtlRunResult? Result { get; set; }

    [Display(Name = "觸發方式")]
    public EtlRunTrigger? Trigger { get; set; }

    [Display(Name = "開始時間(起)")]
    public DateTime? StartedAtBegin { get; set; }

    [Display(Name = "開始時間(迄)")]
    public DateTime? StartedAtEnd { get; set; }
}
