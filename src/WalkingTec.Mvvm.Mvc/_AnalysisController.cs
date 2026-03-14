#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

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
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly AnalysisVmRegistry _registry;
        private readonly IAnalysisCache? _cache;
        private readonly IAnalysisFieldPolicy? _fieldPolicy;

        public _AnalysisController(AnalysisVmRegistry registry, IAnalysisCache? cache = null, IAnalysisFieldPolicy? fieldPolicy = null)
        {
            _registry = registry;
            _cache = cache;
            _fieldPolicy = fieldPolicy;
        }

        /// <summary>
        /// GET /_analysis/meta?listVmType=Foo.BarListVM
        /// 回傳指定 ListVM 上標記 [Dimension]/[Measure] 的欄位清單。
        /// </summary>
        [HttpGet("meta")]
        public IActionResult GetMeta([FromQuery] string listVmType)
        {
            Type vmType;
            try { vmType = _registry.Resolve(listVmType); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

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
                    : Array.Empty<string>(),
                isDate = f.IsDate,
                hierarchy = f.Hierarchy.ToString()
            }));
        }

        /// <summary>
        /// POST /_analysis/query
        /// 執行分析查詢。
        /// </summary>
        [HttpPost("query")]
        public IActionResult Query([FromBody] AnalysisQueryRequest req)
        {
            if (req.Dimensions.Count > 3) return BadRequest("最多選取 3 個維度。");
            if (req.Measures.Count == 0)  return BadRequest("至少需要選取 1 個度量指標。");
            if (req.Measures.Count > 3)   return BadRequest("最多選取 3 個度量。");

            Type vmType;
            try { vmType = _registry.Resolve(req.ListVmType); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            BaseVM vm;
            try { vm = CreateAndBindVm(vmType, req.SearcherFormData); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            var fields = InvokeGetAnalysisFields(vm, vmType);
            if (_fieldPolicy != null)
            {
                fields = _fieldPolicy.Filter(fields, HttpContext?.User ?? new System.Security.Claims.ClaimsPrincipal()).ToList();
            }
            var baseQuery = InvokeGetSearchQuery(vm, vmType);
            if (baseQuery == null) return BadRequest("無法取得查詢來源。");

            var hierarchyError = ValidateDimensionHierarchies(req.DimensionHierarchies, fields);
            if (hierarchyError != null) return BadRequest(hierarchyError);

            string? identityKey = Wtm?.LoginUserInfo != null ? $"{Wtm.LoginUserInfo.CurrentTenant}_{Wtm.LoginUserInfo.UserId}" : null;

            try
            {
                var result = new AnalysisQueryEngine(GroupByStrategyResolver.Default, _cache).ExecuteDynamic(baseQuery, req, fields, identityKey: identityKey, cancellationToken: HttpContext?.RequestAborted ?? default);
                return new JsonResult(result, _camelCase);
            }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        }

        [HttpPost("pivot")]
        public IActionResult Pivot([FromBody] AnalysisPivotRequest req)
        {
            if (req.Dimensions.Count > 3) return BadRequest("最多選取 3 個維度。");
            if (req.Measures.Count == 0)  return BadRequest("至少需要選取 1 個度量指標。");
            if (req.Measures.Count > 3)   return BadRequest("最多選取 3 個度量。");
            if (string.IsNullOrEmpty(req.PivotDimension)) return BadRequest("必須指定 PivotDimension。");

            Type vmType;
            try { vmType = _registry.Resolve(req.ListVmType); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            BaseVM pivotVm;
            try { pivotVm = CreateAndBindVm(vmType, req.SearcherFormData); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            var fields = InvokeGetAnalysisFields(pivotVm, vmType);
            if (_fieldPolicy != null)
            {
                fields = _fieldPolicy.Filter(fields, HttpContext?.User ?? new System.Security.Claims.ClaimsPrincipal()).ToList();
            }
            var baseQuery = InvokeGetSearchQuery(pivotVm, vmType);
            if (baseQuery == null) return BadRequest("無法取得查詢來源。");

            var hierarchyError = ValidateDimensionHierarchies(req.DimensionHierarchies, fields);
            if (hierarchyError != null) return BadRequest(hierarchyError);

            string? identityKey = Wtm?.LoginUserInfo != null ? $"{Wtm.LoginUserInfo.CurrentTenant}_{Wtm.LoginUserInfo.UserId}" : null;

            try
            {
                var result = new AnalysisQueryEngine(GroupByStrategyResolver.Default, _cache).ExecutePivotDynamic(baseQuery, req, fields, identityKey: identityKey, cancellationToken: HttpContext?.RequestAborted ?? default);
                return new JsonResult(result, _camelCase);
            }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        }

        /// <summary>
        /// POST /_analysis/export?format=xlsx|csv
        /// 匯出分析結果為 Excel 或 CSV。
        /// </summary>
        [HttpPost("export")]
        public IActionResult Export([FromBody] AnalysisQueryRequest? req,
            [FromQuery] string format = "xlsx",
            [FromQuery] bool includeChart = false,
            [FromQuery] string chartType = "bar")
        {
            if (req == null) return BadRequest("Request body is required.");

            Type vmType;
            try { vmType = _registry.Resolve(req.ListVmType); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            BaseVM vm;
            try { vm = CreateAndBindVm(vmType, req.SearcherFormData); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            var fields = InvokeGetAnalysisFields(vm, vmType);
            if (_fieldPolicy != null)
            {
                fields = _fieldPolicy.Filter(fields, HttpContext?.User ?? new System.Security.Claims.ClaimsPrincipal()).ToList();
            }
            var baseQuery = InvokeGetSearchQuery(vm, vmType);
            if (baseQuery == null) return BadRequest("無法取得查詢來源。");

            var hierarchyError = ValidateDimensionHierarchies(req.DimensionHierarchies, fields);
            if (hierarchyError != null) return BadRequest(hierarchyError);

            string? identityKey = Wtm?.LoginUserInfo != null ? $"{Wtm.LoginUserInfo.CurrentTenant}_{Wtm.LoginUserInfo.UserId}" : null;

            AnalysisQueryResponse result;
            try { result = new AnalysisQueryEngine(GroupByStrategyResolver.Default, _cache).ExecuteDynamic(baseQuery, req, fields, identityKey: identityKey, cancellationToken: HttpContext?.RequestAborted ?? default); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            if (result.Truncated)
                Response.Headers["X-Analysis-Truncated"] = "true";

            if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
            {
                var csv = BuildCsv(result);
                return File(Encoding.UTF8.GetBytes(csv), "text/csv", "analysis.csv");
            }

            var xlsx = AnalysisExcelExporter.Export(result, includeChart, chartType);
            return File(xlsx,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "analysis.xlsx");
        }

        /// <summary>
        /// POST /_analysis/pivot/export?format=xlsx|csv
        /// 匯出 Pivot 分析結果為 Excel 或 CSV。
        /// </summary>
        [HttpPost("pivot/export")]
        public IActionResult PivotExport([FromBody] AnalysisPivotRequest req,
            [FromQuery] string format = "xlsx",
            [FromQuery] bool includeChart = false,
            [FromQuery] string chartType = "bar")
        {
            Type vmType;
            try { vmType = _registry.Resolve(req.ListVmType); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            BaseVM vm;
            try { vm = CreateAndBindVm(vmType, req.SearcherFormData); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            var fields = InvokeGetAnalysisFields(vm, vmType);
            if (_fieldPolicy != null)
            {
                fields = _fieldPolicy.Filter(fields, HttpContext?.User ?? new System.Security.Claims.ClaimsPrincipal()).ToList();
            }
            var baseQuery = InvokeGetSearchQuery(vm, vmType);
            if (baseQuery == null) return BadRequest("無法取得查詢來源。");

            var hierarchyError = ValidateDimensionHierarchies(req.DimensionHierarchies, fields);
            if (hierarchyError != null) return BadRequest(hierarchyError);

            string? identityKey = Wtm?.LoginUserInfo != null ? $"{Wtm.LoginUserInfo.CurrentTenant}_{Wtm.LoginUserInfo.UserId}" : null;

            AnalysisPivotResponse result;
            try { result = new AnalysisQueryEngine(GroupByStrategyResolver.Default, _cache).ExecutePivotDynamic(baseQuery, req, fields, identityKey: identityKey, cancellationToken: HttpContext?.RequestAborted ?? default); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            // Adapt AnalysisPivotResponse to AnalysisQueryResponse format for CSV/Excel export
            var queryResult = new AnalysisQueryResponse
            {
                Columns = result.Columns,
                Rows = result.Rows,
                TotalCount = result.Rows.Count,
                Truncated = false
            };

            if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
            {
                var csv = BuildCsv(queryResult);
                return File(Encoding.UTF8.GetBytes(csv), "text/csv", "analysis_pivot.csv");
            }

            var xlsx = AnalysisExcelExporter.Export(queryResult, includeChart, chartType);
            return File(xlsx,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "analysis_pivot.xlsx");
        }

        // ─── Helpers ───────────────────────────────────────────────────────

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
                    return $"欄位 '{kvp.Key}' 不是日期型別，不支援時間層級切換。";
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
                var searcherType = vmType.BaseType?.GetGenericArguments().FirstOrDefault();
                if (searcherType != null)
                {
                    var searcher = JsonSerializer.Deserialize(searcherJson, searcherType, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
        {
            var method = vmType.GetMethod("GetSearchQuery");
            return (method?.Invoke(vm, null) as IQueryable)!;
        }

        private static IEnumerable<AnalysisFieldMeta> InvokeGetAnalysisFields(BaseVM vm, Type vmType)
        {
            var method = vmType.GetMethod("GetAnalysisFields");
            return (method?.Invoke(vm, null) as IEnumerable<AnalysisFieldMeta>)!;
        }

        private static string BuildCsv(AnalysisQueryResponse result)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", result.Columns));
            foreach (var row in result.Rows)
            {
                var values = result.Columns.Select(c =>
                {
                    var v = row.ContainsKey(c) ? row[c] : null;
                    var s = v?.ToString() ?? "";
                    if (s.Contains(",") || s.Contains("\"")) s = "\"" + s.Replace("\"", "\"\"") + "\"";
                    return s;
                });
                sb.AppendLine(string.Join(",", values));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 提供相容舊版前端的日期格式化轉換。
        /// </summary>
        private class DateTimeConverter : JsonConverter<DateTime>
        {
            private static readonly string[] Formats = { "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd" };

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
