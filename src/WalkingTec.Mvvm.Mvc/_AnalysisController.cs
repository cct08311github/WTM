#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
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
        private readonly AnalysisVmRegistry _registry;
        private readonly IAnalysisCache? _cache;

        public _AnalysisController(AnalysisVmRegistry registry, IAnalysisCache? cache = null)
        {
            _registry = registry;
            _cache = cache;
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

            return Ok(fields.Select(f => new
            {
                f.FieldName,
                f.DisplayName,
                Kind = f.Kind.ToString(),
                AllowedFuncs = f.Kind == AnalysisFieldKind.Measure
                    ? GetAllowedFuncNames(f.AllowedFuncs)
                    : Array.Empty<string>()
            }));
        }

        /// <summary>
        /// POST /_analysis/query
        /// 執行動態 GroupBy 聚合查詢，回傳聚合結果。
        /// </summary>
        [HttpPost("query")]
        public IActionResult Query([FromBody] AnalysisQueryRequest req)
        {
            if (req.Dimensions.Count > 3) return BadRequest("最多選取 3 個維度。");
            if (req.Measures.Count > 3)   return BadRequest("最多選取 3 個度量。");

            Type vmType;
            try { vmType = _registry.Resolve(req.ListVmType); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            var vm = CreateAndBindVm(vmType, req.SearcherFormData);
            var fields = InvokeGetAnalysisFields(vm, vmType);
            var baseQuery = InvokeGetSearchQuery(vm, vmType);
            if (baseQuery == null) return BadRequest("無法取得查詢來源。");

            try
            {
                var result = new AnalysisQueryEngine(_cache).ExecuteDynamic(baseQuery, req, fields);
                return Ok(result);
            }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        }

        /// <summary>
        /// POST /_analysis/export?format=xlsx|csv
        /// 匯出分析結果為 Excel 或 CSV。
        /// </summary>
        [HttpPost("export")]
        public IActionResult Export([FromBody] AnalysisQueryRequest req,
                                    [FromQuery] string format = "xlsx")
        {
            // 與 Query 端點一致的維度/度量上限驗證（I-5）
            if (req.Dimensions.Count > 3) return BadRequest("最多選取 3 個維度。");
            if (req.Measures.Count > 3)   return BadRequest("最多選取 3 個度量。");

            Type vmType;
            try { vmType = _registry.Resolve(req.ListVmType); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            var vm = CreateAndBindVm(vmType, req.SearcherFormData);
            var fields = InvokeGetAnalysisFields(vm, vmType);
            var baseQuery = InvokeGetSearchQuery(vm, vmType);
            if (baseQuery == null) return BadRequest("無法取得查詢來源。");

            AnalysisQueryResponse result;
            try { result = new AnalysisQueryEngine(_cache).ExecuteDynamic(baseQuery, req, fields); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
            {
                var csv = BuildCsv(result);
                return File(Encoding.UTF8.GetBytes(csv), "text/csv", "analysis.csv");
            }

            var xlsx = AnalysisExcelExporter.Export(result);
            return File(xlsx,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "analysis.xlsx");
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
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
    }
}
