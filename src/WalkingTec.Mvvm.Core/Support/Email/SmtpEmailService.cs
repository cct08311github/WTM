#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core.Support.Email;

/// <summary>
/// <see cref="IWtmEmailService"/> implementation backed by <see cref="System.Net.Mail.SmtpClient"/>
/// (BCL — no additional NuGet packages required).
/// </summary>
/// <remarks>
/// Override <see cref="CreateTransport"/> in a subclass to substitute a fake transport during
/// unit testing — this avoids any real SMTP connection.
/// When <see cref="EmailOptions.Enabled"/> is <c>false</c> the service is a silent no-op,
/// making it safe to register in all environments and enable only via configuration.
/// </remarks>
public class SmtpEmailService : IWtmEmailService
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailService> _logger;

    /// <summary>
    /// Initialises a new instance with the supplied options and logger.
    /// </summary>
    public SmtpEmailService(EmailOptions options, ILogger<SmtpEmailService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage msg, CancellationToken ct = default)
    {
        if (msg is null) throw new ArgumentNullException(nameof(msg));
        if (msg.To is not { Length: > 0 })
            throw new ArgumentException("at least one To recipient is required", nameof(msg));

        if (!_options.Enabled)
        {
            _logger.LogDebug("WTM email is disabled — message to '{To}' dropped.", string.Join(", ", msg.To));
            return;
        }

        ct.ThrowIfCancellationRequested();

        using var mail      = BuildMailMessage(msg);
        using var transport = CreateTransport(_options);

        try
        {
            await transport.SendMailAsync(mail, ct).ConfigureAwait(false);
            _logger.LogInformation("Email sent to '{To}', subject '{Subject}'.",
                string.Join(", ", msg.To), msg.Subject);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to send email to '{To}', subject '{Subject}'.",
                string.Join(", ", msg.To), msg.Subject);
            throw;
        }
    }

    // ── Overridable factory ────────────────────────────────────────────────

    /// <summary>
    /// Creates the <see cref="ISmtpTransport"/> used to deliver the message.
    /// Override this method in unit-test subclasses to inject a fake transport without
    /// requiring a live SMTP server.
    /// </summary>
    /// <param name="options">Current e-mail options.</param>
    /// <returns>A transport ready to send mail.</returns>
    protected virtual ISmtpTransport CreateTransport(EmailOptions options)
    {
        var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl             = options.EnableSsl,
            DeliveryMethod        = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false
        };

        if (!string.IsNullOrEmpty(options.UserName))
        {
            client.Credentials = new NetworkCredential(options.UserName, options.Password);
        }

        return new SmtpClientTransport(client);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private MailMessage BuildMailMessage(EmailMessage msg)
    {
        var from = string.IsNullOrEmpty(_options.FromDisplayName)
            ? new MailAddress(_options.FromAddress)
            : new MailAddress(_options.FromAddress, _options.FromDisplayName);

        var mail = new MailMessage
        {
            From       = from,
            Subject    = msg.Subject,
            Body       = msg.Body,
            IsBodyHtml = msg.IsHtml
        };

        foreach (var to in msg.To)
            mail.To.Add(to);

        foreach (var cc in msg.Cc)
            mail.CC.Add(cc);

        foreach (var attachment in msg.Attachments)
        {
            var stream = new MemoryStream(attachment.Data);
            var contentType = string.IsNullOrEmpty(attachment.ContentType)
                ? "application/octet-stream"
                : attachment.ContentType;

            mail.Attachments.Add(new Attachment(stream, attachment.FileName, contentType));
        }

        return mail;
    }

    // ── Default real transport adapter ────────────────────────────────────────

    /// <summary>
    /// Wraps a real <see cref="SmtpClient"/> as an <see cref="ISmtpTransport"/>.
    /// Disposed together with the adapter.
    /// </summary>
    private sealed class SmtpClientTransport : ISmtpTransport, IDisposable
    {
        private readonly SmtpClient _client;

        public SmtpClientTransport(SmtpClient client) => _client = client;

        public Task SendMailAsync(MailMessage message, CancellationToken cancellationToken = default)
            => _client.SendMailAsync(message, cancellationToken);

        public void Dispose() => _client.Dispose();
    }
}
