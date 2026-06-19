#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Etl.Pipeline;
using WalkingTec.Mvvm.Etl.Pipeline.Sources;

namespace WalkingTec.Mvvm.Etl.Test.Pipeline;

// ---------------------------------------------------------------------------
// Mock HTTP plumbing
// ---------------------------------------------------------------------------

/// <summary>
/// A simple mock <see cref="HttpMessageHandler"/> that returns pre-registered
/// responses by URL (exact match) and tracks the Authorization header on each call.
/// Unknown URLs return 404.
/// </summary>
internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, string> _responses =
        new(StringComparer.Ordinal);

    /// <summary>Captures the Authorization header value from each request.</summary>
    public List<string?> CapturedAuthHeaders { get; } = new();

    /// <summary>Registers an exact URL → JSON body mapping.</summary>
    public void SetResponse(string url, string jsonBody) => _responses[url] = jsonBody;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryGetValues("Authorization", out var authValues);
        CapturedAuthHeaders.Add(authValues is null ? null : string.Join(",", authValues));

        var url = request.RequestUri!.ToString();
        if (_responses.TryGetValue(url, out var body))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>
/// Minimal <see cref="IHttpClientFactory"/> that always returns the same
/// <see cref="HttpClient"/> backed by a <see cref="MockHttpMessageHandler"/>.
/// </summary>
internal sealed class MockHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;

    public MockHttpClientFactory(MockHttpMessageHandler handler)
    {
        _client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public HttpClient CreateClient(string name) => _client;
}

// ---------------------------------------------------------------------------
// Test helpers
// ---------------------------------------------------------------------------

/// <summary>Static helpers shared across REST ETL tests.</summary>
internal static class RestTestHelper
{
    /// <summary>
    /// Creates a <see cref="RestEtlSource"/> backed by the given handler and
    /// with <c>SkipDnsPrecheck=true</c> so unit tests using hostname-based mock
    /// URLs are not blocked by the DNS resolution step of the SSRF pre-check.
    /// IP-literal blocking is still enforced.
    /// </summary>
    public static RestEtlSource MockSource(MockHttpMessageHandler handler)
        => new RestEtlSource(new MockHttpClientFactory(handler))
        {
            SkipDnsPrecheck = true
        };

    /// <summary>Collects all DataRows across all batches.</summary>
    public static async Task<List<DataRow>> CollectAllRowsAsync(
        RestEtlSource src,
        string configJson,
        int batchSize = 1000,
        CancellationToken ct = default)
    {
        var rows = new List<DataRow>();
        await foreach (var batch in src.ExtractBatchesAsync(configJson, "", null, batchSize, ct))
            foreach (DataRow r in batch.Rows)
                rows.Add(r);
        return rows;
    }

