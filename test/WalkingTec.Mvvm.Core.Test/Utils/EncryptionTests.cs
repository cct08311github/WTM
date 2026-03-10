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
