#nullable enable
using System;

namespace WalkingTec.Mvvm.Core.Support.Email;

/// <summary>
/// Represents an outbound e-mail message delivered through <see cref="IWtmEmailService"/>.
/// </summary>
public sealed class EmailMessage
{
    /// <summary>
    /// One or more recipient e-mail addresses.  At least one entry is required.
    /// </summary>
    public string[] To { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Optional carbon-copy recipients.
    /// </summary>
    public string[] Cc { get; set; } = Array.Empty<string>();

    /// <summary>
    /// E-mail subject line.
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Message body — either plain text or HTML depending on <see cref="IsHtml"/>.
    /// </summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// When <c>true</c> the body is treated as HTML; otherwise as plain text.
    /// Default: <c>false</c>.
    /// </summary>
    public bool IsHtml { get; set; }

    /// <summary>
    /// Optional file attachments expressed as (FileName, Data) pairs.
    /// </summary>
    public EmailAttachment[] Attachments { get; set; } = Array.Empty<EmailAttachment>();
}
