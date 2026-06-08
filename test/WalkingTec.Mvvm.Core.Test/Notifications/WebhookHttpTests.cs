#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Notifications;
using WalkingTec.Mvvm.Core.Notifications.Providers;

namespace WalkingTec.Mvvm.Core.Test.Notifications;

/// <summary>
/// Tests for issue #219: verifies HTTP behaviour of the webhook sink —
/// POST method, URL, JSON body capture, and retry on transient failures.
/// All tests use a <see cref="MockHttpMessageHandler"/> and make no real network calls.
/// </summary>
[TestClass]
public class WebhookHttpTests
{
    private static WebhookMessage SampleMsg() => new()
    {
        Title  = "KPI Alert",
        Body   = "Revenue dropped 10%",
        Level  = WebhookLevel.Warning,
        Fields = new[] { new KeyValuePair<string, string>("Threshold", "10%") }
    };

    private const string WebhookUrl = "https://oapi.dingtalk.com/robot/send?access_token=test";

    // ── POST method + URL ─────────────────────────────────────────────────

    [TestMethod]
    public async Task SendAsync_posts_to_webhook_url()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var sink = BuildDingTalkSink(handler, WebhookUrl);

        await sink.SendAsync(SampleMsg());

        Assert.AreEqual(1, handler.CallCount);
        Assert.AreEqual(HttpMethod.Post, handler.LastMethod);
        Assert.IsNotNull(handler.LastRequestUri);
        StringAssert.Contains(handler.LastRequestUri.ToString(), "oapi.dingtalk.com");
    }

    [TestMethod]
    public async Task SendAsync_sends_json_content_type()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var sink = BuildDingTalkSink(handler, WebhookUrl);

        await sink.SendAsync(SampleMsg());

        Assert.IsNotNull(handler.LastContentType);
        StringAssert.Contains(handler.LastContentType, "application/json");
    }

    [TestMethod]
    public async Task SendAsync_body_contains_provider_specific_json()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.OK);
        var sink = BuildDingTalkSink(handler, WebhookUrl);

        await sink.SendAsync(SampleMsg());

        var body = handler.LastBody ?? string.Empty;
        StringAssert.Contains(body, "markdown");          // DingTalk msgtype
        StringAssert.Contains(body, "KPI Alert");
    }

    // ── Retry on transient 5xx ────────────────────────────────────────────

    [TestMethod]
    public async Task SendAsync_retries_on_5xx_and_succeeds()
    {
        // First call → 503, second call → 200
        var handler = new SequencedMockHandler(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);

        var options = new WtmWebhookOptions
        {
            Provider   = WebhookProviderKind.DingTalk,
            WebhookUrl = WebhookUrl,
            MaxRetries = 2,
            TimeoutSeconds = 10
        };

        var sink = BuildDingTalkSinkWithOptions(handler, options);
        await sink.SendAsync(SampleMsg());

        Assert.AreEqual(2, handler.CallCount, "Expected exactly 2 HTTP calls (1 fail + 1 success).");
    }

    [TestMethod]
    public async Task SendAsync_throws_after_all_retries_exhausted()
    {
        // All calls → 500
        var handler = new MockHttpMessageHandler(HttpStatusCode.InternalServerError);
        var options = new WtmWebhookOptions
        {
            Provider       = WebhookProviderKind.DingTalk,
            WebhookUrl     = WebhookUrl,
            MaxRetries     = 1,   // 1 retry → 2 total attempts
            TimeoutSeconds = 10
        };

        var sink = BuildDingTalkSinkWithOptions(handler, options);

        await Assert.ThrowsExceptionAsync<HttpRequestException>(
            () => sink.SendAsync(SampleMsg()));
        Assert.IsTrue(handler.CallCount >= 2, $"Expected >= 2 calls but got {handler.CallCount}.");
    }

    [TestMethod]
    public async Task SendAsync_does_not_retry_on_4xx()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.Unauthorized);
        var options = new WtmWebhookOptions
        {
            Provider       = WebhookProviderKind.DingTalk,
            WebhookUrl     = WebhookUrl,
            MaxRetries     = 2,
            TimeoutSeconds = 10
        };
        var sink = BuildDingTalkSinkWithOptions(handler, options);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => sink.SendAsync(SampleMsg()));

        Assert.AreEqual(1, handler.CallCount, "4xx must not be retried.");
    }

    // ── SSRF / URL validation ─────────────────────────────────────────────

    [TestMethod]
    public void ValidateOptions_rejects_http_url()
    {
        var opts = new WtmWebhookOptions { WebhookUrl = "http://example.com/webhook" };
        Assert.ThrowsException<ArgumentException>(
            () => WebhookProviderBase.ValidateOptions(opts));
    }

    [TestMethod]
    public void ValidateOptions_rejects_empty_url()
    {
        var opts = new WtmWebhookOptions { WebhookUrl = string.Empty };
        Assert.ThrowsException<ArgumentException>(
            () => WebhookProviderBase.ValidateOptions(opts));
    }

    [TestMethod]
    [DataRow("https://127.0.0.1/hook")]
    [DataRow("https://10.0.0.1/hook")]
    [DataRow("https://192.168.1.1/hook")]
    [DataRow("https://169.254.169.254/hook")]
    public void ValidateOptions_rejects_private_ip_in_url(string url)
    {
        var opts = new WtmWebhookOptions { WebhookUrl = url };
        Assert.ThrowsException<ArgumentException>(
            () => WebhookProviderBase.ValidateOptions(opts));
    }

    [TestMethod]
    public void ValidateOptions_accepts_https_public_url()
    {
        var opts = new WtmWebhookOptions { WebhookUrl = "https://hooks.slack.com/services/T/B/x" };
        // Must not throw
        WebhookProviderBase.ValidateOptions(opts);
    }

    [TestMethod]
    public void ValidateOptions_rejects_host_not_in_allowlist()
    {
        var opts = new WtmWebhookOptions
        {
            WebhookUrl          = "https://hooks.slack.com/services/T/B/x",
            AllowedHostSuffixes = new[] { "oapi.dingtalk.com" }  // Slack host not allowed
        };
        Assert.ThrowsException<ArgumentException>(
            () => WebhookProviderBase.ValidateOptions(opts));
    }

    [TestMethod]
    public void ValidateOptions_accepts_host_matching_allowlist()
    {
        var opts = new WtmWebhookOptions
        {
            WebhookUrl          = "https://oapi.dingtalk.com/robot/send?access_token=x",
            AllowedHostSuffixes = new[] { "oapi.dingtalk.com" }
        };
        // Must not throw
        WebhookProviderBase.ValidateOptions(opts);
    }

    // ── SSRF IsBlockedIp unit tests ───────────────────────────────────────

    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("10.0.0.1")]
    [DataRow("172.16.0.1")]
    [DataRow("172.31.255.255")]
    [DataRow("192.168.100.1")]
    [DataRow("169.254.169.254")]
    [DataRow("100.64.0.1")]
    [DataRow("224.0.0.1")]
    public void IsBlockedIp_blocks_private_and_reserved_ranges(string ip)
    {
        var addr = System.Net.IPAddress.Parse(ip);
        Assert.IsTrue(WebhookProviderBase.IsSsrfBlockedIp(addr),
            $"{ip} should be blocked.");
    }

    [TestMethod]
    [DataRow("8.8.8.8")]
    [DataRow("1.1.1.1")]
    [DataRow("104.18.0.1")]  // Cloudflare CDN
    public void IsBlockedIp_allows_public_ips(string ip)
    {
        var addr = System.Net.IPAddress.Parse(ip);
        Assert.IsFalse(WebhookProviderBase.IsSsrfBlockedIp(addr),
            $"{ip} should not be blocked.");
    }

    // ── Composite sink ────────────────────────────────────────────────────

    [TestMethod]
    public async Task CompositeWtmWebhookSink_delivers_to_all_sinks()
    {
        var handler1 = new MockHttpMessageHandler(HttpStatusCode.OK);
        var handler2 = new MockHttpMessageHandler(HttpStatusCode.OK);

        var sink1 = BuildDingTalkSink(handler1, WebhookUrl);
        var sink2 = BuildDingTalkSink(handler2, WebhookUrl);

        var composite = new CompositeWtmWebhookSink(
            new List<IWtmWebhookSink> { sink1, sink2 },
            NullLogger<CompositeWtmWebhookSink>.Instance);

        await composite.SendAsync(SampleMsg());

        Assert.AreEqual(1, handler1.CallCount, "sink1 should receive exactly one call.");
        Assert.AreEqual(1, handler2.CallCount, "sink2 should receive exactly one call.");
    }

    [TestMethod]
    public async Task CompositeWtmWebhookSink_continues_even_when_one_sink_fails()
    {
        var failingHandler = new MockHttpMessageHandler(HttpStatusCode.InternalServerError);
        var successHandler = new MockHttpMessageHandler(HttpStatusCode.OK);

        var failOpts = new WtmWebhookOptions
        {
            Provider = WebhookProviderKind.DingTalk, WebhookUrl = WebhookUrl, MaxRetries = 0
        };
        var sink1 = BuildDingTalkSinkWithOptions(failingHandler, failOpts);
        var sink2 = BuildDingTalkSink(successHandler, WebhookUrl);

        var composite = new CompositeWtmWebhookSink(
            new List<IWtmWebhookSink> { sink1, sink2 },
            NullLogger<CompositeWtmWebhookSink>.Instance);

        // Should not throw — CompositeWtmWebhookSink swallows individual failures.
        await composite.SendAsync(SampleMsg());

        Assert.AreEqual(1, successHandler.CallCount, "sink2 must still be called despite sink1 failing.");
    }

    // ── Factory helpers ───────────────────────────────────────────────────

    private static IWtmWebhookSink BuildDingTalkSink(
        HttpMessageHandler handler, string webhookUrl)
    {
        var options = new WtmWebhookOptions
        {
            Provider       = WebhookProviderKind.DingTalk,
            WebhookUrl     = webhookUrl,
            MaxRetries     = 0,  // disable retries by default for simplicity
            TimeoutSeconds = 10
        };
        return BuildDingTalkSinkWithOptions(handler, options);
    }

    private static IWtmWebhookSink BuildDingTalkSinkWithOptions(
        HttpMessageHandler handler, WtmWebhookOptions options)
    {
        var httpClient = new HttpClient(handler);
        var factory    = new FixedHttpClientFactory(httpClient);
        return new DingTalkWebhookSink(factory, options, NullLogger<DingTalkWebhookSink>.Instance);
    }
}

