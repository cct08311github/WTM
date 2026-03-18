#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test;

/// <summary>
/// Unit tests for JwtOption.IsDefaultOrWeakKey() — Issue #552.
/// Verifies that the well-known default key (and its padded variant) are
/// detected as weak so that AddWtmAuthentication() can refuse to start.
/// </summary>
[TestClass]
public class JwtOptionTests
{
    // ─── IsDefaultOrWeakKey ────────────────────────────────────────────────────

    [TestMethod]
    public void IsDefaultOrWeakKey_unset_key_returns_true()
    {
        // Key was never set — backing field holds the well-known default
        var opt = new JwtOption();
        Assert.IsTrue(opt.IsDefaultOrWeakKey());
    }

    [TestMethod]
    public void IsDefaultOrWeakKey_default_value_set_explicitly_returns_true()
    {
        // Operator copied the default into config — padded with 'x' to 32 chars
        var opt = new JwtOption { SecurityKey = JwtOption.WellKnownDefaultKey };
        Assert.IsTrue(opt.IsDefaultOrWeakKey(),
            "Explicitly setting the well-known default key (which gets padded) must still be rejected");
    }

    [TestMethod]
    public void IsDefaultOrWeakKey_strong_custom_key_returns_false()
    {
        var opt = new JwtOption { SecurityKey = "MyR@ndomStr0ngK3y_NotTheDefault!!" };
        Assert.IsFalse(opt.IsDefaultOrWeakKey());
    }

    [TestMethod]
    public void IsDefaultOrWeakKey_strong_key_shorter_than_32_returns_false()
    {
        // Short key gets padded with 'x', but the base is not the well-known default
        var opt = new JwtOption { SecurityKey = "CustomKey12345" };
        Assert.IsFalse(opt.IsDefaultOrWeakKey(),
            "A short custom key that isn't the default should NOT be flagged as weak");
    }

    [TestMethod]
    public void IsDefaultOrWeakKey_key_exactly_32_chars_strong_returns_false()
    {
        var opt = new JwtOption { SecurityKey = "ABCDEFGHIJKLMNOPQRSTUVWXYZ012345" };
        Assert.IsFalse(opt.IsDefaultOrWeakKey());
    }

    // ─── SecurityKey padding behaviour (pre-existing, regression guard) ────────

    [TestMethod]
    public void SecurityKey_short_value_is_padded_to_32()
    {
        var opt = new JwtOption { SecurityKey = "short" };
        Assert.AreEqual(32, opt.SecurityKey.Length);
    }

    [TestMethod]
    public void SecurityKey_already_32_chars_is_not_changed()
    {
        const string key = "ExactlyThirtyTwoCharactersHere!!";
        var opt = new JwtOption { SecurityKey = key };
        Assert.AreEqual(key, opt.SecurityKey);
    }
}
