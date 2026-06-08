using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Dashboard;
public class WidgetDataResult
{
    public object? Value { get; set; }
    public object? PreviousValue { get; set; }
    public List<Dictionary<string, object?>>? Rows { get; set; }
    public List<string>? Columns { get; set; }
    public Dictionary<string, object?>? Metadata { get; set; }

    /// <summary>
    /// When non-null, the widget fetch encountered an error (e.g. timeout).
    /// The dashboard can render an inline error state for this widget without
    /// affecting other widgets on the same dashboard.
    /// </summary>
    public string? Error { get; set; }
}