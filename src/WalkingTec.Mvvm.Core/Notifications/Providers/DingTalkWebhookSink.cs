#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core.Notifications.Providers;

/// <summary>
/// 钉钉 (DingTalk) webhook adapter — uses the <c>markdown</c> msgtype.
/// When <see cref="WtmWebhookOptions.Secret"/> is set the request URL is
/// signed with HMAC-SHA256 (timestamp + "\n" + secret) per the DingTalk
/// Custom Robot docs (v2 signing mode).
/// </summary>
internal class DingTalkWebhookSink : WebhookProviderBase
{
    public DingTalkWebhookSink(
        System.Net.Http.IHttpClientFactory httpClientFactory,
        WtmWebhookOptions options,
        ILogger<DingTalkWebhookSink> logger)
        : base(httpClientFactory, options, logger) { }

    protected override string BuildPayload(WebhookMessage message)
    {
        // DingTalk markdown card — level prefix in the title
        var levelPrefix = message.Level switch
        {
            WebhookLevel.Warning => "⚠️ ",
            WebhookLevel.Error   => "🚨 ",
            _                    => "ℹ️ "
        };

        var sb = new StringBuilder();
        sb.Append("## ").Append(levelPrefix).AppendLine(message.Title);
        if (!string.IsNullOrWhiteSpace(message.Body))
        {
            sb.AppendLine();
            sb.AppendLine(message.Body);
        }
        AppendFields(sb, message.Fields);

        // DingTalk markdown msgtype payload
        var doc = new Dictionary<string, object>
        {
            ["msgtype"] = "markdown",
            ["markdown"] = new Dictionary<string, object>
            {
                ["title"]   = message.Title,
                ["text"]    = sb.ToString()
            }
        };

        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    protected override string BuildRequestUrl(WtmWebhookOptions options)
    {
        if (string.IsNullOrEmpty(options.Secret))
            return options.WebhookUrl;

        // DingTalk v2 sign: timestamp + "\n" + secret → HMAC-SHA256 → Base64 → URL-encode
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var stringToSign = $"{timestamp}\n{options.Secret}";
        var keyBytes = Encoding.UTF8.GetBytes(options.Secret);
        var msgBytes = Encoding.UTF8.GetBytes(stringToSign);
        var hash = HMACSHA256.HashData(keyBytes, msgBytes);
        var sign = Uri.EscapeDataString(Convert.ToBase64String(hash));

        var separator = options.WebhookUrl.Contains('?') ? "&" : "?";
        return $"{options.WebhookUrl}{separator}timestamp={timestamp}&sign={sign}";
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    internal static void AppendFields(
        StringBuilder sb,
        IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        if (fields is { Count: > 0 })
        {
            sb.AppendLine();
            foreach (var kv in fields)
            {
                sb.Append("- **").Append(kv.Key).Append("**: ").AppendLine(kv.Value);
            }
        }
    }
}
