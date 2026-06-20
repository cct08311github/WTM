#nullable enable

namespace WalkingTec.Mvvm.Core.Support.Email;

/// <summary>
/// A single file attachment included in an <see cref="EmailMessage"/>.
/// </summary>
public sealed class EmailAttachment
{
    /// <summary>
    /// File name shown to the recipient (e.g. <c>"report.pdf"</c>).
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Raw attachment bytes.
    /// </summary>
    public byte[] Data { get; set; } = System.Array.Empty<byte>();

    /// <summary>
    /// MIME content type (e.g. <c>"application/pdf"</c>).
    /// When <c>null</c> or empty the transport layer uses
    /// <c>"application/octet-stream"</c> as a fallback.
    /// </summary>
    public string? ContentType { get; set; }
}
