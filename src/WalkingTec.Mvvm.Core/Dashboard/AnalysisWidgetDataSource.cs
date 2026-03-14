#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Dashboard;

/// <summary>
/// Bridges Analysis Mode to the Dashboard widget data pipeline.
/// Translates widget source config (listVmType, dimensions, measures, filters)
/// into AnalysisQueryEngine calls and maps the result to WidgetDataResult.
/// </summary>
public class AnalysisWidgetDataSource : IWidgetDataSource
{
    private readonly AnalysisVmRegistry _registry;
    private readonly IServiceProvider _serviceProvider;
    private readonly AnalysisQueryEngine _engine;

    public string Name => "analysis";
    public WidgetDataSourceKind Kind => WidgetDataSourceKind.Analysis;

    public AnalysisWidgetDataSource(
        AnalysisVmRegistry registry,
        IServiceProvider serviceProvider,
        AnalysisQueryEngine engine)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public Task<WidgetDataResult> GetDataAsync(WidgetDataRequest request, CancellationToken ct = default)
    {
        if (!request.Parameters.TryGetValue("listVmType", out var listVmType) || string.IsNullOrWhiteSpace(listVmType))
            throw new InvalidOperationException("Widget data request missing required parameter 'listVmType'.");

        // 1. Resolve VM type from whitelist
        var vmType = _registry.Resolve(listVmType);

        // 2. Create VM instance and bind WTMContext via scoped service
        using var scope = _serviceProvider.CreateScope();
        var wtm = scope.ServiceProvider.GetService<WTMContext>();

        BaseVM vm;
        try
        {
            vm = (Activator.CreateInstance(vmType) as BaseVM)!;
        }
        catch (MissingMethodException)
        {
            throw new InvalidOperationException(
                $"VM type '{vmType.FullName}' must have a public parameterless constructor.");
        }

        if (vm is null)
            throw new InvalidOperationException(
                $"VM type '{vmType.FullName}' must inherit from BaseVM.");

        if (wtm != null) vm.Wtm = wtm;

        // 3. Get base query via reflection
        var baseQuery = InvokeGetSearchQuery(vm, vmType)
            ?? throw new InvalidOperationException(
                $"VM type '{vmType.FullName}' GetSearchQuery() returned null.");

        // 4. Get analysis field whitelist
        var fields = InvokeGetAnalysisFields(vm, vmType);

        // 5. Build AnalysisQueryRequest from widget parameters
        var analysisReq = BuildAnalysisRequest(request.Parameters);

        // 6. Execute via engine (handles TargetInvocationException unwrapping internally)
        var response = _engine.ExecuteDynamic(baseQuery, analysisReq, fields, cancellationToken: ct);

        // 7. Map to WidgetDataResult
        var result = new WidgetDataResult
        {
            Columns = response.Columns,
            Rows = response.Rows,
            Metadata = new Dictionary<string, object?>
            {
                ["totalCount"] = response.TotalCount,
                ["truncated"] = response.Truncated
            }
        };

        return Task.FromResult(result);
    }

    private static AnalysisQueryRequest BuildAnalysisRequest(Dictionary<string, string> parameters)
    {
        var req = new AnalysisQueryRequest();

        if (parameters.TryGetValue("listVmType", out var lvt))
            req.ListVmType = lvt;

        if (parameters.TryGetValue("dimensions", out var dims) && !string.IsNullOrWhiteSpace(dims))
            req.Dimensions = JsonSerializer.Deserialize<List<string>>(dims) ?? new List<string>();

        if (parameters.TryGetValue("measures", out var measures) && !string.IsNullOrWhiteSpace(measures))
            req.Measures = JsonSerializer.Deserialize<List<MeasureRequest>>(measures) ?? new List<MeasureRequest>();

        if (parameters.TryGetValue("filters", out var filters) && !string.IsNullOrWhiteSpace(filters))
            req.Filters = JsonSerializer.Deserialize<List<FilterCondition>>(filters) ?? new List<FilterCondition>();

        return req;
    }

    private static IQueryable InvokeGetSearchQuery(BaseVM vm, Type vmType)
    {
        var method = vmType.GetMethod(
            "GetSearchQuery",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            Type.EmptyTypes,
            null);
        if (method == null)
            throw new InvalidOperationException(
                $"VM type '{vmType.FullName}' does not have a GetSearchQuery() method.");
        try
        {
            var result = method.Invoke(vm, null);
            if (result is IQueryable q) return q;
            throw new InvalidOperationException(
                $"VM type '{vmType.FullName}' GetSearchQuery() did not return IQueryable.");
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private static IEnumerable<AnalysisFieldMeta> InvokeGetAnalysisFields(BaseVM vm, Type vmType)
    {
        var method = vmType.GetMethod(
            "GetAnalysisFields",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            Type.EmptyTypes,
            null);
        if (method == null)
            throw new InvalidOperationException(
                $"VM type '{vmType.FullName}' does not have a GetAnalysisFields() method.");
        try
        {
            var result = method.Invoke(vm, null) as IEnumerable<AnalysisFieldMeta>;
            if (result is null)
                throw new InvalidOperationException(
                    $"VM type '{vmType.FullName}' GetAnalysisFields() returned null.");
            return result;
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }
}
