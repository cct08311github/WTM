using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>Guid-key model with three columns that can each be a duplicate group (#397).</summary>
    public class ImportSameRecordDoc : BasePoco
    {
        [Display(Name = "编码")]
        [Required]
        public string Code { get; set; }

        [Display(Name = "标题")]
        public string Title { get; set; }

        [Display(Name = "引用")]
        public string Ref { get; set; }
    }

    /// <summary>Int-key variant (the key is assigned by the caller, like BatchIntKeyDoc) (#397).</summary>
    public class ImportSameRecordIntDoc : TopBasePoco
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public new int ID { get; set; }

        [Display(Name = "编码")]
        [Required]
        public string Code { get; set; }

        [Display(Name = "标题")]
        public string Title { get; set; }
    }

    /// <summary>String-key variant (#397).</summary>
    public class ImportSameRecordStringDoc : TopBasePoco
    {
        [Key]
        [StringLength(50)]
        public new string ID { get; set; }

        [Display(Name = "编码")]
        [Required]
        public string Code { get; set; }

        [Display(Name = "标题")]
        public string Title { get; set; }
    }
}
