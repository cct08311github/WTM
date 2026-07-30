#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace WalkingTec.Mvvm.Core
{
    public class JwtOption
    {
        public string Issuer { get; set; } = "http://localhost";
        public string Audience { get; set; } = "http://localhost";
        public int Expires { get; set; } = 3600;

        /// <summary>
        /// Well-known default shipped in source — publicly readable on GitHub.
        /// Any deployment still using this value is exploitable.
        /// </summary>
        internal const string WellKnownDefaultKey = "wtmwtmwtmwtmwtmwtm";

        /// <summary>
        /// Issue #923/#931: every signing-key value that has ever shipped in this
        /// repository's source tree — the CLR default, every demo <c>appsettings.json</c>
        /// placeholder, the developer manual's placeholder, AND every fixed literal a test
        /// fixture used (<c>test/</c> is not excluded from the public GitHub mirror sync —
        /// see <c>.sync/github-excludes.txt</c> — so a test-only literal is exactly as public
        /// as a demo config value). Checked independently of length in
        /// <see cref="IsWeakSigningKey"/> — the manual's placeholder
        /// (<c>your-256-bit-secret-key-here-min-32-chars!</c>) is 42 UTF-8 bytes and would
        /// pass a length-only check while still being public. Both the raw base value and its
        /// <see cref="string.PadRight(int, char)"/>(32, 'x') form are included: an operator
        /// could have copied either representation into their own configuration. Compared
        /// case-insensitively (see <see cref="IsWeakSigningKey"/>).
        /// </summary>
        /// <remarks>
        /// #931 review: entries stay here even after the test files that used them are
        /// changed to generate a random key instead (see
        /// <c>test/WalkingTec.Mvvm.Core.Test/Security/JwtTestKeys.cs</c>) — these exact
        /// strings are permanently recoverable from git history via the public mirror
        /// regardless of what the CURRENT tree contains. Deleting a literal from the tests
        /// that used it does not un-publish it.
        /// </remarks>
        internal static readonly IReadOnlySet<string> KnownPublicKeys = BuildKnownPublicKeys();

        private static IReadOnlySet<string> BuildKnownPublicKeys()
        {
            string[] baseKeys =
            {
                WellKnownDefaultKey,
                "super",
                "superSecretKey@345",
                "your-256-bit-secret-key-here-min-32-chars!",
                // #931 item 2: fixed literals a test fixture used pre-#931, four found by
                // cross-vendor review of #923 itself and three more by sweeping the whole
                // tree for the same pattern once the first four were found (this repo's own
                // security posture: a confirmed issue class gets a full-codebase sweep, not
                // just the reported instances). Never re-used after #931 (see
                // JwtTestKeys.cs / TokenTestFixture.GeneratedSecurityKey), but blocklisted
                // permanently regardless — see the class remarks above.
                "WTM_Test_Key_AtLeast_32_Characters!!", // src/WalkingTec.Mvvm.Mvc.Tests/Fixtures/TokenTestFixture.cs, pre-#931
                "MyR@ndomStr0ngK3y_NotTheDefault!!",     // test/WalkingTec.Mvvm.Core.Test/JwtOptionTests.cs, pre-#931 (asserted as a *strong* example — the exact failure mode this blocklist exists to catch)
                "myTestSecretKey1234567890abcdefg",      // added by #923 itself
                "OperatorSuppliedSecretManagerKey_32Bytes!!", // added by the #923 design-gate review round
                "denylist_test_secret_key_32chars!!",    // test/WalkingTec.Mvvm.Core.Test/Security/AccessTokenDenylistTests.cs, pre-#931, found by the sweep
                "atomic_rotation_secret_key_32chars!!",  // test/WalkingTec.Mvvm.Core.Test/Security/RefreshTokenAtomicRotationTests.cs, pre-#931, found by the sweep
                "wtm_very_secret_key_1234567890123",     // test/WalkingTec.Mvvm.Core.Test/TokenServiceTests.cs, pre-#931, found by the sweep
            };
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in baseKeys)
            {
                set.Add(key);
                set.Add(key.PadRight(32, 'x'));
            }
            return set;
        }

        /// <summary>
        /// Backing field for <see cref="SecurityKey"/> — holds EXACTLY what was assigned, no
        /// padding. Defaults to <see cref="WellKnownDefaultKey"/> (unpadded) so "never
        /// configured" and "explicitly set to the literal default" are the same, intentional,
        /// value — see <see cref="IsFactoryDefaultKey"/>.
        /// </summary>
        private string _rawSecurityKey = WellKnownDefaultKey;

        /// <summary>
        /// The signing key exactly as configured — round-trips unchanged through the getter,
        /// the setter, and JSON (de)serialization.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>#931 review, the design fix (not a symptom patch):</b> before this fix, this
        /// getter returned the PADDED value (see <see cref="EffectiveSecurityKey"/> below for
        /// where that padding now lives), so a getter→setter round-trip laundered a weak raw
        /// key into a not-weak one:
        /// <code>
        /// jwt.SecurityKey = "";              // raw = "" -&gt; weak
        /// var effective = jwt.SecurityKey;   // returned 32 'x' characters (BUG)
        /// jwt.SecurityKey = effective;       // raw = 32 x's -&gt; NOT weak (BUG)
        /// </code>
        /// The HMAC bytes were identical before and after, but <see cref="IsWeakSigningKey"/>
        /// flipped from <c>true</c> to <c>false</c>. Any <c>System.Text.Json</c>
        /// serialize/deserialize round-trip performed exactly this laundering, because a
        /// serializer only ever sees the public getter — see
        /// <c>JwtOptionTests.SecurityKey_RoundTripsThroughSystemTextJson_WeakVerdictSurvives</c>.
        /// </para>
        /// <para>
        /// Now this property NEVER pads. The actual HMAC signing material — padded to at
        /// least 32 characters with <c>'x'</c> when the raw value is shorter, for the same
        /// reason padding existed originally (a direct <c>new JwtOption { SecurityKey = "x"
        /// }</c> construction, or a console/ETL host that never calls
        /// <c>AddWtmAuthentication</c>, must not crash signing with <c>IDX10720</c>) — lives
        /// in <see cref="EffectiveSecurityKey"/>. Every production sink that constructs a
        /// <c>SymmetricSecurityKey</c> MUST read <see cref="EffectiveSecurityKey"/>, never
        /// this property directly: <c>TokenService.cs</c>,
        /// <c>FrameworkServiceExtension.cs</c>, and both <c>WTMContext.User.cs</c> sinks.
        /// </para>
        /// </remarks>
        public string SecurityKey
        {
            get => _rawSecurityKey;
            set => _rawSecurityKey = value;
        }

        /// <summary>
        /// The actual bytes used to sign/validate HMAC-SHA256 tokens:
        /// <see cref="SecurityKey"/>, padded with <c>'x'</c> to at least 32 characters if
        /// shorter. #765/#571: HMAC-SHA256 (<c>IDX10720</c>) requires a &gt;= 256-bit / 32-byte
        /// key. Computed on read, not cached — the padding ALGORITHM is byte-for-byte
        /// identical to what the old <c>SecurityKey</c> setter used to do; only WHERE it runs
        /// moved (#931 item 1). Never assign this value to <see cref="SecurityKey"/> — that
        /// reintroduces the exact laundering <see cref="SecurityKey"/>'s own remarks describe.
        /// </summary>
        public string EffectiveSecurityKey
        {
            get
            {
                var effective = _rawSecurityKey ?? "";
                if (effective.Length < 32)
                {
                    var count = 32 - effective.Length;
                    for (int i = 0; i < count; i++)
                        effective += "x";
                }
                return effective;
            }
        }

        public string? LoginPath { get; set; }

        /// <summary>
        /// Returns <c>true</c> when <see cref="SecurityKey"/> is still the literal well-known
        /// default value. Publicly known and must be rejected at startup.
        /// </summary>
        /// <remarks>
        /// This predicate is intentionally narrow — it answers "has the operator ever
        /// touched <c>JwtOptions.SecurityKey</c>", which
        /// <c>WtmConfigValidationExtension</c>'s private <c>IsJwtActive</c> helper uses to
        /// decide whether JWT is in use at all before running the other JWT validators. It
        /// does NOT catch every weak key — a demo key like <c>"super"</c> or a too-short
        /// custom key both return <c>false</c> here. For the actual security invariant, use
        /// <see cref="IsWeakSigningKey"/>.
        /// </remarks>
        /// <remarks>
        /// #931 item 4 fix: previously compared against the PADDED backing field, so a
        /// non-default custom key that happened to pad to the exact same 32-character string
        /// as the padded default — e.g. the 19-byte <c>"wtmwtmwtmwtmwtmwtmx"</c>, or the
        /// default plus any of 1–13 more <c>'x'</c> characters — incorrectly returned
        /// <c>true</c>, which made <c>IsJwtActive</c> compute <c>false</c> and skip the weak-key
        /// validator entirely for a key that was not actually the default. Since
        /// <see cref="SecurityKey"/> no longer pads (#931 item 1), this is now a plain,
        /// unambiguous string comparison against the raw value — there is no padded form left
        /// to collide against.
        /// </remarks>
        public bool IsFactoryDefaultKey() => SecurityKey == WellKnownDefaultKey;

        /// <summary>
        /// Obsolete alias for <see cref="IsFactoryDefaultKey"/> — kept for source
        /// compatibility. Its behaviour has NOT changed: it still only detects the shipped
        /// CLR default, not the broader set of publicly known or too-short keys that
        /// <see cref="IsWeakSigningKey"/> exists to catch (issue #923).
        /// </summary>
        /// <remarks>
        /// Code that copied this method's old name into an "is JWT active" check (the same
        /// idiom <c>WtmConfigValidationExtension.IsJwtActive</c> used) must switch to
        /// <see cref="IsFactoryDefaultKey"/> for that purpose and to
        /// <see cref="IsWeakSigningKey"/> for the actual security check. Using THIS method
        /// for the latter silently treats a weak (but non-default) key as "JWT not in use"
        /// and skips validation — the wrong direction.
        /// </remarks>
        [Obsolete("Use IsFactoryDefaultKey() for activation detection or IsWeakSigningKey(out reason) for the security check. See issue #923.")]
        public bool IsDefaultOrWeakKey() => IsFactoryDefaultKey();

        /// <summary>
        /// Issue #923 — the actual security invariant: <c>true</c> when this key must not be
        /// used to sign or validate a JWT. Evaluated against <see cref="SecurityKey"/> (the
        /// raw, pre-padding value — see its own remarks for why evaluating the padded
        /// <see cref="EffectiveSecurityKey"/> would make a short-key check vacuously true).
        /// </summary>
        /// <param name="reason">
        /// One of <see cref="WeakReasonUnset"/>, <see cref="WeakReasonKnownPublicKey"/>, or
        /// <see cref="WeakReasonTooShort"/> when this returns <c>true</c>; <c>null</c> when
        /// it returns <c>false</c>. These are fixed constants, not per-call interpolated
        /// text, specifically so a caller (see
        /// <c>FrameworkServiceExtension.AddWtmAuthentication</c>) can compare
        /// <paramref name="reason"/> by reference equality to distinguish "unset or publicly
        /// known" from "too short" — the #923 design's Development carve-out is lenient for
        /// the former and deliberately strict for the latter (row (d) of the design table:
        /// an operator who set a too-short key believed it was supported, and that false
        /// belief must surface locally, not just in production).
        /// </param>
        /// <remarks>
        /// A key is weak when it is null/empty/whitespace-only, is — regardless of length —
        /// one of <see cref="KnownPublicKeys"/> (the manual's 42-byte placeholder would
        /// otherwise pass a length-only check), or is shorter than 32 UTF-8 bytes (the
        /// hard minimum <c>SymmetricSignatureProvider</c> enforces for <c>HmacSha256</c>,
        /// error code <c>IDX10720</c> — 32 bytes is the algorithm's key-size FLOOR, not itself
        /// a claim that any key meeting it has 256 bits of actual entropy; a 32-byte key of
        /// repeated characters passes this check and is still a bad key, which is exactly what
        /// <see cref="HasLowCharacterDiversity"/> exists to flag separately). The blocklist
        /// check runs BEFORE the length check deliberately: a value that is both too short and
        /// a known public key (e.g. the unset default, or <c>"super"</c>) must report
        /// <see cref="WeakReasonKnownPublicKey"/>, not <see cref="WeakReasonTooShort"/> — see
        /// the carve-out distinction above. The blocklist comparison trims surrounding
        /// whitespace and is case-insensitive, so <c>"  super  "</c> and <c>"SUPER"</c> are
        /// both caught.
        /// </remarks>
        public bool IsWeakSigningKey(out string? reason)
        {
            var raw = SecurityKey;
            if (string.IsNullOrWhiteSpace(raw))
            {
                reason = WeakReasonUnset;
                return true;
            }

            var trimmed = raw.Trim();

            if (KnownPublicKeys.Contains(trimmed))
            {
                reason = WeakReasonKnownPublicKey;
                return true;
            }

            if (Encoding.UTF8.GetByteCount(trimmed) < 32)
            {
                reason = WeakReasonTooShort;
                return true;
            }

            reason = null;
            return false;
        }

        /// <summary>
        /// <see cref="IsWeakSigningKey"/>'s <c>reason</c> when <see cref="SecurityKey"/> was
        /// never explicitly set (raw value still the CLR field-initializer default).
        /// </summary>
        public const string WeakReasonUnset = "JwtOptions.SecurityKey is not set.";

        /// <summary>
        /// <see cref="IsWeakSigningKey"/>'s <c>reason</c> when the raw value matches
        /// <see cref="KnownPublicKeys"/> — checked independently of length.
        /// </summary>
        public const string WeakReasonKnownPublicKey = "JwtOptions.SecurityKey is one of the publicly known placeholder values shipped with WTM.";

        /// <summary>
        /// <see cref="IsWeakSigningKey"/>'s <c>reason</c> when the raw value is shorter than
        /// 32 UTF-8 bytes and is NOT one of <see cref="KnownPublicKeys"/>.
        /// </summary>
        public const string WeakReasonTooShort = "JwtOptions.SecurityKey is shorter than the 32-byte (256-bit) minimum required for HMAC-SHA256 signing.";

        /// <summary>
        /// Issue #923 — warn-only signal: <c>true</c> when the raw configured key has fewer
        /// than 8 distinct characters, e.g. an operator manually padding a short key out to
        /// 32+ characters with a repeated filler character to satisfy the length check.
        /// </summary>
        /// <remarks>
        /// This does NOT gate startup and must never be treated as a substitute for
        /// <see cref="IsWeakSigningKey"/>: it is not an entropy measurement (a genuine
        /// 40-character English sentence also has fewer than 8 distinct characters and would
        /// trigger it), so per this repo's Compatibility &gt; Security priority ordering, a
        /// heuristic false positive here must never stop production from booting — the
        /// caller is expected to log and continue, not throw.
        /// </remarks>
        public bool HasLowCharacterDiversity
        {
            get
            {
                var raw = SecurityKey;
                if (string.IsNullOrWhiteSpace(raw)) return false;
                return new HashSet<char>(raw.Trim()).Count < 8;
            }
        }
    }
}
