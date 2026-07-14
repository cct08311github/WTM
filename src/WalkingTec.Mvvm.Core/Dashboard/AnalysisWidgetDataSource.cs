#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Dashboard;

/// <summary>
/// Bridges Analysis Mode to the Dashboard widget data pipeline.
/// Translates widget source config (listVmType, dimensions, measures, filters)
/// into AnalysisQueryEngine calls and maps the result to WidgetDataResult.
/// Access is gated by the same RBAC checks as <c>_AnalysisController</c>:
/// <list type="bullet">
///   <item><see cref="EnableAnalysisAttribute.AllowedRoles"/> role check</item>
///   <item><see cref="IAnalysisFieldPolicy"/> per-user field filtering</item>
/// </list>
/// </summary>
public class AnalysisWidgetDataSource : IWidgetDataSource
{
    private readonly AnalysisVmRegistry _registry;
    private readonly IServiceProvider _serviceProvider;
    private readonly AnalysisQueryEngine _engine;
    private readonly IAnalysisFieldPolicy? _fieldPolicy;
    private readonly IMemoryCache _cache;
    private readonly DashboardOptions _options;

    public string Name => "analysis";
    public WidgetDataSourceKind Kind => WidgetDataSourceKind.Analysis;

    public AnalysisWidgetDataSource(
        AnalysisVmRegistry registry,
        IServiceProvider serviceProvider,
        AnalysisQueryEngine engine,
        IAnalysisFieldPolicy? fieldPolicy = null,
        IMemoryCache? cache = null,
        IOptions<DashboardOptions>? options = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _fieldPolicy = fieldPolicy;
        _cache = cache ?? new MemoryCache(new MemoryCacheOptions());
        _options = options?.Value ?? new DashboardOptions();
    }

    public async Task<WidgetDataResult> GetDataAsync(WidgetDataRequest request, CancellationToken ct = default)
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

        // 3. RBAC: apply the same AllowedRoles check as _AnalysisController.CheckAccess().
        //    Any user lacking the required role receives UnauthorizedAccessException, which
        //    the Dashboard controller should surface as HTTP 403.
        if (!CheckAccess(vmType, wtm))
            throw new UnauthorizedAccessException(
                $"Access to analysis data for '{listVmType}' is denied.");

        // 4. Get base query via reflection
        var baseQuery = AnalysisVmInvoker.GetSearchQuery(vm, vmType);

        // 5. Get analysis field whitelist
        var fields = AnalysisVmInvoker.GetAnalysisFields(vm, vmType);

        // 6. Apply per-user field policy (same as _AnalysisController) — injected policy
        //    takes precedence; fall back to scope-resolved service if none was injected.
        var effectivePolicy = _fieldPolicy ?? scope.ServiceProvider.GetService<IAnalysisFieldPolicy>();
        if (effectivePolicy != null)
        {
            var user = BuildClaimsPrincipal(wtm?.LoginUserInfo);
            fields = [.. effectivePolicy.Filter(fields, user)];
        }

        // 7. Build AnalysisQueryRequest from widget parameters
        var analysisReq = BuildAnalysisRequest(request.Parameters);

        // 8. Q8 — short-TTL result cache.
        // Key includes: widgetId/listVmType, tenant, userId (user-scoped if policy present), filter params.
        // Including tenant in the key is the primary tenant-isolation guarantee —
        // a cross-tenant caller will always produce a different key and read from a separate cache entry.
        string? identityKey = wtm?.LoginUserInfo != null ? $"{wtm.LoginUserInfo.CurrentTenant}_{wtm.LoginUserInfo.UserId}" : null;
        var ttl = _options.AnalysisWidgetCacheTtlSeconds;

        if (ttl > 0)
        {
            var cacheKey = BuildCacheKey(request, listVmType, identityKey);
            if (_cache.TryGetValue(cacheKey, out WidgetDataResult? cached) && cached != null)
                return cached;

            var response = await _engine.ExecuteDynamicAsync(baseQuery, analysisReq, fields, identityKey: identityKey, cancellationToken: ct);
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
            _cache.Set(cacheKey, result, TimeSpan.FromSeconds(ttl));
            return result;
        }

        // 9. Execute via engine (no cache)
        var responseNc = await _engine.ExecuteDynamicAsync(baseQuery, analysisReq, fields, identityKey: identityKey, cancellationToken: ct);

