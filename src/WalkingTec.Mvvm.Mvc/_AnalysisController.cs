#nullable enable
using System;
using System.Collections.Generic;
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
        /// 執行動態 GroupBy 聚合查詢，回傳聚合結果。
        /// </summary>
        [HttpPost("query")]
        public IActionResult Query([FromBody] AnalysisQueryRequest? req)
        {
            if (req is null) return BadRequest("Invalid request body.");
            if (req.Dimensions.Count > 3) return BadRequest("最多選取 3 個維度。");
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

            try
            {
                var result = new AnalysisQueryEngine(GroupByStrategyResolver.Default, _cache).ExecuteDynamic(baseQuery, req, fields);
                return new JsonResult(result, _camelCase);
            }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        }

        [HttpPost("pivot")]
        public IActionResult Pivot([FromBody] AnalysisPivotRequest req)
        {
            if (req.Dimensions.Count > 3) return BadRequest("最多選取 3 個維度。");
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

            try
            {
                var result = new AnalysisQueryEngine(GroupByStrategyResolver.Default, _cache).ExecutePivotDynamic(baseQuery, req, fields);
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
                                    [FromQuery] bool includeChart = false)
        {
            if (req is null) return BadRequest("Invalid request body.");
            // 與 Query 端點一致的維度/度量上限驗證（I-5）
            if (req.Dimensions.Count > 3) return BadRequest("最多選取 3 個維度。");
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

            AnalysisQueryResponse result;
            try { result = new AnalysisQueryEngine(GroupByStrategyResolver.Default, _cache).ExecuteDynamic(baseQuery, req, fields); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
            {
                var csv = BuildCsv(result);
                return File(Encoding.UTF8.GetBytes(csv), "text/csv", "analysis.csv");
            }

            var xlsx = AnalysisExcelExporter.Export(result, includeChart);
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
                                         [FromQuery] bool includeChart = false)
        {
            if (req.Dimensions.Count > 3) return BadRequest("最多選取 3 個維度。");
            if (req.Measures.Count > 3)   return BadRequest("最多選取 3 個度量。");
            if (string.IsNullOrEmpty(req.PivotDimension)) return BadRequest("必須指定 PivotDimension。");

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

            AnalysisPivotResponse result;
            try { result = new AnalysisQueryEngine(GroupByStrategyResolver.Default, _cache).ExecutePivotDynamic(baseQuery, req, fields); }
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

            var xlsx = AnalysisExcelExporter.Export(queryResult, includeChart);
            return File(xlsx,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "analysis_pivot.xlsx");
        }

        // ─── Helpers ───────────────────────────────────────────────────────

        private BaseVM CreateAnalysisVm(Type vmType)
        {
            // Activator.CreateInstance 在 VM 無 public parameterless constructor 時拋 MissingMethodException（I-7）
            try
            {
                var vm = Activator.CreateInstance(vmType) as BaseVM;
                if (vm is null)
                {
                    throw new InvalidOperationException(
                        $"VM 型別 '{vmType.FullName}' 必須繼承 BaseVM 才能用於 Analysis Mode。");
                }
                vm.Wtm = Wtm;
                return vm;
            }
            catch (MissingMethodException)
            {
                throw new InvalidOperationException(
                    $"VM 型別 '{vmType.FullName}' 必須有 public parameterless constructor 才能用於 Analysis Mode。");
            }
        }

        private BaseVM CreateAndBindVm(Type vmType, string? searcherFormData)
        {
            var vm = CreateAnalysisVm(vmType);

            if (!string.IsNullOrEmpty(searcherFormData))
            {
                var searcherProp = vmType.GetProperty("Searcher");
                if (searcherProp != null)
                {
                    // JsonException → 400（I-8）
                    try
                    {
                        var searcher = JsonSerializer.Deserialize(
                            searcherFormData,
                            searcherProp.PropertyType,
                            SearcherJsonOptions);
                        searcherProp.SetValue(vm, searcher);
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidOperationException($"搜尋條件格式錯誤：{ex.Message}", ex);
                    }
                }
            }

            return vm;
        }

        private static IEnumerable<AnalysisFieldMeta> InvokeGetAnalysisFields(BaseVM vm, Type vmType)
        {
            // 明確指定 binding flags 與無參數多載，避免 null 或 AmbiguousMatchException（C-3）
            var method = vmType.GetMethod(
                "GetAnalysisFields",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);
            if (method == null)
                throw new InvalidOperationException(
                    $"VM 型別 '{vmType.FullName}' 找不到 GetAnalysisFields() 方法。");
            try
            {
                var result = method.Invoke(vm, null) as IEnumerable<AnalysisFieldMeta>;
                if (result is null)
                {
                    throw new InvalidOperationException(
                        $"VM 型別 '{vmType.FullName}' 的 GetAnalysisFields() 未回傳欄位集合。");
                }
                return result;
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        private static System.Linq.IQueryable? InvokeGetSearchQuery(BaseVM vm, Type vmType)
        {
            // 明確指定無參數多載，避免 AmbiguousMatchException（C-4）
            var method = vmType.GetMethod(
                "GetSearchQuery",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);
            if (method == null) return null;
            try
            {
                var result = method.Invoke(vm, null);
                if (result == null) return null;
                if (result is System.Linq.IQueryable q) return q;
                throw new InvalidOperationException(
                    $"VM 型別 '{vmType.FullName}' 的 GetSearchQuery() 未回傳 IQueryable。");
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        /// <summary>
        /// 驗證 DimensionHierarchies 中的每個 key 都對應到 IsDate=true 的維度欄位。
        /// 回傳 null 表示通過；否則回傳錯誤訊息字串。
        /// </summary>
        private static string? ValidateDimensionHierarchies(
            Dictionary<string, DateHierarchy>? hierarchies,
            IEnumerable<AnalysisFieldMeta> fields)
        {
            if (hierarchies == null || hierarchies.Count == 0) return null;

            var fieldMap = fields.ToDictionary(f => f.FieldName, StringComparer.Ordinal);
            foreach (var kvp in hierarchies)
            {
                if (!fieldMap.TryGetValue(kvp.Key, out var meta))
                    return $"Field '{kvp.Key}' is not a valid analysis field.";
                if (!meta.IsDate)
                    return $"Field '{kvp.Key}' is not a date dimension.";
            }
            return null;
        }

        private static string[] GetAllowedFuncNames(AggregateFunc funcs)
            => Enum.GetValues<AggregateFunc>()
                   .Where(f => funcs.HasFlag(f))
                   .Select(f => f.ToString())
                   .ToArray();

        private static string BuildCsv(AnalysisQueryResponse result)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", result.Columns.Select(EscapeCsvCell)));
            foreach (var row in result.Rows)
            {
                var values = result.Columns.Select(c =>
                    EscapeCsvCell(row.GetValueOrDefault(c)?.ToString() ?? ""));
                sb.AppendLine(string.Join(",", values));
            }
            return sb.ToString();
        }

        private static string EscapeCsvCell(string val)
        {
            if (string.IsNullOrEmpty(val)) return "";
            // 防 CSV formula injection（I-2）：以公式字元開頭時前置 tab
            if (val[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
                val = "\t" + val;
            // 包含逗號、換行或引號時用 RFC 4180 引號包圍
            if (val.Contains(',') || val.Contains('\n') || val.Contains('"'))
                val = $"\"{val.Replace("\"", "\"\"")}\"";
            return val;
        }

        private static readonly JsonSerializerOptions SearcherJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new FlexibleDateTimeConverter() }
        };

        /// <summary>
        /// Accepts common date/time formats from frontend date-pickers
        /// (e.g. LayUI "yyyy/MM/dd", "yyyy-MM-dd", ISO 8601, etc.)
        /// in addition to the default System.Text.Json DateTime handling.
        /// </summary>
        private sealed class FlexibleDateTimeConverter : JsonConverter<DateTime>
        {
            private static readonly string[] Formats =
            {
                "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm", "yyyy/MM/dd",
                "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
                "yyyy.MM.dd HH:mm:ss", "yyyy.MM.dd",
            };

            public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                var str = reader.GetString();
                if (string.IsNullOrEmpty(str))
                    return default;

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
