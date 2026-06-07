#nullable enable
using System;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Configures how a model property is rendered as a list (grid) column.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class ListColumnAttribute : Attribute
    {
        /// <summary>
        /// Column width in pixels.
        /// (default reproduces current generated/runtime behavior: 0 = auto)
        /// </summary>
        public int Width { get; set; } = 0;

        /// <summary>
        /// Horizontal alignment of column content.
        /// (default reproduces current generated/runtime behavior: Auto)
        /// </summary>
        public GridColumnAlignEnum Align { get; set; } = GridColumnAlignEnum.Auto;

        /// <summary>
        /// Whether the column is sortable.
        /// (default reproduces current generated/runtime behavior: true)
        /// </summary>
        public bool Sort { get; set; } = true;

        /// <summary>
        /// Whether the column is hidden by default.
        /// (default reproduces current generated/runtime behavior: false)
        /// </summary>
        public bool Hide { get; set; } = false;

        /// <summary>
        /// Whether to show a column total (footer aggregate).
        /// (default reproduces current generated/runtime behavior: false)
        /// </summary>
        public bool ShowTotal { get; set; } = false;

        /// <summary>
        /// Whether and where the column is fixed (pinned) horizontally.
        /// (default reproduces current generated/runtime behavior: None = not fixed)
        /// </summary>
        public GridColumnFixedEnum Fixed { get; set; } = GridColumnFixedEnum.None;
    }
}
