#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Notifications;

/// <summary>
/// Abstraction for sending SMS messages.
/// This seam is opt-in: by default a no-op implementation is registered.
/// Register a real provider by replacing the registration, for example:
/// <code>
///   services.AddSingleton&lt;ISmsSender, MyAliyunSmsSender&gt;();
/// </code>
/// No cloud SDK is bundled — the host application supplies the concrete implementation.
/// (issue #424)
/// </summary>
public interface ISmsSender
{
    /// <summary>
    /// Sends an SMS message to <paramref name="phoneNumber"/>.
    /// </summary>
    /// <param name="phoneNumber">
    /// Destination phone number. Format is implementation-specific
    /// (e.g., <c>+8613812345678</c> or <c>13812345678</c>).
    /// </param>
    /// <param name="message">Plain-text message body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> when the message was accepted for delivery;
    /// <c>false</c> when delivery failed (no exception is thrown for recoverable failures).
    /// </returns>
    Task<bool> SendAsync(string phoneNumber, string message, CancellationToken ct = default);

    /// <summary>
    /// Sends an SMS message using a provider-managed template.
    /// </summary>
    /// <param name="phoneNumber">
    /// Destination phone number. Format is implementation-specific.
    /// </param>
    /// <param name="templateCode">
    /// Provider-assigned template identifier (e.g., <c>SMS_12345678</c>).
    /// </param>
    /// <param name="templateParams">
    /// Key/value pairs interpolated into the template by the provider.
    /// Pass an empty array or <c>null</c> if the template has no parameters.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> when the message was accepted for delivery;
    /// <c>false</c> when delivery failed.
    /// </returns>
    Task<bool> SendTemplateAsync(
        string phoneNumber,
        string templateCode,
        System.Collections.Generic.IReadOnlyList<System.Collections.Generic.KeyValuePair<string, string>>? templateParams,
        CancellationToken ct = default);
}
