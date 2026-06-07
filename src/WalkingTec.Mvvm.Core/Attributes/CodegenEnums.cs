#nullable enable

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Comparison operator used by <see cref="SearchFieldAttribute"/> when filtering list data.
    /// </summary>
    public enum SearchOperator
    {
        /// <summary>
        /// Infer the operator from the CLR type of the property
        /// (default reproduces current generated/runtime behavior).
        /// </summary>
        Auto = 0,
        /// <summary>
        /// String contains (LIKE %value%).
        /// </summary>
        Contains = 1,
        /// <summary>
        /// Exact equality match.
        /// </summary>
        Equal = 2,
        /// <summary>
        /// Inclusive range (requires two bound values; used for numeric and date fields).
        /// </summary>
        Between = 3,
    }

    /// <summary>
    /// Form control type used by <see cref="FormFieldAttribute"/> to render a property in add/edit forms.
    /// </summary>
    public enum FormControlType
    {
        /// <summary>
        /// Infer the control type from the CLR type of the property
        /// (default reproduces current generated/runtime behavior).
        /// </summary>
        Auto = 0,
        /// <summary>
        /// Single-line text input.
        /// </summary>
        Text = 1,
        /// <summary>
        /// Multi-line text area.
        /// </summary>
        TextArea = 2,
        /// <summary>
        /// Numeric input.
        /// </summary>
        Number = 3,
        /// <summary>
        /// Date picker (date only).
        /// </summary>
        Date = 4,
        /// <summary>
        /// Date-time picker.
        /// </summary>
        DateTime = 5,
        /// <summary>
        /// Boolean toggle / switch.
        /// </summary>
        Switch = 6,
        /// <summary>
        /// Dropdown combo-box with list items.
        /// </summary>
        ComboBox = 7,
        /// <summary>
        /// Radio button group.
        /// </summary>
        Radio = 8,
        /// <summary>
        /// Checkbox or checkbox group.
        /// </summary>
        CheckBox = 9,
        /// <summary>
        /// File upload control.
        /// </summary>
        Upload = 10,
    }
}
