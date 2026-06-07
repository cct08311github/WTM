#nullable enable
using System;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Configures how a model property is handled during Excel import.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class ImportConfigAttribute : Attribute
    {
        /// <summary>
        /// The expected data type of the import column.
        /// (default reproduces current generated/runtime behavior: Dynamic = infer at runtime)
        /// </summary>
        public ColumnDataType DataType { get; set; } = ColumnDataType.Dynamic;

        /// <summary>
        /// Whether this column is required during import (import fails if the cell is empty).
        /// (default reproduces current generated/runtime behavior: false)
        /// </summary>
        public bool RequiredOnImport { get; set; } = false;

        /// <summary>
        /// Override for the Excel column header text matched during import.
        /// (default reproduces current generated/runtime behavior: null = use display name / property name)
        /// </summary>
        public string? ColumnHeader { get; set; } = null;

        /// <summary>
        /// Expected date/datetime parse format string (e.g. "yyyy-MM-dd").
        /// (default reproduces current generated/runtime behavior: null = use default culture parsing)
        /// </summary>
        public string? DateFormat { get; set; } = null;
    }
}
