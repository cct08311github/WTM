namespace WalkingTec.Mvvm.Core.ConfigOptions
{
    public class UIOptions
    {
        public DataTableOptions DataTable { get; set; }
        public ComboBoxOptions ComboBox { get; set; }
        public DateTimeOptions DateTime { get; set; }
        public SearchPanelOptions SearchPanel { get; set; }

        /// <summary>
        /// When true, text coming from data or configuration that LayUI TagHelpers and the grid
        /// place into HTML content (display values, titles, labels, button text, flow info, grid cells
        /// without a custom format) is HTML-encoded. Default is false: the text is emitted as is, so
        /// pages that intentionally render HTML keep working.
        /// </summary>
        public bool EncodeHtmlContent { get; set; }

        public class DataTableOptions
        {
            /// <summary>
            /// 默认列表行数
            /// </summary>
            public int RPP { get; set; }

            public bool ShowPrint { get; set; }

            public bool ShowFilter { get; set; }
        }

        public class ComboBoxOptions
        {

            /// <summary>
            /// 默认允许ComboBox搜索
            /// </summary>
            public bool DefaultEnableSearch { get; set; }
        }

        public class DateTimeOptions
        {

            /// <summary>
            /// 默认开启DateTime只读
            /// </summary>
            public bool DefaultReadonly { get; set; }
        }

        public class SearchPanelOptions
        {

            /// <summary>
            /// 默认展开SearchPanel内容
            /// </summary>
            public bool DefaultExpand { get; set; }
        }
    }
}
