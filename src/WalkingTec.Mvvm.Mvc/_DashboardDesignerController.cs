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
    /// <summary>
    /// Designer-specific endpoints that back the no-code dashboard designer UI.
    ///
    /// The designer reuses all existing <see cref="_DashboardController"/> CRUD and
    /// data-source endpoints.  This controller adds only two designer-specific ones:
    ///   GET  /_dashboard-designer/vm-meta?vmType=…  — meta for a single Analysis VM
    ///   POST /_dashboard-designer/preview           — data preview for a widget draft
    ///
    /// Auth: same <see cref="AllRightsAttribute"/> as the runtime dashboard controller.
    /// Tenant scoping: same <see cref="GetTenantId()"/> pattern.
    /// </summary>
    [ApiController]
    [Route("/_dashboard-designer")]
    [AllRights]
    public class _DashboardDesignerController : BaseController
    {
        private readonly IDashboardService _dashboardService;
        private readonly DashboardOptions _options;
        private readonly AnalysisVmRegistry? _registry;
        private readonly ILogger<_DashboardDesignerController> _logger;

        public _DashboardDesignerController(
            IDashboardService dashboardService,
            IOptions<DashboardOptions> options,
            AnalysisVmRegistry? registry = null,
            ILogger<_DashboardDesignerController>? logger = null)
        {
            _dashboardService = dashboardService;
            _options = options.Value;
            _registry = registry;
            _logger = logger ?? NullLogger<_DashboardDesignerController>.Instance;
        }

        private string GetTenantId() => Wtm?.LoginUserInfo?.TenantCode ?? "";

        // ── GET /_dashboard-designer/vm-meta?vmType=… ──────────────────────────
        /// <summary>
        /// Returns the dimensions and measures for a single registered Analysis VM.
        /// Reuses <see cref="AnalysisFieldScanner"/> — the same scanner used by
        /// <see cref="_DashboardController.GetDataSources"/>.
        /// </summary>
        [HttpGet("vm-meta")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetVmMeta([FromQuery] string vmType)
        {
            if (string.IsNullOrWhiteSpace(vmType))
                return BadRequest("vmType is required.");

            if (_registry == null)
                return NotFound("Analysis VM registry is not configured.");

            Type resolvedType;
            try
            {
                resolvedType = _registry.Resolve(vmType);
            }
            catch (AnalysisVmNotFoundException)
            {
                return NotFound($"Analysis VM '{vmType}' is not registered.");
            }

            var modelType = GetModelType(resolvedType);
            var fields = AnalysisFieldScanner.ScanModel(modelType).ToList();

            return Ok(new
            {
                vmType,
                dimensions = fields
                    .Where(f => f.Kind == AnalysisFieldKind.Dimension)
                    .Select(f => new { field = f.FieldName, displayName = f.DisplayName, isDate = f.IsDate }),
                measures = fields
                    .Where(f => f.Kind == AnalysisFieldKind.Measure)
                    .Select(f => new { field = f.FieldName, displayName = f.DisplayName, allowedFuncs = GetAllowedFuncNames(f.AllowedFuncs) })
            });
        }

        // ── POST /_dashboard-designer/preview ─────────────────────────────────
        /// <summary>
        /// Preview a widget definition draft by fetching live data.
        ///
        /// The designer creates a transient <see cref="DashboardDefinition"/> containing
        /// only the single widget under preview, saves it temporarily to the service,
        /// calls <see cref="IDashboardService.GetWidgetDataAsync"/>, then deletes the
        /// transient definition.  This way we reuse the exact same data-fetch pipeline
        /// as the runtime and never duplicate query logic.
        ///
        /// The transient dashboard is owned by the current user and scoped to their
        /// tenant so tenant isolation is fully preserved.
        /// </summary>
        [HttpPost("preview")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> Preview([FromBody] WidgetDefinition widget, CancellationToken ct)
        {
            // #948: global editing kill switch — Preview persists a transient dashboard
            // (see CreateAsync below) and is part of the designer/authoring surface, so it
            // is gated exactly like _DashboardController.Create/Update/Delete.
            if (!_options.EnableEditing) return Forbid();

            if (widget == null)
                return BadRequest("Widget definition is required.");

            if (string.IsNullOrWhiteSpace(widget.Type))
                return BadRequest("Widget type is required.");

            // Validate widget type against allowlist (same logic as _DashboardController)
            var allowedTypes = _options.AllowedWidgetTypes;
            if (allowedTypes is { Length: > 0 })
            {
                if (!Array.Exists(allowedTypes, t => string.Equals(t, widget.Type, StringComparison.OrdinalIgnoreCase)))
                    return BadRequest($"Widget type '{widget.Type}' is not in the allowed list.");
            }

            // Validate filter ops
            if (widget.Source?.Filters != null)
            {
                foreach (var filter in widget.Source.Filters)
                {
                    if (!FilterConfig.AllowedOps.Contains(filter.Op))
                        return BadRequest($"Filter op '{filter.Op}' is not allowed.");
                }
            }

            var userId = Wtm?.LoginUserInfo?.ITCode ?? "";
            var tenantId = GetTenantId();
            const string previewWidgetId = "_preview";

            // Build a transient single-widget dashboard
            var transient = new DashboardDefinition
            {
                Title = "_designer_preview",
                Owner = userId,
                TenantId = tenantId,
                RefreshInterval = 0,
                Layout = [new LayoutItem { Id = previewWidgetId, X = 0, Y = 0, W = 12, H = 4 }],
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    [previewWidgetId] = widget
                }
            };

            string? transientId = null;
            try
            {
                transientId = await _dashboardService.CreateAsync(transient);
                var result = await _dashboardService.GetWidgetDataAsync(
                    transientId, previewWidgetId, null, tenantId, ct);
                return Ok(result);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "[Designer] Widget config validation failed in preview.");
                return BadRequest(ex.Message);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "[Designer] Widget data fetch failed in preview.");
                return StatusCode(StatusCodes.Status502BadGateway, "Widget data fetch failed.");
            }
            finally
            {
                // Always clean up the transient dashboard, even on error.
                if (transientId != null)
                {
                    try { await _dashboardService.DeleteAsync(transientId, tenantId); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "[Designer] Failed to delete transient preview dashboard {Id}.", transientId);
                    }
                }
            }
        }

        // ── Designer page action ──────────────────────────────────────────────
        // Note: the Designer view is served via _DashboardPageController.Designer().

        // ── Helpers ───────────────────────────────────────────────────────────
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
