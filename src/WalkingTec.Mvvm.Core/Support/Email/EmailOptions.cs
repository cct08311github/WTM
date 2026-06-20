#nullable enable

namespace WalkingTec.Mvvm.Core.Support.Email;

/// <summary>
/// Configuration for the WTM SMTP e-mail service.
/// Bind via an <see cref="System.Action{EmailOptions}"/> delegate passed to
/// <see cref="EmailServiceCollectionExtensions.AddWtmEmail"/>.
/// </summary>
/// <remarks>
/// This class is intentionally independent of <c>Configs.cs</c> — it belongs solely
/// to the opt-in e-mail subsystem and carries no default-behaviour implications.
/// </remarks>
public sealed class EmailOptions
{
    /// <summary>
    /// SMTP server host name or IP address (e.g. <c>"smtp.gmail.com"</c>).
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// SMTP server port.  Typical values: 25 (relay), 465 (SSL), 587 (STARTTLS).
    /// Default: <c>587</c>.
    /// </summary>
    public int Port { get; set; } = 587;

    /// <summary>
    /// When <c>true</c> the connection is wrapped in SSL/TLS.
    /// Default: <c>true</c>.
    /// </summary>
    public bool EnableSsl { get; set; } = true;

    /// <summary>
    /// SMTP authentication user name.
    /// Leave empty if the relay accepts unauthenticated connections.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// SMTP authentication password.  Never logged.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// The e-mail address placed in the <c>From</c> header.
    /// </summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>
    /// The display name placed in the <c>From</c> header (e.g. <c>"My App Alerts"</c>).
    /// Optional — when <c>null</c> only the address is used.
    /// </summary>
    public string? FromDisplayName { get; set; }

    /// <summary>
    /// Master switch.  When <c>false</c> (the default) no e-mails are sent even if
    /// <c>AddWtmEmail</c> has been called, making it safe to register in all
    /// environments and enable only in production via configuration.
    /// </summary>
    public bool Enabled { get; set; } = false;
}
