#nullable enable
using System.Text.RegularExpressions;

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// Perf(#713): source-generated regex accessors for the compile-time-constant,
/// already-static-cached patterns used by <see cref="EtlErrorSanitizer"/>. See
/// <c>WalkingTec.Mvvm.Core.Helper.CoreRegexes</c> for the equivalent in Core.
/// </summary>
internal static partial class EtlRegexes
{
    // EtlErrorSanitizer — redacts connection-string fragments from exception messages
    // before they are persisted to RunLog or dispatched via webhook alerts.
    [GeneratedRegex(@"Password\s*=[^;""']*", RegexOptions.IgnoreCase)]
    internal static partial Regex PasswordFragmentRegex();

    [GeneratedRegex(@"Pwd\s*=[^;""']*", RegexOptions.IgnoreCase)]
    internal static partial Regex PwdFragmentRegex();

    [GeneratedRegex(@"User Id\s*=[^;""']*", RegexOptions.IgnoreCase)]
    internal static partial Regex UserIdFragmentRegex();

    [GeneratedRegex(@"Uid\s*=[^;""']*", RegexOptions.IgnoreCase)]
    internal static partial Regex UidFragmentRegex();

    [GeneratedRegex(@"Data Source\s*=[^;""']*", RegexOptions.IgnoreCase)]
    internal static partial Regex DataSourceFragmentRegex();

    [GeneratedRegex(@"Server\s*=[^;""']*", RegexOptions.IgnoreCase)]
    internal static partial Regex ServerFragmentRegex();

    [GeneratedRegex(@"Database\s*=[^;""']*", RegexOptions.IgnoreCase)]
    internal static partial Regex DatabaseFragmentRegex();
}
