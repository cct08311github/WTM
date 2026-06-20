#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core.Notifications;

/// <summary>
/// A no-op <see cref="ISmsSender"/> that logs every call and returns
/// <c>true</c> without invoking any external SMS provider.
/// This implementation is registered by <see cref="SmsServiceCollectionExtensions.AddWtmSms"/>
/// so that <see cref="ISmsSender"/> is always resolvable from the container.
/// Replace it at startup to activate a real provider:
/// <code>
///   // After AddWtmSms() — the last registration wins.
///   services.AddSingleton&lt;ISmsSender, MyAliyunSmsSender&gt;();
/// </code>
/// (issue #424)
/// </summary>
internal class NullSmsSender : ISmsSender
{
    private readonly ILogger<NullSmsSender> _logger;

    public NullSmsSender(ILogger<NullSmsSender> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> SendAsync(string phoneNumber, string message, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "NullSmsSender: SendAsync called for phone={PhoneNumber} (no provider configured).",
            phoneNumber);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> SendTemplateAsync(
        string phoneNumber,
        string templateCode,
        IReadOnlyList<KeyValuePair<string, string>>? templateParams,
        CancellationToken ct = default)
    {
        _logger.LogDebug(
            "NullSmsSender: SendTemplateAsync called for phone={PhoneNumber}, template={TemplateCode} (no provider configured).",
            phoneNumber,
            templateCode);
        return Task.FromResult(true);
    }
}