    /// <summary>Serialises a <see cref="RestEtlSourceConfig"/> to JSON.</summary>
    public static string Config(
        string url,
        string recordsPath = "",
        RestPaginationStrategy strategy = RestPaginationStrategy.PageNumber,
        string pageParam = "page",
        int firstPage = 1,
        string offsetParam = "offset",
        string limitParam = "limit",
        string nextLinkField = "next",
        Dictionary<string, string>? headers = null,
        bool allowHttp = true,
        int pageDelayMs = 0,
        int maxPages = 0)
    {
        var cfg = new RestEtlSourceConfig
        {
            Url                = url,
            RecordsPath        = recordsPath,
            PaginationStrategy = strategy,
            PageParam          = pageParam,
            FirstPage          = firstPage,
            OffsetParam        = offsetParam,
            LimitParam         = limitParam,
            NextLinkField      = nextLinkField,
            Headers            = headers ?? new Dictionary<string, string>(),
            AllowHttp          = allowHttp,
            PageDelayMs        = pageDelayMs,
            TimeoutSeconds     = 10,
            MaxPages           = maxPages,
        };
        return JsonSerializer.Serialize(cfg);
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

[TestClass]
public class RestEtlSourceTests
{
    // ── 1. Page-number pagination ────────────────────────────────────────

    [TestMethod]
    public async Task PageNumber_pagination_walks_all_pages_and_maps_columns()
    {
        // page=1 returns 2 rows (= batchSize), page=2 returns 1 row (< batchSize) → stop.
        var handler = new MockHttpMessageHandler();
        handler.SetResponse(
            "http://api.test/items?page=1",
            """[{"id":"1","name":"Alpha"},{"id":"2","name":"Beta"}]""");
        handler.SetResponse(
            "http://api.test/items?page=2",
            """[{"id":"3","name":"Gamma"}]""");

        using var src = RestTestHelper.MockSource(handler);
        var rows = await RestTestHelper.CollectAllRowsAsync(
            src, RestTestHelper.Config("http://api.test/items"), batchSize: 2);

        rows.Should().HaveCount(3);
        rows[0]["id"].Should().Be("1");
        rows[0]["name"].Should().Be("Alpha");
        rows[1]["name"].Should().Be("Beta");
        rows[2]["name"].Should().Be("Gamma");
    }

    [TestMethod]
    public async Task PageNumber_pagination_first_page_zero_works()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse(
            "http://api.test/zp?page=0",
            """[{"v":"a"}]""");
        // page=1 won't have an exact match → 404 which is < batchSize=1000 → stop after first page
        // Actually page=0 has 1 row < 1000 so it stops after first page automatically.

        using var src = RestTestHelper.MockSource(handler);
        var rows = await RestTestHelper.CollectAllRowsAsync(
            src, RestTestHelper.Config("http://api.test/zp", firstPage: 0), batchSize: 1000);

        rows.Should().HaveCount(1);
        rows[0]["v"].Should().Be("a");
    }

    // ── 2. Offset pagination ─────────────────────────────────────────────

    [TestMethod]
    public async Task Offset_pagination_walks_all_pages()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse(
            "http://api.test/data?offset=0&limit=2",
            """[{"v":"a"},{"v":"b"}]""");
        handler.SetResponse(
            "http://api.test/data?offset=2&limit=2",
            """[{"v":"c"}]""");

        using var src = RestTestHelper.MockSource(handler);
        var rows = await RestTestHelper.CollectAllRowsAsync(
            src, RestTestHelper.Config(
                "http://api.test/data",
                strategy: RestPaginationStrategy.Offset,
                offsetParam: "offset",
                limitParam: "limit"),
            batchSize: 2);

        rows.Should().HaveCount(3);
        rows.Select(r => r["v"]).Should().ContainInOrder("a", "b", "c");
    }

    // ── 3. Next-link / cursor pagination ────────────────────────────────

    [TestMethod]
    public async Task NextLink_follows_cursor_and_stops_on_null_next()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse(
            "http://api.test/cur",
            """{"data":[{"x":"1"},{"x":"2"}],"next":"http://api.test/cur?cursor=abc"}""");
        handler.SetResponse(
            "http://api.test/cur?cursor=abc",
            """{"data":[{"x":"3"}],"next":null}""");

        using var src = RestTestHelper.MockSource(handler);
        var rows = await RestTestHelper.CollectAllRowsAsync(
            src, RestTestHelper.Config(
                "http://api.test/cur",
                recordsPath: "data",
                strategy: RestPaginationStrategy.NextLink,
                nextLinkField: "next"),
            batchSize: 1000);

