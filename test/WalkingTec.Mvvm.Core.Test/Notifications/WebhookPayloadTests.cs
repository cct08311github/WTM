#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Notifications;
using WalkingTec.Mvvm.Core.Notifications.Providers;

namespace WalkingTec.Mvvm.Core.Test.Notifications;

/// <summary>
/// Tests for issue #219: verifies that each provider adapter serialises
/// a <see cref="WebhookMessage"/> into the expected JSON card shape.
/// No real HTTP calls are made.
/// </summary>
[TestClass]
public class WebhookPayloadTests
{
    private static WebhookMessage SampleMessage(WebhookLevel level = WebhookLevel.Info) =>
        new()
        {
            Title  = "ETL pipeline finished",
            Body   = "Job **LoadCustomers** completed.",
            Level  = level,
            Fields = new[]
            {
                new KeyValuePair<string, string>("Rows processed", "12345"),
                new KeyValuePair<string, string>("Duration",       "1m 23s")
            }
        };

    // ── DingTalk ─────────────────────────────────────────────────────────

    [TestMethod]
    public void DingTalk_payload_has_markdown_msgtype()
    {
        var msg = SampleMessage();
        var sink = new DingTalkTestHelper();
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        Assert.AreEqual("markdown", doc.GetProperty("msgtype").GetString());
    }

    [TestMethod]
    public void DingTalk_payload_contains_title_in_markdown_node()
    {
        var msg = SampleMessage();
        var sink = new DingTalkTestHelper();
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        var title = doc.GetProperty("markdown").GetProperty("title").GetString();
        Assert.IsNotNull(title);
        StringAssert.Contains(title, "ETL pipeline finished");
    }

    [TestMethod]
    public void DingTalk_payload_contains_body_and_fields_in_text()
    {
        var msg = SampleMessage();
        var sink = new DingTalkTestHelper();
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        var text = doc.GetProperty("markdown").GetProperty("text").GetString()!;
        StringAssert.Contains(text, "LoadCustomers");
        StringAssert.Contains(text, "Rows processed");
        StringAssert.Contains(text, "12345");
    }

    [TestMethod]
    [DataRow(WebhookLevel.Warning, "⚠️")]
    [DataRow(WebhookLevel.Error,   "🚨")]
    [DataRow(WebhookLevel.Info,    "ℹ️")]
    public void DingTalk_payload_text_contains_level_emoji(WebhookLevel level, string emoji)
    {
        var msg = SampleMessage(level);
        var sink = new DingTalkTestHelper();
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        var text = doc.GetProperty("markdown").GetProperty("text").GetString()!;
        StringAssert.Contains(text, emoji);
    }

    // ── DingTalk signing ──────────────────────────────────────────────────

    [TestMethod]
    public void DingTalk_signed_url_contains_timestamp_and_sign()
    {
        var options = new WtmWebhookOptions
        {
            WebhookUrl = "https://oapi.dingtalk.com/robot/send?access_token=abc",
            Secret     = "mysecret123"
        };
        var url = DingTalkWebhookSink_Exposed.BuildUrl(options);

        StringAssert.Contains(url, "timestamp=");
        StringAssert.Contains(url, "sign=");
    }

    [TestMethod]
    public void DingTalk_unsigned_url_is_unchanged()
    {
        var options = new WtmWebhookOptions
        {
            WebhookUrl = "https://oapi.dingtalk.com/robot/send?access_token=abc",
            Secret     = null
        };
        var url = DingTalkWebhookSink_Exposed.BuildUrl(options);
        Assert.AreEqual(options.WebhookUrl, url);
    }

    // ── WeCom ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void WeCom_payload_has_markdown_msgtype_and_content()
    {
        var msg = SampleMessage();
        var sink = new WeComTestHelper();
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        Assert.AreEqual("markdown", doc.GetProperty("msgtype").GetString());

        var content = doc.GetProperty("markdown").GetProperty("content").GetString()!;
        StringAssert.Contains(content, "ETL pipeline finished");
    }

    [TestMethod]
    public void WeCom_payload_contains_fields()
    {
        var msg = SampleMessage();
        var sink = new WeComTestHelper();
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        var content = doc.GetProperty("markdown").GetProperty("content").GetString()!;
        StringAssert.Contains(content, "Rows processed");
        StringAssert.Contains(content, "1m 23s");
    }

    [TestMethod]
    [DataRow(WebhookLevel.Warning, "warning")]
    [DataRow(WebhookLevel.Error,   "warning")]   // WeCom maps Error → warning colour
    [DataRow(WebhookLevel.Info,    "info")]
    public void WeCom_payload_contains_colour_tag(WebhookLevel level, string colourTag)
    {
        var msg = SampleMessage(level);
        var sink = new WeComTestHelper();
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        var content = doc.GetProperty("markdown").GetProperty("content").GetString()!;
        StringAssert.Contains(content, colourTag);
    }

