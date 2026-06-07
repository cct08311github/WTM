#nullable enable
using System;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Configures how a model property is rendered as a search panel field.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class SearchFieldAttribute : Attribute
    {
        /// <summary>
        /// The comparison operator used when filtering by this field.
        /// (default reproduces current generated/runtime behavior: Auto = infer from CLR type)
        /// </summary>
        public SearchOperator Operator { get; set; } = SearchOperator.Auto;

        /// <summary>
        /// Whether this field is shown in the search panel.
        /// (default reproduces current generated/runtime behavior: true)
        /// </summary>
        public bool ShowInPanel { get; set; } = true;

        /// <summary>
        /// Whether date/datetime fields use a range picker (from–to).
        /// (default reproduces current generated/runtime behavior: true)
        /// </summary>
        public bool DateRange { get; set; } = true;

        /// <summary>
        /// Display order within the search panel (lower values appear first).
        /// (default reproduces current generated/runtime behavior: int.MaxValue = natural order)
        /// </summary>
        public int Order { get; set; } = int.MaxValue;
    }
}