// ── Test infrastructure ────────────────────────────────────────────────────

/// <summary>Always returns the configured status code.</summary>
internal sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    public int CallCount { get; private set; }
    public HttpMethod? LastMethod { get; private set; }
    public Uri? LastRequestUri { get; private set; }
    public string? LastBody { get; private set; }
    public string? LastContentType { get; private set; }

    public MockHttpMessageHandler(HttpStatusCode statusCode) => _statusCode = statusCode;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastMethod     = request.Method;
        LastRequestUri = request.RequestUri;
        if (request.Content != null)
        {
            LastBody        = await request.Content.ReadAsStringAsync(cancellationToken);
            LastContentType = request.Content.Headers.ContentType?.ToString();
        }
        return new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent("{\"errcode\":0}", Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>Returns a sequence of status codes (cycles through on overflow).</summary>
internal sealed class SequencedMockHandler : HttpMessageHandler
{
    private readonly HttpStatusCode[] _codes;
    private int _index;
    public int CallCount { get; private set; }

    public SequencedMockHandler(params HttpStatusCode[] codes) => _codes = codes;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        var code = _codes[Math.Min(_index++, _codes.Length - 1)];
        return Task.FromResult(new HttpResponseMessage(code)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>Returns the same <see cref="HttpClient"/> for any name.</summary>
internal sealed class FixedHttpClientFactory : System.Net.Http.IHttpClientFactory
{
    private readonly HttpClient _client;
    public FixedHttpClientFactory(HttpClient client) => _client = client;
    public HttpClient CreateClient(string name) => _client;
}
