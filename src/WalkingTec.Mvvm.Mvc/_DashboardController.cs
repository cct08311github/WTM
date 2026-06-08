using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Dashboard;

namespace WalkingTec.Mvvm.Mvc
{
    [ApiController]
    [Route("/_dashboard")]
    [AllRights]
    public class _DashboardController : BaseController
    {
        private readonly IDashboardService _dashboardService;
        private readonly DashboardOptions _options;
        private readonly IEnumerable<IWidgetDataSource> _dataSources;
        private readonly AnalysisVmRegistry _registry;
        private readonly ILogger<_DashboardController> _logger;

        public _DashboardController(
            IDashboardService dashboardService,
            IOptions<DashboardOptions> options,
            IEnumerable<IWidgetDataSource> dataSources,
            AnalysisVmRegistry registry = null,
            ILogger<_DashboardController>? logger = null)
        {
            _dashboardService = dashboardService;
            _options = options.Value;
            _dataSources = dataSources;
            _logger = logger ?? NullLogger<_DashboardController>.Instance;
            _registry = registry;
        }

        private (string userId, string[] roles) GetUserInfo()
        {
            var userId = Wtm?.LoginUserInfo?.ITCode ?? "";
            var roles = Wtm?.LoginUserInfo?.Roles?.Select(r => r.RoleCode).ToArray() ?? Array.Empty<string>();
            return (userId, roles);
        }

        private string GetTenantId()
        {
            return Wtm?.LoginUserInfo?.TenantCode ?? "";
        }

