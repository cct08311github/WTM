#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Notifications;

/// <summary>
/// Abstraction for dispatching structured notifications to an external webhook endpoint.
/// Implementations are opt-in and registered via
/// <see cref="WebhookServiceCollectionExtensions.AddWtmWebhookSink"/>.
/// </summary>
/// <remarks>
/// The sink is the shared foundation consumed by both Dashboard KPI alert integrations
/// and ETL pipeline alerts (issue #219).  Multiple sinks may be registered and composed
/// via the fan-out wrapper <see cref="CompositeWtmWebhookSink"/>.
/// </remarks>
public interface IWtmWebhookSink
{
    /// <summary>
    /// Sends <paramref name="message"/> to the configured webhook endpoint.
    /// </summary>
    /// <param name="message">Message to deliver. Must not be <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the message has been delivered (or retries exhausted).</returns>
    Task SendAsync(WebhookMessage message, CancellationToken ct = default);
}
