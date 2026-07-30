#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Test.Security;

namespace WalkingTec.Mvvm.Core.Test;

/// <summary>
/// Unit tests for <see cref="JwtOption"/> — Issue #552 (original), extended by Issue #923.
///
/// #923's defect: <see cref="JwtOption.SecurityKey"/>'s setter silently pads any value
/// shorter than 32 chars with 'x' (unchanged by #923 — see the padding-preservation tests
/// below), so the OLD <c>IsDefaultOrWeakKey()</c> (only ever checking for the literal
/// well-known default) let demo-shipped keys like <c>"super"</c> and the developer manual's
/// 42-byte placeholder pass the startup guard. This file now covers TWO methods:
/// <list type="bullet">
/// <item><see cref="JwtOption.IsFactoryDefaultKey"/> (the renamed, behaviourally UNCHANGED
/// old method — still narrow, still only detects the literal CLR default) — used for
/// "is JWT active" detection.</item>
/// <item><see cref="JwtOption.IsWeakSigningKey"/> (new, #923) — the actual security
/// invariant: too short (&lt; 32 UTF-8 bytes), unset, or a publicly known placeholder,
/// evaluated against the RAW pre-padding value.</item>
/// </list>
/// </summary>
[TestClass]
public class JwtOptionTests
{
    // ─── IsFactoryDefaultKey (renamed from IsDefaultOrWeakKey; behaviour unchanged) ────

    [TestMethod]
    public void IsFactoryDefaultKey_unset_key_returns_true()
    {
        // Key was never set — backing field holds the well-known default
        var opt = new JwtOption();
        Assert.IsTrue(opt.IsFactoryDefaultKey());
    }

    [TestMethod]
    public void IsFactoryDefaultKey_default_value_set_explicitly_returns_true()
    {
        // Operator copied the default into config — padded with 'x' to 32 chars
        var opt = new JwtOption { SecurityKey = JwtOption.WellKnownDefaultKey };
        Assert.IsTrue(opt.IsFactoryDefaultKey(),
            "Explicitly setting the well-known default key (which gets padded) must still be rejected");
    }

    [TestMethod]
    public void IsFactoryDefaultKey_strong_custom_key_returns_false()
    {
        var opt = new JwtOption { SecurityKey = JwtTestKeys.StrongCustomKey };
        Assert.IsFalse(opt.IsFactoryDefaultKey());
    }

    /// <summary>
    /// #923: this test's ORIGINAL name/assertion was
    /// <c>IsDefaultOrWeakKey_strong_key_shorter_than_32_returns_false</c>, asserting
    /// <c>Assert.IsFalse</c> — i.e. that a 14-character custom key was NOT weak. That
    /// assertion was itself the bug's blind spot: <c>IsDefaultOrWeakKey()</c> (now
    /// <see cref="JwtOption.IsFactoryDefaultKey"/>) never looked at length at all, so this
    /// test only ever proved "this key is not the literal well-known default", while its
    /// name implied something much stronger ("a strong key... returns false" reads as a
    /// weak-key check). Kept HERE (same key, same scenario) as the negative-space
    /// counterpart to <see cref="IsWeakSigningKey_short_custom_key_that_is_not_the_well_known_default_still_returns_true"/>
    /// below: <see cref="JwtOption.IsFactoryDefaultKey"/> is legitimately still <c>false</c>
    /// for this key (that predicate's narrow "is it literally the default" job did not
    /// change), while <see cref="JwtOption.IsWeakSigningKey"/> is <c>true</c> for the exact
    /// same key — proving the two methods now answer genuinely different questions.
    /// </summary>
    [TestMethod]
    public void IsFactoryDefaultKey_short_custom_key_that_is_not_the_well_known_default_returns_false()
    {
        var opt = new JwtOption { SecurityKey = "CustomKey12345" };
        Assert.IsFalse(opt.IsFactoryDefaultKey(),
            "A short custom key that isn't the literal default is still not the FACTORY default " +
            "— IsFactoryDefaultKey()'s narrow job did not change. See IsWeakSigningKey for the " +
            "security check, which correctly flags this same key as weak.");
    }

