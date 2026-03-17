#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Http;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    /// <summary>
    /// _AnalysisController 整合測試。
    /// 覆蓋：GetMeta、Query、Export 三端點的路由守衛、白名單驗證、happy path 及 CSV 注入防護。
    ///
    /// 測試資料注入策略：使用靜態欄位 _testData，
    /// 讓巢狀 VM（由 Activator.CreateInstance 建立）的 GetSearchQuery() 回傳可控的資料集，
    /// 無須真實資料庫連線。
    /// </summary>
    [TestClass]
    public class AnalysisControllerTests
    {
        // ─── 測試模型 ──────────────────────────────────────────────────────────

        private class SaleRecord : TopBasePoco
        {
            [Dimension(DisplayName = "地區")]  public string Region   { get; set; }
            [Dimension(DisplayName = "類別")]  public string Category { get; set; }

            [Measure(AllowedFuncs =
                AggregateFunc.Sum | AggregateFunc.Count | AggregateFunc.Avg |
                AggregateFunc.Max | AggregateFunc.Min,
                DisplayName = "金額")]
            public decimal Amount { get; set; }
        }

        // 靜態注入點：每次測試前在 Setup() 清空，happy-path 測試自行填入
        private static IList<SaleRecord> _testData = new List<SaleRecord>();

        [EnableAnalysis]
        private class SaleRecordListVM : BasePagedListVM<SaleRecord, BaseSearcher>
        {
            // Activator.CreateInstance 使用 parameterless constructor；
            // GetSearchQuery() 讀取外部類別的靜態欄位，不需要 DC（資料庫）
            public override IOrderedQueryable<SaleRecord> GetSearchQuery()
                => _testData.AsQueryable().OrderByDescending(x => x.ID);
        }

        // ─── 含日期維度的測試模型 ────────────────────────────────────────────────

        private class OrderRecord : TopBasePoco
        {
            [Dimension(DisplayName = "訂單日期", Hierarchy = DateHierarchy.Month)]
            public DateTime OrderDate { get; set; }

            [Dimension(DisplayName = "地區")]
            public string Region { get; set; }

            [Measure(AllowedFuncs = AggregateFunc.Sum, DisplayName = "金額")]
            public decimal Amount { get; set; }
        }

        private static IList<OrderRecord> _orderData = new List<OrderRecord>();

        [EnableAnalysis]
        private class OrderRecordListVM : BasePagedListVM<OrderRecord, BaseSearcher>
        {
            public override IOrderedQueryable<OrderRecord> GetSearchQuery()
                => _orderData.AsQueryable().OrderByDescending(x => x.ID);
        }

        // ─── 含日期搜尋條件的測試模型（SearcherFormData 日期格式測試）────────────

        private class DateSearcher : BaseSearcher
        {
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
        }

        [EnableAnalysis]
        private class DateSearchListVM : BasePagedListVM<SaleRecord, DateSearcher>
        {
            public override IOrderedQueryable<SaleRecord> GetSearchQuery()
                => _testData.AsQueryable().OrderByDescending(x => x.ID);
        }

        // ─── nullable enum 搜尋條件（SearcherFormData nullable enum 測試，#471）─────

        private enum PaymentKind { Cash = 0, Card = 1, Transfer = 2 }

        private class EnumSearcher : BaseSearcher
        {
            public PaymentKind? Payment { get; set; }
        }

        [EnableAnalysis]
        private class EnumSearchListVM : BasePagedListVM<SaleRecord, EnumSearcher>
        {
            public override IOrderedQueryable<SaleRecord> GetSearchQuery()
                => _testData.AsQueryable().OrderByDescending(x => x.ID);
        }

        // ─── 基礎設施 ──────────────────────────────────────────────────────────

        private AnalysisVmRegistry _registry;

        [TestInitialize]
        public void Setup()
        {
            _testData = new List<SaleRecord>();
            _orderData = new List<OrderRecord>();
            _registry = new AnalysisVmRegistry();
            // 掃描本測試 assembly，會找到 SaleRecordListVM（帶 [EnableAnalysis]）
            _registry.Build(new[] { typeof(AnalysisControllerTests).Assembly });
        }

        private _AnalysisController CreateController(IAnalysisFieldPolicy policy = null)
        {
            var controller = new _AnalysisController(_registry, Microsoft.Extensions.Logging.Abstractions.NullLogger<_AnalysisController>.Instance, null, policy);
            controller.Wtm = MockWtmContext.CreateWtmContext();
            return controller;
        }

        private class MockFieldPolicy : IAnalysisFieldPolicy
        {
            public IEnumerable<AnalysisFieldMeta> Filter(IEnumerable<AnalysisFieldMeta> fields, System.Security.Claims.ClaimsPrincipal user)
            {
                return fields.Where(f => f.FieldName != "Region");
            }
        }

        [TestMethod]
        public void GetMeta_with_policy_filters_fields()
        {
            var controller = CreateController(new MockFieldPolicy());
            var result = controller.GetMeta(typeof(SaleRecordListVM).FullName) as OkObjectResult;
            Assert.IsNotNull(result);

            var value = result.Value as IEnumerable<object>;
            Assert.IsNotNull(value);
            
            // Should not contain "Region"
            var json = System.Text.Json.JsonSerializer.Serialize(value);
            Assert.IsFalse(json.Contains("\"fieldName\":\"Region\""));
            Assert.IsTrue(json.Contains("\"fieldName\":\"Category\""));
        }

        [TestMethod]
        public void Query_returns_400_for_filtered_dimension_field()
        {
            var controller = CreateController(new MockFieldPolicy());
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });
            var result = controller.Query(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "存取被 Policy 過濾的欄位應回傳 400");
        }

        private static AnalysisQueryRequest Req(
            string[] dims,
            (string field, AggregateFunc func)[] msrs = null)
            => new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = dims?.ToList() ?? new List<string>(),
                Measures   = msrs?.Select(m => new MeasureRequest { Field = m.field, Func = m.func }).ToList()
                             ?? new List<MeasureRequest>()
            };

        // ─── GetMeta ───────────────────────────────────────────────────────────

        [TestMethod]
        public void GetMeta_returns_field_list_for_registered_vm()
        {
            var result = CreateController().GetMeta(typeof(SaleRecordListVM).FullName) as OkObjectResult;
            Assert.IsNotNull(result, "GetMeta 應回傳 200 OK");
        }

        [TestMethod]
        public void GetMeta_returns_400_for_unregistered_vm()
        {
            var result = CreateController().GetMeta("No.Such.Vm") as BadRequestObjectResult;
            Assert.IsNotNull(result, "未註冊 VM 應回傳 400");
        }

        // ─── Query 路由守衛 ───────────────────────────────────────────────────

        [TestMethod]
        public void Query_returns_400_when_dimensions_exceed_3()
        {
            var req = Req(dims: new[] { "A", "B", "C", "D" });
            var result = CreateController().Query(req) as BadRequestObjectResult;
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Value.ToString().Contains("維度"),
                "錯誤訊息應包含「維度」");
        }

        [TestMethod]
        public void Query_returns_400_when_measures_exceed_3()
        {
            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures   = Enumerable.Range(0, 4)
                    .Select(_ => new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum })
                    .ToList()
            };
            var result = CreateController().Query(req) as BadRequestObjectResult;
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Value.ToString().Contains("度量"),
                "錯誤訊息應包含「度量」");
        }

        [TestMethod]
        public void Query_returns_400_for_unregistered_vm()
        {
            var req = new AnalysisQueryRequest
            {
                ListVmType = "No.Such.Vm",
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest> { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } }
            };
            var result = CreateController().Query(req) as BadRequestObjectResult;
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public void Query_returns_400_for_invalid_dimension_field()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Amount = 100m }
            };
            var req = Req(
                dims: new[] { "NonexistentField" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });
            var result = CreateController().Query(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "不在白名單的維度欄位應回傳 400");
        }

        // ─── Query happy path ─────────────────────────────────────────────────

        [TestMethod]
        public void Query_returns_aggregated_rows_for_valid_request()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "B", Amount = 200m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Category = "A", Amount = 300m },
            };

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = CreateController().Query(req) as JsonResult;
            Assert.IsNotNull(result);

            var response = result.Value as AnalysisQueryResponse;
            Assert.IsNotNull(response);
            Assert.AreEqual(2, response.Rows.Count, "應有 2 個地區分組");

            var huaDong = response.Rows.Single(r => r["Region"].ToString() == "華東");
            Assert.AreEqual(300m, Convert.ToDecimal(huaDong["Amount_Sum"]),
                "華東 Sum = 100+200 = 300");
        }

        [TestMethod]
        public void Query_empty_data_returns_200_with_no_rows()
        {
            // _testData 為空（Setup 已清空）
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = CreateController().Query(req) as JsonResult;
            Assert.IsNotNull(result);

            var response = result.Value as AnalysisQueryResponse;
            Assert.IsNotNull(response);
            Assert.AreEqual(0, response.Rows.Count);
            Assert.IsFalse(response.Truncated);
        }

        // ─── Export 路由守衛 ──────────────────────────────────────────────────

        [TestMethod]
        public void Export_returns_400_when_dimensions_exceed_3()
        {
            var req = Req(dims: new[] { "A", "B", "C", "D" });
            var result = CreateController().Export(req) as BadRequestObjectResult;
            Assert.IsNotNull(result);
        }

        [TestMethod]
        public void Export_returns_400_for_unregistered_vm()
        {
            var req = new AnalysisQueryRequest
            {
                ListVmType = "No.Such.Vm",
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest> { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } }
            };
            var result = CreateController().Export(req) as BadRequestObjectResult;
            Assert.IsNotNull(result);
        }

        // ─── Export xlsx / csv ────────────────────────────────────────────────

        [TestMethod]
        public void Export_xlsx_returns_xlsx_content_type_and_filename()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = CreateController().Export(req, "xlsx") as FileContentResult;
            Assert.IsNotNull(result);
            Assert.AreEqual(
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                result.ContentType,
                "xlsx MIME type 不正確");
            Assert.AreEqual("analysis.xlsx", result.FileDownloadName);
        }

        [TestMethod]
        public void Export_csv_returns_csv_content_type_and_filename()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = CreateController().Export(req, "csv") as FileContentResult;
            Assert.IsNotNull(result);
            Assert.AreEqual("text/csv", result.ContentType);
            Assert.AreEqual("analysis.csv", result.FileDownloadName);
        }

        [TestMethod]
        public void Export_csv_header_contains_correct_column_names()
        {
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = CreateController().Export(req, "csv") as FileContentResult;
            Assert.IsNotNull(result);

            var csv = Encoding.UTF8.GetString(result.FileContents);
            // 第一行為 header，應使用 display names（#495）
            var header = csv.Split('\n')[0].TrimEnd('\r');
            Assert.IsTrue(header.Contains("地區"), "header 應包含維度顯示名稱 '地區'");
            Assert.IsTrue(header.Contains("金額 合計"), "header 應包含量值顯示名稱 '金額 合計'");
        }

        // ─── CSV 公式注入防護 ──────────────────────────────────────────────────

        [TestMethod]
        public void Export_csv_escapes_formula_injection_in_dimension_value()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord
                {
                    ID       = Guid.NewGuid(),
                    Region   = "=HYPERLINK(\"http://evil.com\")",
                    Category = "A",
                    Amount   = 100m
                }
            };

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = CreateController().Export(req, "csv") as FileContentResult;
            Assert.IsNotNull(result);
            var csv = Encoding.UTF8.GetString(result.FileContents);

            // 公式字元開頭的值必須以 tab 前置，絕不能直接出現 ,=HYPERLINK
            Assert.IsFalse(csv.Contains(",=HYPERLINK"),
                "CSV 不應含未逸脫的 formula 值（,=HYPERLINK）");
            // Value is quoted then tab-prefixed: \t"=HYPERLINK(...)
            Assert.IsTrue(csv.Contains("\t\"=HYPERLINK") || csv.Contains("\t=HYPERLINK"),
                "危險值應以 tab 前置（\\t=HYPERLINK 或 \\t\"=HYPERLINK）");
        }

        [TestMethod]
        public void Export_csv_escapes_plus_sign_formula()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "+SUM(A1)", Category = "A", Amount = 50m }
            };

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = CreateController().Export(req, "csv") as FileContentResult;
            Assert.IsNotNull(result);
            var csv = Encoding.UTF8.GetString(result.FileContents);

            Assert.IsFalse(csv.Contains(",+SUM"), "CSV 不應含未逸脫的 +SUM formula");
            Assert.IsTrue(csv.Contains("\t+SUM"), "危險值應以 tab 前置");
        }

        // ─── XSS Payload 回歸保護 ─────────────────────────────────────────────
        //
        // CSV 是純文字格式，不解析 HTML。測試確認：
        //   1. XSS payload 不導致例外或崩潰
        //   2. 輸出中 payload 以原始文字保留（不 HTML-encode 也不截斷）
        //   3. 不以 formula 字元開頭（<script> 不是 =,+,-,@ 所以不觸發公式轉義）

        [TestMethod]
        public void Export_csv_with_xss_payload_in_dimension_value_does_not_throw()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord
                {
                    ID       = Guid.NewGuid(),
                    Region   = "<script>alert(1)</script>",
                    Category = "A",
                    Amount   = 100m
                }
            };

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = CreateController().Export(req, "csv") as FileContentResult;

            Assert.IsNotNull(result, "Export should not throw with XSS payload in dimension value");
            var csv = System.Text.Encoding.UTF8.GetString(result.FileContents).TrimStart('\xEF', '\xBB', '\xBF');

            // payload 應以明文保留（CSV 不解析 HTML）
            Assert.IsTrue(csv.Contains("<script>"),
                "XSS payload should be preserved as plain text in CSV");
            // 不應被 HTML-encode（避免雙重逸脫）
            Assert.IsFalse(csv.Contains("&lt;script&gt;"),
                "CSV exporter must not HTML-encode cell values");
        }

        [TestMethod]
        public void Export_csv_with_null_byte_in_dimension_value_does_not_throw()
        {
            // null byte (\0) 在維度值中不應造成崩潰
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "A\0B", Category = "X", Amount = 50m }
            };

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            // Should not throw
            var result = CreateController().Export(req, "csv") as FileContentResult;

            Assert.IsNotNull(result, "Export should not crash on null byte in dimension value");
        }

        // ─── DimensionHierarchies 驗證 ─────────────────────────────────────────

        [TestMethod]
        public void Query_with_valid_DimensionHierarchies_on_date_field_returns_200()
        {
            _orderData = new List<OrderRecord>
            {
                new OrderRecord { ID = Guid.NewGuid(), OrderDate = new DateTime(2026, 1, 15), Region = "華東", Amount = 100m }
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(OrderRecordListVM).FullName,
                Dimensions = new List<string> { "OrderDate" },
                Measures = new List<MeasureRequest> { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                DimensionHierarchies = new Dictionary<string, DateHierarchy>
                {
                    ["OrderDate"] = DateHierarchy.Month
                }
            };

            var result = CreateController().Query(req) as JsonResult;
            Assert.IsNotNull(result, "帶有效 DimensionHierarchies 的查詢應回傳 200");
        }

        [TestMethod]
        public void Query_with_DimensionHierarchies_on_non_date_field_returns_400()
        {
            _orderData = new List<OrderRecord>
            {
                new OrderRecord { ID = Guid.NewGuid(), OrderDate = new DateTime(2026, 1, 15), Region = "華東", Amount = 100m }
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(OrderRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest> { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                DimensionHierarchies = new Dictionary<string, DateHierarchy>
                {
                    ["Region"] = DateHierarchy.Month
                }
            };

            var result = CreateController().Query(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "非日期欄位設定 DimensionHierarchies 應回傳 400");
            Assert.IsTrue(result.Value.ToString().Contains("not a date dimension"),
                "錯誤訊息應包含 'not a date dimension'");
        }

        [TestMethod]
        public void Query_without_DimensionHierarchies_works_as_before()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m }
            };

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });
            // DimensionHierarchies is null by default — backward compat
            Assert.IsNull(req.DimensionHierarchies);

            var result = CreateController().Query(req) as JsonResult;
            Assert.IsNotNull(result, "不帶 DimensionHierarchies 的查詢應向下相容回傳 200");
        }

        [TestMethod]
        public void GetMeta_returns_IsDate_and_Hierarchy_for_date_fields()
        {
            var controller = CreateController();
            var result = controller.GetMeta(typeof(OrderRecordListVM).FullName) as OkObjectResult;
            Assert.IsNotNull(result);

            var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
            // OrderDate 應有 IsDate=true 和 Hierarchy=Month
            Assert.IsTrue(json.Contains("\"isDate\":true"), "日期欄位應有 isDate=true");
            Assert.IsTrue(json.Contains("\"hierarchy\":\"Month\""), "日期欄位應有 hierarchy=Month");

            // Region 應有 IsDate=false
            Assert.IsTrue(json.Contains("\"isDate\":false"), "非日期欄位應有 isDate=false");
        }

        // ─── SearcherFormData 日期格式解析（Fixes #264） ─────────────────────

        [DataTestMethod]
        [DataRow("{\"StartDate\":\"2025/10/01\"}", "slash format")]
        [DataRow("{\"StartDate\":\"2025-10-01\"}", "ISO short")]
        [DataRow("{\"StartDate\":\"2025-10-01 14:30:00\"}", "ISO with time")]
        [DataRow("{\"StartDate\":\"2025/10/01 14:30\"}", "slash with time")]
        [DataRow("{\"StartDate\":\"2025.10.01\"}", "dot format")]
        public void Query_accepts_various_date_formats_in_SearcherFormData(string json, string label)
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m }
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(DateSearchListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest> { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                SearcherFormData = json
            };

            var result = CreateController().Query(req);
            Assert.IsNotInstanceOfType(result, typeof(BadRequestObjectResult),
                $"日期格式 '{label}' 應被接受，不應回傳 400");
        }

        [TestMethod]
        public void Query_returns_400_for_invalid_date_in_SearcherFormData()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m }
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(DateSearchListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest> { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                SearcherFormData = "{\"StartDate\":\"not-a-date\"}"
            };

            var result = CreateController().Query(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "無效日期格式應回傳 400");
        }

        // ─── SearcherFormData nullable enum 反序列化（Fixes #471）──────────────

        [DataTestMethod]
        [DataRow("{\"payment\":\"1\"}", "numeric string")]
        [DataRow("{\"payment\":\"Card\"}", "enum name string")]
        [DataRow("{\"payment\":1}",       "numeric literal")]
        public void Query_accepts_nullable_enum_in_SearcherFormData(string json, string label)
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m }
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(EnumSearchListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest> { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                SearcherFormData = json
            };

            var result = CreateController().Query(req);
            Assert.IsNotInstanceOfType(result, typeof(BadRequestObjectResult),
                $"nullable enum 格式 '{label}' 應被接受，不應回傳 400");
        }

        [TestMethod]
        public void Query_accepts_absent_nullable_enum_in_SearcherFormData()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m }
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(EnumSearchListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures = new List<MeasureRequest> { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                SearcherFormData = "{}" // 空搜尋條件（前端未選擇 enum dropdown）
            };

            var result = CreateController().Query(req);
            Assert.IsNotInstanceOfType(result, typeof(BadRequestObjectResult),
                "未選擇 enum 時應正常執行，不應回傳 400");
        }

        // ─── Null request guard (#268) ──────────────────────────────────────

        [TestMethod]
        public void Query_null_request_returns_400()
        {
            var result = CreateController().Query(null) as BadRequestObjectResult;
            Assert.IsNotNull(result, "null request 應回傳 400");
        }

        // ─── Export filter 正向測試 (#427) ──────────────────────────────────────

        [TestMethod]
        public void Export_csv_with_filter_returns_only_matching_rows()
        {
            // Arrange — 3 regions, only 華東 matches filter
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華北", Amount = 200m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Amount = 300m },
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new FilterCondition { Field = "Region", Operator = FilterOperator.Eq, Value = "華東" }
                }
            };

            var result = CreateController().Export(req, "csv") as FileContentResult;
            Assert.IsNotNull(result, "帶 filter 的匯出應回傳 csv 檔案");

            var csv = Encoding.UTF8.GetString(result.FileContents);
            var dataLines = csv.Split('\n')
                .Skip(1)             // skip header
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            Assert.AreEqual(1, dataLines.Count,
                "filter Region=華東 應只匯出 1 個資料列（非全量 3 列）");
            Assert.IsTrue(dataLines[0].Contains("華東"),
                "匯出的資料列應包含 '華東'");
        }

        [TestMethod]
        public void Export_xlsx_with_filter_returns_only_matching_rows()
        {
            // Arrange — 2 categories, only A matches filter
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 150m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華北", Category = "B", Amount = 200m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Category = "B", Amount = 250m },
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Category" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new FilterCondition { Field = "Category", Operator = FilterOperator.Eq, Value = "A" }
                }
            };

            var result = CreateController().Export(req, "xlsx") as FileContentResult;
            Assert.IsNotNull(result, "帶 filter 的 xlsx 匯出應回傳檔案");

            using var ms = new MemoryStream(result.FileContents);
            var wb = new XSSFWorkbook(ms);
            var sheet = wb.GetSheetAt(0);
            // Row 0 = header; filter → only 1 data row
            Assert.AreEqual(1, sheet.LastRowNum,
                "filter Category=A 應只匯出 1 個資料列（LastRowNum=1，即 header + 1 data）");
            Assert.AreEqual("A", sheet.GetRow(1).GetCell(0).StringCellValue,
                "第一個資料儲存格應為 Category='A'");
        }

        [TestMethod]
        public void Export_null_request_returns_400()
        {
            var result = CreateController().Export(null) as BadRequestObjectResult;
            Assert.IsNotNull(result, "null request 應回傳 400");
        }

        // ─── Export truncated header (#291) ───────────────────────────────────

        [TestMethod]
        public void Export_xlsx_sets_truncated_header_when_over_10000_rows()
        {
            // Inject 10,001 rows with unique Region values so GROUP BY produces
            // 10,001 distinct groups — exceeding MaxRows (10,000)
            _testData = Enumerable.Range(1, 10_001)
                .Select(i => new SaleRecord { Region = "R" + i, Amount = i })
                .ToList();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var httpCtx = new DefaultHttpContext();
            var controller = new _AnalysisController(_registry, Microsoft.Extensions.Logging.Abstractions.NullLogger<_AnalysisController>.Instance, null, null);
            controller.Wtm = MockWtmContext.CreateWtmContext();
            controller.ControllerContext = new ControllerContext { HttpContext = httpCtx };

            var result = controller.Export(req, "xlsx") as FileContentResult;
            Assert.IsNotNull(result, "應回傳 xlsx 檔案");
            Assert.AreEqual("true", httpCtx.Response.Headers["X-Analysis-Truncated"].ToString(),
                "截斷時應設置 X-Analysis-Truncated: true header");
        }

        [TestMethod]
        public void Export_xlsx_no_truncated_header_when_under_limit()
        {
            _testData = Enumerable.Range(1, 5)
                .Select(i => new SaleRecord { Region = "R" + i, Amount = i * 100 })
                .ToList();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var httpCtx = new DefaultHttpContext();
            var controller = new _AnalysisController(_registry, Microsoft.Extensions.Logging.Abstractions.NullLogger<_AnalysisController>.Instance, null, null);
            controller.Wtm = MockWtmContext.CreateWtmContext();
            controller.ControllerContext = new ControllerContext { HttpContext = httpCtx };

            var result = controller.Export(req, "xlsx") as FileContentResult;
            Assert.IsNotNull(result);
            Assert.IsFalse(httpCtx.Response.Headers.ContainsKey("X-Analysis-Truncated"),
                "未截斷時不應設置 X-Analysis-Truncated header");
        }

        // ─── 零個 measures 驗證（#296） ─────────────────────────────────────

        [TestMethod]
        public void Query_with_zero_measures_returns_400()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Amount = 100m }
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>()   // 空陣列
            };

            var result = CreateController().Query(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "空 measures 陣列應回傳 400");
            Assert.IsTrue(result.Value?.ToString()?.Contains("度量指標") == true,
                "錯誤訊息應包含「度量指標」");
        }

        [TestMethod]
        public void Export_with_zero_measures_returns_400()
        {
            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>()
            };

            var result = CreateController().Export(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "Export 空 measures 陣列應回傳 400");
            Assert.IsTrue(result.Value?.ToString()?.Contains("度量指標") == true,
                "錯誤訊息應包含「度量指標」");
        }

        // ─── GetMeta 欄位結構驗證（#306） ─────────────────────────────────────

        [TestMethod]
        [TestCategory("Analysis")]
        public void GetMeta_returns_correct_field_count_and_kinds()
        {
            var result = CreateController().GetMeta(typeof(SaleRecordListVM).FullName) as OkObjectResult;
            Assert.IsNotNull(result);

            var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
            // SaleRecord 有 2 個 Dimension（Region, Category）+ 1 個 Measure（Amount）
            // 驗證 kind 值正確
            Assert.IsTrue(json.Contains("\"kind\":\"Dimension\""), "應有 Dimension 欄位");
            Assert.IsTrue(json.Contains("\"kind\":\"Measure\""),   "應有 Measure 欄位");
            // Measure 應有 allowedFuncs
            Assert.IsTrue(json.Contains("\"allowedFuncs\""), "Measure 應包含 allowedFuncs");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void GetMeta_measure_field_has_non_empty_allowedFuncs()
        {
            var result = CreateController().GetMeta(typeof(SaleRecordListVM).FullName) as OkObjectResult;
            Assert.IsNotNull(result);

            var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
            // Amount 允許 Sum, Count, Avg, Max, Min → 至少要包含 "Sum"
            Assert.IsTrue(json.Contains("\"Sum\""), "Amount 的 allowedFuncs 應包含 Sum");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void GetMeta_dimension_fields_have_empty_allowedFuncs()
        {
            var result = CreateController().GetMeta(typeof(SaleRecordListVM).FullName) as OkObjectResult;
            Assert.IsNotNull(result);

            var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
            // Dimension 的 allowedFuncs 應為空陣列 []
            Assert.IsTrue(json.Contains("\"allowedFuncs\":[]"),
                "Dimension 欄位的 allowedFuncs 應為空陣列");
        }

        // ─── Multi-dimension + multi-measure happy path（#306） ───────────────

        [TestMethod]
        [TestCategory("Analysis")]
        public void Query_happy_path_returns_200_with_aggregated_rows()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 200m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Category = "B", Amount = 300m },
            };

            // 2 dimensions × 2 measures
            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region", "Category" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum },
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Count }
                }
            };

            var result = CreateController().Query(req) as JsonResult;
            Assert.IsNotNull(result, "Query 2D×2M 應回傳 200");

            var response = result.Value as AnalysisQueryResponse;
            Assert.IsNotNull(response);
            // 華東-A, 華南-B → 2 個分組
            Assert.AreEqual(2, response.Rows.Count, "應有 2 個分組");

            var huaDongA = response.Rows.Single(r =>
                r["Region"].ToString() == "華東" && r["Category"].ToString() == "A");
            Assert.AreEqual(300m, Convert.ToDecimal(huaDongA["Amount_Sum"]),
                "華東-A Sum = 100+200 = 300");
            Assert.AreEqual(2m, Convert.ToDecimal(huaDongA["Amount_Count"]),
                "華東-A Count = 2");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void Query_three_dimensions_three_measures_returns_200()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Category = "B", Amount = 200m },
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region", "Category", "Region" }, // 允許重複
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum },
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Count },
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Avg }
                }
            };

            var result = CreateController().Query(req);
            // 3D×3M 不超限制，不應回傳 400 路由守衛錯誤
            Assert.IsNotInstanceOfType(result, typeof(BadRequestObjectResult),
                "3D×3M 不應回傳 400（路由守衛）");
        }

        // ─── Query 帶 filter 條件的 happy path（#306） ───────────────────────

        [TestMethod]
        [TestCategory("Analysis")]
        public void Query_with_eq_filter_returns_filtered_result()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Category = "B", Amount = 200m },
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new FilterCondition { Field = "Region", Operator = FilterOperator.Eq, Value = "華東" }
                }
            };

            var result = CreateController().Query(req) as JsonResult;
            Assert.IsNotNull(result, "帶 Eq filter 的查詢應回傳 200");

            var response = result.Value as AnalysisQueryResponse;
            Assert.IsNotNull(response);
            Assert.AreEqual(1, response.Rows.Count, "Eq filter 後應只有 1 個地區");
            Assert.AreEqual("華東", response.Rows[0]["Region"].ToString());
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void Query_with_multiple_filter_conditions_AND_logic_returns_narrowed_result()
        {
            // Arrange — 3 records; only 華東+Amount>150 (i.e. 華東 B=200) should survive AND
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "B", Amount = 200m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Category = "A", Amount = 300m },
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new FilterCondition { Field = "Region", Operator = FilterOperator.Eq, Value = "華東" },
                    new FilterCondition { Field = "Amount", Operator = FilterOperator.Gt, Value = "150" }
                }
            };

            var result = CreateController().Query(req) as JsonResult;
            Assert.IsNotNull(result, "多條件 AND filter 應回傳 200");

            var response = result.Value as AnalysisQueryResponse;
            Assert.IsNotNull(response);
            Assert.AreEqual(1, response.Rows.Count, "AND: Region=華東 AND Amount>150 → 只剩 1 列");
            Assert.AreEqual(200m, Convert.ToDecimal(response.Rows[0]["Amount_Sum"]));
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void Query_filter_with_non_whitelist_field_returns_400()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Amount = 100m }
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new FilterCondition { Field = "NonExistentField", Operator = FilterOperator.Eq, Value = "X" }
                }
            };

            var result = CreateController().Query(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "filter 帶非白名單欄位應回傳 400");
        }

        // ─── 空 dimensions array 行為（#306） ────────────────────────────────

        [TestMethod]
        [TestCategory("Analysis")]
        public void Query_empty_dimensions_returns_400()
        {
            // #348: 後端與前端 validateSelection 一致，零維度應回傳 400
            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string>(),   // 零維度
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                }
            };

            var result = CreateController().Query(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "零維度查詢應回傳 400");
        }

        // ─── DimensionHierarchies key 不存在欄位（#306） ─────────────────────

        [TestMethod]
        [TestCategory("Analysis")]
        public void Query_DimensionHierarchies_with_nonexistent_field_returns_400()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Amount = 100m }
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                DimensionHierarchies = new Dictionary<string, DateHierarchy>
                {
                    ["NonExistentField"] = DateHierarchy.Month
                }
            };

            var result = CreateController().Query(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "DimensionHierarchies 指向不存在欄位應回傳 400");
            Assert.IsTrue(result.Value?.ToString()?.Contains("NonExistentField") == true,
                "錯誤訊息應包含不存在的欄位名");
        }

        // ─── CSV formula injection 補充（@, - 字元）（#306） ─────────────────

        [TestMethod]
        [TestCategory("Analysis")]
        public void Export_csv_escapes_at_sign_formula()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "@SUM(A1)", Category = "A", Amount = 50m }
            };

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = CreateController().Export(req, "csv") as FileContentResult;
            Assert.IsNotNull(result);
            var csv = Encoding.UTF8.GetString(result.FileContents);

            Assert.IsFalse(csv.Contains(",@SUM"), "CSV 不應含未逸脫的 @SUM formula");
            Assert.IsTrue(csv.Contains("\t@SUM"), "@ 開頭的危險值應以 tab 前置");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void Export_csv_escapes_minus_sign_formula()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "-1+2", Category = "A", Amount = 50m }
            };

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = CreateController().Export(req, "csv") as FileContentResult;
            Assert.IsNotNull(result);
            var csv = Encoding.UTF8.GetString(result.FileContents);

            Assert.IsFalse(csv.Contains(",-1+2"), "CSV 不應含未逸脫的 - 開頭 formula");
            Assert.IsTrue(csv.Contains("\t-1+2"), "- 開頭的危險值應以 tab 前置");
        }

        // ─── Export CSV 截斷 header（#291, #306） ─────────────────────────────

        [TestMethod]
        [TestCategory("Analysis")]
        public void Export_csv_sets_truncated_header_when_over_10000_rows()
        {
            _testData = Enumerable.Range(1, 10_001)
                .Select(i => new SaleRecord { Region = "R" + i, Amount = i })
                .ToList();

            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var httpCtx = new DefaultHttpContext();
            var controller = new _AnalysisController(_registry, Microsoft.Extensions.Logging.Abstractions.NullLogger<_AnalysisController>.Instance, null, null);
            controller.Wtm = MockWtmContext.CreateWtmContext();
            controller.ControllerContext = new ControllerContext { HttpContext = httpCtx };

            var result = controller.Export(req, "csv") as FileContentResult;
            Assert.IsNotNull(result, "應回傳 csv 檔案");
            Assert.AreEqual("text/csv", result.ContentType);
            Assert.AreEqual("true", httpCtx.Response.Headers["X-Analysis-Truncated"].ToString(),
                "CSV 截斷時應設置 X-Analysis-Truncated: true header");
        }

        // ─── Unicode 特殊字元在 filter value（#306） ─────────────────────────

        [TestMethod]
        [TestCategory("Analysis")]
        public void Query_filter_with_unicode_value_works_correctly()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "東南亞🌏", Category = "A", Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "歐洲€", Category = "B", Amount = 200m },
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new FilterCondition { Field = "Region", Operator = FilterOperator.Eq, Value = "東南亞🌏" }
                }
            };

            var result = CreateController().Query(req) as JsonResult;
            Assert.IsNotNull(result, "Unicode filter value 應正常查詢不拋例外");

            var response = result.Value as AnalysisQueryResponse;
            Assert.IsNotNull(response);
            Assert.AreEqual(1, response.Rows.Count, "應只有 1 個匹配結果");
            Assert.AreEqual("東南亞🌏", response.Rows[0]["Region"].ToString());
        }

        // ─── Pivot 端點 Controller 層測試（#306） ─────────────────────────────

        [TestMethod]
        [TestCategory("Analysis")]
        public void Pivot_returns_400_when_measures_is_empty()
        {
            var req = new AnalysisPivotRequest
            {
                ListVmType      = typeof(SaleRecordListVM).FullName,
                Dimensions      = new List<string> { "Region", "Category" },
                Measures        = new List<MeasureRequest>(),
                PivotDimension  = "Category"
            };

            var result = CreateController().Pivot(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "Pivot 空 measures 應回傳 400");
            Assert.IsTrue(result.Value?.ToString()?.Contains("度量指標") == true,
                "Pivot 400 錯誤訊息應包含「度量指標」");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void Pivot_returns_400_when_measures_exceed_3()
        {
            var req = new AnalysisPivotRequest
            {
                ListVmType     = typeof(SaleRecordListVM).FullName,
                Dimensions     = new List<string> { "Region", "Category" },
                Measures       = Enumerable.Range(0, 4)
                    .Select(_ => new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum })
                    .ToList(),
                PivotDimension = "Category"
            };

            var result = CreateController().Pivot(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "Pivot 超過 3 個 measures 應回傳 400");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void Pivot_returns_400_when_PivotDimension_is_empty()
        {
            var req = new AnalysisPivotRequest
            {
                ListVmType     = typeof(SaleRecordListVM).FullName,
                Dimensions     = new List<string> { "Region", "Category" },
                Measures       = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                PivotDimension = ""  // 空字串
            };

            var result = CreateController().Pivot(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "Pivot 空 PivotDimension 應回傳 400");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void Pivot_returns_400_for_unregistered_vm()
        {
            var req = new AnalysisPivotRequest
            {
                ListVmType     = "No.Such.Vm",
                Dimensions     = new List<string> { "Region", "Category" },
                Measures       = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                PivotDimension = "Category"
            };

            var result = CreateController().Pivot(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "Pivot 未註冊 VM 應回傳 400");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void Pivot_happy_path_returns_200_with_pivoted_rows()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "B", Amount = 200m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Category = "A", Amount = 300m },
            };

            var req = new AnalysisPivotRequest
            {
                ListVmType     = typeof(SaleRecordListVM).FullName,
                Dimensions     = new List<string> { "Region", "Category" },
                Measures       = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                PivotDimension = "Category"
            };

            var result = CreateController().Pivot(req) as JsonResult;
            Assert.IsNotNull(result, "Pivot happy path 應回傳 200");

            var response = result.Value as AnalysisPivotResponse;
            Assert.IsNotNull(response, "Pivot 回傳值應為 AnalysisPivotResponse");
            // Row dims: Region; pivot values: A, B（sorted）
            Assert.AreEqual(2, response.Rows.Count, "應有 2 個地區列");
            Assert.IsTrue(response.PivotValues.Contains("A"), "Pivot values 應包含 A");
            Assert.IsTrue(response.PivotValues.Contains("B"), "Pivot values 應包含 B");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void PivotExport_xlsx_returns_correct_content_type()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Category = "B", Amount = 200m },
            };

            var req = new AnalysisPivotRequest
            {
                ListVmType     = typeof(SaleRecordListVM).FullName,
                Dimensions     = new List<string> { "Region", "Category" },
                Measures       = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                PivotDimension = "Category"
            };

            var result = CreateController().PivotExport(req, "xlsx") as FileContentResult;
            Assert.IsNotNull(result, "PivotExport xlsx 應回傳檔案");
            Assert.AreEqual(
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                result.ContentType, "xlsx MIME type 不正確");
            Assert.AreEqual("analysis_pivot.xlsx", result.FileDownloadName);
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void PivotExport_csv_returns_correct_content_type()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Category = "B", Amount = 200m },
            };

            var req = new AnalysisPivotRequest
            {
                ListVmType     = typeof(SaleRecordListVM).FullName,
                Dimensions     = new List<string> { "Region", "Category" },
                Measures       = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                PivotDimension = "Category"
            };

            var result = CreateController().PivotExport(req, "csv") as FileContentResult;
            Assert.IsNotNull(result, "PivotExport csv 應回傳檔案");
            Assert.AreEqual("text/csv", result.ContentType);
            Assert.AreEqual("analysis_pivot.csv", result.FileDownloadName);
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void PivotExport_returns_400_for_unregistered_vm()
        {
            var req = new AnalysisPivotRequest
            {
                ListVmType     = "No.Such.Vm",
                Dimensions     = new List<string> { "Region", "Category" },
                Measures       = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                PivotDimension = "Category"
            };

            var result = CreateController().PivotExport(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "PivotExport 未註冊 VM 應回傳 400");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void PivotExport_returns_400_when_zero_measures()
        {
            var req = new AnalysisPivotRequest
            {
                ListVmType     = typeof(SaleRecordListVM).FullName,
                Dimensions     = new List<string> { "Region", "Category" },
                Measures       = new List<MeasureRequest>(),
                PivotDimension = "Category"
            };

            var result = CreateController().PivotExport(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "PivotExport 空 measures 應回傳 400");
        }

        // ─── #348: 零維度驗證（Export / Pivot / PivotExport）──────────────────

        [TestMethod]
        [TestCategory("Analysis")]
        public void Export_empty_dimensions_returns_400()
        {
            // #348: Export 端點應與 Query 一致，零維度回傳 400
            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string>(),
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                }
            };

            var result = CreateController().Export(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "Export 零維度應回傳 400");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void Pivot_empty_dimensions_returns_400()
        {
            // #348: Pivot 端點零維度應回傳 400
            var req = new AnalysisPivotRequest
            {
                ListVmType     = typeof(SaleRecordListVM).FullName,
                Dimensions     = new List<string>(),
                Measures       = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                PivotDimension = "Region"
            };

            var result = CreateController().Pivot(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "Pivot 零維度應回傳 400");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void PivotExport_empty_dimensions_returns_400()
        {
            // #348: PivotExport 端點零維度應回傳 400
            var req = new AnalysisPivotRequest
            {
                ListVmType     = typeof(SaleRecordListVM).FullName,
                Dimensions     = new List<string>(),
                Measures       = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                PivotDimension = "Region"
            };

            var result = CreateController().PivotExport(req) as BadRequestObjectResult;
            Assert.IsNotNull(result, "PivotExport 零維度應回傳 400");
        }

        // ─── Regression: duplicate dimensions (#345) ──────────────────────────

        /// <summary>
        /// Regression (#345): Query 端點收到重複維度 (["Region","Region"]) 時，
        /// 不應回傳 500 或靜默錯誤資料。
        /// 允許的行為：回傳 400（明確拒絕）或回傳 200 且結果一致（等同去重後的查詢）。
        /// </summary>
        [TestMethod]
        [TestCategory("Analysis")]
        public void Query_with_duplicate_dimensions_does_not_return_500()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Category = "B", Amount = 200m },
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region", "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                }
            };

            var result = CreateController().Query(req);

            // Must be either 200 (JsonResult) or 400 (BadRequest) — NOT 500
            Assert.IsTrue(
                result is JsonResult || result is BadRequestObjectResult,
                $"重複維度查詢應回傳 200 或 400，實際型別：{result?.GetType().Name}");

            // If 200: result must have at most 2 rows (no cartesian explosion)
            if (result is JsonResult jsonResult)
            {
                var response = jsonResult.Value as AnalysisQueryResponse;
                Assert.IsNotNull(response);
                Assert.IsTrue(response.Rows.Count <= 2,
                    $"重複維度不應造成笛卡兒乘積，實際 {response.Rows.Count} 列");
            }
        }

        /// <summary>
        /// Regression (#345): Export 端點收到重複維度時不應回傳 500。
        /// </summary>
        [TestMethod]
        [TestCategory("Analysis")]
        public void Export_with_duplicate_dimensions_does_not_return_500()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region", "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                }
            };

            var result = CreateController().Export(req, "csv");

            Assert.IsTrue(
                result is FileContentResult || result is BadRequestObjectResult,
                $"重複維度 Export 應回傳 200 或 400，實際型別：{result?.GetType().Name}");
        }

        // ─── Regression: ValidateFields with duplicate dimensions (#345) ───────

        /// <summary>
        /// Regression (#345): ValidateFields 本身不拒絕重複維度（每個名稱都在白名單中）。
        /// 驗證引擎不會因 GroupBy 時的 Dictionary key 衝突而崩潰。
        /// </summary>
        [TestMethod]
        [TestCategory("Analysis")]
        public void Query_duplicate_valid_dimension_fields_returns_consistent_data()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "B", Amount = 200m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Category = "A", Amount = 300m },
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Category", "Category" },   // duplicate
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Count }
                }
            };

            IActionResult result = null;
            try
            {
                result = CreateController().Query(req);
            }
            catch (Exception ex)
            {
                Assert.Fail($"重複維度不應拋未攔截例外到 Controller 層：{ex.GetType().Name}: {ex.Message}");
            }

            Assert.IsNotNull(result, "Controller 應回傳非 null 結果");
            Assert.IsTrue(
                result is JsonResult || result is BadRequestObjectResult,
                $"應回傳 JsonResult 或 BadRequestObjectResult，實際：{result.GetType().Name}");

            if (result is JsonResult jr)
            {
                var resp = jr.Value as AnalysisQueryResponse;
                Assert.IsNotNull(resp);
                // Category 有 A、B 兩個分組，重複維度不應造成超過 2 個分組
                Assert.IsTrue(resp.Rows.Count <= 2,
                    $"重複 Category 維度不應超過 2 個分組，實際：{resp.Rows.Count}");
            }
        }

        // ─── Pivot / PivotExport 守衛測試 ──────────────────────────────────────────
        //
        // Query 和 Export 已有 null → 400 及欄位白名單測試，但 Pivot/PivotExport 缺失同等覆蓋。

        private static AnalysisPivotRequest PivotReq(
            string[] dims,
            string pivotDim,
            (string field, AggregateFunc func)[] msrs = null)
            => new AnalysisPivotRequest
            {
                ListVmType     = typeof(SaleRecordListVM).FullName,
                Dimensions     = dims?.ToList() ?? new List<string>(),
                PivotDimension = pivotDim,
                Measures       = msrs?.Select(m => new MeasureRequest { Field = m.field, Func = m.func }).ToList()
                                 ?? new List<MeasureRequest>()
            };

        [TestMethod]
        public void Pivot_null_request_returns_400()
        {
            var result = CreateController().Pivot(null);
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "Pivot null body 應回傳 400");
        }

        [TestMethod]
        public void PivotExport_null_request_returns_400()
        {
            var result = CreateController().PivotExport(null);
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "PivotExport null body 應回傳 400");
        }

        [TestMethod]
        public void Pivot_returns_400_when_PivotDimension_not_in_dimensions_list()
        {
            // Region 在白名單，但 PivotDimension = "Category" 不在所選 Dimensions 中
            var req = PivotReq(
                dims:     new[] { "Region" },
                pivotDim: "Category",
                msrs:     new[] { ("Amount", AggregateFunc.Sum) });
            var result = CreateController().Pivot(req);
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "PivotDimension 不在 Dimensions 清單中應回傳 400");
        }

        [TestMethod]
        public void Pivot_returns_400_for_invalid_pivot_dimension_field()
        {
            // "InvalidField" 不在模型白名單
            var req = PivotReq(
                dims:     new[] { "InvalidField" },
                pivotDim: "InvalidField",
                msrs:     new[] { ("Amount", AggregateFunc.Sum) });
            var result = CreateController().Pivot(req);
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "不在白名單中的維度欄位應回傳 400");
        }

        [TestMethod]
        public void PivotExport_returns_400_when_PivotDimension_not_in_dimensions_list()
        {
            var req = PivotReq(
                dims:     new[] { "Region" },
                pivotDim: "Category",
                msrs:     new[] { ("Amount", AggregateFunc.Sum) });
            var result = CreateController().PivotExport(req);
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "PivotExport：PivotDimension 不在 Dimensions 清單中應回傳 400");
        }

        [TestMethod]
        public void PivotExport_returns_400_for_invalid_dimension_field()
        {
            var req = PivotReq(
                dims:     new[] { "InvalidField" },
                pivotDim: "InvalidField",
                msrs:     new[] { ("Amount", AggregateFunc.Sum) });
            var result = CreateController().PivotExport(req);
            Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult),
                "PivotExport：不在白名單中的維度欄位應回傳 400");
        }

        // ─── CSV UTF-8 BOM 迴歸測試 ───────────────────────────────────────────

        [TestMethod]
        [TestCategory("Analysis")]
        public void Export_csv_starts_with_utf8_bom()
        {
            // Windows Excel 需要 UTF-8 BOM（EF BB BF）才能正確識別 UTF-8 編碼，
            // 否則中文欄位會顯示亂碼。此測試確保 BOM 不被意外移除。
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = CreateController().Export(req, "csv") as FileContentResult;
            Assert.IsNotNull(result);

            // UTF-8 BOM = EF BB BF
            Assert.IsTrue(result.FileContents.Length >= 3, "CSV 內容不應為空");
            Assert.AreEqual(0xEF, result.FileContents[0], "第 1 byte 應為 BOM EF");
            Assert.AreEqual(0xBB, result.FileContents[1], "第 2 byte 應為 BOM BB");
            Assert.AreEqual(0xBF, result.FileContents[2], "第 3 byte 應為 BOM BF");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void Export_pivot_csv_starts_with_utf8_bom()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "華南", Category = "B", Amount = 200m },
            };

            var req = new AnalysisPivotRequest
            {
                ListVmType     = typeof(SaleRecordListVM).FullName,
                Dimensions     = new List<string> { "Region", "Category" },
                Measures       = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                PivotDimension = "Category"
            };

            var result = CreateController().PivotExport(req, "csv") as FileContentResult;
            Assert.IsNotNull(result, "PivotExport CSV 應回傳 FileContentResult");

            Assert.IsTrue(result.FileContents.Length >= 3, "Pivot CSV 內容不應為空");
            Assert.AreEqual(0xEF, result.FileContents[0], "第 1 byte 應為 BOM EF");
            Assert.AreEqual(0xBB, result.FileContents[1], "第 2 byte 應為 BOM BB");
            Assert.AreEqual(0xBF, result.FileContents[2], "第 3 byte 應為 BOM BF");
        }

        // ─── includeChart / chartType Controller 層參數傳遞 (#360) ──────────────

        /// <summary>
        /// Export?format=xlsx&includeChart=true — 驗證 includeChart 確實傳遞至 AnalysisExcelExporter。
        /// </summary>
        [TestMethod]
        [TestCategory("Analysis")]
        public void Export_xlsx_with_includeChart_true_contains_chart()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { Region = "North", Amount = 100m },
                new SaleRecord { Region = "South", Amount = 200m },
            };
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = CreateController().Export(req, "xlsx", includeChart: true, chartType: "bar") as FileContentResult;

            Assert.IsNotNull(result, "應回傳 xlsx FileContentResult");
            using var ms = new MemoryStream(result.FileContents);
            var wb = new XSSFWorkbook(ms);
            var sheet = wb.GetSheetAt(0) as XSSFSheet;
            Assert.IsNotNull(sheet);
            var drawing = sheet.GetDrawingPatriarch() as XSSFDrawing;
            Assert.IsNotNull(drawing, "includeChart=true 應在 xlsx 中嵌入 Drawing");
            Assert.IsTrue(drawing.GetCharts().Count >= 1, "Drawing 中應有至少 1 個 chart");
        }

        /// <summary>
        /// Export?format=xlsx&includeChart=true&chartType=pie — 驗證 chartType 確實傳遞至 AnalysisExcelExporter。
        /// </summary>
        [TestMethod]
        [TestCategory("Analysis")]
        public void Export_xlsx_with_chartType_pie_contains_chart()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { Region = "North", Amount = 100m },
                new SaleRecord { Region = "South", Amount = 200m },
            };
            var req = Req(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });

            var result = CreateController().Export(req, "xlsx", includeChart: true, chartType: "pie") as FileContentResult;

            Assert.IsNotNull(result, "應回傳 xlsx FileContentResult");
            using var ms = new MemoryStream(result.FileContents);
            var wb = new XSSFWorkbook(ms);
            var sheet = wb.GetSheetAt(0) as XSSFSheet;
            Assert.IsNotNull(sheet);
            var drawing = sheet.GetDrawingPatriarch() as XSSFDrawing;
            Assert.IsNotNull(drawing, "chartType=pie + includeChart=true 應在 xlsx 中嵌入 Drawing");
            Assert.IsTrue(drawing.GetCharts().Count >= 1);
        }

        /// <summary>
        /// PivotExport?format=xlsx&includeChart=true — 驗證 PivotExport 的 includeChart 確實傳遞。
        /// </summary>
        [TestMethod]
        [TestCategory("Analysis")]
        public void PivotExport_xlsx_with_includeChart_true_contains_chart()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { Region = "North", Category = "A", Amount = 100m },
                new SaleRecord { Region = "South", Category = "B", Amount = 200m },
            };
            var req = PivotReq(
                dims:     new[] { "Region", "Category" },
                pivotDim: "Category",
                msrs:     new[] { ("Amount", AggregateFunc.Sum) });

            var result = CreateController().PivotExport(req, "xlsx", includeChart: true, chartType: "bar") as FileContentResult;

            Assert.IsNotNull(result, "PivotExport 應回傳 xlsx FileContentResult");
            using var ms = new MemoryStream(result.FileContents);
            var wb = new XSSFWorkbook(ms);
            var sheet = wb.GetSheetAt(0) as XSSFSheet;
            Assert.IsNotNull(sheet);
            var drawing = sheet.GetDrawingPatriarch() as XSSFDrawing;
            Assert.IsNotNull(drawing, "PivotExport includeChart=true 應在 xlsx 中嵌入 Drawing");
            Assert.IsTrue(drawing.GetCharts().Count >= 1);
        }

        // ─── #377: SQL injection / XSS / 邊界安全測試 ───────────────────────────────

        [TestMethod]
        [TestCategory("Analysis")]
        public void Query_filter_value_with_sql_injection_pattern_does_not_throw()
        {
            // Expression Tree approach treats filter values as parameters, never raw SQL.
            // This test locks in that guarantee: a SQL injection string must not throw or 500.
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "North", Category = "A", Amount = 100m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "'; DROP TABLE OrderItems --", Category = "B", Amount = 200m },
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new FilterCondition { Field = "Region", Operator = FilterOperator.Eq, Value = "'; DROP TABLE OrderItems --" }
                },
            };

            // Must not throw; must return 200 with exactly 1 matching row
            var result = CreateController().Query(req) as JsonResult;
            Assert.IsNotNull(result, "SQL injection in filter value must not throw — should return 200");
            var response = result.Value as AnalysisQueryResponse;
            Assert.IsNotNull(response);
            Assert.AreEqual(1, response.Rows.Count, "Exactly 1 row matches the injection string as a literal value");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void Query_filter_value_with_xss_payload_does_not_throw()
        {
            // XSS payload as a filter value must be treated as a plain string.
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "<script>alert(1)</script>", Category = "A", Amount = 50m },
                new SaleRecord { ID = Guid.NewGuid(), Region = "Safe", Category = "B", Amount = 150m },
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
                Filters = new List<FilterCondition>
                {
                    new FilterCondition { Field = "Region", Operator = FilterOperator.Eq, Value = "<script>alert(1)</script>" }
                },
            };

            var result = CreateController().Query(req) as JsonResult;
            Assert.IsNotNull(result, "XSS payload in filter value must return 200");
            var response = result.Value as AnalysisQueryResponse;
            Assert.IsNotNull(response);
            Assert.AreEqual(1, response.Rows.Count);
            Assert.AreEqual("<script>alert(1)</script>", response.Rows[0]["Region"]?.ToString());
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void Export_csv_with_xss_payload_in_dimension_value_stores_verbatim()
        {
            // XSS payload in dimension value must appear verbatim in CSV (no HTML-encoding).
            // CSV is plain text — the browser/Excel parses it, not an HTML parser.
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "<script>alert(1)</script>", Category = "A", Amount = 100m },
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
            };

            var result = CreateController().Export(req, "csv") as FileContentResult;
            Assert.IsNotNull(result, "Export CSV should return FileContentResult");

            var raw = result.FileContents;
            int bom = (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) ? 3 : 0;
            var text = System.Text.Encoding.UTF8.GetString(raw, bom, raw.Length - bom);

            // The raw XSS string should appear in the CSV output — no HTML encoding
            Assert.IsTrue(text.Contains("<script>alert(1)</script>"),
                $"CSV should contain verbatim XSS string. Actual CSV:\n{text}");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void Export_xlsx_with_xss_payload_in_dimension_value_stored_as_plain_string()
        {
            // XSS payload in xlsx must be stored as a string cell with the literal value,
            // not HTML-encoded or interpreted as executable content.
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "<script>alert(1)</script>", Category = "A", Amount = 100m },
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
            };

            var result = CreateController().Export(req, "xlsx") as FileContentResult;
            Assert.IsNotNull(result, "Export xlsx should return FileContentResult");

            using var ms = new MemoryStream(result.FileContents);
            var wb = new XSSFWorkbook(ms);
            var sheet = wb.GetSheetAt(0);
            // Row 1 (index 1) is the first data row; column 0 is the Region dimension
            var cell = sheet.GetRow(1)?.GetCell(0);
            Assert.IsNotNull(cell, "Data row should exist");
            Assert.AreEqual(CellType.String, cell.CellType);
            Assert.AreEqual("<script>alert(1)</script>", cell.StringCellValue,
                "xlsx cell should store verbatim XSS string, not HTML-encoded");
        }


        // ─── CheckAccess RBAC 測試 ──────────────────────────────────────────────
        //
        // 驗證五個端點（GetMeta / Query / Pivot / Export / PivotExport）的 Forbid 路徑：
        //   - 缺少必要角色 → ForbidResult
        //   - Admin 繞過 AllowedRoles → 200
        //   - 持有正確角色 → 200

        /// <summary>角色限制 VM：只有 "Analyst" 可存取。</summary>
        [EnableAnalysis(AllowedRoles = "Analyst")]
        private class RestrictedSaleListVM : BasePagedListVM<SaleRecord, BaseSearcher>
        {
            public override IOrderedQueryable<SaleRecord> GetSearchQuery()
                => _testData.AsQueryable().OrderByDescending(x => x.ID);
        }

        private _AnalysisController CreateControllerWithRoles(params string[] roles)
        {
            var controller = CreateController();
            controller.Wtm.LoginUserInfo.Roles = roles
                .Select(r => new SimpleRole { RoleName = r })
                .ToList();
            return controller;
        }

        private static AnalysisQueryRequest RestrictedReq(
            string[] dims,
            (string field, AggregateFunc func)[] msrs = null)
            => new AnalysisQueryRequest
            {
                ListVmType = typeof(RestrictedSaleListVM).FullName,
                Dimensions = dims?.ToList() ?? new List<string>(),
                Measures   = msrs?.Select(m => new MeasureRequest { Field = m.field, Func = m.func }).ToList()
                             ?? new List<MeasureRequest>()
            };

        [TestMethod]
        public void GetMeta_returns_403_when_user_lacks_required_role()
        {
            var controller = CreateControllerWithRoles("Viewer");
            var result = controller.GetMeta(typeof(RestrictedSaleListVM).FullName);
            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "缺少 Analyst 角色應回傳 ForbidResult");
        }

        [TestMethod]
        public void GetMeta_returns_200_when_admin_bypasses_AllowedRoles()
        {
            var controller = CreateControllerWithRoles("Admin");
            var result = controller.GetMeta(typeof(RestrictedSaleListVM).FullName);
            Assert.IsInstanceOfType(result, typeof(OkObjectResult),
                "Admin 應繞過 AllowedRoles 限制");
        }

        [TestMethod]
        public void GetMeta_returns_200_when_user_has_required_role()
        {
            var controller = CreateControllerWithRoles("Analyst");
            var result = controller.GetMeta(typeof(RestrictedSaleListVM).FullName);
            Assert.IsInstanceOfType(result, typeof(OkObjectResult),
                "持有 Analyst 角色應成功存取");
        }

        [TestMethod]
        public void Query_returns_403_when_user_lacks_required_role()
        {
            var controller = CreateControllerWithRoles("Viewer");
            var req = RestrictedReq(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });
            var result = controller.Query(req);
            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "Query：缺少必要角色應回傳 ForbidResult");
        }

        [TestMethod]
        public void Pivot_returns_403_when_user_lacks_required_role()
        {
            var controller = CreateControllerWithRoles("Viewer");
            var req = new AnalysisPivotRequest
            {
                ListVmType     = typeof(RestrictedSaleListVM).FullName,
                Dimensions     = new List<string> { "Region", "Category" },
                Measures       = new List<MeasureRequest>
                                 { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                PivotDimension = "Category"
            };
            var result = controller.Pivot(req);
            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "Pivot：缺少必要角色應回傳 ForbidResult");
        }

        [TestMethod]
        public void Export_returns_403_when_user_lacks_required_role()
        {
            var controller = CreateControllerWithRoles("Viewer");
            var req = RestrictedReq(
                dims: new[] { "Region" },
                msrs: new[] { ("Amount", AggregateFunc.Sum) });
            var result = controller.Export(req);
            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "Export：缺少必要角色應回傳 ForbidResult");
        }

        [TestMethod]
        public void PivotExport_returns_403_when_user_lacks_required_role()
        {
            var controller = CreateControllerWithRoles("Viewer");
            var req = new AnalysisPivotRequest
            {
                ListVmType     = typeof(RestrictedSaleListVM).FullName,
                Dimensions     = new List<string> { "Region", "Category" },
                Measures       = new List<MeasureRequest>
                                 { new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum } },
                PivotDimension = "Category"
            };
            var result = controller.PivotExport(req);
            Assert.IsInstanceOfType(result, typeof(ForbidResult),
                "PivotExport：缺少必要角色應回傳 ForbidResult");
        }



        [TestMethod]
        [TestCategory("Analysis")]
        public void Export_csv_with_includeMetadata_true_prepends_metadata_rows()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
            };

            var result = CreateController().Export(req, "csv", includeMetadata: true) as FileContentResult;
            Assert.IsNotNull(result, "應回傳 FileContentResult");

            // Strip BOM
            var raw = result.FileContents;
            int bom = (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) ? 3 : 0;
            var text = System.Text.Encoding.UTF8.GetString(raw, bom, raw.Length - bom);
            var lines = text.Split('\n');

            Assert.IsTrue(lines[0].StartsWith("匯出時間,"), $"Line 0 應為匯出時間，實際：{lines[0]}");
            Assert.IsTrue(lines[1].StartsWith("QueryHash,"), $"Line 1 應為 QueryHash，實際：{lines[1]}");
            Assert.IsTrue(lines[2].StartsWith("資料筆數,"), $"Line 2 應為資料筆數，實際：{lines[2]}");
            Assert.IsTrue(lines[3].StartsWith("已截斷,"), $"Line 3 應為已截斷，實際：{lines[3]}");
            Assert.AreEqual("", lines[4].Trim(), $"Line 4 應為空行，實際：{lines[4]}");
            // Line 5 is column header — uses display names (#495)
            Assert.IsTrue(lines[5].Contains("地區"), $"Line 5 應包含維度顯示名稱 '地區'，實際：{lines[5]}");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void Export_csv_default_no_metadata_header_at_row0()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
            };

            var result = CreateController().Export(req, "csv") as FileContentResult;
            Assert.IsNotNull(result, "應回傳 FileContentResult");

            var raw = result.FileContents;
            int bom = (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) ? 3 : 0;
            var text = System.Text.Encoding.UTF8.GetString(raw, bom, raw.Length - bom);
            var lines = text.Split('\n');

            // Default — first line is the column header, uses display names (#495)
            Assert.IsTrue(lines[0].Contains("地區"), $"預設 CSV 第 0 行應為欄位顯示名稱標頭，實際：{lines[0]}");
        }

        [TestMethod]
        [TestCategory("Analysis")]
        public void Export_xlsx_with_includeMetadata_true_has_metadata_sheet()
        {
            _testData = new List<SaleRecord>
            {
                new SaleRecord { ID = Guid.NewGuid(), Region = "華東", Category = "A", Amount = 100m },
            };

            var req = new AnalysisQueryRequest
            {
                ListVmType = typeof(SaleRecordListVM).FullName,
                Dimensions = new List<string> { "Region" },
                Measures   = new List<MeasureRequest>
                {
                    new MeasureRequest { Field = "Amount", Func = AggregateFunc.Sum }
                },
            };

            var result = CreateController().Export(req, "xlsx", includeMetadata: true) as FileContentResult;
            Assert.IsNotNull(result, "應回傳 FileContentResult");

            using var ms = new MemoryStream(result.FileContents);
            var wb = new XSSFWorkbook(ms);
            Assert.AreEqual(2, wb.NumberOfSheets, "includeMetadata=true 應有 2 個工作表");
            var meta = wb.GetSheet("Metadata");
            Assert.IsNotNull(meta, "應有 Metadata 工作表");
            Assert.AreEqual("匯出時間", meta.GetRow(0).GetCell(0).StringCellValue);
        }
    }
}
