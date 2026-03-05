#nullable disable
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

        public _AnalysisController(AnalysisVmRegistry registry)
            => _registry = registry;

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
                var result = new AnalysisQueryEngine().ExecuteDynamic(baseQuery, req, fields);
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
            Type vmType;
            try { vmType = _registry.Resolve(req.ListVmType); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

            var vm = CreateAndBindVm(vmType, req.SearcherFormData);
            var fields = InvokeGetAnalysisFields(vm, vmType);
            var baseQuery = InvokeGetSearchQuery(vm, vmType);
            if (baseQuery == null) return BadRequest("無法取得查詢來源。");

            AnalysisQueryResponse result;
            try { result = new AnalysisQueryEngine().ExecuteDynamic(baseQuery, req, fields); }
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
            var vm = (BaseVM)Activator.CreateInstance(vmType);
            vm.Wtm = Wtm;
            return vm;
        }

        private BaseVM CreateAndBindVm(Type vmType, string searcherFormData)
        {
            var vm = CreateAnalysisVm(vmType);

            if (!string.IsNullOrEmpty(searcherFormData))
            {
                var searcherProp = vmType.GetProperty("Searcher");
                if (searcherProp != null)
                {
                    var searcher = JsonSerializer.Deserialize(
                        searcherFormData,
                        searcherProp.PropertyType,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    searcherProp.SetValue(vm, searcher);
                }
            }

            return vm;
        }

        private static IEnumerable<AnalysisFieldMeta> InvokeGetAnalysisFields(BaseVM vm, Type vmType)
        {
            var method = vmType.GetMethod("GetAnalysisFields");
            return (IEnumerable<AnalysisFieldMeta>)method.Invoke(vm, null);
        }

        private static System.Linq.IQueryable InvokeGetSearchQuery(BaseVM vm, Type vmType)
        {
            var method = vmType.GetMethod("GetSearchQuery");
            return method?.Invoke(vm, null) as System.Linq.IQueryable;
        }

        private static string[] GetAllowedFuncNames(AggregateFunc funcs)
            => Enum.GetValues<AggregateFunc>()
                   .Where(f => funcs.HasFlag(f))
                   .Select(f => f.ToString())
                   .ToArray();

        private static string BuildCsv(AnalysisQueryResponse result)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", result.Columns));
            foreach (var row in result.Rows)
            {
                var values = result.Columns.Select(c =>
                {
                    var val = row.GetValueOrDefault(c)?.ToString() ?? "";
                    // 包含逗號或換行時用引號包圍
                    return val.Contains(',') || val.Contains('\n')
                        ? $"\"{val.Replace("\"", "\"\"")}\"" : val;
                });
                sb.AppendLine(string.Join(",", values));
            }
            return sb.ToString();
        }
    }
}
