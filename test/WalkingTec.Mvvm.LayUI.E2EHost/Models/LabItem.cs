using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.LayUI.E2EHost.Models
{
    /// <summary>
    /// Synthetic entity behind the data table fixture page (/Lab/Grid): enough rows for several pages, a sortable
    /// number, and names that contain characters which need HTML / JavaScript encoding.
    /// </summary>
    public class LabItem : BasePoco
    {
        [Display(Name = "Item Name")]
        [StringLength(100)]
        public string Name { get; set; }

        [Display(Name = "Category")]
        public GadgetCategory Category { get; set; }

        [Display(Name = "Score")]
        public int Score { get; set; }

        [Display(Name = "Active")]
        public bool Active { get; set; }

        [Display(Name = "Created")]
        public DateTime Created { get; set; }
    }
}
