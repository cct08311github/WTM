using System;
using System.Text;

namespace WalkingTec.Mvvm.Core;

/// <summary>
/// Strips control characters (CR, LF, TAB and other non-printable controls)
/// from values before they are interpolated into log messages, to prevent
/// log-forging attacks (CWE-117, CodeQL cs/log-forging).
///
/// Defense-in-depth: Serilog's structured logging already escapes these
/// characters in rendered output, but explicit sanitization is required to
/// satisfy static-analysis tools such as CodeQL.
/// </summary>
public static class LogSanitizer
{
    /// <summary>
    /// Sanitizes <paramref name="value"/> for safe inclusion in a log message.
    /// CR (\r), LF (\n) and TAB (\t) are replaced with a space.
    /// All other control characters are dropped.
    /// The result is truncated to <paramref name="maxLength"/> characters
    /// (default 200) and a <c>...[truncated]</c> suffix is appended when
    /// truncation occurs.
    /// </summary>
    /// <param name="value">The user-controlled value to sanitize.</param>
    /// <param name="maxLength">
    /// Maximum number of output characters before the truncation suffix is
    /// appended. Defaults to 200. Pass 0 to always return just the suffix.
    /// </param>
    /// <returns>A sanitized, length-bounded string safe for log messages.</returns>
    public static string Sanitize(string? value, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(Math.Min(value.Length, maxLength + 1));
        foreach (var c in value)
        {
            if (c == '\r' || c == '\n' || c == '\t') sb.Append(' ');
            else if (char.IsControl(c)) continue;
            else sb.Append(c);
        }

        return sb.Length > maxLength
            ? sb.ToString(0, maxLength) + "...[truncated]"
            : sb.ToString();
    }
}
