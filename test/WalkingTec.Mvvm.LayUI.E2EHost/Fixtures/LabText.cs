using System.Collections.Generic;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.LayUI.E2EHost.Fixtures
{
    /// <summary>
    /// Synthetic text used by the fixture pages. <see cref="Special"/> holds characters that need HTML and JavaScript
    /// encoding (angle brackets, ampersand, both quote kinds, backslash, a closing script / textarea tag, template
    /// delimiters and non-ASCII text). The browser tests expect it to be shown and posted back unchanged and shown as text.
    /// Each element-like fragment carries an id starting with "e2e-probe" so that a test can check that no element was created from it.
    /// </summary>
    public static class LabText
    {
        public const string Special = "Tom & \"Jerry\" 'it' <i id=\"e2e-probe\">tag</i> </textarea></script> \\ ${x} {{y}} 100% 測試";

        /// <summary>Same idea for item lists (check boxes, radios, combo boxes, transfer, tree).</summary>
        public const string SpecialItem = "A&B <i id=\"e2e-probe-item\">x</i> \"q\" 'q'";

        public const string Plain = "plain";

        public static List<ComboSelectListItem> Items()
        {
            return new List<ComboSelectListItem>
            {
                new ComboSelectListItem { Value = "plain", Text = "Plain choice" },
                new ComboSelectListItem { Value = SpecialItem, Text = SpecialItem },
                new ComboSelectListItem { Value = "third", Text = "Third choice" }
            };
        }

        /// <summary>Items whose text needs encoding but whose values are plain (the usual case: ids as values).</summary>
        public static List<ComboSelectListItem> PlainValueItems()
        {
            return new List<ComboSelectListItem>
            {
                new ComboSelectListItem { Value = "p1", Text = "First item" },
                new ComboSelectListItem { Value = "p2", Text = SpecialItem },
                new ComboSelectListItem { Value = "p3", Text = "Third item" }
            };
        }

        /// <summary>Tree whose leaves point at pages: the tree container loads the page of the clicked leaf into its content area.</summary>
        public static List<TreeSelectListItem> TreeWithPages()
        {
            return new List<TreeSelectListItem>
            {
                new TreeSelectListItem { Value = "pa", Id = "pa", Text = "Pane A", Url = "/Lab/Pane?id=a" },
                new TreeSelectListItem { Value = "pb", Id = "pb", Text = "Pane B", Url = "/Lab/Pane?id=b" },
                new TreeSelectListItem { Value = "pc", Id = "pc", Text = "Broken pane", Url = "/Lab/Fail" }
            };
        }

        public static List<TreeSelectListItem> Tree()
        {
            return new List<TreeSelectListItem>
            {
                new TreeSelectListItem
                {
                    Value = "root1", Id = "root1", Text = "Branch one", Expended = true,
                    Children = new List<TreeSelectListItem>
                    {
                        new TreeSelectListItem { Value = "leaf1a", Id = "leaf1a", Text = "Leaf 1A" },
                        new TreeSelectListItem { Value = "leaf1b", Id = "leaf1b", Text = SpecialItem }
                    }
                },
                new TreeSelectListItem
                {
                    Value = "root2", Id = "root2", Text = "Branch two",
                    Children = new List<TreeSelectListItem>
                    {
                        new TreeSelectListItem { Value = "leaf2a", Id = "leaf2a", Text = "Leaf 2A" }
                    }
                }
            };
        }
    }
}
