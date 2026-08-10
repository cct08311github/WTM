#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Utils
{
    [TestClass]
    public class EncryptionTests
    {
        // ─── AES Round-Trip ──────────────────────────────────────────────────

        [TestMethod]
        public void EncryptString_DecryptString_RoundTrip_ReturnsOriginal()
        {
            var plaintext = "Hello, AES-256!";
            var key = "my-secret-key";

            var encrypted = WalkingTec.Mvvm.Core.Utils.EncryptString(plaintext, key);
            var decrypted = WalkingTec.Mvvm.Core.Utils.DecryptString(encrypted, key);

            decrypted.Should().Be(plaintext);
        }

        [TestMethod]
        public void EncryptString_DecryptString_UnicodeRoundTrip()
        {
            var plaintext = "密碼テスト🔐Ñoño 中文測試";
            var key = "unicode-key-密碼";

            var encrypted = WalkingTec.Mvvm.Core.Utils.EncryptString(plaintext, key);
            var decrypted = WalkingTec.Mvvm.Core.Utils.DecryptString(encrypted, key);

            decrypted.Should().Be(plaintext);
        }

        // ─── Various Key Lengths ─────────────────────────────────────────────

        [DataTestMethod]
        [DataRow("a", "short key")]
        [DataRow("Hello World 123", "this-is-a-longer-key-that-exceeds-32-characters-easily")]
        [DataRow("test data", "k")]
        public void EncryptString_DecryptString_VariousKeyLengths_RoundTrips(string plaintext, string key)
        {
            var encrypted = WalkingTec.Mvvm.Core.Utils.EncryptString(plaintext, key);
            var decrypted = WalkingTec.Mvvm.Core.Utils.DecryptString(encrypted, key);

            decrypted.Should().Be(plaintext);
        }

        // ─── Empty Input ─────────────────────────────────────────────────────

        [TestMethod]
        public void EncryptString_EmptyInput_ReturnsEmpty()
        {
            WalkingTec.Mvvm.Core.Utils.EncryptString("", "key").Should().BeEmpty();
        }

        [TestMethod]
        public void DecryptString_EmptyInput_ReturnsEmpty()
        {
            WalkingTec.Mvvm.Core.Utils.DecryptString("", "key").Should().BeEmpty();
        }

        // ─── Different Keys Produce Different Ciphertexts ────────────────────

        [TestMethod]
        public void EncryptString_DifferentKeys_ProduceDifferentCiphertexts()
        {
            var plaintext = "same plaintext";

            var enc1 = WalkingTec.Mvvm.Core.Utils.EncryptString(plaintext, "key-one");
            var enc2 = WalkingTec.Mvvm.Core.Utils.EncryptString(plaintext, "key-two");

            enc1.Should().NotBe(enc2);
        }

        // ─── Random IV: Same Plaintext Encrypted Twice Differs ───────────────

        [TestMethod]
        public void EncryptString_SamePlaintextTwice_ProducesDifferentCiphertexts()
        {
            var plaintext = "deterministic?";
            var key = "same-key";

            var enc1 = WalkingTec.Mvvm.Core.Utils.EncryptString(plaintext, key);
            var enc2 = WalkingTec.Mvvm.Core.Utils.EncryptString(plaintext, key);

            enc1.Should().NotBe(enc2, "each encryption should use a random IV");

            // But both should decrypt to the same plaintext
            WalkingTec.Mvvm.Core.Utils.DecryptString(enc1, key).Should().Be(plaintext);
            WalkingTec.Mvvm.Core.Utils.DecryptString(enc2, key).Should().Be(plaintext);
        }

        // ─── Wrong Key Fails ─────────────────────────────────────────────────

        [TestMethod]
        public void DecryptString_WrongKey_ReturnsEmptyOrGarbage()
        {
            var encrypted = WalkingTec.Mvvm.Core.Utils.EncryptString("secret", "correct-key");

            // With wrong key, AES decrypt will fail (padding exception) and
            // DES fallback will also fail, returning empty string
            var result = WalkingTec.Mvvm.Core.Utils.DecryptString(encrypted, "wrong-key");
            result.Should().NotBe("secret");
        }

        // ─── DES Backward Compatibility ──────────────────────────────────────

        [TestMethod]
        public void DecryptString_DESEncryptedData_FallsBackCorrectly()
        {
            // Encrypt using legacy DES via the private CreateLegacyDes helper
            var plaintext = "legacy data";
            var key = "testkey1";

            string desEncrypted = EncryptWithLegacyDes(plaintext, key);

            // DecryptString should fall back to DES and succeed
            var decrypted = WalkingTec.Mvvm.Core.Utils.DecryptString(desEncrypted, key);
            decrypted.Should().Be(plaintext);
        }

        [TestMethod]
        public void DecryptStringLegacy_DirectCall_Works()
        {
#pragma warning disable CS0618 // Obsolete warning expected
            var plaintext = "legacy test";
            var key = "mykey123";

            string desEncrypted = EncryptWithLegacyDes(plaintext, key);

            var decrypted = WalkingTec.Mvvm.Core.Utils.DecryptStringLegacy(desEncrypted, key);
            decrypted.Should().Be(plaintext);
#pragma warning restore CS0618
        }

        [TestMethod]
        public void DecryptStringLegacy_EmptyInput_ReturnsEmpty()
        {
#pragma warning disable CS0618
            WalkingTec.Mvvm.Core.Utils.DecryptStringLegacy("", "key").Should().BeEmpty();
#pragma warning restore CS0618
        }

        // ─── CipherAlgorithm overload (issue #1086) ───────────────────────────
        //
        // Utils.EncryptString(string, string) now delegates to
        // Utils.EncryptString(string, string, CipherAlgorithm.Aes256Cbc). AES-256-CBC
        // generates a fresh random IV per call (see
        // EncryptString_SamePlaintextTwice_ProducesDifferentCiphertexts above), so two
        // independent calls can never be byte-identical to each other. What IS provable
        // — and what these tests pin — is: (a) both overloads produce the same wire
        // format (deterministic ciphertext length for a given plaintext length) and
        // round-trip through DecryptString identically, and (b) a ciphertext built from
        // a *fixed* IV via the same private CreateAes factory decrypts to the exact
        // expected plaintext, which pins the SHA-256 key derivation / AES-256 / CBC /
        // PKCS7 / IV-prefix format that both overloads share.

        [TestMethod]
        public void EncryptString_TwoArgOverload_MatchesThreeArgAes256CbcOverload()
        {
            var plaintext = "Delegation parity check 委派一致性";
            var key = "parity-key";

            var twoArg = WalkingTec.Mvvm.Core.Utils.EncryptString(plaintext, key);
            var threeArg = WalkingTec.Mvvm.Core.Utils.EncryptString(plaintext, key, CipherAlgorithm.Aes256Cbc);

            // Same plaintext length + same algorithm ⇒ deterministic Base64 length
            // (16-byte IV + PKCS7-padded cipher), even though the bytes themselves
            // differ because each call gets its own random IV.
            twoArg.Length.Should().Be(threeArg.Length);

            // Both must decrypt back to the same plaintext, proving the two overloads
            // run the identical AES-256-CBC code path.
            WalkingTec.Mvvm.Core.Utils.DecryptString(twoArg, key).Should().Be(plaintext);
            WalkingTec.Mvvm.Core.Utils.DecryptString(threeArg, key).Should().Be(plaintext);
        }

        [TestMethod]
        public void EncryptString_Aes256CbcAlgorithm_OutputConsistentWithTwoArgOverload()
        {
            var plaintext = "explicit aes algorithm parity";
            var key = "aes-parity-key";

            var twoArg = WalkingTec.Mvvm.Core.Utils.EncryptString(plaintext, key);
            var explicitAes = WalkingTec.Mvvm.Core.Utils.EncryptString(plaintext, key, CipherAlgorithm.Aes256Cbc);

            explicitAes.Length.Should().Be(twoArg.Length);
            WalkingTec.Mvvm.Core.Utils.DecryptString(explicitAes, key).Should().Be(plaintext);
        }

        [TestMethod]
        public void DecryptString_KnownAesVector_FixedIv_DecryptsToExpectedPlaintext()
        {
            // Builds an AES-256-CBC ciphertext by hand (fixed IV, via the same private
            // CreateAes factory Utils.EncryptString uses internally) instead of going
            // through EncryptString, so the expected plaintext is a genuine known-answer
            // pin rather than a value read back from randomized output.
            var plaintext = "known-answer-test-vector 已知答案測試向量";
            var key = "known-vector-key";
            var fixedIv = new byte[16] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

            var method = typeof(WalkingTec.Mvvm.Core.Utils).GetMethod(
                "CreateAes",
                BindingFlags.NonPublic | BindingFlags.Static);
            method.Should().NotBeNull("CreateAes should exist as a private static method");

            using var aes = (Aes)method!.Invoke(null, new object[] { key })!;
            aes.IV = fixedIv;

            byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
            using var ms = new MemoryStream();
            using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                cs.Write(plainBytes, 0, plainBytes.Length);
                cs.FlushFinalBlock();
            }
            byte[] cipherBytes = ms.ToArray();

            byte[] payload = new byte[fixedIv.Length + cipherBytes.Length];
            Buffer.BlockCopy(fixedIv, 0, payload, 0, fixedIv.Length);
            Buffer.BlockCopy(cipherBytes, 0, payload, fixedIv.Length, cipherBytes.Length);

            var knownCiphertext = Convert.ToBase64String(payload);

            WalkingTec.Mvvm.Core.Utils.DecryptString(knownCiphertext, key).Should().Be(plaintext);
        }

        [TestMethod]
        public void EncryptStringLegacy_DecryptStringLegacy_RoundTrip()
        {
#pragma warning disable CS0618
            var plaintext = "legacy round trip";
            var key = "deskey12";

            var encrypted = WalkingTec.Mvvm.Core.Utils.EncryptStringLegacy(plaintext, key);
            var decrypted = WalkingTec.Mvvm.Core.Utils.DecryptStringLegacy(encrypted, key);

            decrypted.Should().Be(plaintext);
#pragma warning restore CS0618
        }

        [TestMethod]
        public void EncryptStringLegacy_EmptyInput_ReturnsEmpty()
        {
#pragma warning disable CS0618
            WalkingTec.Mvvm.Core.Utils.EncryptStringLegacy("", "key").Should().BeEmpty();
#pragma warning restore CS0618
        }

        [TestMethod]
        public void EncryptString_LegacyDesAlgorithm_DecryptsViaDecryptStringLegacy()
        {
#pragma warning disable CS0618
            var plaintext = "opt-in legacy des";
            var key = "rollback-key";

            var encrypted = WalkingTec.Mvvm.Core.Utils.EncryptString(plaintext, key, CipherAlgorithm.LegacyDes);
            var decrypted = WalkingTec.Mvvm.Core.Utils.DecryptStringLegacy(encrypted, key);

            decrypted.Should().Be(plaintext);
#pragma warning restore CS0618
        }

        [DataTestMethod]
        // Short plaintext ⇒ DES ciphertext decodes to < 32 bytes ⇒ DecryptString takes
        // the immediate legacy-DES path without ever attempting AES.
        [DataRow("hi")]
        // Long plaintext ⇒ DES ciphertext decodes to >= 32 bytes ⇒ DecryptString first
        // attempts AES (fails with CryptographicException against DES bytes) and only
        // then falls back to legacy DES — exercising the real fallback branch.
        [DataRow("this plaintext is deliberately long enough to push the DES ciphertext past the 32-byte AES-attempt threshold")]
        public void EncryptString_LegacyDesAlgorithm_DecryptsViaDecryptStringFallback(string plaintext)
        {
            var key = "rollback-key-2";

            var encrypted = WalkingTec.Mvvm.Core.Utils.EncryptString(plaintext, key, CipherAlgorithm.LegacyDes);
            var decrypted = WalkingTec.Mvvm.Core.Utils.DecryptString(encrypted, key);

            decrypted.Should().Be(plaintext);
        }

        [TestMethod]
        public void EncryptString_LegacyDesAlgorithm_MatchesKnownEightPointXWireFormat()
        {
            // ─── 8.x wire-format pin — DO NOT "fix" a red result by updating the
            // expected value ─────────────────────────────────────────────────────
            //
            // Every other DES test in this file is self-referential: it encrypts with
            // this codebase's own CreateLegacyDes and decrypts with the same method, so
            // if someone changes the key/IV derivation inside CreateLegacyDes, those
            // tests drift together and stay green — while ciphertext written by this
            // build would silently stop being readable by an actual 8.x-era WTM
            // deployment. That's exactly the rollback capability issue #1086 exists to
            // preserve, so it needs a test anchored to something outside this codebase.
            //
            // The anchor here is a manual diff between HEAD's CreateLegacyDes and 6.3.27
            // commit c1985f5f6's GenerateDESCryptoServiceProvider:
            //
            //   c1985f5f6 (6.3.27):  while (key.Length > 8) { key = key.Substring(0, 8); }
            //   HEAD (CreateLegacyDes): if (key.Length > 8) { key = key.Substring(0, 8); }
            //
            // `Substring(0, 8)` always yields a string of length exactly 8, so the
            // `while` loop in 6.3.27 can only ever execute its body once — `if` and
            // `while` are therefore equivalent here. Every other line (PadRight to the
            // provider's min key size, UTF8 byte conversion, Key = IV = that byte array)
            // is unchanged between the two versions. So for any input key, 6.3.27's
            // EncryptString and this build's EncryptStringLegacy derive the identical
            // DES Key/IV and must produce byte-identical ciphertext for the same
            // plaintext — DES has no IV randomization step (unlike the AES path), so
            // this is a genuine deterministic equality, not a statistical one.
            //
            // The expected Base64 below was produced by this build's
            // EncryptString(plaintext, key, CipherAlgorithm.LegacyDes) and, by the
            // equivalence above, is also what 6.3.27's EncryptString(plaintext, key)
            // produces. If this test goes red, the DES key/IV derivation or ciphertext
            // format changed — confirm whether that's an intentional format break
            // before touching the expected value, since an unintentional change here
            // silently breaks the ability to roll back to 8.x-era ciphertext.
            var plaintext = "WTM legacy DES wire-format pin for issue #1086 rollback test vector";
            var key = "wtm-8x-rollback-key";
            const string expectedCiphertext = "9zeL1CgUzLOwsWubQKgUvRVgUwX+854Q58nCGSzegsWbWBS1NFmxzHxL2Ta9jsH7vAs52mffmriLn6Bx5UzEsRuNZ3FBiWad";

            // Sanity-check the fixture itself: the decoded ciphertext must be >= 32
            // bytes so that DecryptString below genuinely exercises its
            // attempt-AES-first-then-fall-back-to-DES branch (see
            // EncryptString_LegacyDesAlgorithm_DecryptsViaDecryptStringFallback above),
            // not the short-circuit path for < 32-byte payloads.
            Convert.FromBase64String(expectedCiphertext).Length.Should().BeGreaterThanOrEqualTo(32);

            // DES has no random IV, so this is deterministic byte-for-byte equality —
            // not a round-trip check.
            var actual = WalkingTec.Mvvm.Core.Utils.EncryptString(plaintext, key, CipherAlgorithm.LegacyDes);
            actual.Should().Be(expectedCiphertext);

            WalkingTec.Mvvm.Core.Utils.DecryptString(expectedCiphertext, key).Should().Be(plaintext);
        }

        // ─── Issue #1085(a): AES "succeeds without throwing but is not really the
        // plaintext" must still fall back to legacy DES, not return garbage ──────
        //
        // Before this fix, DecryptString's only fallback trigger was a thrown
        // CryptographicException. PKCS7 unpadding validation only re-checks the
        // padding-count byte against itself when that count is 1 (there is nothing
        // else to compare), so any ciphertext block whose *decrypted* last byte
        // happens to be 0x01 unpads "successfully" no matter what the other 15
        // bytes are. That lets a wrong-key AES decrypt complete without exception
        // while returning bytes that are not the real plaintext at all.

        [TestMethod]
        public void DecryptString_AesUnpadsWithoutExceptionButOutputIsNotPlaintext_FallsBackToLegacyDes()
        {
            // Deterministic construction (not a statistical 1/512 one) of "AES-256-CBC
            // decrypts without throwing, but the output is not plaintext": pick an IV and a
            // desired decrypted block (15 bytes of 0xFF — never a valid UTF-8 lead byte —
            // followed by a 0x01 PKCS7 padding marker, which is always accepted on its own).
            // Then derive the one ciphertext block that decrypts to that value under the
            // known AES key DecryptString will use, via a raw single-block ECB encrypt
            // (CBC-decrypt(C) = ECB-decrypt(C) XOR previous-block-or-IV, so
            // C = ECB-encrypt(desiredPlaintext XOR IV) is the block whose CBC decryption is
            // exactly desiredPlaintext).
            var key = "positive-evidence-key";
            byte[] aesKey = SHA256.HashData(Encoding.UTF8.GetBytes(key));

            byte[] iv = new byte[16]; // arbitrary, fully test-controlled — no secret involved
            byte[] desiredPlaintextBlock = new byte[16];
            for (int i = 0; i < 15; i++)
            {
                desiredPlaintextBlock[i] = 0xFF; // 0xFF is never a valid UTF-8 byte at all
            }
            desiredPlaintextBlock[15] = 0x01; // PKCS7 "remove last 1 byte" — self-certifying

            byte[] xored = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                xored[i] = (byte)(desiredPlaintextBlock[i] ^ iv[i]);
            }

            byte[] cipherBlock;
            using (var ecb = Aes.Create())
            {
                ecb.Mode = CipherMode.ECB;
                ecb.Padding = PaddingMode.None;
                ecb.Key = aesKey;
                using var encryptor = ecb.CreateEncryptor();
                cipherBlock = encryptor.TransformFinalBlock(xored, 0, xored.Length);
            }

            byte[] fullCipher = new byte[32];
            Buffer.BlockCopy(iv, 0, fullCipher, 0, 16);
            Buffer.BlockCopy(cipherBlock, 0, fullCipher, 16, 16);
            var craftedCiphertext = Convert.ToBase64String(fullCipher);

            // Sanity-check the fixture's premise directly: the raw candidate this
            // construction produces (15x 0xFF after PKCS7 unpadding removes the trailing
            // 0x01) is not valid UTF-8, so LooksLikePlaintext must reject it.
            byte[] rawCandidate = new byte[15];
            for (int i = 0; i < 15; i++)
            {
                rawCandidate[i] = 0xFF;
            }
            WalkingTec.Mvvm.Core.Utils.LooksLikePlaintext(rawCandidate).Should().BeFalse(
                "15 bytes of 0xFF is never valid UTF-8");

            // DecryptString must fall back to legacy DES exactly as it would for
            // ciphertext AES could never decode — not return the AES garbage — so it must
            // match calling DecryptStringLegacy on the same input independently.
#pragma warning disable CS0618
            var expectedLegacyResult = WalkingTec.Mvvm.Core.Utils.DecryptStringLegacy(craftedCiphertext, key);
#pragma warning restore CS0618
            var actual = WalkingTec.Mvvm.Core.Utils.DecryptString(craftedCiphertext, key);

            actual.Should().Be(expectedLegacyResult,
                "AES unpadded without throwing but produced non-plaintext bytes, so DecryptString " +
                "must fall back to legacy DES exactly as DecryptStringLegacy would resolve it directly, " +
                "instead of returning the garbage AES bytes");
        }

        // ─── LooksLikePlaintext, exercised directly (internal, via InternalsVisibleTo) ──
        //
        // Complements the deterministic end-to-end test above by pinning the heuristic's
        // actual decision boundary: valid UTF-8 without disallowed control bytes passes;
        // invalid UTF-8 and disallowed control bytes are rejected; tab/CR/LF are allowed;
        // an empty candidate passes (a legitimate AES decrypt of a non-empty ciphertext can
        // still yield an empty plaintext).

        [TestMethod]
        public void LooksLikePlaintext_EmptyArray_ReturnsTrue()
        {
            WalkingTec.Mvvm.Core.Utils.LooksLikePlaintext(Array.Empty<byte>()).Should().BeTrue();
        }

        [TestMethod]
        public void LooksLikePlaintext_ValidUtf8Ascii_ReturnsTrue()
        {
            WalkingTec.Mvvm.Core.Utils.LooksLikePlaintext(Encoding.UTF8.GetBytes("connection string looking text"))
                .Should().BeTrue();
        }

        [TestMethod]
        public void LooksLikePlaintext_ValidUtf8MultiByte_ReturnsTrue()
        {
            // Chinese text + an emoji (4-byte UTF-8 sequence)
            WalkingTec.Mvvm.Core.Utils.LooksLikePlaintext(Encoding.UTF8.GetBytes("中文測試🔐"))
                .Should().BeTrue();
        }

        [TestMethod]
        public void LooksLikePlaintext_TabCrLf_AreAllowed()
        {
            WalkingTec.Mvvm.Core.Utils.LooksLikePlaintext(Encoding.UTF8.GetBytes("line1\tcol\r\nline2"))
                .Should().BeTrue();
        }

        [TestMethod]
        public void LooksLikePlaintext_InvalidUtf8Bytes_ReturnsFalse()
        {
            // 0xFF is not a valid UTF-8 byte anywhere in a sequence.
            byte[] invalid = [0xFF, 0xFE, 0xFD, 0xFC];
            WalkingTec.Mvvm.Core.Utils.LooksLikePlaintext(invalid).Should().BeFalse();
        }

        [TestMethod]
        public void LooksLikePlaintext_LoneContinuationByte_ReturnsFalse()
        {
            // 0x80 is a UTF-8 continuation byte and is never valid as a standalone byte.
            byte[] invalid = [0x80, 0x41, 0x42];
            WalkingTec.Mvvm.Core.Utils.LooksLikePlaintext(invalid).Should().BeFalse();
        }

        [TestMethod]
        public void LooksLikePlaintext_TruncatedMultiByteSequence_ReturnsFalse()
        {
            // 0xE4 0xB8 starts a valid 3-byte sequence but is missing its final byte.
            byte[] invalid = [0xE4, 0xB8];
            WalkingTec.Mvvm.Core.Utils.LooksLikePlaintext(invalid).Should().BeFalse();
        }

        [TestMethod]
        public void LooksLikePlaintext_NonTabCrLfControlChar_ReturnsFalse()
        {
            // NUL byte: valid single-byte UTF-8, but a disallowed control character.
            byte[] withNul = [(byte)'a', 0x00, (byte)'b'];
            WalkingTec.Mvvm.Core.Utils.LooksLikePlaintext(withNul).Should().BeFalse();
        }

        // ─── GetDecryptRoute (issue #1085(b)) ─────────────────────────────────
        //
        // GetDecryptRoute exposes the same routing decision DecryptString makes
        // internally (both call the private ClassifyDecryptRoute), so a downstream
        // no longer has to reimplement its own — and possibly backwards — guess at
        // "will this cipher text hit the probabilistic AES-attempt branch".

        [TestMethod]
        public void GetDecryptRoute_NullInput_ReturnsEmpty()
        {
            WalkingTec.Mvvm.Core.Utils.GetDecryptRoute(null).Should().Be(DecryptRoute.Empty);
        }

        [TestMethod]
        public void GetDecryptRoute_EmptyStringInput_ReturnsEmpty()
        {
            WalkingTec.Mvvm.Core.Utils.GetDecryptRoute("").Should().Be(DecryptRoute.Empty);
        }

        [TestMethod]
        public void GetDecryptRoute_NotValidBase64_ReturnsNotBase64()
        {
            // Space is normalized to '+' by DecryptString before decoding, so this needs a
            // character that is invalid in Base64 even after that substitution.
            WalkingTec.Mvvm.Core.Utils.GetDecryptRoute("this is not base64 at all !!!")
                .Should().Be(DecryptRoute.NotBase64);
        }

        [TestMethod]
        public void GetDecryptRoute_ShortLegacyDesCiphertext_ReturnsLegacyDesShort()
        {
            // "hi" DES-encrypts to a single 8-byte block, whose Base64 form decodes to well
            // under 32 bytes — the deterministic (non-AES-attempt) branch.
            string desEncrypted = EncryptWithLegacyDes("hi", "testkey1");

            Convert.FromBase64String(desEncrypted).Length.Should().BeLessThan(32, "fixture sanity check");
            WalkingTec.Mvvm.Core.Utils.GetDecryptRoute(desEncrypted).Should().Be(DecryptRoute.LegacyDesShort);
        }

        [TestMethod]
        public void GetDecryptRoute_AesCiphertext_ReturnsAesAttempted()
        {
            var encrypted = WalkingTec.Mvvm.Core.Utils.EncryptString("x", "some-key");

            Convert.FromBase64String(encrypted).Length.Should().BeGreaterThanOrEqualTo(32, "fixture sanity check");
            WalkingTec.Mvvm.Core.Utils.GetDecryptRoute(encrypted).Should().Be(DecryptRoute.AesAttempted);
        }

        [TestMethod]
        public void GetDecryptRoute_KnownEightPointXDesWireFormatVector_ReturnsAesAttempted()
        {
            // ─── This test's very existence is the documentation that GetDecryptRoute does
            // NOT identify which algorithm a cipher text was encrypted with ───────────────
            //
            // This is the exact same hardcoded 8.x-era DES ciphertext pinned in
            // EncryptString_LegacyDesAlgorithm_MatchesKnownEightPointXWireFormat above: a
            // genuine DES ciphertext (produced by a real 6.3.27-equivalent DES encryption,
            // not AES) that happens to decode to 72 bytes — >= the 32-byte threshold. So
            // DecryptRoute.AesAttempted is the correct answer here even though the input is
            // unambiguously DES, not AES: AesAttempted means "DecryptString will try AES
            // first on this input", never "this cipher text is AES-encrypted". A caller that
            // reads AesAttempted as "confirmed AES" has the API backwards.
            const string knownEightPointXDesCiphertext =
                "9zeL1CgUzLOwsWubQKgUvRVgUwX+854Q58nCGSzegsWbWBS1NFmxzHxL2Ta9jsH7vAs52mffmriLn6Bx5UzEsRuNZ3FBiWad";

            Convert.FromBase64String(knownEightPointXDesCiphertext).Length.Should().BeGreaterThanOrEqualTo(32, "fixture sanity check");
            WalkingTec.Mvvm.Core.Utils.GetDecryptRoute(knownEightPointXDesCiphertext)
                .Should().Be(DecryptRoute.AesAttempted,
                    "a >= 32-byte DES ciphertext still routes through the probabilistic AES-attempt " +
                    "branch — GetDecryptRoute reports the route DecryptString takes, not the algorithm " +
                    "the cipher text was actually produced with");
        }

        // ─── Helper: encrypt with legacy DES (mirrors old EncryptString) ─────

        private static string EncryptWithLegacyDes(string plaintext, string key)
        {
            // Use reflection to call the private CreateLegacyDes method
            var method = typeof(WalkingTec.Mvvm.Core.Utils).GetMethod(
                "CreateLegacyDes",
                BindingFlags.NonPublic | BindingFlags.Static);
            method.Should().NotBeNull("CreateLegacyDes should exist as a private static method");

            using var des = (DES)method!.Invoke(null, new object[] { key })!;
            byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);

            using var ms = new MemoryStream();
            using (var cs = new CryptoStream(ms, des.CreateEncryptor(), CryptoStreamMode.Write))
            {
                cs.Write(plainBytes, 0, plainBytes.Length);
                cs.FlushFinalBlock();
            }

            return Convert.ToBase64String(ms.ToArray());
        }
    }
}
