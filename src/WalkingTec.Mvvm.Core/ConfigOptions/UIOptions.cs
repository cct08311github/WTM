#nullable enable
namespace WalkingTec.Mvvm.Core.ConfigOptions
{
    public class UIOptions
    {
        public DataTableOptions DataTable { get; set; } = new DataTableOptions();
        public ComboBoxOptions ComboBox { get; set; } = new ComboBoxOptions();
        public DateTimeOptions DateTime { get; set; } = new DateTimeOptions();
        public SearchPanelOptions SearchPanel { get; set; } = new SearchPanelOptions();

        public class DataTableOptions
        {
            /// <summary>
            /// 默认列表行数
            /// </summary>
            public int RPP { get; set; } = DefaultConfigConsts.DEFAULT_RPP;

            public bool ShowPrint { get; set; }

            public bool ShowFilter { get; set; }
        }

        public class ComboBoxOptions
        {

            /// <summary>
            /// 默认允许ComboBox搜索
            /// </summary>
            public bool DefaultEnableSearch { get; set; } = DefaultConfigConsts.DEFAULT_COMBOBOX_DEFAULT_ENABLE_SEARCH;
        }

        public class DateTimeOptions
        {

            /// <summary>
            /// 默认开启DateTime只读
            /// </summary>
            public bool DefaultReadonly { get; set; } = DefaultConfigConsts.DEFAULT_DATETIME_DEFAULT_READONLY;
        }

        public class SearchPanelOptions
        {

            /// <summary>
            /// 默认展开SearchPanel内容
            /// </summary>
            public bool DefaultExpand { get; set; } = DefaultConfigConsts.DEFAULT_SEARCHPANEL_DEFAULT_EXPAND;
        }
    }
}
