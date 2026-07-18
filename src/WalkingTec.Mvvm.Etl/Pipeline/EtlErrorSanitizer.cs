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
    // Patterns covering common ADO.NET / EF Core connection string fragments.
    // Perf(#713): converted to [GeneratedRegex] accessors (EtlRegexes) — same pattern text
    // and RegexOptions.IgnoreCase as before; RegexOptions.Compiled is dropped as redundant
    // with source generation (the source-gen implementation is already compiled).
    private static readonly Regex[] _sensitivePatterns =
    [
        EtlRegexes.PasswordFragmentRegex(),
        EtlRegexes.PwdFragmentRegex(),
        EtlRegexes.UserIdFragmentRegex(),
        EtlRegexes.UidFragmentRegex(),
        EtlRegexes.DataSourceFragmentRegex(),
        EtlRegexes.ServerFragmentRegex(),
        EtlRegexes.DatabaseFragmentRegex(),
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
