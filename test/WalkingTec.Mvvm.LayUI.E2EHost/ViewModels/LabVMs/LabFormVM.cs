using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.LayUI.E2EHost.Fixtures;
using WalkingTec.Mvvm.LayUI.E2EHost.Models;
using WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.GadgetVMs;

namespace WalkingTec.Mvvm.LayUI.E2EHost.ViewModels.LabVMs
{
    /// <summary>A file reference used by the multi upload fixture (the tag helper reads FileId from ISubFile).</summary>
    public class LabFile : ISubFile
    {
        public Guid FileId { get; set; }
        public FileAttachment File { get; set; }
        public int Order { get; set; }
    }

    /// <summary>
    /// One view model for all the Lab fixture pages: a property for every kind of form field the LayUI tag helpers render.
    /// The values are synthetic. The pages post to /Lab/Save, which records the raw form so that a test can read back what was sent.
    /// </summary>
    public class LabFormVM : BaseVM
    {
        // ---- text inputs
        [Display(Name = "Title")]
        public string Title { get; set; }

        [Display(Name = "Secret")]
        public string Secret { get; set; }

        [Display(Name = "Memo")]
        public string Memo { get; set; }

        [Display(Name = "Rich")]
        public string Rich { get; set; }

        [Display(Name = "Keep")]
        public string Keep { get; set; }

        [Display(Name = "Shown")]
        public string Shown { get; set; }

        [Display(Name = "Shown Amount")]
        public decimal ShownAmount { get; set; }

        // ---- sliders, colour
        [Display(Name = "Level")]
        public int Level { get; set; }

        [Display(Name = "Low")]
        public int Low { get; set; }

        [Display(Name = "High")]
        public int High { get; set; }

        [Display(Name = "Rate")]
        public decimal Rate { get; set; }

        [Display(Name = "Color")]
        public string Color { get; set; }

        // ---- choices
        [Display(Name = "Enabled")]
        public bool? Enabled { get; set; }

        [Display(Name = "Kind")]
        public GadgetCategory? Kind { get; set; }

        [Display(Name = "Choice")]
        public string Choice { get; set; }

        [Display(Name = "Flags")]
        public List<string> Flags { get; set; }

        [Display(Name = "Combo")]
        public GadgetCategory? Combo { get; set; }

        [Display(Name = "Multi Combo")]
        public List<string> MultiCombo { get; set; }

        [Display(Name = "Region")]
        public string Region { get; set; }

        [Display(Name = "City")]
        public string City { get; set; }

        [Display(Name = "Remote")]
        public string Remote { get; set; }

        [Display(Name = "Remote Radio")]
        public string RemoteRadio { get; set; }

        [Display(Name = "Remote Check")]
        public List<string> RemoteCheck { get; set; }

        [Display(Name = "Transfer")]
        public List<string> Picked { get; set; }

        // ---- dates
        [Display(Name = "Day")]
        public DateTime? Day { get; set; }

        [Display(Name = "Moment")]
        public DateTime? Moment { get; set; }

        [Display(Name = "Period")]
        public string Period { get; set; }

        // ---- tree
        [Display(Name = "Branch")]
        public string Branch { get; set; }

        [Display(Name = "Branches")]
        public List<string> Branches { get; set; }

        [Display(Name = "Remote Branch")]
        public string RemoteBranch { get; set; }

        [Display(Name = "Linked Branch")]
        public string LinkedBranch { get; set; }

        [Display(Name = "Tree City")]
        public string TreeCity { get; set; }

        /// <summary>The node the tree container with the special ids shows as selected (not a posted field).</summary>
        public string SpecialNodeId { get; set; }

        // ---- selector and files
        [Display(Name = "Gadget")]
        public Guid? GadgetId { get; set; }

        [Display(Name = "Gadgets")]
        public List<Guid> GadgetIds { get; set; }

        [Display(Name = "Attachment")]
        public Guid? AttachmentId { get; set; }

        [Display(Name = "Picture")]
        public Guid? PictureId { get; set; }

        [Display(Name = "Files")]
        public List<LabFile> Files { get; set; }

        // ---- editable grid
        public LabDetailListVM DetailGrid { get; set; }

        [Display(Name = "Detail Rows")]
        public List<LabDetailRow> DetailRows { get; set; }

        // ---- items, not posted
        public List<ComboSelectListItem> TextItems { get; set; }
        public List<ComboSelectListItem> PlainValueItems { get; set; }
        public List<ComboSelectListItem> RegionItems { get; set; }
        public List<TreeSelectListItem> TreeItems { get; set; }
        public List<TreeSelectListItem> PageTreeItems { get; set; }
        public List<TreeSelectListItem> SpecialTreeItems { get; set; }
        public List<string> PlainItems { get; set; }
        public GadgetListVM AllGadgets { get; set; }

        /// <summary>Selector fixture whose value is the name of the row (a string with characters that need encoding for some rows).</summary>
        [Display(Name = "Item names")]
        public List<string> ItemNames { get; set; }
        public LabItemListVM AllItems { get; set; }

        /// <summary>Synthetic characters that need encoding, see <see cref="LabText.Special"/>.</summary>
        public string SpecialText => LabText.Special;

        public LabFormVM()
        {
            DetailGrid = new LabDetailListVM { DetailGridPrix = "DetailRows" };
        }

        protected override void InitVM()
        {
            DetailGrid.CopyContext(this);
            TextItems = LabText.Items();
            PlainValueItems = LabText.PlainValueItems();
            RegionItems = new List<ComboSelectListItem>
            {
                new ComboSelectListItem { Value = "north", Text = "North" },
                new ComboSelectListItem { Value = "south", Text = "South" },
                new ComboSelectListItem { Value = LabText.SpecialId, Text = "Special region" }
            };
            TreeItems = LabText.Tree();
            PageTreeItems = LabText.TreeWithPages();
            SpecialTreeItems = LabText.TreeWithSpecialIds();
            PlainItems = new List<string> { "alpha", "beta", "gamma" };
            AllGadgets = new GadgetListVM();
            AllItems = new LabItemListVM();
            Level = Level == 0 ? 30 : Level;
            Low = Low == 0 ? 20 : Low;
            High = High == 0 ? 70 : High;
            Rate = Rate == 0 ? 30.5m : Rate;
        }
    }
}
