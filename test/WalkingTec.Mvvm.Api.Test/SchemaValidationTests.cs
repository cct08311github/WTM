using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// 驗證 API 回應的 JSON Schema，確保 contract 穩定。
/// 測重點：必要欄位存在性、類型正確性、ProblemDetails 錯誤格式。
/// </summary>
[TestClass]
public class SchemaValidationTests
{
    private const string StudentListVm =
        "WalkingTec.Mvvm.Demo.ViewModels.StudentVMs.StudentListVM";

    private static DemoWebApplicationFactory _factory = null!;
    private static HttpClient _authedClient = null!;

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        _factory = new DemoWebApplicationFactory();
        _authedClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = true,
            HandleCookies = true,
        });
        await LoginAsync(_authedClient);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _authedClient.Dispose();
        _factory.Dispose();
    }

    private static async Task LoginAsync(HttpClient client)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ITCode"] = "admin",
            ["Password"] = "000000",
        });
        var resp = await client.PostAsync("/Login/Login", form);
        Assert.IsTrue(resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.Redirect,
            $"Setup: login failed with {(int)resp.StatusCode}");
    }

    // ═══════════════════════════════════════════════════════════════
    // GET /_analysis/meta — Schema Validation
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 驗證 /_analysis/meta 回應每個欄位都有必要屬性。
    /// AnalysisFieldMeta 必要欄位：fieldName, fieldType, displayName
    /// </summary>
    [TestMethod]
    public async Task GetMeta_Schema_HasRequiredFields()
    {
        var resp = await _authedClient.GetAsync($"/_analysis/meta?listVmType={StudentListVm}");
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind);

        foreach (var field in doc.RootElement.EnumerateArray())
        {
            // fieldName 是字串且不為空
            Assert.IsTrue(field.TryGetProperty("fieldName", out var fn),
                "Each meta field must have 'fieldName'");
            Assert.AreEqual(JsonValueKind.String, fn.ValueKind);
            Assert.IsFalse(string.IsNullOrEmpty(fn.GetString()),
                "'fieldName' must not be empty");

            // kind 是字串且值為 "Dimension" 或 "Measure"（API 使用 "kind" 而非 "fieldType"）
            Assert.IsTrue(field.TryGetProperty("kind", out var ft),
                "Each meta field must have 'kind'");
            Assert.AreEqual(JsonValueKind.String, ft.ValueKind);
            var ftValue = ft.GetString();
            Assert.IsTrue(ftValue == "Dimension" || ftValue == "Measure",
                $"kind must be 'Dimension' or 'Measure', got '{ftValue}'");

            // displayName 是字串
            Assert.IsTrue(field.TryGetProperty("displayName", out var dn),
                "Each meta field must have 'displayName'");
            Assert.AreEqual(JsonValueKind.String, dn.ValueKind);
        }
    }

    /// <summary>
    /// 驗證 /_analysis/meta 回應的 extra fields 不會造成破壞性變更。
    /// 前端額外欄位應該被忽略（forward-compatible）。
    /// </summary>
    [TestMethod]
    public async Task GetMeta_Schema_ExtraFieldsIgnored()
    {
        var resp = await _authedClient.GetAsync($"/_analysis/meta?listVmType={StudentListVm}");
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // 至少應該有一個欄位
        Assert.IsTrue(doc.RootElement.GetArrayLength() > 0,
            "Meta response should contain at least one field");

        // 如果有第一個欄位，嘗試取得所有已知欄位（不應該拋例外交）
        foreach (var field in doc.RootElement.EnumerateArray())
        {
            _ = field.TryGetProperty("fieldName", out _);
            _ = field.TryGetProperty("kind", out _);
            _ = field.TryGetProperty("displayName", out _);
            _ = field.TryGetProperty("allowedFuncs", out _);
            _ = field.TryGetProperty("isDate", out _);
            _ = field.TryGetProperty("allowedValues", out _);
            _ = field.TryGetProperty("format", out _);
            // 未知欄位不應該造成錯誤
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // POST /_analysis/query — Schema Validation
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 驗證 /_analysis/query 回應是包含 rows 的物件。
    /// </summary>
    [TestMethod]
    public async Task Query_Schema_HasRowsArray()
    {
        // 先取 meta 取得有效欄位
        var metaResp = await _authedClient.GetAsync(
            $"/_analysis/meta?listVmType={StudentListVm}");
        Assert.AreEqual(HttpStatusCode.OK, metaResp.StatusCode);

        var metaBody = await metaResp.Content.ReadAsStringAsync();
        using var metaDoc = JsonDocument.Parse(metaBody);

        string? firstDimField = null;
        foreach (var f in metaDoc.RootElement.EnumerateArray())
        {
            if (f.TryGetProperty("kind", out var ft) &&
                ft.GetString() == "Dimension" &&
                f.TryGetProperty("fieldName", out var fn))
            {
                firstDimField = fn.GetString();
                break;
            }
        }
        Assert.IsNotNull(firstDimField, "No Dimension field found in meta");

        var queryReq = new
        {
            listVmType = StudentListVm,
            dimensions = new[] { firstDimField },
            measures = new[] { new { field = "RecordCount", func = 1 } }, // func 1 = Count
            filters = Array.Empty<object>(),
        };

        var resp = await _authedClient.PostAsJsonAsync("/_analysis/query", queryReq);
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual(JsonValueKind.Object, doc.RootElement.ValueKind);

        // 回應應該有 rows 陣列（聚合結果）
        Assert.IsTrue(doc.RootElement.TryGetProperty("rows", out var rows),
            "Query response must have 'rows' property");
        Assert.AreEqual(JsonValueKind.Array, rows.ValueKind,
            "'rows' must be an array");
    }

    // ═══════════════════════════════════════════════════════════════
    // Error Response — ProblemDetails RFC 7807 Validation
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 驗證無效 VM type 回傳 400 且 body 是 ProblemDetails 格式。
    /// RFC 7807: { type, title, status, detail, instance }
    /// </summary>
    [TestMethod]
    public async Task GetMeta_BadVmType_Returns404NotFound()
    {
        var resp = await _authedClient.GetAsync(
            "/_analysis/meta?listVmType=Nonexistent.FakeVM");

        // AnalysisVmNotFoundException → NotFound(404), not BadRequest(400)
        Assert.AreEqual(HttpStatusCode.NotFound, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // ProblemDetails 必要欄位：title, status（type 在 ProblemDetails 預設 null，STJ 會省略）
        Assert.IsTrue(doc.RootElement.TryGetProperty("title", out var titleElem),
            "ProblemDetails must have 'title'");
        Assert.IsTrue(doc.RootElement.TryGetProperty("status", out var statusElem),
            "ProblemDetails must have 'status'");

        Assert.AreEqual(JsonValueKind.String, titleElem.ValueKind);
        // Status is 404 to match HTTP NotFound response
        var statusText = statusElem.GetRawText();
        var statusVal = int.TryParse(statusText.Trim('"'), out var sv) ? sv : int.Parse(statusText);
        Assert.AreEqual(404, statusVal, "'status' should be 404");
    }

    /// <summary>
    /// 驗證缺少必要參數時回傳 400 且是 ProblemDetails 格式。
    /// </summary>
    [TestMethod]
    public async Task GetMeta_MissingParam_ReturnsProblemDetails()
    {
        var resp = await _authedClient.GetAsync("/_analysis/meta");

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var statusText = doc.RootElement.GetProperty("status").GetRawText();
        // STJ serializes int? as JSON number (raw text = "400"), parse safely.
        var statusVal = int.TryParse(statusText.Trim('"'), out var s) ? s : int.Parse(statusText);
        Assert.AreEqual(400, statusVal);
        // type is null in ProblemDetails default, STJ omits it from output
        Assert.IsTrue(doc.RootElement.TryGetProperty("title", out _));
    }

    /// <summary>
    /// 驗證 null body 回傳 400 且是 ProblemDetails 格式。
    /// </summary>
    [TestMethod]
    public async Task Query_NullBody_ReturnsProblemDetails()
    {
        var resp = await _authedClient.PostAsync(
            "/_analysis/query",
            new StringContent("null", System.Text.Encoding.UTF8, "application/json"));

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var statusText = doc.RootElement.GetProperty("status").GetRawText();
        // STJ serializes int? as JSON number (raw text = "400"), parse safely.
        var statusVal = int.TryParse(statusText.Trim('"'), out var s) ? s : int.Parse(statusText);
        Assert.AreEqual(400, statusVal);
    }
}
