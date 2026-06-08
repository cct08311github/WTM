#nullable enable
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core.Notifications.Providers;

/// <summary>
/// Slack webhook adapter — uses Block Kit with a <c>section</c> block for the body
/// and a colour-coded legacy <c>attachment</c> for the level indicator.
/// Compatible with Slack Incoming Webhooks.
/// </summary>
internal class SlackWebhookSink : WebhookProviderBase
{
    public SlackWebhookSink(
        System.Net.Http.IHttpClientFactory httpClientFactory,
        WtmWebhookOptions options,
        ILogger<SlackWebhookSink> logger)
        : base(httpClientFactory, options, logger) { }

    protected override string BuildPayload(WebhookMessage message)
    {
        var color = message.Level switch
        {
            WebhookLevel.Warning => "#FFC200",
            WebhookLevel.Error   => "#D40E0D",
            _                    => "#2EB67D"  // Slack green
        };

        var levelEmoji = message.Level switch
        {
            WebhookLevel.Warning => ":warning: ",
            WebhookLevel.Error   => ":red_circle: ",
            _                    => ":information_source: "
        };

        // Block Kit — section block for the body text
        var blocks = new List<object>
        {
            new Dictionary<string, object>
            {
                ["type"] = "header",
                ["text"] = new Dictionary<string, string>
                {
                    ["type"]  = "plain_text",
                    ["text"]  = levelEmoji + message.Title,
                    ["emoji"] = "true"
                }
            }
        };

        if (!string.IsNullOrWhiteSpace(message.Body))
        {
            blocks.Add(new Dictionary<string, object>
            {
                ["type"] = "section",
                ["text"] = new Dictionary<string, string>
                {
                    ["type"] = "mrkdwn",
                    ["text"] = message.Body
                }
            });
        }

        // Fields rendered as a two-column section
        if (message.Fields is { Count: > 0 })
        {
            var fieldObjects = new List<object>();
            foreach (var kv in message.Fields)
            {
                fieldObjects.Add(new Dictionary<string, string>
                {
                    ["type"] = "mrkdwn",
                    ["text"] = $"*{kv.Key}*\n{kv.Value}"
                });
            }
            blocks.Add(new Dictionary<string, object>
            {
                ["type"]   = "section",
                ["fields"] = fieldObjects
            });
        }

        // Legacy colour attachment for the level stripe on the left side
        var attachments = new List<object>
        {
            new Dictionary<string, object>
            {
                ["color"]    = color,
                ["fallback"] = $"[{message.Level}] {message.Title}"
            }
        };

        var doc = new Dictionary<string, object>
        {
            ["blocks"]      = blocks,
            ["attachments"] = attachments
        };

        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }
}
