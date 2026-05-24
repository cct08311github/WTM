using System;
using System.IO;

namespace WalkingTec.Mvvm.Core.Helper;

/// <summary>
/// Centralised helper for safely combining file-system paths when one or more
/// path segments originate from external (user-supplied) input.
///
/// All methods perform a canonical-path check — using <see cref="Path.GetFullPath"/>
/// followed by a prefix comparison — to guarantee the resolved path stays inside
/// the designated base directory, regardless of any traversal sequences embedded
/// in the user-supplied segment.
/// </summary>
public static class SafePathHelper
{
    /// <summary>
    /// Combine <paramref name="baseDir"/> with a user-supplied relative path and
    /// verify the resolved absolute path stays within <paramref name="baseDir"/>.
    /// </summary>
    /// <param name="baseDir">
    ///   The trusted base directory. Must be an absolute path; if relative it is
    ///   resolved through <see cref="Path.GetFullPath"/> first.
    /// </param>
    /// <param name="userPath">
    ///   A user-supplied relative path segment appended to <paramref name="baseDir"/>.
    ///   Must not be null, empty, or whitespace-only; must not be an absolute (rooted)
    ///   path; must not contain null bytes or OS-invalid path characters; must resolve
    ///   within <paramref name="baseDir"/> after canonicalization.
    /// </param>
    /// <returns>
    ///   The fully-resolved, canonical absolute path that is guaranteed to reside
    ///   inside <paramref name="baseDir"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    ///   <paramref name="baseDir"/> or <paramref name="userPath"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentException">
    ///   <paramref name="userPath"/> is empty or whitespace, is rooted, contains
    ///   OS-invalid path characters (including null bytes), contains a <c>..</c>
    ///   traversal sequence, or resolves outside <paramref name="baseDir"/>.
    /// </exception>
    public static string SafeCombine(string baseDir, string userPath)
    {
        if (baseDir is null) throw new ArgumentNullException(nameof(baseDir));
        if (userPath is null) throw new ArgumentNullException(nameof(userPath));

        if (string.IsNullOrWhiteSpace(userPath))
            throw new ArgumentException("User-supplied path segment must not be null, empty, or whitespace.", nameof(userPath));

        // Reject null-byte injection and other characters that are invalid in OS paths.
        if (userPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new ArgumentException(
                "User-supplied path segment contains invalid path characters (including null bytes).", nameof(userPath));

        // Reject absolute overrides (e.g. "/etc/passwd", "C:\\Windows\\...").
        if (Path.IsPathRooted(userPath))
            throw new ArgumentException(
                "User-supplied path segment must be relative, not absolute.", nameof(userPath));

        // Reject literal ".." sequences even before canonicalization; this is a
        // defence-in-depth guard — the canonical check below is the definitive gate.
        if (userPath.Contains(".."))
            throw new ArgumentException(
                "User-supplied path segment must not contain path-traversal sequences ('..').", nameof(userPath));

        // Canonicalize the base directory and ensure it ends with a separator so
        // that the StartsWith check cannot be fooled by a prefix coincidence
        // (e.g. baseDir = "/foo" matching combined = "/foobar/secret").
        string canonicalBase = Path.GetFullPath(baseDir);
        if (!canonicalBase.EndsWith(Path.DirectorySeparatorChar)
            && !canonicalBase.EndsWith(Path.AltDirectorySeparatorChar))
        {
            canonicalBase += Path.DirectorySeparatorChar;
        }

        // Resolve the combined path to its canonical absolute form.
        string combined = Path.GetFullPath(Path.Combine(canonicalBase, userPath));

        // Final canonical check: the resolved path must be at or below canonicalBase.
        if (!combined.StartsWith(canonicalBase, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"The resolved path '{combined}' escapes the allowed base directory '{canonicalBase}'.",
                nameof(userPath));

        return combined;
    }
}
