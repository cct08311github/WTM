#nullable enable
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core.Notifications.Providers;

/// <summary>
/// 企业微信 (WeCom / WeChat Work) webhook adapter — uses the <c>markdown</c> msgtype.
/// WeCom does not support server-side URL signing; the webhook URL itself is the secret.
/// </summary>
internal class WeComWebhookSink : WebhookProviderBase
{
    public WeComWebhookSink(
        System.Net.Http.IHttpClientFactory httpClientFactory,
        WtmWebhookOptions options,
        ILogger<WeComWebhookSink> logger)
        : base(httpClientFactory, options, logger) { }

    protected override string BuildPayload(WebhookMessage message)
    {
        var levelTag = message.Level switch
        {
            WebhookLevel.Warning => "<font color=\"warning\">",
            WebhookLevel.Error   => "<font color=\"warning\">", // WeCom only has warning/info/comment
            _                    => "<font color=\"info\">"
        };

        var sb = new StringBuilder();
        // WeCom markdown: bold via ** and coloured text via <font> tags
        sb.Append("**").Append(levelTag).Append(message.Title).Append("</font>**");
        if (!string.IsNullOrWhiteSpace(message.Body))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine(message.Body);
        }
        AppendFields(sb, message.Fields);

        var doc = new Dictionary<string, object>
        {
            ["msgtype"] = "markdown",
            ["markdown"] = new Dictionary<string, object>
            {
                ["content"] = sb.ToString()
            }
        };

        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    internal static void AppendFields(
        StringBuilder sb,
        System.Collections.Generic.IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        if (fields is { Count: > 0 })
        {
            sb.AppendLine();
            foreach (var kv in fields)
            {
                sb.Append("> **").Append(kv.Key).Append("**: ").AppendLine(kv.Value);
            }
        }
    }
}
