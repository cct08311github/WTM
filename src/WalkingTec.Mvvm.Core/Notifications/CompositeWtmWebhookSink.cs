#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core.Notifications;

/// <summary>
/// Fan-out sink that dispatches the same <see cref="WebhookMessage"/> to every
/// registered <see cref="IWtmWebhookSink"/> in parallel.  Individual sink failures
/// are logged and do not abort the fan-out — all sinks are attempted regardless.
/// </summary>
/// <remarks>
/// This is an opt-in composition layer; when only a single sink is registered the
/// framework injects that concrete sink directly and this wrapper is not used.
/// The DI registration in <see cref="WebhookServiceCollectionExtensions.AddWtmWebhookSinks"/>
/// automatically wraps multiple sinks in this class.
/// </remarks>
public sealed class CompositeWtmWebhookSink : IWtmWebhookSink
{
    private readonly IReadOnlyList<IWtmWebhookSink> _sinks;
    private readonly ILogger<CompositeWtmWebhookSink> _logger;

    public CompositeWtmWebhookSink(
        IReadOnlyList<IWtmWebhookSink> sinks,
        ILogger<CompositeWtmWebhookSink> logger)
    {
        _sinks  = sinks  ?? throw new ArgumentNullException(nameof(sinks));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Dispatches <paramref name="message"/> to all inner sinks in parallel.
    /// All sinks are attempted even if some fail.
    /// </summary>
    public async Task SendAsync(WebhookMessage message, CancellationToken ct = default)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));

        var tasks = new Task[_sinks.Count];
        for (var i = 0; i < _sinks.Count; i++)
        {
            var sink = _sinks[i];
            tasks[i] = SendSafeAsync(sink, message, ct);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task SendSafeAsync(IWtmWebhookSink sink, WebhookMessage message, CancellationToken ct)
    {
        try
        {
            await sink.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "WtmWebhookSink: sink {SinkType} failed to deliver message '{Title}'.",
                sink.GetType().Name, message.Title);
        }
    }
}
