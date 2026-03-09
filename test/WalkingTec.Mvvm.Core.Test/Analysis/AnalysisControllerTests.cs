#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

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

        // ─── 基礎設施 ──────────────────────────────────────────────────────────

        private AnalysisVmRegistry _registry;

        [TestInitialize]
        public void Setup()
        {
            _testData = new List<SaleRecord>();
            _registry = new AnalysisVmRegistry();
            // 掃描本測試 assembly，會找到 SaleRecordListVM（帶 [EnableAnalysis]）
            _registry.Build(new[] { typeof(AnalysisControllerTests).Assembly });
        }

        private _AnalysisController CreateController(IAnalysisFieldPolicy policy = null)
        {
            var controller = new _AnalysisController(_registry, null, policy);
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
            Assert.IsFalse(json.Contains("\"FieldName\":\"Region\""));
            Assert.IsTrue(json.Contains("\"FieldName\":\"Category\""));
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

            var result = CreateController().Query(req) as OkObjectResult;
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

            var result = CreateController().Query(req) as OkObjectResult;
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
            // 第一行為 header
            var header = csv.Split('\n')[0].TrimEnd('\r');
            Assert.IsTrue(header.Contains("Region"), "header 應包含 Region");
            Assert.IsTrue(header.Contains("Amount_Sum"), "header 應包含 Amount_Sum");
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
            Assert.IsTrue(csv.Contains("\t=HYPERLINK"),
                "危險值應以 tab 前置（\\t=HYPERLINK）");
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
    }
}
