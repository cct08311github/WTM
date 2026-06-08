#nullable enable
using System;
using System.Text.RegularExpressions;

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// Sanitizes exception messages before storing in RunLog.
/// Prevents connection strings and stack traces from leaking into the database.
/// Full stack traces should be written to structured logging (ILogger) instead.
/// </summary>
public static class EtlErrorSanitizer
{
    // Patterns covering common ADO.NET / EF Core connection string fragments
    private static readonly Regex[] _sensitivePatterns =
    [
        new Regex(@"Password\s*=[^;""']*",   RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"Pwd\s*=[^;""']*",        RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"User Id\s*=[^;""']*",    RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"Uid\s*=[^;""']*",        RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"Data Source\s*=[^;""']*",RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"Server\s*=[^;""']*",     RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"Database\s*=[^;""']*",   RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private const int MaxLength = 2000;

    /// <summary>
    /// Returns a sanitized, length-capped message suitable for RunLog storage.
    /// Uses ex.Message only (no stack trace) and redacts connection string fragments.
    /// </summary>
    public static string Sanitize(Exception ex)
    {
        return SanitizeRaw(ex.Message);
    }

    /// <summary>
    /// Returns a sanitized, length-capped copy of the given raw string,
    /// redacting connection string fragments.
    /// Used by alert dispatch paths for defence-in-depth sanitization of
    /// already-stored error messages before they leave the system via webhooks.
    /// </summary>
    public static string SanitizeRaw(string raw)
    {
        var message = raw;
        foreach (var pattern in _sensitivePatterns)
            message = pattern.Replace(message, "[redacted]");
        return message.Length > MaxLength ? message[..MaxLength] : message;
    }
}
