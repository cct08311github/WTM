#nullable enable
using System;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Support.Email;

/// <summary>
/// Internal abstraction over <see cref="System.Net.Mail.SmtpClient"/> that allows
/// unit tests to substitute a fake transport without a live SMTP server.
/// </summary>
/// <remarks>
/// This interface is exposed so that external test projects can override
/// <see cref="SmtpEmailService.CreateTransport"/> in a subclass without needing
/// <c>InternalsVisibleTo</c>.  Consumers should depend on <see cref="IWtmEmailService"/>
/// rather than on this transport abstraction directly.
/// </remarks>
public interface ISmtpTransport : IDisposable
{
    /// <summary>Sends <paramref name="message"/> asynchronously.</summary>
    Task SendMailAsync(MailMessage message, CancellationToken cancellationToken = default);
}
