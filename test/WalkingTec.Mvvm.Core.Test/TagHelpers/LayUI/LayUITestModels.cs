using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers.LayUI
{
    /// <summary>
    /// Plain VM that carries one property per kind of field the LayUI tag helpers can bind to.
    /// </summary>
    public class LayuiTestVM : BaseVM
    {
        [Display(Name = "User Name")]
        [Required]
        public string Name { get; set; }

        public string Memo { get; set; }

        public int? Age { get; set; }

        public DateTime? Birthday { get; set; }

        public bool IsActive { get; set; }

        public SchoolTypeEnum? Kind { get; set; }

        public List<string> Tags { get; set; }

        public Guid? SchoolId { get; set; }

        public List<Guid> SchoolIds { get; set; }

        public string Color { get; set; }

        public int Score { get; set; }

        public int ScoreMax { get; set; }

        public string Html { get; set; }

        public Guid? PhotoId { get; set; }

        public FileAttachment Photo { get; set; }

        public List<TestSubFile> Files { get; set; }

        public List<ComboSelectListItem> AllSchools { get; set; }

        public List<TreeSelectListItem> Menus { get; set; }

        public List<string> Names { get; set; }
    }

    public class TestSubFile : ISubFile
    {
        public Guid FileId { get; set; }
        public FileAttachment File { get; set; }
        public int Order { get; set; }
    }

    /// <summary>
    /// Minimal list VM (School) used by wt:grid / wt:searchpanel / wt:selector. No database is needed to render them.
    /// </summary>
    public class LayuiSchoolListVM : BasePagedListVM<School, BaseSearcher>
    {
        protected override IEnumerable<IGridColumn<School>> InitGridHeader()
        {
            return new List<GridColumn<School>>
            {
                this.MakeGridHeader(x => x.SchoolCode),
                this.MakeGridHeader(x => x.SchoolName),
                this.MakeGridHeader(x => x.SchoolType),
            };
        }

        public override IOrderedQueryable<School> GetSearchQuery()
        {
            return DC.Set<School>().OrderBy(x => x.ID);
        }
    }
}
