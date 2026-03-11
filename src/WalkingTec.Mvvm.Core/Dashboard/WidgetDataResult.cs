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
}