    [TestMethod]
    public void IsFactoryDefaultKey_key_exactly_32_chars_strong_returns_false()
    {
        var opt = new JwtOption { SecurityKey = JwtTestKeys.StrongCustomKey };
        Assert.IsFalse(opt.IsFactoryDefaultKey());
    }

    // ─── #931 item 4: IsFactoryDefaultKey must not be fooled by a near-default raw value ──

    /// <summary>
    /// #931 item 4 regression: before this fix, <c>IsFactoryDefaultKey()</c> compared the
    /// PADDED backing field, so this 19-byte custom key — one character longer than the
    /// literal default — padded out to the EXACT SAME 32-character string as the padded
    /// default and was incorrectly treated as the factory default. That made
    /// <c>WtmConfigValidationExtension.IsJwtActive</c> compute <c>false</c> for this key and
    /// skip the weak-key validator entirely, even though this key is not the default at all.
    /// Now that <see cref="JwtOption.SecurityKey"/> never pads, there is no padded form left
    /// to collide against — this is a plain raw-string comparison.
    /// </summary>
    [TestMethod]
    public void IsFactoryDefaultKey_default_plus_one_extra_character_returns_false()
    {
        var opt = new JwtOption { SecurityKey = JwtOption.WellKnownDefaultKey + "x" }; // "wtmwtmwtmwtmwtmwtmx", 19 bytes
        Assert.IsFalse(opt.IsFactoryDefaultKey(),
            "#931: a 19-byte key that is NOT the literal default must not be treated as the " +
            "factory default just because it used to pad to the same 32-character string.");
    }

    /// <summary>
    /// #931 item 4: "the same holds for the default plus 1-13 x's" — every one of those
    /// intermediate lengths also used to pad to the identical 32-character string.
    /// </summary>
    [TestMethod]
    public void IsFactoryDefaultKey_default_plus_various_extra_x_counts_all_return_false()
    {
        for (int extraXCount = 1; extraXCount <= 13; extraXCount++)
        {
            var raw = JwtOption.WellKnownDefaultKey + new string('x', extraXCount);
            var opt = new JwtOption { SecurityKey = raw };
            Assert.IsFalse(opt.IsFactoryDefaultKey(),
                $"#931: '{raw}' (default + {extraXCount} extra 'x') is not the literal default " +
                "and must not be misclassified as one.");
        }
    }

    // ─── IsDefaultOrWeakKey (obsolete alias) — proves it still behaves like IsFactoryDefaultKey ──

    [TestMethod]
#pragma warning disable CS0618 // intentional: proving the obsolete alias's behaviour is unchanged
    public void IsDefaultOrWeakKey_obsolete_alias_matches_IsFactoryDefaultKey_for_default_key()
    {
        var opt = new JwtOption();
        Assert.IsTrue(opt.IsDefaultOrWeakKey());
        Assert.AreEqual(opt.IsFactoryDefaultKey(), opt.IsDefaultOrWeakKey());
    }

    [TestMethod]
    public void IsDefaultOrWeakKey_obsolete_alias_matches_IsFactoryDefaultKey_for_demo_key()
    {
        // #923: the obsolete alias intentionally does NOT gain the new weak-key behaviour —
        // downstream code that copied its old name must migrate explicitly (see class doc).
        var opt = new JwtOption { SecurityKey = "super" };
        Assert.IsFalse(opt.IsDefaultOrWeakKey(),
            "The obsolete alias must keep its OLD (narrow) behaviour, not silently gain the new " +
            "IsWeakSigningKey semantics — that would be an unreviewed behaviour change on a method " +
            "existing callers still compile against.");
        Assert.AreEqual(opt.IsFactoryDefaultKey(), opt.IsDefaultOrWeakKey());
    }
#pragma warning restore CS0618

    // ─── IsWeakSigningKey (#923) — the security invariant ──────────────────────────────

    [TestMethod]
    public void IsWeakSigningKey_unset_key_returns_true()
    {
        var opt = new JwtOption();
        Assert.IsTrue(opt.IsWeakSigningKey(out var reason));
        Assert.IsNotNull(reason);
    }

