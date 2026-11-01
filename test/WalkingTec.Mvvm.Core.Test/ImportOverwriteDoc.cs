using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// Model for the import overwrite tests (#396): one mapped scalar, one [NotMapped] scalar, and one property that is
    /// excluded from the model only by the fluent configuration in the test DataContext (invisible to attribute checks).
    /// </summary>
    public class ImportOverwriteDoc : BasePoco
    {
        [Display(Name = "编码")]
        [Required]
        public string Code { get; set; }

        [Display(Name = "标题")]
        public string Title { get; set; }

        [Display(Name = "备注")]
        [NotMapped]
        public string Memo { get; set; }

        [Display(Name = "忽略")]
        public string FluentIgnored { get; set; }

        [Display(Name = "标签")]
        public List<string> Tags { get; set; }

        [Display(Name = "忽略标签")]
        public List<string> FluentIgnoredTags { get; set; }
    }
}
