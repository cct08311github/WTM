using System.ComponentModel.DataAnnotations;

namespace WalkingTec.Mvvm.Core
{
    public class ErrorMessage : TopBasePoco
    {
        [Display(Name = "Sys.RowIndex")]
        public long Index { get; set; }

        [Display(Name = "Sys.CellIndex")]
        public long Cell { get; set; }
        [Display(Name = "Sys.ErrorMsg")]
        public string? Message { get; set; }
    }
}
