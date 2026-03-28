#nullable enable
namespace WalkingTec.Mvvm.Core.ConfigOptions
{
    /// <summary>
    /// Centralized UI styling options. Provides overridable CSS class names,
    /// sizing defaults, and rendering options for TagHelpers.
    /// Consumers configure via <c>services.Configure&lt;WtmUIOptions&gt;(...)</c>.
    /// All defaults match LayUI classes for zero-config backwards compatibility.
    /// </summary>
    public class WtmUIOptions
    {
        #region CSS Class Mappings

        /// <summary>Primary button class (e.g. "layui-btn").</summary>
        public string ButtonPrimaryClass { get; set; } = "layui-btn";

        /// <summary>Small button class (e.g. "layui-btn layui-btn-xs").</summary>
        public string ButtonSmallClass { get; set; } = "layui-btn layui-btn-xs";

        /// <summary>Danger/delete button class.</summary>
        public string ButtonDangerClass { get; set; } = "layui-btn layui-btn-danger";

        /// <summary>Warm/warning button class.</summary>
        public string ButtonWarmClass { get; set; } = "layui-btn layui-btn-warm";

        /// <summary>Form item wrapper class.</summary>
        public string FormItemClass { get; set; } = "layui-form-item";

        /// <summary>Form label class.</summary>
        public string FormLabelClass { get; set; } = "layui-form-label";

        /// <summary>Text input class.</summary>
        public string InputClass { get; set; } = "layui-input";

        /// <summary>Form group/container class.</summary>
        public string FormGroupClass { get; set; } = "layui-form";

        /// <summary>Disabled element class.</summary>
        public string DisabledClass { get; set; } = "layui-disabled";

        /// <summary>Table class.</summary>
        public string TableClass { get; set; } = "layui-table";

        /// <summary>Row container class.</summary>
        public string RowClass { get; set; } = "layui-row";

        #endregion

        #region Grid System

        /// <summary>Number of grid columns (default 12 for LayUI).</summary>
        public int GridColumns { get; set; } = 12;

        /// <summary>Column class prefix for medium screens (e.g. "layui-col-md").</summary>
        public string ColumnMdPrefix { get; set; } = "layui-col-md";

        /// <summary>Column class prefix for small screens.</summary>
        public string ColumnSmPrefix { get; set; } = "layui-col-sm";

        /// <summary>Column class prefix for extra-small screens.</summary>
        public string ColumnXsPrefix { get; set; } = "layui-col-xs";

        #endregion

        #region Sizing & Layout

        /// <summary>Default label width in pixels (null = auto).</summary>
        public int? DefaultLabelWidth { get; set; }

        /// <summary>Margin offset added to input when label has explicit width.</summary>
        public int LabelMarginOffset { get; set; } = 30;

        /// <summary>Default input height in CSS (e.g. "28px").</summary>
        public string DefaultInputHeight { get; set; } = "28px";

        #endregion

        #region Required Field Marker

        /// <summary>
        /// HTML for the required field marker. Default is a red asterisk.
        /// Override to add icons, screen-reader text, or change styling.
        /// </summary>
        public string RequiredMarkerHtml { get; set; } = "<span aria-hidden=\"true\" style=\"color:red\">*</span>";

        #endregion

        #region Accessibility

        /// <summary>Whether to add aria-required="true" to required form fields.</summary>
        public bool EnableAriaRequired { get; set; } = true;

        #endregion
    }
}
