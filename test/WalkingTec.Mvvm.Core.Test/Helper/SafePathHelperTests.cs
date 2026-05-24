using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Helper;

namespace WalkingTec.Mvvm.Core.Test.Helper;

[TestClass]
public class SafePathHelperTests
{
    // Use a stable base directory that is guaranteed to exist on every platform.
    private static readonly string BaseDir =
        Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // -----------------------------------------------------------------------
    // Happy-path tests
    // -----------------------------------------------------------------------

    [TestMethod]
    public void SafeCombine_SimpleRelativePath_ReturnsCombinedAbsolutePath()
    {
        string result = SafePathHelper.SafeCombine(BaseDir, "subdir");

        string expected = Path.GetFullPath(Path.Combine(BaseDir, "subdir"));
        Assert.AreEqual(expected, result,
            "A simple relative segment should resolve to an absolute path under baseDir.");
        Assert.IsTrue(result.StartsWith(BaseDir, StringComparison.OrdinalIgnoreCase),
            "Result must start with baseDir.");
    }

    [TestMethod]
    public void SafeCombine_NestedRelativePath_ReturnsCombinedAbsolutePath()
    {
        string result = SafePathHelper.SafeCombine(BaseDir, $"a{Path.DirectorySeparatorChar}b{Path.DirectorySeparatorChar}c");

        string expected = Path.GetFullPath(Path.Combine(BaseDir, "a", "b", "c"));
        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void SafeCombine_BaseWithoutTrailingSeparator_Succeeds()
    {
        // baseDir without a trailing separator must still work correctly.
        string baseWithoutSep = BaseDir.TrimEnd(Path.DirectorySeparatorChar);
        string result = SafePathHelper.SafeCombine(baseWithoutSep, "output.cs");

        Assert.IsTrue(result.StartsWith(
            Path.GetFullPath(baseWithoutSep) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase),
            "Result must reside inside baseDir even when trailing separator is absent.");
    }

    [TestMethod]
    public void SafeCombine_BaseWithTrailingSeparator_Succeeds()
    {
        string baseWithSep = BaseDir + Path.DirectorySeparatorChar;
        string result = SafePathHelper.SafeCombine(baseWithSep, "output.cs");

        Assert.IsTrue(result.StartsWith(
            Path.GetFullPath(BaseDir) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
    }

    // -----------------------------------------------------------------------
    // Rejection tests — path traversal
    // -----------------------------------------------------------------------

    [TestMethod]
    public void SafeCombine_DotDotEscape_ThrowsArgumentException()
    {
        var ex = Assert.ThrowsException<ArgumentException>(
            () => SafePathHelper.SafeCombine(BaseDir, $"..{Path.DirectorySeparatorChar}escape"));

        Assert.IsTrue(ex.Message.Contains("..") || ex.Message.Contains("traversal"),
            "Exception message should mention the traversal sequence.");
    }

    [TestMethod]
    public void SafeCombine_DotDotInMiddle_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(
            () => SafePathHelper.SafeCombine(BaseDir, $"a{Path.DirectorySeparatorChar}..{Path.DirectorySeparatorChar}..{Path.DirectorySeparatorChar}etc"));
    }

    [TestMethod]
    public void SafeCombine_DotDotLiteralString_ThrowsArgumentException()
    {
        // Even a segment that is exactly ".." should be rejected.
        Assert.ThrowsException<ArgumentException>(
            () => SafePathHelper.SafeCombine(BaseDir, ".."));
    }

    // -----------------------------------------------------------------------
    // Rejection tests — absolute overrides
    // -----------------------------------------------------------------------

    [TestMethod]
    public void SafeCombine_AbsolutePath_ThrowsArgumentException()
    {
        // On Windows use C:\Windows; on Unix use /etc/passwd.
        string absolutePath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? @"C:\Windows"
            : "/etc/passwd";

        Assert.ThrowsException<ArgumentException>(
            () => SafePathHelper.SafeCombine(BaseDir, absolutePath));
    }

    // -----------------------------------------------------------------------
    // Rejection tests — null/empty input
    // -----------------------------------------------------------------------

    [TestMethod]
    public void SafeCombine_NullUserPath_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(
            () => SafePathHelper.SafeCombine(BaseDir, null!));
    }

    [TestMethod]
    public void SafeCombine_NullBaseDir_ThrowsArgumentNullException()
    {
        Assert.ThrowsException<ArgumentNullException>(
            () => SafePathHelper.SafeCombine(null!, "safe"));
    }

    [TestMethod]
    public void SafeCombine_EmptyUserPath_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(
            () => SafePathHelper.SafeCombine(BaseDir, string.Empty));
    }

    [TestMethod]
    public void SafeCombine_WhitespaceOnlyUserPath_ThrowsArgumentException()
    {
        Assert.ThrowsException<ArgumentException>(
            () => SafePathHelper.SafeCombine(BaseDir, "   "));
    }

    // -----------------------------------------------------------------------
    // Rejection tests — null-byte injection
    // -----------------------------------------------------------------------

    [TestMethod]
    public void SafeCombine_NullByteInjection_ThrowsArgumentException()
    {
        // '\0' is an OS-invalid path character on all platforms.
        Assert.ThrowsException<ArgumentException>(
            () => SafePathHelper.SafeCombine(BaseDir, "safe\0../etc/passwd"));
    }

    // -----------------------------------------------------------------------
    // OrdinalIgnoreCase boundary — platform compatibility
    // -----------------------------------------------------------------------

    [TestMethod]
    public void SafeCombine_SamePathDifferentCase_Succeeds()
    {
        // Mixing case in the relative segment should still resolve correctly.
        // The canonical check uses OrdinalIgnoreCase so this must not throw.
        string result = SafePathHelper.SafeCombine(BaseDir, "SubDir");
        Assert.IsNotNull(result);
    }

    // -----------------------------------------------------------------------
    // Boundary case: current-directory dot
    // -----------------------------------------------------------------------

    [TestMethod]
    public void SafeCombine_SingleDot_ThrowsArgumentException()
    {
        // "." resolves via Path.GetFullPath to the canonical baseDir itself
        // (without a trailing separator), so combined == baseDir, which does
        // NOT start with canonicalBase + "/" — the separator-aware boundary
        // check correctly rejects it.  By design: callers must supply a
        // non-empty relative segment that names a file or subdirectory, not
        // the base directory itself.
        Assert.ThrowsException<ArgumentException>(
            () => SafePathHelper.SafeCombine(BaseDir, "."),
            "Single '.' should be rejected because it resolves to baseDir itself, " +
            "which fails the StartsWith(canonicalBase + separator) check.");
    }

    // -----------------------------------------------------------------------
    // Separator-aware prefix check (defence against "/foobar" bypassing "/foo")
    // -----------------------------------------------------------------------

    [TestMethod]
    public void SafeCombine_PrefixCoincidence_IsBlocked()
    {
        // Simulate a scenario where baseDir = /tmp/foo and a crafted path
        // would produce /tmp/foobar/secret without the separator-aware check.
        // With our separator-suffixed canonical base, /tmp/foobar does NOT
        // start with /tmp/foo/, so it should be rejected.
        string narrowBase = Path.Combine(BaseDir, "foo");

        // "." from a crafted narrow base + "../foobar" — traversal, rejected.
        Assert.ThrowsException<ArgumentException>(
            () => SafePathHelper.SafeCombine(narrowBase, $"..{Path.DirectorySeparatorChar}foobar"));
    }
}
