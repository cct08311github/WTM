#nullable enable
using System.Text.Json.Serialization;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// A single declarative action dispatched by the client-side
    /// <c>ff.DispatchAction</c> handler. Introduced in issue #789 Phase 3C
    /// as a CSP-safe replacement for the legacy <c>IsScript</c> response path
    /// which relied on client-side eval() of the response body.
    /// </summary>
    /// <remarks>
    /// Uses a flat schema with nullable fields + <see cref="JsonIgnoreCondition.WhenWritingNull"/>
    /// so each action serializes to a compact, discriminated payload.
    /// Unknown action types are logged and ignored by the client dispatcher.
    /// Construct instances via object initializer or through the fluent
    /// methods on <see cref="WtmActionResultExtension"/>.
    /// </remarks>
    public class WtmAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("winId")]
        public string? WinId { get; set; }

        [JsonPropertyName("index")]
        public int? Index { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
