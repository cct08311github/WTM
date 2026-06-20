#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Support.Email;

/// <summary>
/// A no-op <see cref="IWtmEmailService"/> that silently discards every message.
/// Registered as the default implementation so that resolving <see cref="IWtmEmailService"/>
/// from DI never throws when e-mail has not been configured.
/// Replace it by calling <see cref="EmailServiceCollectionExtensions.AddWtmEmail"/>.
/// </summary>
internal sealed class NullEmailService : IWtmEmailService
{
    /// <summary>Shared singleton instance.</summary>
    internal static readonly NullEmailService Instance = new();

    /// <inheritdoc />
    public Task SendAsync(EmailMessage msg, CancellationToken ct = default)
        => Task.CompletedTask;
}
