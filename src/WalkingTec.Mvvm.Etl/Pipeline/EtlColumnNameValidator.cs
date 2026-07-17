#nullable enable
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WalkingTec.Mvvm.Etl.Pipeline;

/// <summary>
/// #680: defense-in-depth allowlist for staging column names entering the ETL
/// pipeline. Column names originate from the batch schema — CSV/Excel headers
/// or REST JSON keys — when <see cref="EtlPipelineConfig.ColumnMappings"/> is
/// not configured, and are later embedded directly into loader-generated SQL:
/// <list type="bullet">
/// <item>MSSQL: bracket-quoted (e.g. <c>[Name]</c>) in the MERGE/INSERT text
/// built by <see cref="Loaders.MssqlBulkLoader"/>.</item>
/// <item>Oracle: embedded UNQUOTED by <see cref="Loaders.OracleBulkLoader"/> —
/// deliberately, per #499, so identifiers case-fold consistently with the
/// USER_TABLES/USER_TAB_COLUMNS existence probes elsewhere in that loader.
/// Quoted identifiers are case-sensitive in Oracle; mixing quoted DML with the
/// uppercase dictionary probes caused ORA-00955 on run 2+ (see #499 for the
/// full case-folding rationale — that decision is NOT revisited here).</item>
/// </list>
/// Bracket-quoting alone was not sufficient (MSSQL's quoter did not escape a
/// literal <c>]</c> — see the sibling fix in <c>MssqlBulkLoader.QuoteIdentifier</c>)
/// and the Oracle path has no quoting at all, so both loaders depend on this
/// allowlist running exactly once, here, before either loader ever sees the
/// column name. Enforcing <c>^[\p{L}\p{N}_#$]+$</c> means every character that
/// would otherwise require quoting/escaping (space, <c>]</c>, <c>'</c>,
/// <c>;</c>, <c>--</c>, …) is rejected outright — the Oracle unquoted path
/// becomes safe by construction because allowlisted characters never need
/// quoting.
/// <para>
/// <c>\p{L}</c> (Unicode "Letter" categories) and <c>\p{N}</c> (Unicode
/// "Number" categories) are used instead of <c>A-Za-z0-9</c> so that
/// non-ASCII identifiers — notably CJK column headers such as
/// <c>订单编号</c> or <c>客户名称</c>, routine in this framework's primary
/// (Chinese) audience's CSV/Excel sources — are accepted. This is
/// intentionally a Unicode-letter/number allowlist, not an ASCII-only one:
/// the security boundary is the *category* of character permitted (no SQL
/// metacharacter, whitespace, or punctuation survives the allowlist),
/// not the *script*. .NET's regex engine classifies CJK Unified Ideographs
/// as Unicode category Lo ("Letter, other"), so they match <c>\p{L}</c>.
/// </para>
/// <para>
/// A source column that legitimately needs a disallowed character can be
/// renamed to a conforming target name via
/// <see cref="EtlPipelineConfig.ColumnMappings"/> before it reaches the loader
/// — this is intentionally a rejection, not a silent strip/rename, so a
/// misconfigured pipeline fails loudly instead of quietly loading under a
/// mangled column name.
/// </para>
/// </summary>
public static class EtlColumnNameValidator
{
    /// <summary>
    /// Oracle identifiers permit letters, digits, <c>_</c>, <c>#</c>, <c>$</c>
    /// — and Oracle's own letter/digit classes are Unicode-aware for
    /// non-ASCII database character sets (e.g. AL32UTF8), so restricting
    /// this allowlist's "letter"/"digit" classes to ASCII would be an
    /// unnecessary compatibility regression, not an Oracle requirement.
    /// MSSQL is at least as permissive. This validator intentionally applies
    /// the stricter, symbol-restricted allowlist regardless of target
    /// provider (only <c>_</c>, <c>#</c>, <c>$</c> permitted beyond
    /// Unicode letters/digits) so a job's column set behaves identically
    /// against either target rather than silently working on one and
    /// failing (or worse, being exploitable) on the other.
    /// </summary>
    public static readonly Regex AllowedColumnName =
        new(@"^[\p{L}\p{N}_#$]+$", RegexOptions.Compiled);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="columnName"/> is
    /// non-null/non-empty and matches <see cref="AllowedColumnName"/>.
    /// </summary>
    public static bool IsValid(string? columnName) =>
        !string.IsNullOrEmpty(columnName) && AllowedColumnName.IsMatch(columnName);

    /// <summary>
    /// Validates every name in <paramref name="columnNames"/> against the
    /// allowlist. Throws <see cref="System.ArgumentException"/> naming the
    /// first offending column on the first violation found (fail-fast —
    /// mirrors the existing <c>MssqlBulkLoader.IsSafeWhereClause</c> guard
    /// style rather than collecting every violation).
    /// </summary>
    public static void ValidateOrThrow(IEnumerable<string> columnNames)
    {
        foreach (var name in columnNames)
        {
            if (!IsValid(name))
            {
                throw new System.ArgumentException(
                    $"Staging column name '{name}' is not allowed. Column names must " +
                    @"match ^[\p{L}\p{N}_#$]+$ (Unicode letters/digits, underscore, '#', '$' only). " +
                    "Rename non-conforming source columns via EtlPipelineConfig.ColumnMappings.",
                    nameof(columnNames));
            }
        }
    }
}
