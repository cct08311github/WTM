using System.Text.RegularExpressions;

namespace WalkingTec.Mvvm.Core;

/// <summary>
/// Strips control characters (CR, LF, tabs) from values before logging
/// to prevent log-forging attacks. Defense-in-depth — Serilog's structured
/// logging already escapes these, but explicit sanitization satisfies
/// static analysis tools (CodeQL cs/log-forging).
/// </summary>
public static partial class LogSanitizer
{
    [GeneratedRegex(@"[\r\n\t]")]
    private static partial Regex ControlChars();

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        return ControlChars().Replace(value, " ");
    }
}
