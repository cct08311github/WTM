#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Support.Email;

/// <summary>
/// Abstraction for sending e-mail notifications from WTM components.
/// The default registration is a no-op so that resolving this service never throws
/// when e-mail has not been configured.  Opt-in via
/// <see cref="EmailServiceCollectionExtensions.AddWtmEmail"/>.
/// </summary>
public interface IWtmEmailService
{
    /// <summary>
    /// Sends <paramref name="msg"/> asynchronously.
    /// </summary>
    /// <param name="msg">The message to deliver. Must not be <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the message has been accepted by the transport.</returns>
    Task SendAsync(EmailMessage msg, CancellationToken ct = default);
}