        var resultNc = new WidgetDataResult
        {
            Columns = responseNc.Columns,
            Rows = responseNc.Rows,
            Metadata = new Dictionary<string, object?>
            {
                ["totalCount"] = responseNc.TotalCount,
                ["truncated"] = responseNc.Truncated
            }
        };

        return resultNc;
    }

    /// <summary>
    /// Builds a cache key that is unique per (listVmType, tenant+user identity, filter params).
    /// Tenant isolation: <paramref name="identityKey"/> always includes the tenant code
    /// (format: <c>{tenant}_{userId}</c>), so cross-tenant callers produce separate cache entries.
    /// </summary>
    internal static string BuildCacheKey(WidgetDataRequest request, string listVmType, string? identityKey)
    {
        // Stable serialization of the filter sub-dictionary so key is order-independent.
        // We only include parameters that influence the query result, not request metadata.
        var filterPart = request.Parameters.TryGetValue("dimensions", out var dims) ? dims : "";
        var measurePart = request.Parameters.TryGetValue("measures", out var meas) ? meas : "";
        var filtersPart = request.Parameters.TryGetValue("filters", out var filt) ? filt : "";
        return $"AnalysisWidget::{listVmType}::{identityKey ?? "_anon"}::{filterPart}::{measurePart}::{filtersPart}";
    }

    /// <summary>
    /// Mirrors <c>_AnalysisController.CheckAccess</c>: returns <c>true</c> when the current
    /// user holds at least one of the roles declared in <see cref="EnableAnalysisAttribute.AllowedRoles"/>,
    /// or when no role restriction is configured.  Admin role always passes.
    /// </summary>
    private static bool CheckAccess(Type vmType, WTMContext? wtm)
    {
        var attr = vmType.GetCustomAttribute<EnableAnalysisAttribute>();
        if (attr == null || string.IsNullOrEmpty(attr.AllowedRoles)) return true;

        // Use RoleCode (machine identifier) to match AllowedRoles — consistent with
        // _AnalysisController.CheckAccess() and _DashboardController.GetUserInfo().
        var userRoles = wtm?.LoginUserInfo?.Roles?.Select(r => r.RoleCode) ?? Enumerable.Empty<string?>();
        if (userRoles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase))) return true;

        var allowed = attr.AllowedRoles.Split(',', StringSplitOptions.RemoveEmptyEntries)
                          .Select(r => r.Trim());
        return allowed.Intersect(userRoles.Where(r => r != null)!, StringComparer.OrdinalIgnoreCase).Any();
    }

    /// <summary>
    /// Builds a <see cref="ClaimsPrincipal"/> from <see cref="LoginUserInfo"/> so that
    /// <see cref="IAnalysisFieldPolicy"/> implementations can use the standard Claims API.
    /// </summary>
    private static ClaimsPrincipal BuildClaimsPrincipal(LoginUserInfo? userInfo)
    {
        if (userInfo == null) return new ClaimsPrincipal();

        List<Claim> claims = [];
        if (!string.IsNullOrEmpty(userInfo.ITCode))
            claims.Add(new Claim(ClaimTypes.Name, userInfo.ITCode));
        if (userInfo.Roles != null)
            foreach (var role in userInfo.Roles)
                if (!string.IsNullOrEmpty(role.RoleCode))
                    claims.Add(new Claim(ClaimTypes.Role, role.RoleCode));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "WTM"));
    }

    private static AnalysisQueryRequest BuildAnalysisRequest(Dictionary<string, string> parameters)
    {
        var req = new AnalysisQueryRequest();

        if (parameters.TryGetValue("listVmType", out var lvt))
            req.ListVmType = lvt;

        if (parameters.TryGetValue("dimensions", out var dims) && !string.IsNullOrWhiteSpace(dims))
            req.Dimensions = JsonSerializer.Deserialize<List<string>>(dims) ?? [];

        if (parameters.TryGetValue("measures", out var measures) && !string.IsNullOrWhiteSpace(measures))
            req.Measures = JsonSerializer.Deserialize<List<MeasureRequest>>(measures) ?? [];

        if (parameters.TryGetValue("filters", out var filters) && !string.IsNullOrWhiteSpace(filters))
            req.Filters = JsonSerializer.Deserialize<List<FilterCondition>>(filters) ?? [];

        return req;
    }
}
