using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.LayUI.E2EHost.Models
{
    /// <summary>
    /// A row of the editable grid on /Lab/Detail. It is not stored (no DbSet): the grid only needs a TopBasePoco to describe its columns,
    /// the rows live in the browser until the form is posted.
    /// </summary>
    public class LabDetailRow : TopBasePoco
    {
        [Display(Name = "Row Name")]
        public string Name { get; set; }

        [Display(Name = "Row Kind")]
        public GadgetCategory Kind { get; set; }

        [Display(Name = "Row Day")]
        public DateTime? Day { get; set; }
    }
}
