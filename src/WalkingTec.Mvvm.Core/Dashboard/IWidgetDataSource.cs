using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Dashboard;
public interface IWidgetDataSource
{
    string Name { get; }
    WidgetDataSourceKind Kind { get; }
    Task<WidgetDataResult> GetDataAsync(WidgetDataRequest request, CancellationToken ct = default);
}