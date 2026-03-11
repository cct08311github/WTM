using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
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

        public _DashboardController(IDashboardService dashboardService, IOptions<DashboardOptions> options)
        {
            _dashboardService = dashboardService;
            _options = options.Value;
        }

        private (string userId, string[] roles) GetUserInfo()
        {
            var userId = Wtm?.LoginUserInfo?.ITCode ?? "";
            var roles = Wtm?.LoginUserInfo?.Roles?.Select(r => r.RoleCode).ToArray() ?? Array.Empty<string>();
            return (userId, roles);
        }

        [HttpGet("list")]
        public async Task<IActionResult> List()
        {
            var (userId, roles) = GetUserInfo();
            var list = await _dashboardService.ListAsync(userId, roles);
            return Ok(list);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> Get(string id)
        {
            var dashboard = await _dashboardService.GetAsync(id);
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

            var id = await _dashboardService.CreateAsync(dashboard);
            return Ok(id);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(string id, [FromBody] DashboardDefinition dashboard)
        {
            if (dashboard == null || dashboard.Id != id) return BadRequest();

            var existing = await _dashboardService.GetAsync(id);
            if (existing == null) return NotFound();

            var (userId, roles) = GetUserInfo();
            if (!_dashboardService.CanEdit(existing, userId, roles))
            {
                return Forbid();
            }

            dashboard.Owner = existing.Owner; // preserve owner
            
            await _dashboardService.UpdateAsync(dashboard);
            return Ok();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            var existing = await _dashboardService.GetAsync(id);
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
            var dashboard = await _dashboardService.GetAsync(id);
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
            var dashboard = await _dashboardService.GetAsync(id);
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
            // Placeholder for MVP
            return Ok(new List<object>());
        }
    }
}