    [TestMethod]
    public void IsWeakSigningKey_well_known_default_returns_true()
    {
        var opt = new JwtOption { SecurityKey = JwtOption.WellKnownDefaultKey };
        Assert.IsTrue(opt.IsWeakSigningKey(out _));
    }

    [TestMethod]
    public void IsWeakSigningKey_well_known_default_padded_form_set_directly_returns_true()
    {
        // An operator who copied the ALREADY-padded 32-char form of the default (e.g. from a
        // log line or old doc) into config must still be caught — KnownPublicKeys includes
        // both the raw and PadRight(32,'x') forms of every known key.
        var opt = new JwtOption { SecurityKey = JwtOption.WellKnownDefaultKey.PadRight(32, 'x') };
        Assert.IsTrue(opt.IsWeakSigningKey(out _));
    }

    [TestMethod]
    public void IsWeakSigningKey_demo_key_super_returns_true()
    {
        var opt = new JwtOption { SecurityKey = "super" };
        Assert.IsTrue(opt.IsWeakSigningKey(out var reason));
        Assert.IsNotNull(reason);
    }

    [TestMethod]
    public void IsWeakSigningKey_demo_key_superSecretKey345_returns_true()
    {
        var opt = new JwtOption { SecurityKey = "superSecretKey@345" };
        Assert.IsTrue(opt.IsWeakSigningKey(out _));
    }

