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
/// 驗證 _AnalysisController 的完整 HTTP 管線行為。
/// 測試重點：路由守衛（auth）、Content-Type、JSON schema、400/401 錯誤格式。
/// 這些行為無法被 MockController 單元測試覆蓋（中間件、內容協商皆繞過）。
/// </summary>
[TestClass]
public class AnalysisApiTests
{
    // StudentListVM 是 demo 中唯一有 [EnableAnalysis] 標記的 ListVM
    private const string StudentListVm =
        "WalkingTec.Mvvm.Demo.ViewModels.StudentVMs.StudentListVM";

    private static DemoWebApplicationFactory _factory = null!;
    private static HttpClient _authedClient = null!;

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        _factory = new DemoWebApplicationFactory();

        // 建立一個在整個測試類別生命週期內共用的已驗證 client
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
            ["ITCode"]   = "admin",
            ["Password"] = "000000",
        });
        var resp = await client.PostAsync("/Login/Login", form);
        // 登入成功後 AllowAutoRedirect=true 會跟到首頁（200）
        Assert.IsTrue(resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.Redirect,
            $"Setup: login failed with {(int)resp.StatusCode}");
    }

    private static HttpClient CreateUnauthClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

    // ── GET /_analysis/meta ─────────────────────────────────────────────────

    [TestMethod]
    public async Task GetMeta_WithoutAuth_ReturnsUnauthorizedOrRedirect()
    {
        var client = CreateUnauthClient();

        var resp = await client.GetAsync($"/_analysis/meta?listVmType={StudentListVm}");

        // _AnalysisController 是 [ApiController]，框架 filter 應回傳 401
        // 若 cookie auth redirect 仍生效則為 302
        Assert.IsTrue(
            resp.StatusCode == HttpStatusCode.Unauthorized ||
            resp.StatusCode == HttpStatusCode.Redirect ||
            resp.StatusCode == HttpStatusCode.Found,
            $"Unauthenticated meta request should be 401/302, got {(int)resp.StatusCode}");
    }

    [TestMethod]
    public async Task GetMeta_WithAuth_Returns200AndJsonArray()
    {
        var resp = await _authedClient.GetAsync($"/_analysis/meta?listVmType={StudentListVm}");

        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"Authenticated meta request failed: {(int)resp.StatusCode}");

        // Content-Type must be application/json
        var contentType = resp.Content.Headers.ContentType?.MediaType;
        Assert.AreEqual("application/json", contentType,
            $"Expected application/json, got {contentType}");

        // Response must be a non-empty JSON array
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind,
            "Meta response root must be a JSON array");
        Assert.IsTrue(doc.RootElement.GetArrayLength() > 0,
            "Meta response must contain at least one field definition");
    }

    [TestMethod]
    public async Task GetMeta_WithUnregisteredVmType_Returns400()
    {
        var resp = await _authedClient.GetAsync(
            "/_analysis/meta?listVmType=Nonexistent.FakeListVM");

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            $"Unregistered VM type should return 400, got {(int)resp.StatusCode}");
    }

    [TestMethod]
    public async Task GetMeta_WithMissingVmType_Returns400()
    {
        var resp = await _authedClient.GetAsync("/_analysis/meta");

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            $"Missing listVmType should return 400, got {(int)resp.StatusCode}");
    }

    // ── POST /_analysis/query ───────────────────────────────────────────────

    [TestMethod]
    public async Task Query_WithoutAuth_ReturnsUnauthorizedOrRedirect()
    {
        var client = CreateUnauthClient();
        var req = new { listVmType = StudentListVm, dimensions = new[] { "Name" } };

        var resp = await client.PostAsJsonAsync("/_analysis/query", req);

        Assert.IsTrue(
            resp.StatusCode == HttpStatusCode.Unauthorized ||
            resp.StatusCode == HttpStatusCode.Redirect ||
            resp.StatusCode == HttpStatusCode.Found,
            $"Unauthenticated query should be 401/302, got {(int)resp.StatusCode}");
    }

    [TestMethod]
    public async Task Query_WithNullBody_Returns400()
    {
        // POST with null/empty body
        var resp = await _authedClient.PostAsync(
            "/_analysis/query",
            new StringContent("null", System.Text.Encoding.UTF8, "application/json"));

        Assert.AreEqual(HttpStatusCode.BadRequest, resp.StatusCode,
            $"Null request body should return 400, got {(int)resp.StatusCode}");
    }

    [TestMethod]
    public async Task Query_WithValidRequest_Returns200AndJsonObject()
    {
        // 第一步：先拿到合法欄位名（從 meta）
        var metaResp = await _authedClient.GetAsync(
            $"/_analysis/meta?listVmType={StudentListVm}");
        Assert.AreEqual(HttpStatusCode.OK, metaResp.StatusCode, "Meta prerequisite failed");

        var metaBody = await metaResp.Content.ReadAsStringAsync();
        using var metaDoc = JsonDocument.Parse(metaBody);

        // 找第一個 Dimension 欄位
        string? firstDimField = null;
        foreach (var field in metaDoc.RootElement.EnumerateArray())
        {
            if (field.TryGetProperty("fieldType", out var ft) &&
                ft.GetString() == "Dimension")
            {
                if (field.TryGetProperty("fieldName", out var fn))
                {
                    firstDimField = fn.GetString();
                    break;
                }
            }
        }

        Assert.IsNotNull(firstDimField, "No Dimension field found in meta — cannot construct query");

        // 第二步：執行 query
        var queryReq = new
        {
            listVmType = StudentListVm,
            dimensions = new[] { firstDimField },
            measures = Array.Empty<object>(),
            filters = Array.Empty<object>(),
        };

        var resp = await _authedClient.PostAsJsonAsync("/_analysis/query", queryReq);

        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"Valid query request failed: {(int)resp.StatusCode}");

        var contentType = resp.Content.Headers.ContentType?.MediaType;
        Assert.AreEqual("application/json", contentType,
            $"Query response should be application/json, got {contentType}");

        // Response body 應為 JSON object（含 rows 或類似欄位）
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual(JsonValueKind.Object, doc.RootElement.ValueKind,
            "Query response root must be a JSON object");
    }

    // ── POST /_analysis/export ──────────────────────────────────────────────

    [TestMethod]
    public async Task Export_WithValidRequest_Returns200AndExcelContent()
    {
        // 先取得 meta 找維度欄位
        var metaResp = await _authedClient.GetAsync(
            $"/_analysis/meta?listVmType={StudentListVm}");
        Assert.AreEqual(HttpStatusCode.OK, metaResp.StatusCode);
        var metaBody = await metaResp.Content.ReadAsStringAsync();
        using var metaDoc = JsonDocument.Parse(metaBody);

        string? firstDimField = null;
        foreach (var field in metaDoc.RootElement.EnumerateArray())
        {
            if (field.TryGetProperty("fieldType", out var ft) &&
                ft.GetString() == "Dimension" &&
                field.TryGetProperty("fieldName", out var fn))
            {
                firstDimField = fn.GetString();
                break;
            }
        }
        Assert.IsNotNull(firstDimField, "No Dimension field found");

        var exportReq = new
        {
            listVmType = StudentListVm,
            dimensions = new[] { firstDimField },
            measures = Array.Empty<object>(),
            filters = Array.Empty<object>(),
        };

        var resp = await _authedClient.PostAsJsonAsync(
            "/_analysis/export?format=xlsx", exportReq);

        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"Export request failed: {(int)resp.StatusCode}");

        // Excel MIME type
        var contentType = resp.Content.Headers.ContentType?.MediaType;
        Assert.IsTrue(
            contentType?.Contains("spreadsheetml") == true ||
            contentType?.Contains("octet-stream") == true,
            $"Excel export should have xlsx content-type, got {contentType}");

        // File should have non-trivial content (>1KB for a valid xlsx)
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.IsTrue(bytes.Length > 1024,
            $"Excel file too small ({bytes.Length} bytes) — likely empty or error");
    }
}
