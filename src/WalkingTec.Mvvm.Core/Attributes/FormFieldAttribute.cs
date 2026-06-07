#nullable enable
using System;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Configures how a model property is rendered in an add/edit form.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class FormFieldAttribute : Attribute
    {
        /// <summary>
        /// The form control type used to render this field.
        /// (default reproduces current generated/runtime behavior: Auto = infer from CLR type)
        /// </summary>
        public FormControlType ControlType { get; set; } = FormControlType.Auto;

        /// <summary>
        /// Number of grid columns the control spans in the form layout.
        /// (default reproduces current generated/runtime behavior: 1)
        /// </summary>
        public int Colspan { get; set; } = 1;

        /// <summary>
        /// Optional group/section label this field belongs to.
        /// (default reproduces current generated/runtime behavior: null = no grouping)
        /// </summary>
        public string? Group { get; set; } = null;

        /// <summary>
        /// Display order within the form or group (lower values appear first).
        /// (default reproduces current generated/runtime behavior: int.MaxValue = natural order)
        /// </summary>
        public int Order { get; set; } = int.MaxValue;

        /// <summary>
        /// Placeholder text shown inside the control when empty.
        /// (default reproduces current generated/runtime behavior: null = no placeholder)
        /// </summary>
        public string? Placeholder { get; set; } = null;

        /// <summary>
        /// Whether the control is read-only when editing an existing record
        /// (but editable when creating a new one).
        /// (default reproduces current generated/runtime behavior: false)
        /// </summary>
        public bool ReadonlyOnEdit { get; set; } = false;
    }
}