    [TestMethod]
    public void IsWeakSigningKey_manual_placeholder_key_returns_true_despite_being_42_bytes()
    {
        // The developer manual's placeholder is 42 UTF-8 bytes — well past the 32-byte
        // length floor. This is the test that proves the blocklist is independent of
        // length: a length-only check would let this one through.
        const string manualPlaceholder = "your-256-bit-secret-key-here-min-32-chars!";
        Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(manualPlaceholder) > 32,
            "test precondition: the manual placeholder must be longer than 32 bytes");
        var opt = new JwtOption { SecurityKey = manualPlaceholder };
        Assert.IsTrue(opt.IsWeakSigningKey(out var reason));
        Assert.IsNotNull(reason);
    }

    [TestMethod]
    public void IsWeakSigningKey_demo_key_with_surrounding_whitespace_returns_true()
    {
        // Whitespace-bypass: "  super  ".Trim() == "super", still blocklisted.
        var opt = new JwtOption { SecurityKey = "  super  " };
        Assert.IsTrue(opt.IsWeakSigningKey(out _));
    }

    [TestMethod]
    public void IsWeakSigningKey_demo_key_uppercased_returns_true()
    {
        // Case-bypass: blocklist comparison is OrdinalIgnoreCase.
        var opt = new JwtOption { SecurityKey = "SUPER" };
        Assert.IsTrue(opt.IsWeakSigningKey(out _));
    }

    // ─── #931 item 6: isolate Trim()/OrdinalIgnoreCase from the length guard ───────────────
    //
    // IsWeakSigningKey_demo_key_with_surrounding_whitespace_returns_true and
    // IsWeakSigningKey_demo_key_uppercased_returns_true above both use "super" (5 bytes) —
    // even with Trim()/OrdinalIgnoreCase completely removed, or the blocklist emptied
    // entirely, those two tests would STILL pass, because the < 32-byte length guard alone
    // already flags a 5-byte value as weak. They do not actually bind to the clause they are
    // named after. The two tests below use the 42-byte manual-placeholder value (well past
    // the length floor even with the whitespace/case transformation applied), so ONLY a
    // working Trim() / OrdinalIgnoreCase blocklist match can make them weak — deleting either
    // mechanism turns these red without touching the length guard at all.

    [TestMethod]
    public void IsWeakSigningKey_manual_placeholder_with_whitespace_and_over_32_bytes_isolates_Trim()
    {
        const string padded = "  your-256-bit-secret-key-here-min-32-chars!  ";
        Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(padded) > 32,
            "test precondition: even the UNTRIMMED value must already be over 32 bytes, so " +
            "the length guard alone cannot explain a weak verdict here.");
        var opt = new JwtOption { SecurityKey = padded };
        Assert.IsTrue(opt.IsWeakSigningKey(out var reason),
            "#931: only Trim() before the blocklist lookup can catch this — the length guard " +
            "does not fire on its own for a 47-byte value.");
        Assert.AreEqual(JwtOption.WeakReasonKnownPublicKey, reason);
    }

    [TestMethod]
    public void IsWeakSigningKey_manual_placeholder_uppercased_and_over_32_bytes_isolates_OrdinalIgnoreCase()
    {
        const string uppercased = "YOUR-256-BIT-SECRET-KEY-HERE-MIN-32-CHARS!";
        Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(uppercased) > 32,
            "test precondition: the value is already over 32 bytes on its own, so the length " +
            "guard alone cannot explain a weak verdict here.");
        var opt = new JwtOption { SecurityKey = uppercased };
        Assert.IsTrue(opt.IsWeakSigningKey(out var reason),
            "#931: only a case-insensitive blocklist comparison can catch this — the length " +
            "guard does not fire on its own for a 43-byte value.");
        Assert.AreEqual(JwtOption.WeakReasonKnownPublicKey, reason);
    }

    [TestMethod]
    public void IsWeakSigningKey_31_byte_custom_key_returns_true()
    {
        var key31 = new string('k', 31);
        Assert.AreEqual(31, System.Text.Encoding.UTF8.GetByteCount(key31));
        var opt = new JwtOption { SecurityKey = key31 };
        Assert.IsTrue(opt.IsWeakSigningKey(out var reason));
        Assert.IsNotNull(reason);
    }

    [TestMethod]
    public void IsWeakSigningKey_short_custom_key_that_is_not_the_well_known_default_still_returns_true()
    {
        // #923: see the class doc and IsFactoryDefaultKey_short_custom_key_... above — this
        // is the same 14-char key the pre-#923 test asserted was "not weak". Under the real
        // security invariant it IS weak (too short), even though it is not the literal
        // well-known default.
        var opt = new JwtOption { SecurityKey = "CustomKey12345" };
        Assert.IsTrue(opt.IsWeakSigningKey(out var reason));
        Assert.IsNotNull(reason);
    }

    // ─── reason classification (#923) — load-bearing for AddWtmAuthentication's Development
    // carve-out, which distinguishes "unset/known-public" (lenient in Development) from
    // "too short but not publicly known" (strict in every environment, design table row (d)) ──

    [TestMethod]
    public void IsWeakSigningKey_short_custom_key_reason_is_WeakReasonTooShort()
    {
        var opt = new JwtOption { SecurityKey = "CustomKey12345" };
        opt.IsWeakSigningKey(out var reason);
        Assert.AreEqual(JwtOption.WeakReasonTooShort, reason);
    }

    [TestMethod]
    public void IsWeakSigningKey_unset_key_reason_is_WeakReasonKnownPublicKey_not_TooShort()
    {
        // The unset raw default ("wtmwtmwtmwtmwtmwtm") is ALSO under 32 bytes, but the
        // blocklist check must win so AddWtmAuthentication treats it as lenient-eligible.
        var opt = new JwtOption();
        opt.IsWeakSigningKey(out var reason);
        Assert.AreEqual(JwtOption.WeakReasonKnownPublicKey, reason);
    }

    [TestMethod]
    public void IsWeakSigningKey_demo_key_super_reason_is_WeakReasonKnownPublicKey()
    {
        var opt = new JwtOption { SecurityKey = "super" };
        opt.IsWeakSigningKey(out var reason);
        Assert.AreEqual(JwtOption.WeakReasonKnownPublicKey, reason);
    }

    [TestMethod]
    public void IsWeakSigningKey_31_byte_custom_key_reason_is_WeakReasonTooShort()
    {
        var opt = new JwtOption { SecurityKey = new string('k', 31) };
        opt.IsWeakSigningKey(out var reason);
        Assert.AreEqual(JwtOption.WeakReasonTooShort, reason);
    }

    [TestMethod]
    public void IsWeakSigningKey_exactly_32_byte_custom_key_returns_false()
    {
        // Boundary positive control: exactly 32 bytes, not blocklisted -> NOT weak.
        const string key32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ012345";
        Assert.AreEqual(32, System.Text.Encoding.UTF8.GetByteCount(key32));
        var opt = new JwtOption { SecurityKey = key32 };
        Assert.IsFalse(opt.IsWeakSigningKey(out var reason));
        Assert.IsNull(reason);
    }

    [TestMethod]
    public void IsWeakSigningKey_33_byte_custom_key_returns_false()
    {
        const string key33 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456";
        Assert.AreEqual(33, System.Text.Encoding.UTF8.GetByteCount(key33));
        var opt = new JwtOption { SecurityKey = key33 };
        Assert.IsFalse(opt.IsWeakSigningKey(out _));
    }

    [TestMethod]
    public void IsWeakSigningKey_11_cjk_characters_33_bytes_returns_false()
    {
        // 11 CJK characters = 33 UTF-8 bytes (3 bytes each) — proves the check counts UTF-8
        // BYTES, not .NET chars (which would read this as length 11, well under 32).
        var cjkKey = new string('資', 11);
        Assert.AreEqual(11, cjkKey.Length);
        Assert.AreEqual(33, System.Text.Encoding.UTF8.GetByteCount(cjkKey));
        var opt = new JwtOption { SecurityKey = cjkKey };
        Assert.IsFalse(opt.IsWeakSigningKey(out _));
    }

    [TestMethod]
    public void IsWeakSigningKey_strong_custom_key_returns_false()
    {
        var opt = new JwtOption { SecurityKey = JwtTestKeys.StrongCustomKey };
        Assert.IsFalse(opt.IsWeakSigningKey(out var reason));
        Assert.IsNull(reason);
    }

    // ─── HasLowCharacterDiversity (#923, warn-only) ─────────────────────────────────────

    [TestMethod]
    public void HasLowCharacterDiversity_repeated_character_key_returns_true_but_is_not_weak()
    {
        // 32 'a' characters: long enough (32 bytes) and not blocklisted, so
        // IsWeakSigningKey is false — but diversity is 1 distinct character, so this
        // warn-only heuristic must fire. Proves the two checks are independent.
        var opt = new JwtOption { SecurityKey = new string('a', 32) };
        Assert.IsFalse(opt.IsWeakSigningKey(out _),
            "A 32-byte key must not be gated by IsWeakSigningKey regardless of character diversity");
        Assert.IsTrue(opt.HasLowCharacterDiversity);
    }

    [TestMethod]
    public void HasLowCharacterDiversity_strong_random_key_returns_false()
    {
        // Positive control: a genuinely varied key must NOT trigger the diversity warning.
        var opt = new JwtOption { SecurityKey = JwtTestKeys.StrongCustomKey };
        Assert.IsFalse(opt.HasLowCharacterDiversity);
    }

    [TestMethod]
    public void HasLowCharacterDiversity_unset_key_returns_true()
    {
        // HasLowCharacterDiversity is computed independently of IsWeakSigningKey — it does
        // not short-circuit just because the key is already weak for another reason. The
        // unset/default raw value ("wtmwtmwtmwtmwtmwtm") has only 3 distinct characters
        // (w, t, m), so this is legitimately true here too; AddWtmAuthentication's `else if`
        // ordering (only checked when IsWeakSigningKey is false) is what keeps the two
        // warnings from double-firing in production, not this property itself.
        var opt = new JwtOption();
        Assert.IsTrue(opt.HasLowCharacterDiversity);
    }

    // ─── EffectiveSecurityKey padding behaviour (#931 item 1: padding moved off SecurityKey) ──
    //
    // Pre-#931 these two tests asserted `opt.SecurityKey.Length == 32` /
    // `opt.SecurityKey == key` — i.e. they tested the PADDED getter, which is exactly the
    // design item 1 fixed: SecurityKey must round-trip the raw value unchanged, so these now
    // assert on EffectiveSecurityKey (where the padding now lives) while ALSO asserting
    // SecurityKey itself stays unpadded — the padding ALGORITHM is untouched, only which
    // property exposes it moved.

    [TestMethod]
    public void EffectiveSecurityKey_short_value_is_padded_to_32()
    {
        var opt = new JwtOption { SecurityKey = "short" };
        Assert.AreEqual(32, opt.EffectiveSecurityKey.Length);
        Assert.AreEqual("short", opt.SecurityKey,
            "#931: SecurityKey itself must NOT be padded — only EffectiveSecurityKey is.");
    }

    [TestMethod]
    public void EffectiveSecurityKey_already_32_chars_is_not_changed()
    {
        const string key = "ExactlyThirtyTwoCharactersHere!!";
        var opt = new JwtOption { SecurityKey = key };
        Assert.AreEqual(key, opt.EffectiveSecurityKey);
        Assert.AreEqual(key, opt.SecurityKey);
    }

    // ─── #931 item 1: the getter→setter/serialization laundering fix ──────────────────────

    /// <summary>
    /// #931 item 1, the highest-severity finding: reproduces the EXACT laundering sequence
    /// from the review verbatim. Before the fix, <c>SecurityKey</c>'s getter returned the
    /// PADDED value, so reading it back and assigning it to a new (or the same) instance
    /// silently turned a weak raw key into a not-weak one — the HMAC bytes never changed, only
    /// the verdict did. This is the single test that most directly proves the fix: it fails
    /// against the pre-#931 shape (getter returns padded value) and passes now (getter
    /// returns raw value unchanged).
    /// </summary>
    [TestMethod]
    public void SecurityKey_GetterSetterRoundTrip_DoesNotLaunderWeakKeyIntoStrong()
    {
        var original = new JwtOption { SecurityKey = "" }; // raw = "" -> weak (WeakReasonUnset)
        Assert.IsTrue(original.IsWeakSigningKey(out _), "test precondition: raw empty string must be weak");

        var roundTripped = original.SecurityKey; // the getter
        var copy = new JwtOption { SecurityKey = roundTripped }; // the setter, fed the getter's own output

        Assert.IsTrue(copy.IsWeakSigningKey(out var reason),
            "#931: reading SecurityKey and writing it straight back must not launder a weak " +
            "raw key into a not-weak one. If this fails, SecurityKey's getter is returning a " +
            "padded value again.");
        Assert.AreEqual(JwtOption.WeakReasonUnset, reason);
    }

    /// <summary>
    /// #931 item 1: the same laundering happens automatically through ANY
    /// serialize/deserialize round-trip, since a serializer only ever sees the public getter
    /// — it has no way to know a "raw, pre-padding" value even exists. Explicitly required by
    /// the review: "Add a test that round-trips through System.Text.Json and asserts the weak
    /// verdict survives."
    /// </summary>
    [TestMethod]
    public void SecurityKey_RoundTripsThroughSystemTextJson_WeakVerdictSurvives()
    {
        var original = new JwtOption { SecurityKey = "" };
        Assert.IsTrue(original.IsWeakSigningKey(out _), "test precondition: raw empty string must be weak");

        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<JwtOption>(json);

        Assert.IsNotNull(roundTripped);
        Assert.IsTrue(roundTripped!.IsWeakSigningKey(out var reason),
            "#931: a System.Text.Json serialize/deserialize round-trip must not launder a weak " +
            "raw key into a not-weak one via the padded getter value.");
        Assert.AreEqual(JwtOption.WeakReasonUnset, reason);
    }

    /// <summary>
    /// Positive-control companion: proves the round-trip fix does not ALSO accidentally make
    /// a genuinely strong key look weak after round-tripping — the fix must be symmetric.
    /// </summary>
    [TestMethod]
    public void SecurityKey_RoundTripsThroughSystemTextJson_StrongKeyStaysNotWeak()
    {
        var original = new JwtOption { SecurityKey = JwtTestKeys.StrongCustomKey };
        Assert.IsFalse(original.IsWeakSigningKey(out _), "test precondition: the shared strong test key must not be weak");

        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<JwtOption>(json);

        Assert.IsNotNull(roundTripped);
        Assert.AreEqual(JwtTestKeys.StrongCustomKey, roundTripped!.SecurityKey);
        Assert.IsFalse(roundTripped.IsWeakSigningKey(out _));
    }
}