        [HttpGet("list")]
        [ProducesResponseType(typeof(IReadOnlyList<DashboardSummary>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> List()
        {
            var (userId, roles) = GetUserInfo();
            var tenantId = GetTenantId();
            var list = await _dashboardService.ListAsync(userId, roles, tenantId);
            return Ok(list);
        }

        [HttpGet("{id}")]
        [ProducesResponseType(typeof(DashboardDefinition), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Get(string id)
        {
            var tenantId = GetTenantId();
            var dashboard = await _dashboardService.GetAsync(id, tenantId);
            if (dashboard == null) return NotFound();

            var (userId, roles) = GetUserInfo();
            if (!_dashboardService.CanAccess(dashboard, userId, roles))
            {
                return Forbid();
            }

            return Ok(dashboard);
        }

        [HttpPost("")]
        [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<IActionResult> Create([FromBody] DashboardDefinition dashboard)
        {
            if (dashboard == null) return BadRequest();

            var widgetTypeError = ValidateWidgetTypes(dashboard, _options);
            if (widgetTypeError != null) return BadRequest(widgetTypeError);

            var (userId, _) = GetUserInfo();
            dashboard.Owner = userId;
            dashboard.TenantId = GetTenantId();

            try
            {
                var id = await _dashboardService.CreateAsync(dashboard);
                return Ok(id);
            }
            catch (ArgumentException ex)
            {
                // Q7: service-layer widget config validation rejected the payload.
                _logger.LogWarning(ex, "[Dashboard] Widget config validation failed on Create.");
                return BadRequest(ex.Message);
            }
        }

        [HttpPut("{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Update(string id, [FromBody] DashboardDefinition dashboard)
        {
            if (dashboard == null || dashboard.Id != id) return BadRequest();

            var widgetTypeError = ValidateWidgetTypes(dashboard, _options);
            if (widgetTypeError != null) return BadRequest(widgetTypeError);

            var tenantId = GetTenantId();
            var existing = await _dashboardService.GetAsync(id, tenantId);
            if (existing == null) return NotFound();

            var (userId, roles) = GetUserInfo();
            if (!_dashboardService.CanEdit(existing, userId, roles))
            {
                return Forbid();
            }

            dashboard.Owner = existing.Owner; // preserve owner
            dashboard.TenantId = tenantId;    // server-side tenant, prevent spoofing

            try
            {
                await _dashboardService.UpdateAsync(dashboard);
            }
            catch (ArgumentException ex)
            {
                // Q7: service-layer widget config validation rejected the payload.
                _logger.LogWarning(ex, "[Dashboard] Widget config validation failed on Update.");
                return BadRequest(ex.Message);
            }
            return Ok();
        }

        [HttpDelete("{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Delete(string id)
        {
            var tenantId = GetTenantId();
            var existing = await _dashboardService.GetAsync(id, tenantId);
            if (existing == null) return NotFound();

            var (userId, roles) = GetUserInfo();
            if (!_dashboardService.CanEdit(existing, userId, roles))
            {
                return Forbid();
            }

            // BUG-FIX (Finding 3): pass authenticated tenantId so the service layer scopes the
            // DELETE to the caller's tenant bucket (EF WHERE clause / file-system directory).
            await _dashboardService.DeleteAsync(id, tenantId);
            return Ok();
        }

        [HttpGet("{id}/widget/{wid}/data")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetWidgetData(string id, string wid, CancellationToken ct)
        {
            var tenantId = GetTenantId();
            var dashboard = await _dashboardService.GetAsync(id, tenantId);
            if (dashboard == null) return NotFound();

            var (userId, roles) = GetUserInfo();
            if (!_dashboardService.CanAccess(dashboard, userId, roles))
            {
                return Forbid();
            }

            if (dashboard.Widgets == null || !dashboard.Widgets.ContainsKey(wid))
            {
                return NotFound();
            }

            var parameters = Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString());

            try
            {
                // Pass authenticated tenantId so GetWidgetDataAsync resolves the correct
                // per-tenant dashboard directory (issue #137).
                var result = await _dashboardService.GetWidgetDataAsync(id, wid, parameters, tenantId, ct);
                return Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (InvalidOperationException ex)
            {
                // Log full detail server-side (includes target URL, status, etc.) but return a
                // generic message to the client to avoid leaking internal topology (issue #101).
                _logger.LogWarning(ex,
                    "[Dashboard] Widget data fetch failed. DashboardId={DashboardId} WidgetId={WidgetId}",
                    id, wid);
                return StatusCode(StatusCodes.Status502BadGateway, "Widget data fetch failed.");
            }
        }

        [HttpPost("{id}/widget/{wid}/data")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> PostWidgetData(string id, string wid, [FromBody] Dictionary<string, string> filters, CancellationToken ct)
        {
            var tenantId = GetTenantId();
            var dashboard = await _dashboardService.GetAsync(id, tenantId);
            if (dashboard == null) return NotFound();

            var (userId, roles) = GetUserInfo();
            if (!_dashboardService.CanAccess(dashboard, userId, roles))
            {
                return Forbid();
            }

            if (dashboard.Widgets == null || !dashboard.Widgets.ContainsKey(wid))
            {
                return NotFound();
            }

            try
            {
                // Pass authenticated tenantId so GetWidgetDataAsync resolves the correct
                // per-tenant dashboard directory (issue #137).
                var result = await _dashboardService.GetWidgetDataAsync(id, wid, filters, tenantId, ct);
                return Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (InvalidOperationException ex)
            {
                // Log full detail server-side but return a generic message to the client (issue #101).
                _logger.LogWarning(ex,
                    "[Dashboard] Widget data fetch failed. DashboardId={DashboardId} WidgetId={WidgetId}",
                    id, wid);
                return StatusCode(StatusCodes.Status502BadGateway, "Widget data fetch failed.");
            }
        }

        [HttpGet("datasources")]
        [ProducesResponseType(typeof(IEnumerable<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public IActionResult GetDataSources()
        {
            List<object> result = [];

            // Custom data sources from DI
            foreach (var ds in _dataSources)
            {
                result.Add(new { name = ds.Name, kind = ds.Kind.ToString().ToLowerInvariant() });
            }

            // Analysis Mode registered ListVMs
            if (_registry != null)
            {
                foreach (var kvp in _registry.GetRegisteredTypes())
                {
                    var fields = AnalysisFieldScanner.ScanModel(
                        GetModelType(kvp.Value)).ToList();

                    result.Add(new
                    {
                        name = kvp.Key,
                        kind = "analysis",
                        dimensions = fields
                            .Where(f => f.Kind == AnalysisFieldKind.Dimension)
                            .Select(f => new { field = f.FieldName, displayName = f.DisplayName, isDate = f.IsDate })
                            .ToList(),
                        measures = fields
                            .Where(f => f.Kind == AnalysisFieldKind.Measure)
                            .Select(f => new { field = f.FieldName, displayName = f.DisplayName, allowedFuncs = GetAllowedFuncNames(f.AllowedFuncs) })
                            .ToList()
                    });
                }
            }

            return Ok(result);
        }

        /// <summary>
        /// Controller-layer widget validation (fast-fail before hitting the service).
        /// Checks empty Type, title length, filter Op allowlist, and optionally the
        /// AllowedWidgetTypes allowlist from <paramref name="options"/>.
        /// </summary>
        private static string? ValidateWidgetTypes(DashboardDefinition dashboard, DashboardOptions options)
        {
            if (dashboard.Widgets == null) return null;
            var allowedTypes = options.AllowedWidgetTypes;
            foreach (var (wid, def) in dashboard.Widgets)
            {
                if (string.IsNullOrWhiteSpace(def.Type))
                    return $"Widget '{wid}' 缺少必填的 Type 屬性。";

                // Q7: chartType allowlist — when AllowedWidgetTypes is configured, unknown types are rejected.
                if (allowedTypes is { Length: > 0 })
                {
                    if (!Array.Exists(allowedTypes, t => string.Equals(t, def.Type, StringComparison.OrdinalIgnoreCase)))
                        return $"Widget '{wid}': chartType '{def.Type}' 不在允許清單中。" +
                               $"允許的類型：{string.Join(", ", allowedTypes)}.";
                }

                // S2: Widget title length cap.
                if (!string.IsNullOrEmpty(def.Title) && def.Title.Length > 200)
                    return $"Widget '{wid}' Title 超過 200 個字元上限。";

                // S4: Filter Op allowlist — prevents unknown operator strings from
                // reaching the expression-tree builder.
                if (def.Source?.Filters != null)
                {
                    foreach (var filter in def.Source.Filters)
                    {
                        if (!FilterConfig.AllowedOps.Contains(filter.Op))
                            return $"Widget '{wid}' 的篩選條件包含不支援的運算子 '{filter.Op}'。" +
                                   $"允許的運算子：{string.Join(", ", FilterConfig.AllowedOps)}.";
                    }
                }
            }
            return null;
        }

        private static Type GetModelType(Type vmType)
        {
            var t = vmType.BaseType;
            while (t != null)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(BasePagedListVM<,>))
                    return t.GetGenericArguments()[0];
                t = t.BaseType;
            }
            return typeof(object);
        }

        private static string[] GetAllowedFuncNames(AggregateFunc funcs)
            => Enum.GetValues<AggregateFunc>()
                   .Where(f => funcs.HasFlag(f))
                   .Select(f => f.ToString())
                   .ToArray();
    }
}