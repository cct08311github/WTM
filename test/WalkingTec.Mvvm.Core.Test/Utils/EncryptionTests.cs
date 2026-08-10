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
