#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// 分析模式 API，提供 /meta、/query、/export 三個端點。
    /// 所有存取均須通過 AnalysisVmRegistry 白名單驗證。
    /// </summary>
    [AllRights]
    [ActionDescription("Analysis")]
    [Route("/_analysis")]
    [ApiController]
    public class _AnalysisController : BaseController
    {
        private static readonly JsonSerializerOptions _camelCase = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            // NullableEnumStringConverterFactory: HTML form values are always strings.
            // When a Nullable<TEnum> Searcher field is populated, the form sends "1" (numeric
            // string) or "Card" (enum name). STJ cannot convert these without a custom converter.
            // Fixes #471.
            Converters = { new NullableEnumStringConverterFactory(), new DateTimeConverter() }
        };

        private readonly AnalysisVmRegistry _registry;
        private readonly AnalysisQueryEngine _engine;
        private readonly IAnalysisFieldPolicy? _fieldPolicy;
        private readonly ILogger<_AnalysisController> _logger;
        private readonly ILogger<ActionLog>? _actionLogger;
        private readonly TimeSpan? _cacheTtl;

        public _AnalysisController(
            AnalysisVmRegistry registry,
            AnalysisQueryEngine engine,
            ILogger<_AnalysisController> logger,
            IAnalysisCache? cache = null,
            IAnalysisFieldPolicy? fieldPolicy = null,
            IOptions<Configs>? configs = null,
            ILogger<ActionLog>? actionLogger = null)
        {
            _registry = registry;
            _engine = engine;
            _logger = logger;
            _fieldPolicy = fieldPolicy;
            _cacheTtl = configs?.Value.AnalysisCacheTtl;
            _actionLogger = actionLogger;
        }

        /// <summary>
        /// GET /_analysis/meta?listVmType=Foo.BarListVM
        /// 回傳指定 ListVM 上標記 [Dimension]/[Measure] 的欄位清單。
        /// </summary>
        [HttpGet("meta")]
        [ProducesResponseType(typeof(IEnumerable<object>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public IActionResult GetMeta([FromQuery] string listVmType)
        {
            Type vmType;
            try { vmType = _registry.Resolve(listVmType); }
            catch (AnalysisVmNotFoundException ex) { return NotFound(ex.Message); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            if (!CheckAccess(vmType)) return Forbid();

            var vm = CreateAnalysisVm(vmType);
            var fields = InvokeGetAnalysisFields(vm, vmType);
            if (_fieldPolicy != null)
            {
                fields = _fieldPolicy.Filter(fields, HttpContext?.User ?? new System.Security.Claims.ClaimsPrincipal()).ToList();
            }

            return Ok(fields.Select(f => new
            {
                fieldName = f.FieldName,
                displayName = f.DisplayName,
                kind = f.Kind.ToString(),
                allowedFuncs = f.Kind == AnalysisFieldKind.Measure
                    ? GetAllowedFuncNames(f.AllowedFuncs)
                    : new List<string>(),
                isDate = f.IsDate,
                hierarchy = f.Hierarchy.ToString(),
                allowedValues = f.AllowedValues
            }));
        }

        /// <summary>
        /// POST /_analysis/query
        /// 執行分析查詢。
        /// </summary>
        [HttpPost("query")]
        [ProducesResponseType(typeof(AnalysisQueryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> Query([FromBody] AnalysisQueryRequest? req)
        {
            if (req == null) return BadRequest("Request body is required.");
            if (req.Dimensions.Count == 0) return BadRequest("至少需要選取 1 個維度。");
            if (req.Dimensions.Count > 3) return BadRequest("最多選取 3 個維度。");
            if (req.Measures.Count == 0)  return BadRequest("至少需要選取 1 個度量指標。");
            if (req.Measures.Count > 3)   return BadRequest("最多選取 3 個度量。");

            var errorResult = TryPrepareContext(req, out var ctx);
            if (errorResult != null) return errorResult;

            var sw = Stopwatch.StartNew();
            try
            {
var result = await _engine.ExecuteDynamicAsync(ctx!.BaseQuery, req, ctx.Fields, identityKey: ctx.IdentityKey, cancellationToken: HttpContext?.RequestAborted ?? default);
                sw.Stop();
                _logger.LogInformation("Analysis query completed ListVm={ListVmType} Dims={DimCount} Msrs={MsrCount} ElapsedMs={Elapsed} Truncated={Truncated}",
                    req.ListVmType, req.Dimensions.Count, req.Measures.Count, sw.ElapsedMilliseconds, result.Truncated);
                WriteAnalysisActionLog("Query", req.ListVmType, req.Dimensions, req.Measures, result.TotalCount, sw.ElapsedMilliseconds / 1000.0, result.Truncated);
                return new JsonResult(result, _camelCase);
            }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        }

        [HttpPost("pivot")]
        [ProducesResponseType(typeof(AnalysisPivotResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> Pivot([FromBody] AnalysisPivotRequest? req)
        {
            if (req == null) return BadRequest("Request body is required.");
            if (req.Dimensions.Count == 0) return BadRequest("至少需要選取 1 個維度。");
            if (req.Dimensions.Count > 3) return BadRequest("最多選取 3 個維度。");
            if (req.Measures.Count == 0)  return BadRequest("至少需要選取 1 個度量指標。");
            if (req.Measures.Count > 3)   return BadRequest("最多選取 3 個度量。");
            if (string.IsNullOrEmpty(req.PivotDimension)) return BadRequest("必須指定 PivotDimension。");

            var errorResult = TryPrepareContext(req, out var ctx);
            if (errorResult != null) return errorResult;

            var sw = Stopwatch.StartNew();
            try
            {
var result = await _engine.ExecutePivotDynamicAsync(ctx!.BaseQuery, req, ctx.Fields, identityKey: ctx.IdentityKey, cancellationToken: HttpContext?.RequestAborted ?? default);
                sw.Stop();
                _logger.LogInformation("Analysis pivot completed ListVm={ListVmType} Dims={DimCount} Msrs={MsrCount} Pivot={PivotDim} ElapsedMs={Elapsed}",
                    req.ListVmType, req.Dimensions.Count, req.Measures.Count, req.PivotDimension, sw.ElapsedMilliseconds);
                WriteAnalysisActionLog("Pivot", req.ListVmType, req.Dimensions, req.Measures, result.Rows.Count, sw.ElapsedMilliseconds / 1000.0, result.Truncated);
                return new JsonResult(result, _camelCase);
            }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        }

        /// <summary>
        /// POST /_analysis/export?format=xlsx|csv
        /// 匯出分析結果為 Excel 或 CSV。
        /// </summary>
        [HttpPost("export")]
        [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> Export([FromBody] AnalysisQueryRequest? req,
            [FromQuery] string format = "xlsx",
            [FromQuery] bool includeChart = false,
            [FromQuery] string chartType = "bar",
            [FromQuery] bool includeMetadata = false)
        {
            if (req == null) return BadRequest("Request body is required.");
            if (req.Dimensions.Count == 0) return BadRequest("至少需要選取 1 個維度。");
            if (req.Dimensions.Count > 3) return BadRequest("最多選取 3 個維度。");
            if (req.Measures.Count == 0)  return BadRequest("至少需要選取 1 個度量指標。");
            if (req.Measures.Count > 3)   return BadRequest("最多選取 3 個度量。");

            var errorResult = TryPrepareContext(req, out var ctx);
            if (errorResult != null) return errorResult;

            var sw = Stopwatch.StartNew();
            AnalysisQueryResponse result;
            try
            {
result = await _engine.ExecuteDynamicAsync(ctx!.BaseQuery, req, ctx.Fields, identityKey: ctx.IdentityKey, cancellationToken: HttpContext?.RequestAborted ?? default);
                sw.Stop();
                _logger.LogInformation("Analysis export completed ListVm={ListVmType} Format={Format} ElapsedMs={Elapsed}",
                    req.ListVmType, format, sw.ElapsedMilliseconds);
                WriteAnalysisActionLog($"Export({format})", req.ListVmType, req.Dimensions, req.Measures, result.TotalCount, sw.ElapsedMilliseconds / 1000.0, result.Truncated);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Analysis export failed ListVm={ListVmType}", req.ListVmType);
                return BadRequest(ex.Message);
            }

            if (result.Truncated)
                Response.Headers["X-Analysis-Truncated"] = "true";

            if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
            {
                var csv = BuildCsv(result, includeMetadata);
                var csvEnc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                var csvBytes = csvEnc.GetPreamble().Concat(csvEnc.GetBytes(csv)).ToArray();
                return File(csvBytes, "text/csv", "analysis.csv");
            }

            var xlsx = AnalysisExcelExporter.Export(result, includeChart, chartType, includeMetadata);
            return File(xlsx,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "analysis.xlsx");
        }

        /// <summary>
        /// POST /_analysis/pivot/export?format=xlsx|csv
        /// 匯出 Pivot 分析結果為 Excel 或 CSV。
        /// </summary>
        [HttpPost("pivot/export")]
        [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<IActionResult> PivotExport([FromBody] AnalysisPivotRequest? req,
            [FromQuery] string format = "xlsx",
            [FromQuery] bool includeChart = false,
            [FromQuery] string chartType = "bar",
            [FromQuery] bool includeMetadata = false)
        {
            if (req == null) return BadRequest("Request body is required.");
            if (req.Dimensions.Count == 0) return BadRequest("至少需要選取 1 個維度。");
            if (req.Dimensions.Count > 3) return BadRequest("最多選取 3 個維度。");
            if (req.Measures.Count == 0)  return BadRequest("至少需要選取 1 個度量指標。");
            if (req.Measures.Count > 3)   return BadRequest("最多選取 3 個度量。");

            var errorResult = TryPrepareContext(req, out var ctx);
            if (errorResult != null) return errorResult;

            var sw = Stopwatch.StartNew();
            AnalysisPivotResponse result;
            try
            {
result = await _engine.ExecutePivotDynamicAsync(ctx!.BaseQuery, req, ctx.Fields, identityKey: ctx.IdentityKey, cancellationToken: HttpContext?.RequestAborted ?? default);
                sw.Stop();
                _logger.LogInformation("Analysis pivot export completed ListVm={ListVmType} Format={Format} ElapsedMs={Elapsed}",
                    req.ListVmType, format, sw.ElapsedMilliseconds);
                WriteAnalysisActionLog($"PivotExport({format})", req.ListVmType, req.Dimensions, req.Measures, result.Rows.Count, sw.ElapsedMilliseconds / 1000.0, result.Truncated);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Analysis pivot export failed ListVm={ListVmType}", req.ListVmType);
                return BadRequest(ex.Message);
            }

            // Adapt AnalysisPivotResponse to AnalysisQueryResponse format for CSV/Excel export
            var queryResult = new AnalysisQueryResponse
            {
                Columns = result.Columns,
                Rows = result.Rows,
                TotalCount = result.Rows.Count,
                Truncated = result.Truncated
            };

            if (result.Truncated)
                Response.Headers["X-Analysis-Truncated"] = "true";

            if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
            {
                var csv = BuildCsv(queryResult, includeMetadata);
                var pivotCsvEnc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                var pivotCsvBytes = pivotCsvEnc.GetPreamble().Concat(pivotCsvEnc.GetBytes(csv)).ToArray();
                return File(pivotCsvBytes, "text/csv", "analysis_pivot.csv");
            }

            var xlsx = AnalysisExcelExporter.Export(queryResult, includeChart, chartType, includeMetadata);
            return File(xlsx,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "analysis_pivot.xlsx");
        }

        // ─── Saved Queries ─────────────────────────────────────────────────

        /// <summary>
        /// GET /_analysis/savedqueries?listVmType=Foo.BarListVM
        /// 回傳目前使用者的私有查詢 + 所有公開查詢（依建立時間降序）。
        /// </summary>
        [HttpGet("savedqueries")]
        [ProducesResponseType(typeof(IEnumerable<SavedQuerySummaryDto>), StatusCodes.Status200OK)]
        public IActionResult ListSavedQueries([FromQuery] string listVmType)
        {
            if (string.IsNullOrWhiteSpace(listVmType))
                return BadRequest("listVmType is required.");

            Type vmType;
            try { vmType = _registry.Resolve(listVmType); }
            catch (AnalysisVmNotFoundException ex) { return NotFound(ex.Message); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
            if (!CheckAccess(vmType)) return Forbid();

            var userCode = Wtm?.LoginUserInfo?.ITCode ?? string.Empty;

            var rows = Wtm!.DC.Set<AnalysisSavedQuery>()
                .Where(q => q.ListVmType == listVmType && (q.OwnerCode == userCode || q.IsPublic))
                .OrderByDescending(q => q.CreateTime)
                .Select(q => new SavedQuerySummaryDto
                {
                    Id         = q.ID,
                    Name       = q.Name,
                    ListVmType = q.ListVmType,
                    OwnerCode  = q.OwnerCode ?? string.Empty,
                    IsPublic   = q.IsPublic,
                    IsOwner    = q.OwnerCode == userCode,
                    CreatedAt  = q.CreateTime
                })
                .ToList();

            return Ok(rows);
        }

        /// <summary>
        /// POST /_analysis/savedqueries
        /// 儲存一個查詢設定，回傳新建立的記錄 ID。
        /// </summary>
        [HttpPost("savedqueries")]
        [ProducesResponseType(typeof(object), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(string), StatusCodes.Status400BadRequest)]
        public IActionResult SaveQuery([FromBody] SaveQueryRequest? req)
        {
            if (req == null) return BadRequest("Request body is required.");
            if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("查詢名稱不可為空。");
            if (string.IsNullOrWhiteSpace(req.Config?.ListVmType)) return BadRequest("Config.ListVmType is required.");

            Type vmType;
            try { vmType = _registry.Resolve(req.Config.ListVmType); }
            catch (AnalysisVmNotFoundException ex) { return NotFound(ex.Message); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
            if (!CheckAccess(vmType)) return Forbid();

            var userCode = Wtm?.LoginUserInfo?.ITCode ?? string.Empty;
            var configJson = JsonSerializer.Serialize(req.Config, _camelCase);

            var entity = new AnalysisSavedQuery
            {
                Name       = req.Name.Trim(),
                ListVmType = req.Config.ListVmType,
                ConfigJson = configJson,
                OwnerCode  = userCode,
                IsPublic   = req.IsPublic,
                CreateTime = Wtm!.TimeProvider.GetLocalNow().DateTime,
                CreateBy   = userCode
            };

            Wtm!.DC.Set<AnalysisSavedQuery>().Add(entity);
            Wtm.DC.SaveChanges();

            _logger.LogInformation("Analysis saved query created Id={Id} Name={Name} ListVm={ListVm} Owner={Owner} IsPublic={IsPublic}",
                entity.ID, entity.Name, entity.ListVmType, entity.OwnerCode, entity.IsPublic);

            return CreatedAtAction(nameof(GetSavedQuery), new { id = entity.ID },
                new { id = entity.ID, name = entity.Name });
        }

        /// <summary>
        /// GET /_analysis/savedqueries/{id}
        /// 載入指定 ID 的查詢設定（回傳 AnalysisQueryRequest）。
        /// </summary>
        [HttpGet("savedqueries/{id:guid}")]
        [ProducesResponseType(typeof(AnalysisQueryRequest), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetSavedQuery(Guid id)
        {
            var entity = Wtm!.DC.Set<AnalysisSavedQuery>().FirstOrDefault(q => q.ID == id);
            if (entity == null) return NotFound();

            var userCode = Wtm.LoginUserInfo?.ITCode ?? string.Empty;
            if (!entity.IsPublic && entity.OwnerCode != userCode) return Forbid();

            AnalysisQueryRequest? config;
            try { config = JsonSerializer.Deserialize<AnalysisQueryRequest>(entity.ConfigJson, _camelCase); }
            catch (JsonException) { return BadRequest("儲存的查詢格式無效。"); }

            if (config == null) return BadRequest("儲存的查詢格式無效。");
            Type vmType;
            try { vmType = _registry.Resolve(config.ListVmType); }
            catch (AnalysisVmNotFoundException ex) { return NotFound(ex.Message); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
            if (!CheckAccess(vmType)) return Forbid();

            return new JsonResult(config, _camelCase);
        }

        /// <summary>
        /// DELETE /_analysis/savedqueries/{id}
        /// 刪除儲存的查詢（只有擁有者可刪除）。
        /// </summary>
        [HttpDelete("savedqueries/{id:guid}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> DeleteSavedQuery(Guid id)
        {
            var userCode = Wtm!.LoginUserInfo?.ITCode ?? string.Empty;

            // Single SQL DELETE WHERE — eliminates Load + Remove + SaveChanges round-trip
            var deleted = await Wtm.DC.Set<AnalysisSavedQuery>()
                .Where(q => q.ID == id && q.OwnerCode == userCode)
                .ExecuteDeleteAsync();

            if (deleted > 0)
            {
                _logger.LogInformation("Analysis saved query deleted Id={Id} Owner={Owner}", id, userCode);
                return NoContent();
            }

            // Distinguish 404 (not found) vs 403 (not owner)
            var exists = await Wtm.DC.Set<AnalysisSavedQuery>()
                .AnyAsync(q => q.ID == id);
            return exists ? Forbid() : NotFound();
        }

        // ─── Helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// 寫入 ActionLog，記錄 Analysis 查詢/匯出的稽核軌跡（#561）。
        /// 若 _actionLogger 未注入且 HttpContext 不可用（如單元測試），則靜默略過。
        /// </summary>
        private void WriteAnalysisActionLog(
            string actionName,
            string listVmType,
            List<string> dimensions,
            List<MeasureRequest> measures,
            int rowCount,
            double durationSec,
            bool truncated)
        {
            var logger = _actionLogger
                ?? HttpContext?.RequestServices?.GetService<ILogger<ActionLog>>();
            if (logger == null) return;

            var dims = string.Join(",", dimensions);
            var msrs = string.Join(",", measures.Select(m => $"{m.Field}_{m.Func}"));
            var log = new ActionLog
            {
                LogType    = ActionLogTypesEnum.Normal,
                ActionTime = Wtm!.TimeProvider.GetLocalNow().DateTime,
                ITCode     = Wtm?.LoginUserInfo?.ITCode ?? string.Empty,
                ModuleName = "Analysis",
                ActionName = actionName,
                ActionUrl  = HttpContext?.Request?.Path.Value,
                Duration   = durationSec,
                Remark     = $"ListVm={listVmType} Dims=[{dims}] Msrs=[{msrs}] Rows={rowCount} Truncated={truncated}",
                IP         = HttpContext?.Connection?.RemoteIpAddress?.ToString(),
                TenantCode = Wtm?.LoginUserInfo?.CurrentTenant
            };

            logger.Log<ActionLog>(LogLevel.Information, new EventId(), log, null,
                (a, _) => a.GetLogString());
        }

        /// <summary>
        /// 封裝四個 action 共用的準備結果：VM 解析、存取驗證、欄位掃描、基底查詢、identityKey。
        /// </summary>
        private sealed record PreparedAnalysisContext(
            IQueryable BaseQuery,
            List<AnalysisFieldMeta> Fields,
            string? IdentityKey);

        /// <summary>
        /// 共用準備流程：Resolve → CheckAccess → CreateAndBindVm → GetAnalysisFields →
        /// policy filter → GetSearchQuery → ValidateDimensionHierarchies。
        /// 若任一步驟失敗，設定 <paramref name="ctx"/> 為 null 並回傳對應的錯誤 IActionResult；
        /// 否則回傳 null，ctx 為已填妥的準備結果。
        /// </summary>
        private IActionResult? TryPrepareContext(AnalysisQueryRequest req, out PreparedAnalysisContext? ctx)
        {
            ctx = null;

            Type vmType;
            try { vmType = _registry.Resolve(req.ListVmType); }
            catch (AnalysisVmNotFoundException ex) { return NotFound(ex.Message); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            if (!CheckAccess(vmType)) return Forbid();

            BaseVM vm;
            try { vm = CreateAndBindVm(vmType, req.SearcherFormData); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is JsonException)
            {
                return BadRequest(ex.Message);
            }

            var fields = InvokeGetAnalysisFields(vm, vmType);
            if (_fieldPolicy != null)
            {
                fields = _fieldPolicy.Filter(fields, HttpContext?.User ?? new System.Security.Claims.ClaimsPrincipal()).ToList();
            }

            var baseQuery = InvokeGetSearchQuery(vm, vmType);
            if (baseQuery == null) return BadRequest("無法取得查詢來源。");

            // Defense-in-depth: apply row-level DataPrivilege filtering (#554)
            baseQuery = DCExtension.ApplyDataPrivilegeForAnalysis(baseQuery, Wtm);

            var hierarchyError = ValidateDimensionHierarchies(req.DimensionHierarchies, fields);
            if (hierarchyError != null) return BadRequest(hierarchyError);

            string? identityKey = Wtm?.LoginUserInfo != null ? $"{Wtm.LoginUserInfo.CurrentTenant}_{Wtm.LoginUserInfo.UserId}" : null;

            ctx = new PreparedAnalysisContext(baseQuery, fields.ToList(), identityKey);
            return null;
        }

        private bool CheckAccess(Type vmType)
        {
            var attr = vmType.GetCustomAttribute<EnableAnalysisAttribute>();
            if (attr == null || string.IsNullOrEmpty(attr.AllowedRoles)) return true;

            var userRoles = Wtm?.LoginUserInfo?.Roles?.Select(r => r.RoleCode) ?? Enumerable.Empty<string>();
            if (userRoles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase))) return true;

            var allowed = attr.AllowedRoles.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => r.Trim());
            return allowed.Intersect(userRoles, StringComparer.OrdinalIgnoreCase).Any();
        }

        private static string? ValidateDimensionHierarchies(
            Dictionary<string, DateHierarchy>? hierarchies,
            IEnumerable<AnalysisFieldMeta> fields)
        {
            if (hierarchies == null) return null;
            foreach (var kvp in hierarchies)
            {
                var f = fields.FirstOrDefault(x => x.FieldName == kvp.Key);
                if (f == null) return $"維度 '{kvp.Key}' 不存在於可用欄位清單。";
                if (!f.IsDate && kvp.Value != DateHierarchy.None)
                    return $"欄位 '{kvp.Key}' 不是日期型別 (not a date dimension)，不支援時間層級切換。";
            }
            return null;
        }

        private static List<string> GetAllowedFuncNames(AggregateFunc funcs)
        {
            var res = new List<string>();
            if ((funcs & AggregateFunc.Sum) != 0) res.Add("Sum");
            if ((funcs & AggregateFunc.Count) != 0) res.Add("Count");
            if ((funcs & AggregateFunc.Avg) != 0) res.Add("Avg");
            if ((funcs & AggregateFunc.Max) != 0) res.Add("Max");
            if ((funcs & AggregateFunc.Min) != 0) res.Add("Min");
            return res;
        }

        private BaseVM CreateAnalysisVm(Type vmType)
        {
            var vm = (Activator.CreateInstance(vmType) as BaseVM)!;
            vm.Wtm = Wtm;
            return vm;
        }

        private BaseVM CreateAndBindVm(Type vmType, string? searcherJson)
        {
            var vm = CreateAnalysisVm(vmType);
            if (!string.IsNullOrEmpty(searcherJson))
            {
                // WTM ListVM uses BasePagedListVM<TModel, TSearcher>
                // We need the second generic argument for the Searcher
                var searcherType = vmType.BaseType?.GetGenericArguments().Skip(1).FirstOrDefault();
                if (searcherType != null)
                {
                    var searcher = JsonSerializer.Deserialize(searcherJson, searcherType, _camelCase);
                    if (searcher != null)
                    {
                        var prop = vmType.GetProperty("Searcher");
                        prop?.SetValue(vm, searcher);
                    }
                }
            }
            return vm;
        }

        private static IQueryable InvokeGetSearchQuery(BaseVM vm, Type vmType)
            => AnalysisVmInvoker.GetSearchQuery(vm, vmType);

        private static IList<AnalysisFieldMeta> InvokeGetAnalysisFields(BaseVM vm, Type vmType)
            => AnalysisVmInvoker.GetAnalysisFields(vm, vmType);

        private static string BuildCsv(AnalysisQueryResponse result, bool includeMetadata = false)
        {
            var sb = new StringBuilder();
            if (includeMetadata)
            {
                sb.AppendLine($"匯出時間,{EscapeCsvCell(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC")}");
                sb.AppendLine($"QueryHash,{EscapeCsvCell(result.QueryHash ?? "")}");
                sb.AppendLine($"資料筆數,{result.TotalCount}");
                sb.AppendLine($"已截斷,{(result.Truncated ? "是" : "否")}");
                sb.AppendLine();
            }
            sb.AppendLine(string.Join(",", result.Columns.Select(c =>
                result.ColumnDisplayNames.TryGetValue(c, out var dn) ? dn : c)));
            foreach (var row in result.Rows)
            {
                var values = result.Columns.Select(c =>
                {
                    var v = row.ContainsKey(c) ? row[c] : null;
                    return EscapeCsvCell(v?.ToString());
                });
                sb.AppendLine(string.Join(",", values));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 提供相容各類 Excel/CSV 的欄位轉義（含公式注入防護）。
        /// 供 CsvEscapeTest 回歸測試調用。
        /// </summary>
        private static string EscapeCsvCell(string? val)
        {
            if (val == null) return "";
            var s = val;
            bool needsPrefix = s.StartsWith("=") || s.StartsWith("+") || s.StartsWith("-") || s.StartsWith("@") || s.StartsWith("\t") || s.StartsWith("\r");

            // RFC 4180: quote the original value first, then prepend tab prefix outside the quotes.
            // Order matters: if tab is added first, it ends up trapped inside the quotes.
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r"))
            {
                s = "\"" + s.Replace("\"", "\"\"") + "\"";
            }

            if (needsPrefix)
            {
                s = "\t" + s;
            }
            
            return s;
        }

        /// <summary>
        /// 處理 Nullable&lt;TEnum&gt; 反序列化：HTML form 一律送字串，
        /// 接受 "1"（數字字串）、"Card"（enum 名稱）、數字 literal、null 或空字串。
        /// </summary>
        private class NullableEnumStringConverterFactory : JsonConverterFactory
        {
            public override bool CanConvert(Type typeToConvert)
            {
                var underlying = Nullable.GetUnderlyingType(typeToConvert);
                return underlying?.IsEnum == true;
            }

            public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            {
                var underlying = Nullable.GetUnderlyingType(typeToConvert)!;
                var converterType = typeof(NullableEnumStringConverter<>).MakeGenericType(underlying);
                return (JsonConverter?)Activator.CreateInstance(converterType);
            }
        }

        private class NullableEnumStringConverter<T> : JsonConverter<T?> where T : struct, Enum
        {
            public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.Null) return null;

                if (reader.TokenType == JsonTokenType.Number)
                {
                    if (reader.TryGetInt32(out int num)) return (T)(object)num;
                }

                if (reader.TokenType == JsonTokenType.String)
                {
                    var str = reader.GetString();
                    if (string.IsNullOrEmpty(str)) return null;

                    // Form sends integer string first ("1", "2") — most common case
                    if (int.TryParse(str, out int n)) return (T)(object)n;

                    // Fall back to enum name ("Card", "card")
                    if (Enum.TryParse<T>(str, ignoreCase: true, out var val)) return val;

                    throw new JsonException($"Cannot convert '{str}' to {typeof(T).Name}.");
                }

                throw new JsonException($"Unexpected token {reader.TokenType} for {typeof(T).Name}.");
            }

            public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
            {
                if (value == null) writer.WriteNullValue();
                else writer.WriteNumberValue(Convert.ToInt32(value));
            }
        }

        /// <summary>
        /// 提供相容舊版前端的日期格式化轉換。
        /// </summary>
        private class DateTimeConverter : JsonConverter<DateTime>
        {
            private static readonly string[] Formats = { "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd", "yyyy/MM/dd", "yyyy/MM/dd HH:mm", "yyyy.MM.dd" };

            public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                var str = reader.GetString();
                if (string.IsNullOrEmpty(str)) return default;

                if (DateTime.TryParseExact(str, Formats, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var dt))
                    return dt;

                // Last resort: let the runtime try its own parsing
                if (DateTime.TryParse(str, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out dt))
                    return dt;

                throw new JsonException($"Cannot parse '{str}' as DateTime.");
            }

            public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
            {
                writer.WriteStringValue(value.ToString("yyyy-MM-ddTHH:mm:ss"));
            }
        }
    }
}
