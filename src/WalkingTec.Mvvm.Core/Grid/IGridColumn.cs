#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Grid Column Content Fixed Enum
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum GridColumnFixedEnum
    {
        /// <summary>
        /// 规定在左侧
        /// </summary>
        Left = 0,
        /// <summary>
        /// 规定在右侧
        /// </summary>
        Right = 1,
        /// <summary>
        /// Column is not fixed (default for <see cref="ListColumnAttribute"/>).
        /// </summary>
        None = 2,
    }

    /// <summary>
    /// Grid Column Edit Type Enum
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EditTypeEnum
    {
        Text,
        TextBox,
        ComboBox,
        Datetime,
        CheckBox
    }

    /// <summary>
    /// Grid Column Content Align Enum
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum GridColumnAlignEnum
    {
        /// <summary>
        /// Center
        /// </summary>
        Center = 0,
        /// <summary>
        /// Left
        /// </summary>
        Left = 1,
        /// <summary>
        /// Right
        /// </summary>
        Right = 2,
        /// <summary>
        /// Infer alignment from column content type (default for <see cref="ListColumnAttribute"/>).
        /// </summary>
        Auto = 3,
    }

    /// <summary>
    /// Grid Column Type Enum
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum GridColumnTypeEnum
    {
        /// <summary>
        /// 正常列
        /// </summary>
        Normal = 0,
        /// <summary>
        /// 空列
        /// </summary>
        Space,
        /// <summary>
        /// 操作列
        /// </summary>
        Action
    }

    /// <summary>
    /// Aggregate function type for server-side column footers (#431).
    /// Set on a column via <c>SetAggregate()</c>.
    /// Default <c>None</c> = use the existing per-page <see cref="IGridColumn{T}.ShowTotal"/> behaviour.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum GridAggregateTypeEnum
    {
        /// <summary>No server-side aggregate; use existing ShowTotal per-page sum if set.</summary>
        None = 0,
        /// <summary>Sum of the column over the full filtered result set.</summary>
        Sum,
        /// <summary>Average of the column over the full filtered result set.</summary>
        Avg,
        /// <summary>Count of non-null values of the column over the full filtered result set.</summary>
        Count,
        /// <summary>Minimum value of the column over the full filtered result set.</summary>
        Min,
        /// <summary>Maximum value of the column over the full filtered result set.</summary>
        Max
    }

    /// <summary>
    /// Rich display type for a column (#432).
    /// Set on a column via <c>SetRichColumnType()</c>.
    /// Default <c>Default</c> = plain text / existing format behaviour unchanged.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum GridRichColumnTypeEnum
    {
        /// <summary>Plain text (default, preserves all existing behaviour).</summary>
        Default = 0,
        /// <summary>Renders cell value as a layui progress bar (value must be 0–100).</summary>
        Progress,
        /// <summary>Renders cell value as a layui badge/tag.</summary>
        Tag,
        /// <summary>Renders cell value as an &lt;img&gt; thumbnail (value must be a URL).</summary>
        Image,
        /// <summary>Renders cell value with a locale-aware numeric format string (e.g. "#,##0.00").</summary>
        Currency
    }

    /// <summary>
    /// IGridColumn
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IGridColumn<out T>
    {
        /// <summary>
        /// 表头类型
        /// </summary>
        GridColumnTypeEnum ColumnType { get; set; }

        /// <summary>
        /// 设定字段名
        /// </summary>
        string? Field { get; set; }

        /// <summary>
        /// 标题名称
        /// </summary>
        string? Title { get; set; }

        /// <summary>
        /// 列宽
        /// </summary>
        int? Width { get; set; }
        /// <summary>
        /// //监听单元格事件
        /// </summary>
        string? Event { get; set; }
        /// <summary>
        /// 是否允许排序
        /// </summary>
        bool? Sort { get; set; }

        /// <summary>
        /// 是否固定列
        /// </summary>
        GridColumnFixedEnum? Fixed { get; set; }

        /// <summary>
        /// 对齐方式
        /// </summary>
        GridColumnAlignEnum Align { get; set; }

        /// <summary>
        /// 是否可改变列宽
        /// </summary>
        bool? UnResize { get; set; }

        /// <summary>
        /// 隐藏列
        /// </summary>
        bool? Hide { get; set; }

        /// <summary>
        /// 是否显示汇总
        /// </summary>
        bool? ShowTotal { get; set; }

        /// <summary>
        /// Server-side aggregate function for this column (#431).
        /// When set to a value other than <see cref="GridAggregateTypeEnum.None"/>,
        /// <c>BasePagedListVM.ComputeAggregates()</c> will compute the result over the
        /// <em>full filtered query</em> (not just the current page) and include it in the
        /// JSON response under a key matching the column field name.
        /// Opt-in — default <c>None</c> leaves existing per-page ShowTotal behaviour intact.
        /// </summary>
        GridAggregateTypeEnum AggregateType { get; set; }

        /// <summary>
        /// Rich display type for this column (#432).
        /// Opt-in — default <c>Default</c> leaves existing behaviour intact.
        /// </summary>
        GridRichColumnTypeEnum RichColumnType { get; set; }

        /// <summary>
        /// Optional format string for <see cref="GridRichColumnTypeEnum.Currency"/> columns.
        /// Passed to the LayUI JS template as <c>Number.toLocaleString()</c> options
        /// or a printf-style pattern. Defaults to <c>null</c> (no extra formatting).
        /// Example: <c>"0,0.00"</c>
        /// </summary>
        string? CurrencyFormat { get; set; }

        /// <summary>
        /// Optional name of another column/property on the row that holds the
        /// ISO 4217 currency code for that row (e.g. "CurrencyCode").
        /// When set, the Currency template uses <c>Intl.NumberFormat</c> with
        /// <c>style:'currency'</c> keyed to the per-row code, rather than a fixed
        /// <c>CurrencyFormat</c> string.  Back-compat: if <c>null</c>, falls back
        /// to the existing <c>CurrencyFormat</c> single-currency behaviour.
        /// </summary>
        string? CurrencyCodeField { get; set; }

        /// <summary>
        /// Optional CSS color token for <see cref="GridRichColumnTypeEnum.Tag"/> columns.
        /// The value is placed in the layui <c>class</c> of the tag badge.
        /// Defaults to <c>null</c> (layui default colour).
        /// Example: <c>"green"</c>, <c>"red"</c>, <c>"blue"</c>
        /// </summary>
        string? TagColor { get; set; }

        /// <summary>
        /// Width/height in pixels for <see cref="GridRichColumnTypeEnum.Image"/> columns.
        /// Defaults to <c>null</c> (32 px in the template).
        /// </summary>
        int? ImageSize { get; set; }

        /// <summary>
        /// 子列
        /// </summary>
        IEnumerable<IGridColumn<T>>? Children { get; }

        /// <summary>
        /// 底层子列数量
        /// </summary>
        int ChildrenLength { get; }

        EditTypeEnum? EditType { get; set; }

        List<ComboSelectListItem>? ListItems { get; set; }

        DateTimeTypeEnum? DateType { get; set; }

        bool IsReadOnly { get; set; }

        #region 只读属性 生成 Excel 及其 表头用

        /// <summary>
        /// 最大子列数量
        /// </summary>
        int MaxChildrenCount { get; }

        /// <summary>
        /// 多表头的最大层数
        /// </summary>
        int MaxLevel { get; }

        /// <summary>
        /// 最下层列
        /// </summary>
        IEnumerable<IGridColumn<T>> BottomChildren { get; }

        /// <summary>
        /// 最大深度
        /// </summary>
        int MaxDepth { get; }

        #endregion


        #region 暂时没有用

        string? Id { get; set; }

        /// <summary>
        /// 是否需要分组
        /// </summary>
        bool NeedGroup { get; set; }

        bool IsLocked { get; set; }

        bool Sortable { get; set; }
        /// <summary>
        /// 是否允许多行
        /// </summary>
        bool AllowMultiLine { get; set; }
        /// <summary>
        /// 是否填充
        /// </summary>
        int? Flex { get; set; }

        Type? FieldType { get; }

        string? FieldName { get; }

        /// <summary>
        /// 获取内容
        /// </summary>
        /// <param name="source">源数据</param>
        /// <param name="needFormat">是否适用format</param>
        /// <returns>内容</returns>
        object GetText(object source, bool needFormat = true);

        object? GetObject(object source);
        /// <summary>
        /// 获取前景色
        /// </summary>
        /// <param name="source">源数据</param>
        /// <returns>前景色</returns>
        string GetForeGroundColor(object source);
        /// <summary>
        /// 获取背景色
        /// </summary>
        /// <param name="source">源数据</param>
        /// <returns>背景色</returns>
        string GetBackGroundColor(object source);
        bool HasFormat();

        /// <summary>
        /// When true, the output of the <see cref="Format"/> callback is treated as
        /// plain text and will be HTML-encoded before rendering in the grid cell.
        /// Set this when your <c>SetFormat</c> callback returns a display value derived
        /// from untrusted/user-supplied data to prevent stored XSS.
        /// Defaults to <c>false</c> (verbatim rendering, preserving existing behaviour).
        /// </summary>
        bool EncodeFormat { get; set; }
        #endregion
    }

}
