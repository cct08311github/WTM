using System;
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.LayUI.E2EHost.Models
{
    public enum GadgetCategory
    {
        [Display(Name = "Tool")]
        Tool,
        [Display(Name = "Toy")]
        Toy,
        [Display(Name = "Appliance")]
        Appliance
    }

    /// <summary>
    /// Synthetic entity for the generated CRUD module. It carries a string, an enum, a decimal, a nullable date and a nullable bool
    /// so that the generated search panel, grid, form, import and export templates all have something to render.
    /// </summary>
    public class Gadget : BasePoco
    {
        [Display(Name = "Gadget Name")]
        [Required(ErrorMessage = "Validate.{0}required")]
        [StringLength(50, ErrorMessage = "Validate.{0}stringmax{1}")]
        public string Name { get; set; }

        [Display(Name = "Gadget Code")]
        [Required(ErrorMessage = "Validate.{0}required")]
        [StringLength(20, ErrorMessage = "Validate.{0}stringmax{1}")]
        public string Code { get; set; }

        [Display(Name = "Category")]
        public GadgetCategory Category { get; set; }

        [Display(Name = "Unit Price")]
        public decimal Price { get; set; }

        [Display(Name = "Launch Date")]
        public DateTime? LaunchDate { get; set; }

        [Display(Name = "In Stock")]
        public bool? InStock { get; set; }
    }
}