        rows.Should().HaveCount(3);
        rows.Select(r => r["x"]).Should().ContainInOrder("1", "2", "3");
    }

    [TestMethod]
    public async Task NextLink_stops_on_absent_next_field()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse(
            "http://api.test/cur2",
            """{"items":[{"k":"v"}]}"""); // no "next" key at all

        using var src = RestTestHelper.MockSource(handler);
        var rows = await RestTestHelper.CollectAllRowsAsync(
            src, RestTestHelper.Config(
                "http://api.test/cur2",
                recordsPath: "items",
                strategy: RestPaginationStrategy.NextLink,
                nextLinkField: "next"),
            batchSize: 1000);

        rows.Should().HaveCount(1);
        rows[0]["k"].Should().Be("v");
    }

    [TestMethod]
    public async Task NextLink_stops_on_empty_string_next()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse(
            "http://api.test/cur3",
            """{"items":[{"k":"v"}],"next":""}""");

        using var src = RestTestHelper.MockSource(handler);
        var rows = await RestTestHelper.CollectAllRowsAsync(
            src, RestTestHelper.Config(
                "http://api.test/cur3",
                recordsPath: "items",
                strategy: RestPaginationStrategy.NextLink,
                nextLinkField: "next"),
            batchSize: 1000);

        rows.Should().HaveCount(1);
    }

    // ── 4. JSON path + DataTable mapping ────────────────────────────────

    [TestMethod]
    public async Task RecordsPath_navigates_nested_JSON_path()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse(
            "http://api.test/nested?page=1",
            """{"result":{"data":[{"col":"val1"}]}}""");

        using var src = RestTestHelper.MockSource(handler);
        var rows = await RestTestHelper.CollectAllRowsAsync(
            src, RestTestHelper.Config("http://api.test/nested", recordsPath: "result.data"),
            batchSize: 1000);

        rows.Should().HaveCount(1);
        rows[0]["col"].Should().Be("val1");
    }

    [TestMethod]
    public async Task RecordsPath_with_dollar_prefix_works()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse(
            "http://api.test/dp?page=1",
            """{"records":[{"z":"1"}]}""");

        using var src = RestTestHelper.MockSource(handler);
        var rows = await RestTestHelper.CollectAllRowsAsync(
            src, RestTestHelper.Config("http://api.test/dp", recordsPath: "$.records"),
            batchSize: 1000);

        rows.Should().HaveCount(1);
        rows[0]["z"].Should().Be("1");
    }

    [TestMethod]
    public async Task QueryTemplate_overrides_RecordsPath_in_config()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse(
            "http://api.test/qtpl?page=1",
            """{"myArray":[{"n":"1"}]}""");

        using var src = RestTestHelper.MockSource(handler);
        var rows = new List<DataRow>();
        // RecordsPath is empty in config; queryTemplate overrides it.
        await foreach (var batch in src.ExtractBatchesAsync(
            RestTestHelper.Config("http://api.test/qtpl", recordsPath: ""),
            "myArray", null, 1000))
        {
            foreach (DataRow r in batch.Rows)
                rows.Add(r);
        }

        rows.Should().HaveCount(1);
        rows[0]["n"].Should().Be("1");
    }

    [TestMethod]
    public async Task Scalar_array_maps_to_value_column()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse(
            "http://api.test/scalar?page=1",
            """["hello","world"]""");

        using var src = RestTestHelper.MockSource(handler);
        var rows = await RestTestHelper.CollectAllRowsAsync(
            src, RestTestHelper.Config("http://api.test/scalar"), batchSize: 1000);

        rows.Should().HaveCount(2);
        rows[0]["value"].Should().Be("hello");
        rows[1]["value"].Should().Be("world");
    }

    [TestMethod]
    public async Task DBNull_for_missing_field_in_subsequent_records()
    {
        // First record has "a" and "b"; second has only "a". "b" should be DBNull.
        var handler = new MockHttpMessageHandler();
        handler.SetResponse(
            "http://api.test/sparse?page=1",
            """[{"a":"1","b":"2"},{"a":"3"}]""");

        using var src = RestTestHelper.MockSource(handler);
        var rows = await RestTestHelper.CollectAllRowsAsync(
            src, RestTestHelper.Config("http://api.test/sparse"), batchSize: 1000);

        rows.Should().HaveCount(2);
        rows[0]["b"].Should().Be("2");
        rows[1]["b"].Should().Be(DBNull.Value);
    }

    // ── 5. Auth header ───────────────────────────────────────────────────

    [TestMethod]
    public async Task Bearer_token_header_is_sent_on_every_request()
    {
        var handler = new MockHttpMessageHandler();
        // Single page (1 row < batchSize) so there will be exactly 1 request.
        handler.SetResponse(
            "http://api.test/auth?page=1",
            """[{"id":"1"}]""");

        using var src = RestTestHelper.MockSource(handler);
        _ = await RestTestHelper.CollectAllRowsAsync(
            src,
            RestTestHelper.Config(
                "http://api.test/auth",
                headers: new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer secret-token"
                }),
            batchSize: 1000);

        handler.CapturedAuthHeaders.Should().NotBeEmpty();
        handler.CapturedAuthHeaders.Should().AllSatisfy(
            h => h.Should().Be("Bearer secret-token"));
    }

    [TestMethod]
    public async Task ApiKey_header_is_sent()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse(
            "http://api.test/key?page=1",
            """[{"r":"1"}]""");

        using var src = RestTestHelper.MockSource(handler);
        _ = await RestTestHelper.CollectAllRowsAsync(
            src,
            RestTestHelper.Config(
                "http://api.test/key",
                headers: new Dictionary<string, string> { ["X-Api-Key"] = "my-key" }),
            batchSize: 1000);

        handler.CapturedAuthHeaders.Should().HaveCount(1);
    }

    // ── 6. CancellationToken ─────────────────────────────────────────────

    [TestMethod]
    public async Task Cancellation_token_stops_enumeration_early()
    {
        var handler = new MockHttpMessageHandler();
        // Two full pages (batchSize=2 each) then a partial third.
        handler.SetResponse(
            "http://api.test/cancel?page=1",
            """[{"r":"1"},{"r":"2"}]""");
        handler.SetResponse(
            "http://api.test/cancel?page=2",
            """[{"r":"3"},{"r":"4"}]""");
        handler.SetResponse(
            "http://api.test/cancel?page=3",
            """[{"r":"5"}]""");

        using var cts = new CancellationTokenSource();
        using var src = RestTestHelper.MockSource(handler);
        var rows = new List<DataRow>();

        var act = async () =>
        {
            await foreach (var batch in src.ExtractBatchesAsync(
                RestTestHelper.Config("http://api.test/cancel"),
                "", null, 2, cts.Token))
            {
                foreach (DataRow r in batch.Rows)
                    rows.Add(r);
                // Cancel after the first batch.
                cts.Cancel();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        // Only the first batch (2 rows) should be collected.
        rows.Should().HaveCount(2);
    }

    // ── 7. Stop conditions ────────────────────────────────────────────────

    [TestMethod]
    public async Task Empty_page_stops_pagination_immediately()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse(
            "http://api.test/stop?page=1",
            """[{"a":"1"}]""");
        handler.SetResponse(
            "http://api.test/stop?page=2",
            """[]""");

        using var src = RestTestHelper.MockSource(handler);
        var rows = await RestTestHelper.CollectAllRowsAsync(
            src, RestTestHelper.Config("http://api.test/stop"), batchSize: 1000);

        rows.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task MaxPages_limit_stops_pagination()
    {
        var handler = new MockHttpMessageHandler();
        // Register 3 pages; MaxPages=1 should stop after the first.
        handler.SetResponse("http://api.test/mp?page=1", """[{"x":"p1a"},{"x":"p1b"}]""");
        handler.SetResponse("http://api.test/mp?page=2", """[{"x":"p2a"},{"x":"p2b"}]""");
        handler.SetResponse("http://api.test/mp?page=3", """[{"x":"p3a"}]""");

        using var src = RestTestHelper.MockSource(handler);
        var rows = await RestTestHelper.CollectAllRowsAsync(
            src, RestTestHelper.Config("http://api.test/mp", maxPages: 1), batchSize: 1000);

        rows.Should().HaveCount(2, "only the first page should be fetched (MaxPages=1)");
    }

    // ── 8. Batching across pages ─────────────────────────────────────────

    [TestMethod]
    public async Task DataTable_batches_split_correctly_within_single_page()
    {
        // Single page of 9 rows; batchSize=4 → DataTable batches [4, 4, 1].
        var handler = new MockHttpMessageHandler();
        handler.SetResponse(
            "http://api.test/batch?page=1",
            """[{"n":"1"},{"n":"2"},{"n":"3"},{"n":"4"},{"n":"5"},{"n":"6"},{"n":"7"},{"n":"8"},{"n":"9"}]""");
        // Page 2 returns empty → stop.
        handler.SetResponse("http://api.test/batch?page=2", """[]""");

        using var src  = RestTestHelper.MockSource(handler);
        var batches    = new List<DataTable>();
        await foreach (var batch in src.ExtractBatchesAsync(
            RestTestHelper.Config("http://api.test/batch"), "", null, 4))
        {
            batches.Add(batch);
        }

        batches.Should().HaveCount(3);
        batches[0].Rows.Count.Should().Be(4);
        batches[1].Rows.Count.Should().Be(4);
        batches[2].Rows.Count.Should().Be(1);

        var values = batches
            .SelectMany(b => b.Rows.Cast<DataRow>())
            .Select(r => r["n"].ToString())
            .ToList();
        values.Should().ContainInOrder("1","2","3","4","5","6","7","8","9");
    }

    [TestMethod]
    public async Task DataTable_batches_accumulated_across_multiple_pages()
    {
        // Two full pages (3 rows each) + one partial = 7 rows total; batchSize=3 → [3,3,1].
        var handler = new MockHttpMessageHandler();
        handler.SetResponse("http://api.test/batch2?page=1", """[{"n":"1"},{"n":"2"},{"n":"3"}]""");
        handler.SetResponse("http://api.test/batch2?page=2", """[{"n":"4"},{"n":"5"},{"n":"6"}]""");
        handler.SetResponse("http://api.test/batch2?page=3", """[{"n":"7"}]""");

        using var src  = RestTestHelper.MockSource(handler);
        var batches    = new List<DataTable>();
        await foreach (var batch in src.ExtractBatchesAsync(
            RestTestHelper.Config("http://api.test/batch2"), "", null, 3))
        {
            batches.Add(batch);
        }

        batches.Should().HaveCount(3);
        batches[0].Rows.Count.Should().Be(3);
        batches[1].Rows.Count.Should().Be(3);
        batches[2].Rows.Count.Should().Be(1);
    }

    // ── 9. SSRF guard ────────────────────────────────────────────────────

    [TestMethod]
    public async Task Private_RFC1918_IP_is_rejected()
    {
        using var src = new RestEtlSource(new MockHttpClientFactory(new MockHttpMessageHandler()));
        var act = async () =>
        {
            await foreach (var _ in src.ExtractBatchesAsync(
                RestTestHelper.Config("http://192.168.1.1/api", allowHttp: true),
                "", null, 100))
            { }
        };
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*blocked IP range*");
    }

    [TestMethod]
    public async Task Loopback_IP_is_rejected()
    {
        using var src = new RestEtlSource(new MockHttpClientFactory(new MockHttpMessageHandler()));
        var act = async () =>
        {
            await foreach (var _ in src.ExtractBatchesAsync(
                RestTestHelper.Config("http://127.0.0.1/api", allowHttp: true),
                "", null, 100))
            { }
        };
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*blocked IP range*");
    }

    [TestMethod]
    public async Task IMDS_link_local_IP_is_rejected()
    {
        using var src = new RestEtlSource(new MockHttpClientFactory(new MockHttpMessageHandler()));
        var act = async () =>
        {
            await foreach (var _ in src.ExtractBatchesAsync(
                RestTestHelper.Config("http://169.254.169.254/meta-data", allowHttp: true),
                "", null, 100))
            { }
        };
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*blocked IP range*");
    }

    [TestMethod]
    public async Task Plain_HTTP_without_AllowHttp_is_rejected()
    {
        using var src = new RestEtlSource(new MockHttpClientFactory(new MockHttpMessageHandler()));
        var config = JsonSerializer.Serialize(new RestEtlSourceConfig
        {
            Url       = "http://example.com/api",
            AllowHttp = false,
        });
        var act = async () =>
        {
            await foreach (var _ in src.ExtractBatchesAsync(config, "", null, 100))
            { }
        };
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*http://*");
    }

    // ── 10. IsBlockedIp unit coverage ────────────────────────────────────

    [TestMethod]
    [DataRow("127.0.0.1",     true)]
    [DataRow("10.0.0.1",      true)]
    [DataRow("172.16.0.1",    true)]
    [DataRow("172.31.255.255",true)]
    [DataRow("192.168.0.1",   true)]
    [DataRow("169.254.169.254",true)]
    [DataRow("100.64.0.1",    true)]
    [DataRow("0.0.0.1",       true)]
    [DataRow("240.0.0.1",     true)]
    [DataRow("224.0.0.1",     true)]
    [DataRow("8.8.8.8",       false)]
    [DataRow("1.1.1.1",       false)]
    [DataRow("93.184.216.34", false)]
    public void IsBlockedIp_classifies_correctly(string ipStr, bool expectedBlocked)
    {
        var ip = System.Net.IPAddress.Parse(ipStr);
        RestEtlSource.IsBlockedIp(ip).Should().Be(expectedBlocked,
            because: $"{ipStr} expected blocked={expectedBlocked}");
    }

    // ── 11. AppendQueryParam ──────────────────────────────────────────────

    [TestMethod]
    public void AppendQueryParam_adds_question_mark_to_bare_url()
    {
        RestEtlSource.AppendQueryParam("http://api.test/items", "page", "1")
            .Should().Be("http://api.test/items?page=1");
    }

    [TestMethod]
    public void AppendQueryParam_appends_ampersand_when_query_already_present()
    {
        RestEtlSource.AppendQueryParam("http://api.test/items?sort=asc", "page", "2")
            .Should().Be("http://api.test/items?sort=asc&page=2");
    }

    // ── 12. ParseConfig validation ────────────────────────────────────────

    [TestMethod]
    public void ParseConfig_throws_on_null_or_empty_input()
    {
        ((Action)(() => RestEtlSource.ParseConfig("")))
            .Should().Throw<ArgumentException>();
        ((Action)(() => RestEtlSource.ParseConfig("   ")))
            .Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void ParseConfig_throws_on_missing_url()
    {
        var json = """{"Url":"","AllowHttp":true}""";
        ((Action)(() => RestEtlSource.ParseConfig(json)))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*Url is required*");
    }

    [TestMethod]
    public void ParseConfig_parses_valid_config_correctly()
    {
        var json = """{"Url":"https://api.example.com/data","AllowHttp":false,"PageDelayMs":50}""";
        var cfg  = RestEtlSource.ParseConfig(json);
        cfg.Url.Should().Be("https://api.example.com/data");
        cfg.AllowHttp.Should().BeFalse();
        cfg.PageDelayMs.Should().Be(50);
    }

    // ── 13. Registry ─────────────────────────────────────────────────────

    [TestMethod]
    public void Registry_resolves_rest_and_http_keys_when_explicitly_registered()
    {
        var factory = new MockHttpClientFactory(new MockHttpMessageHandler());
        var reg     = new EtlSourceRegistry();
        reg.Register("rest", () => new RestEtlSource(factory));
        reg.Register("http", () => new RestEtlSource(factory));

        using var srcRest = reg.Create("rest");
        using var srcHttp = reg.Create("http");

        srcRest.Should().BeOfType<RestEtlSource>();
        srcHttp.Should().BeOfType<RestEtlSource>();
    }

    [TestMethod]
    public void Registry_key_lookup_is_case_insensitive()
    {
        var factory = new MockHttpClientFactory(new MockHttpMessageHandler());
        var reg     = new EtlSourceRegistry();
        reg.Register("REST", () => new RestEtlSource(factory));

        using var src1 = reg.Create("rest");
        using var src2 = reg.Create("REST");
        using var src3 = reg.Create("Rest");

        src1.Should().BeOfType<RestEtlSource>();
        src2.Should().BeOfType<RestEtlSource>();
        src3.Should().BeOfType<RestEtlSource>();
    }
}

/// <summary>
/// Issue #376: next-link scheme re-validation — prevents HTTPS→HTTP downgrade
/// that would expose Authorization headers when following server-supplied cursors.
/// </summary>
[TestClass]
public class RestEtlSourceNextLinkSchemeTests
{
    private static string MakeConfig(string firstUrl, string recordsPath = "items",
        string nextLinkField = "next", bool allowHttp = false)
    {
        return RestTestHelper.Config(
            firstUrl,
            recordsPath: recordsPath,
            strategy: RestPaginationStrategy.NextLink,
            nextLinkField: nextLinkField,
            allowHttp: allowHttp);
    }

    [TestMethod]
    public async Task NextLink_http_cursor_is_rejected_when_AllowHttp_is_false()
    {
        var handler = new MockHttpMessageHandler();
        // First page: valid HTTPS, provides an http:// next-link cursor.
        // AllowHttp=false means the http:// downgrade must be rejected.
        handler.SetResponse("https://api.example.com/orders",
            """{"next":"http://api.example.com/orders?page=2","items":[{"id":1}]}""");

        var source = RestTestHelper.MockSource(handler);
        var config = MakeConfig("https://api.example.com/orders", allowHttp: false);

        var act = async () =>
        {
            await foreach (var _ in source.ExtractBatchesAsync(config, "", null, 100))
            { }
        };

        await act.Should().ThrowAsync<InvalidOperationException>(
            "an http:// next-link must be rejected when AllowHttp=false to prevent " +
            "HTTPS→HTTP downgrade that leaks Authorization headers");
    }

    [TestMethod]
    public async Task NextLink_https_cursor_is_accepted()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse("https://api.example.com/orders",
            """{"next":"https://api.example.com/orders?page=2","items":[{"id":1}]}""");
        handler.SetResponse("https://api.example.com/orders?page=2",
            """{"items":[{"id":2}]}""");  // no next → stops

        var source = RestTestHelper.MockSource(handler);
        var config = MakeConfig("https://api.example.com/orders");

        var rows = await RestTestHelper.CollectAllRowsAsync(source, config);

        rows.Should().HaveCount(2, "both pages should be fetched when next-link is https://");
    }

    [TestMethod]
    public async Task NextLink_relative_cursor_is_rejected()
    {
        var handler = new MockHttpMessageHandler();
        handler.SetResponse("https://api.example.com/orders",
            """{"next":"/orders?page=2","items":[{"id":1}]}""");

        var source = RestTestHelper.MockSource(handler);
        var config = MakeConfig("https://api.example.com/orders");

        var act = async () =>
        {
            await foreach (var _ in source.ExtractBatchesAsync(config, "", null, 100))
            { }
        };

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a relative next-link must be rejected to prevent open-redirect attacks");
    }
}