    // ── Feishu ────────────────────────────────────────────────────────────

    [TestMethod]
    public void Feishu_payload_has_post_msg_type()
    {
        var msg = SampleMessage();
        var sink = new FeishuTestHelper(secret: null);
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        Assert.AreEqual("post", doc.GetProperty("msg_type").GetString());
    }

    [TestMethod]
    public void Feishu_payload_contains_title_and_body()
    {
        var msg = SampleMessage();
        var sink = new FeishuTestHelper(secret: null);
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        var zhContent = doc
            .GetProperty("content")
            .GetProperty("post")
            .GetProperty("zh_cn");
        var title = zhContent.GetProperty("title").GetString()!;
        StringAssert.Contains(title, "ETL pipeline finished");

        // Content rows — flatten all text elements and search for body text
        var contentJson = zhContent.GetProperty("content").ToString()!;
        StringAssert.Contains(contentJson, "LoadCustomers");
    }

    [TestMethod]
    public void Feishu_signed_payload_contains_timestamp_and_sign()
    {
        var msg = SampleMessage();
        var sink = new FeishuTestHelper(secret: "feishu-secret");
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        Assert.IsTrue(doc.TryGetProperty("timestamp", out _), "Expected 'timestamp' in signed Feishu payload");
        Assert.IsTrue(doc.TryGetProperty("sign", out _),      "Expected 'sign' in signed Feishu payload");
    }

    [TestMethod]
    public void Feishu_unsigned_payload_has_no_sign_field()
    {
        var msg = SampleMessage();
        var sink = new FeishuTestHelper(secret: null);
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        Assert.IsFalse(doc.TryGetProperty("sign", out _), "Unsigned Feishu payload must not contain 'sign'");
    }

    [TestMethod]
    public void Feishu_ComputeSign_is_deterministic_for_same_inputs()
    {
        var ts = 1_700_000_000L;
        var s1 = FeishuWebhookSink.ComputeSign(ts, "secret");
        var s2 = FeishuWebhookSink.ComputeSign(ts, "secret");
        Assert.AreEqual(s1, s2);
    }

    [TestMethod]
    public void Feishu_ComputeSign_differs_for_different_secrets()
    {
        var ts = 1_700_000_000L;
        var s1 = FeishuWebhookSink.ComputeSign(ts, "secretA");
        var s2 = FeishuWebhookSink.ComputeSign(ts, "secretB");
        Assert.AreNotEqual(s1, s2);
    }

    // ── Slack ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Slack_payload_has_blocks_and_attachments()
    {
        var msg = SampleMessage();
        var sink = new SlackTestHelper();
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        Assert.IsTrue(doc.TryGetProperty("blocks", out var blocks));
        Assert.IsTrue(doc.TryGetProperty("attachments", out var attachments));
        Assert.IsTrue(blocks.GetArrayLength() > 0);
        Assert.IsTrue(attachments.GetArrayLength() > 0);
    }

    [TestMethod]
    public void Slack_payload_header_block_contains_title()
    {
        var msg = SampleMessage();
        var sink = new SlackTestHelper();
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        var firstBlock = doc.GetProperty("blocks")[0];
        Assert.AreEqual("header", firstBlock.GetProperty("type").GetString());
        var text = firstBlock.GetProperty("text").GetProperty("text").GetString()!;
        StringAssert.Contains(text, "ETL pipeline finished");
    }

    [TestMethod]
    [DataRow(WebhookLevel.Warning, "#FFC200")]
    [DataRow(WebhookLevel.Error,   "#D40E0D")]
    [DataRow(WebhookLevel.Info,    "#2EB67D")]
    public void Slack_payload_attachment_has_correct_colour(WebhookLevel level, string color)
    {
        var msg = SampleMessage(level);
        var sink = new SlackTestHelper();
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        var attachment = doc.GetProperty("attachments")[0];
        Assert.AreEqual(color, attachment.GetProperty("color").GetString());
    }

    [TestMethod]
    public void Slack_payload_fields_block_present_when_fields_exist()
    {
        var msg = SampleMessage();  // has Fields
        var sink = new SlackTestHelper();
        var json = sink.GetPayload(msg);
        // There should be a section block with "fields" array
        StringAssert.Contains(json, "\"fields\"");
    }

    // ── Microsoft Teams ───────────────────────────────────────────────────

    [TestMethod]
    public void Teams_payload_has_MessageCard_type_and_context()
    {
        var msg = SampleMessage();
        var sink = new TeamsTestHelper();
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        Assert.AreEqual("MessageCard", doc.GetProperty("@type").GetString());
        Assert.AreEqual("https://schema.org/extensions", doc.GetProperty("@context").GetString());
    }

