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
/// 飞书 (Feishu / Lark) webhook adapter — uses the interactive card (<c>post</c>) message type.
/// When <see cref="WtmWebhookOptions.Secret"/> is set the payload includes a
/// <c>sign</c> field computed as HMAC-SHA256(key=secret, msg="{timestamp}\n{secret}") per Feishu docs.
/// </summary>
internal class FeishuWebhookSink : WebhookProviderBase
{
    private readonly string? _secret;

    public FeishuWebhookSink(
        System.Net.Http.IHttpClientFactory httpClientFactory,
        WtmWebhookOptions options,
        ILogger<FeishuWebhookSink> logger)
        : base(httpClientFactory, options, logger)
    {
        _secret = options?.Secret;
    }

    protected override string BuildPayload(WebhookMessage message)
    {
        var levelEmoji = message.Level switch
        {
            WebhookLevel.Warning => "⚠️ ",
            WebhookLevel.Error   => "🚨 ",
            _                    => "ℹ️ "
        };

        // Build content as a list of element rows for the "post" rich-text card
        var paragraphs = new List<object[]>();

        if (!string.IsNullOrWhiteSpace(message.Body))
        {
            paragraphs.Add(new object[]
            {
                new Dictionary<string, string>
                {
                    ["tag"]  = "text",
                    ["text"] = message.Body
                }
            });
        }

        foreach (var kv in message.Fields)
        {
            paragraphs.Add(new object[]
            {
                new Dictionary<string, string>
                {
                    ["tag"]  = "text",
                    ["text"] = $"{kv.Key}: {kv.Value}"
                }
            });
        }

        var postBody = new Dictionary<string, object>
        {
            ["zh_cn"] = new Dictionary<string, object>
            {
                ["title"]   = levelEmoji + message.Title,
                ["content"] = paragraphs
            }
        };

        var doc = new Dictionary<string, object>
        {
            ["msg_type"] = "post",
            ["content"]  = new Dictionary<string, object> { ["post"] = postBody }
        };

        if (!string.IsNullOrEmpty(_secret))
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            doc["timestamp"] = timestamp.ToString();
            doc["sign"]      = ComputeSign(timestamp, _secret);
        }

        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    // ── Signing helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Feishu sign: HMAC-SHA256(key=secret, msg="{timestampSeconds}\n{secret}"), Base64-encoded.
    /// </summary>
    internal static string ComputeSign(long timestampSeconds, string secret)
    {
        var stringToSign = $"{timestampSeconds}\n{secret}";
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var msgBytes = Encoding.UTF8.GetBytes(stringToSign);
        var hash = HMACSHA256.HashData(keyBytes, msgBytes);
        return Convert.ToBase64String(hash);
    }
}
