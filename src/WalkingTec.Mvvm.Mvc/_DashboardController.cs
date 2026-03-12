using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
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

        public _DashboardController(
            IDashboardService dashboardService,
            IOptions<DashboardOptions> options,
            IEnumerable<IWidgetDataSource> dataSources,
            AnalysisVmRegistry registry = null)
        {
            _dashboardService = dashboardService;
            _options = options.Value;
            _dataSources = dataSources;
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
        public async Task<IActionResult> List()
        {
            var (userId, roles) = GetUserInfo();
            var tenantId = GetTenantId();
            var list = await _dashboardService.ListAsync(userId, roles, tenantId);
            return Ok(list);
        }

        [HttpGet("{id}")]
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
        public async Task<IActionResult> Create([FromBody] DashboardDefinition dashboard)
        {
            if (dashboard == null) return BadRequest();

            var (userId, _) = GetUserInfo();
            dashboard.Owner = userId;
            dashboard.TenantId = GetTenantId();

            var id = await _dashboardService.CreateAsync(dashboard);
            return Ok(id);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(string id, [FromBody] DashboardDefinition dashboard)
        {
            if (dashboard == null || dashboard.Id != id) return BadRequest();

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

            await _dashboardService.UpdateAsync(dashboard);
            return Ok();
        }

        [HttpDelete("{id}")]
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

            await _dashboardService.DeleteAsync(id);
            return Ok();
        }

        [HttpGet("{id}/widget/{wid}/data")]
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
                var result = await _dashboardService.GetWidgetDataAsync(id, wid, parameters, ct);
                return Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
        }

        [HttpPost("{id}/widget/{wid}/data")]
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
                var result = await _dashboardService.GetWidgetDataAsync(id, wid, filters, ct);
                return Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
        }

        [HttpGet("datasources")]
        public IActionResult GetDataSources()
        {
            var result = new List<object>();

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