    [TestMethod]
    public void Teams_payload_title_contains_level_and_message_title()
    {
        var msg = SampleMessage(WebhookLevel.Error);
        var sink = new TeamsTestHelper();
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        var title = doc.GetProperty("title").GetString()!;
        StringAssert.Contains(title, "ERROR");
        StringAssert.Contains(title, "ETL pipeline finished");
    }

    [TestMethod]
    [DataRow(WebhookLevel.Warning, "FFC200")]
    [DataRow(WebhookLevel.Error,   "D40E0D")]
    [DataRow(WebhookLevel.Info,    "0078D7")]
    public void Teams_payload_has_correct_theme_color(WebhookLevel level, string color)
    {
        var msg = SampleMessage(level);
        var sink = new TeamsTestHelper();
        var json = sink.GetPayload(msg);

        var doc = JsonDocument.Parse(json).RootElement;
        Assert.AreEqual(color, doc.GetProperty("themeColor").GetString());
    }

    [TestMethod]
    public void Teams_payload_sections_contain_facts_for_fields()
    {
        var msg = SampleMessage();
        var sink = new TeamsTestHelper();
        var json = sink.GetPayload(msg);

        StringAssert.Contains(json, "\"facts\"");
        StringAssert.Contains(json, "Rows processed");
    }
}

// ── Test-only subclasses to expose internal BuildPayload / BuildRequestUrl ──

internal sealed class DingTalkTestHelper : DingTalkWebhookSink
{
    public DingTalkTestHelper()
        : base(NoOpHttpClientFactory.Instance,
               new WtmWebhookOptions { WebhookUrl = "https://oapi.dingtalk.com/robot/send?access_token=x" },
               Microsoft.Extensions.Logging.Abstractions.NullLogger<DingTalkWebhookSink>.Instance) { }

    public string GetPayload(WebhookMessage msg) => BuildPayload(msg);
}

internal static class DingTalkWebhookSink_Exposed
{
    // Exposes BuildRequestUrl via a test-subclass instance.
    public static string BuildUrl(WtmWebhookOptions options)
    {
        var helper = new DingTalkUrlHelper(options);
        return helper.GetUrl(options);
    }

    private sealed class DingTalkUrlHelper : DingTalkWebhookSink
    {
        public DingTalkUrlHelper(WtmWebhookOptions opts)
            : base(NoOpHttpClientFactory.Instance, opts,
                   Microsoft.Extensions.Logging.Abstractions.NullLogger<DingTalkWebhookSink>.Instance) { }

        public string GetUrl(WtmWebhookOptions opts) => BuildRequestUrl(opts);
    }
}

internal sealed class WeComTestHelper : WeComWebhookSink
{
    public WeComTestHelper()
        : base(NoOpHttpClientFactory.Instance,
               new WtmWebhookOptions { WebhookUrl = "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=x" },
               Microsoft.Extensions.Logging.Abstractions.NullLogger<WeComWebhookSink>.Instance) { }

    public string GetPayload(WebhookMessage msg) => BuildPayload(msg);
}

internal sealed class FeishuTestHelper : FeishuWebhookSink
{
    public FeishuTestHelper(string? secret)
        : base(NoOpHttpClientFactory.Instance,
               new WtmWebhookOptions
               {
                   WebhookUrl = "https://open.feishu.cn/open-apis/bot/v2/hook/xxx",
                   Secret     = secret
               },
               Microsoft.Extensions.Logging.Abstractions.NullLogger<FeishuWebhookSink>.Instance) { }

    public string GetPayload(WebhookMessage msg) => BuildPayload(msg);
}

internal sealed class SlackTestHelper : SlackWebhookSink
{
    public SlackTestHelper()
        : base(NoOpHttpClientFactory.Instance,
               new WtmWebhookOptions { WebhookUrl = "https://hooks.slack.com/services/T00/B00/xxx" },
               Microsoft.Extensions.Logging.Abstractions.NullLogger<SlackWebhookSink>.Instance) { }

    public string GetPayload(WebhookMessage msg) => BuildPayload(msg);
}

internal sealed class TeamsTestHelper : MicrosoftTeamsWebhookSink
{
    public TeamsTestHelper()
        : base(NoOpHttpClientFactory.Instance,
               new WtmWebhookOptions { WebhookUrl = "https://outlook.office.com/webhook/xxx" },
               Microsoft.Extensions.Logging.Abstractions.NullLogger<MicrosoftTeamsWebhookSink>.Instance) { }

    public string GetPayload(WebhookMessage msg) => BuildPayload(msg);
}

/// <summary>
/// A no-op <see cref="System.Net.Http.IHttpClientFactory"/> that satisfies the provider
/// constructor without requiring a real DI container.
/// </summary>
internal sealed class NoOpHttpClientFactory : System.Net.Http.IHttpClientFactory
{
    public static readonly NoOpHttpClientFactory Instance = new();
    private static readonly System.Net.Http.HttpClient _client = new();
    public System.Net.Http.HttpClient CreateClient(string name) => _client;
}
