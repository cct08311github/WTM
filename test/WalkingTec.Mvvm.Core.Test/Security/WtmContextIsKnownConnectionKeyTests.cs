#nullable enable
// Unit tests for WTMContext.IsKnownConnectionKey — Issue #517.
// Verifies the reusable cskey guard exposed from WTMContext behaves identically
// to the original private _FrameworkController.IsKnownConnectionKey it replaces.

using System.Collections.Generic;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Security;

/// <summary>
/// Tests for <see cref="WTMContext.IsKnownConnectionKey"/>.
/// Three invariants:
///   1. null or empty csKey → true  (caller uses the default connection).
///   2. csKey that matches a configured key (case-insensitive) → true.
///   3. csKey that does NOT match any configured key → false.
/// </summary>
[TestClass]
public class WtmContextIsKnownConnectionKeyTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a WTMContext with a Configs that contains the given connection keys.
    /// MockWtmContext.CreateWtmContext() always initialises ConfigInfo to a new Configs()
    /// instance, so we can set Connections directly.
    /// </summary>
    private static WTMContext BuildContextWithConnections(params string[] keys)
    {
        var wtm = MockWtmContext.CreateWtmContext();
        var connections = new List<CS>();
        foreach (var key in keys)
        {
            connections.Add(new CS { Key = key });
        }
        wtm.ConfigInfo!.Connections = connections;
        return wtm;
    }

    // ── Group 1: null / empty → always true ─────────────────────────────────────

    [TestMethod]
    public void IsKnownConnectionKey_null_returns_true()
    {
        var wtm = BuildContextWithConnections("default", "secondary");
        wtm.IsKnownConnectionKey(null).Should().BeTrue(
            "null csKey means 'use default connection' and is always valid");
    }

    [TestMethod]
    public void IsKnownConnectionKey_empty_string_returns_true()
    {
        var wtm = BuildContextWithConnections("default", "secondary");
        wtm.IsKnownConnectionKey("").Should().BeTrue(
            "empty csKey means 'use default connection' and is always valid");
    }

    // ── Group 2: exact known key → true ─────────────────────────────────────────

    [TestMethod]
    public void IsKnownConnectionKey_exact_match_returns_true()
    {
        var wtm = BuildContextWithConnections("default", "secondary", "reporting");
        wtm.IsKnownConnectionKey("secondary").Should().BeTrue(
            "a key that exactly matches a configured connection must return true");
    }

    [TestMethod]
    public void IsKnownConnectionKey_default_key_returns_true()
    {
        var wtm = BuildContextWithConnections("default");
        wtm.IsKnownConnectionKey("default").Should().BeTrue(
            "'default' is a standard configured key and must be accepted");
    }

    // ── Group 3: case-insensitive match → true ───────────────────────────────────

    [TestMethod]
    public void IsKnownConnectionKey_uppercase_input_matches_lowercase_config()
    {
        var wtm = BuildContextWithConnections("default", "secondary");
        wtm.IsKnownConnectionKey("SECONDARY").Should().BeTrue(
            "matching must be case-insensitive — client casing must not matter");
    }

    [TestMethod]
    public void IsKnownConnectionKey_mixed_case_input_matches_config()
    {
        var wtm = BuildContextWithConnections("Reporting");
        wtm.IsKnownConnectionKey("rEpOrTiNg").Should().BeTrue(
            "matching must be case-insensitive regardless of the case used by config");
    }

    // ── Group 4: unknown key → false ────────────────────────────────────────────

    [TestMethod]
    public void IsKnownConnectionKey_unknown_key_returns_false()
    {
        var wtm = BuildContextWithConnections("default", "secondary");
        wtm.IsKnownConnectionKey("attacker-db").Should().BeFalse(
            "an unrecognised key must return false so callers can reject the request");
    }

    [TestMethod]
    public void IsKnownConnectionKey_partial_match_returns_false()
    {
        var wtm = BuildContextWithConnections("default");
        wtm.IsKnownConnectionKey("def").Should().BeFalse(
            "a prefix of a known key must not be accepted — must be an exact (case-insensitive) match");
    }

    [TestMethod]
    public void IsKnownConnectionKey_empty_connections_list_unknown_key_returns_false()
    {
        var wtm = BuildContextWithConnections(); // no connections configured
        wtm.IsKnownConnectionKey("default").Should().BeFalse(
            "when no connections are configured, any non-null/non-empty key must return false");
    }
}
