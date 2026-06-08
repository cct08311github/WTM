#nullable enable
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core.Notifications.Providers;

/// <summary>
/// Microsoft Teams webhook adapter — uses an Adaptive Card payload via
/// Teams Incoming Webhook (the "messageCard" / connector-card format that all
/// Teams tenants support without additional app registration).
/// </summary>
/// <remarks>
/// Payload follows the Office 365 Connector Card schema
/// (<c>@type=MessageCard</c>, <c>@context=https://schema.org/extensions</c>).
/// The newer Adaptive Card spec requires Workflows webhooks; this implementation
/// targets the widely-available connector-card endpoint for maximum compatibility.
/// </remarks>
internal class MicrosoftTeamsWebhookSink : WebhookProviderBase
{
    public MicrosoftTeamsWebhookSink(
        System.Net.Http.IHttpClientFactory httpClientFactory,
        WtmWebhookOptions options,
        ILogger<MicrosoftTeamsWebhookSink> logger)
        : base(httpClientFactory, options, logger) { }

    protected override string BuildPayload(WebhookMessage message)
    {
        var themeColor = message.Level switch
        {
            WebhookLevel.Warning => "FFC200",
            WebhookLevel.Error   => "D40E0D",
            _                    => "0078D7"   // Teams blue
        };

        var levelLabel = message.Level switch
        {
            WebhookLevel.Warning => "⚠ WARNING",
            WebhookLevel.Error   => "🚨 ERROR",
            _                    => "ℹ INFO"
        };

        // Sections contain facts (key/value pairs) and a body text block.
        var sections = new List<object>();

        if (!string.IsNullOrWhiteSpace(message.Body))
        {
            sections.Add(new Dictionary<string, object>
            {
                ["activityText"] = message.Body
            });
        }

        if (message.Fields is { Count: > 0 })
        {
            var facts = new List<object>();
            foreach (var kv in message.Fields)
            {
                facts.Add(new Dictionary<string, string>
                {
                    ["name"]  = kv.Key,
                    ["value"] = kv.Value
                });
            }
            sections.Add(new Dictionary<string, object> { ["facts"] = facts });
        }

        var doc = new Dictionary<string, object>
        {
            ["@type"]      = "MessageCard",
            ["@context"]   = "https://schema.org/extensions",
            ["themeColor"] = themeColor,
            ["summary"]    = message.Title,
            ["title"]      = $"{levelLabel}: {message.Title}",
            ["sections"]   = sections
        };

        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }
}
