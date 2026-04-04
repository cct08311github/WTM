using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;

namespace WalkingTec.Mvvm.Core
{
    public static class PasswordHashHelper
    {
        private static readonly PasswordHasher<string> _pbkdf2Hasher = new();
        private static readonly Regex _md5Pattern =
            new(@"^[0-9A-F]{32}$", RegexOptions.Compiled);

        public static string HashPassword(string? password)
        {
            if (string.IsNullOrEmpty(password)) return string.Empty;
            return BCrypt.Net.BCrypt.HashPassword(password);
        }

        public static PasswordVerifyResult VerifyPassword(
            string? storedHash, string? password)
        {
            if (string.IsNullOrEmpty(storedHash) ||
                string.IsNullOrEmpty(password))
                return PasswordVerifyResult.Failed;

            // Legacy hash detection chain — oldest format first.
            // MD5 (uppercase hex, 32 chars) was used in WTM <= 8.1.12.
            // On match, return SuccessRehashNeeded so the caller upgrades to BCrypt.
            if (IsLegacyMD5Hash(storedHash))
            {
                var md5 = ComputeMD5(password);
                return string.Equals(storedHash, md5, StringComparison.Ordinal)
                    ? PasswordVerifyResult.SuccessRehashNeeded
                    : PasswordVerifyResult.Failed;
            }

            // Detect PBKDF2 hashes from v8.1.13–v8.6.x (ASP.NET Identity PasswordHasher).
            // These use a version-byte prefix: 0x00 (v2) or 0x01 (v3), Base64-encoded
            // as "AAAAA..." or "AQAAAA..." respectively. Rehash to BCrypt on success.
            if (IsLegacyPBKDF2Hash(storedHash))
            {
                var result = _pbkdf2Hasher.VerifyHashedPassword(
                    string.Empty, storedHash, password);
                return result != PasswordVerificationResult.Failed
                    ? PasswordVerifyResult.SuccessRehashNeeded
                    : PasswordVerifyResult.Failed;
            }

            // Current algorithm: BCrypt. If the stored hash is corrupted or in an
            // unrecognized format, BCrypt.Verify throws a parse exception — return
            // Failed instead of crashing, since the password simply cannot be verified.
            try
            {
                bool isMatch = BCrypt.Net.BCrypt.Verify(password, storedHash);
                return isMatch
                    ? PasswordVerifyResult.Success
                    : PasswordVerifyResult.Failed;
            }
            catch
            {
                return PasswordVerifyResult.Failed;
            }
        }

        public static bool IsLegacyMD5Hash(string? hash)
        {
            if (string.IsNullOrEmpty(hash) || hash.Length != 32) return false;
            return _md5Pattern.IsMatch(hash);
        }

        /// <summary>
        /// Detects PBKDF2 hashes produced by ASP.NET Identity PasswordHasher.
        /// V2 hashes start with 0x00 (Base64: "AAAAA"), V3 with 0x01 (Base64: "AQAAAA").
        /// </summary>
        internal static bool IsLegacyPBKDF2Hash(string? hash)
        {
            if (string.IsNullOrEmpty(hash) || hash.Length < 20) return false;
            return hash.StartsWith("AQAAAA", StringComparison.Ordinal)
                || hash.StartsWith("AAAAA", StringComparison.Ordinal);
        }

        internal static string ComputeMD5(string? input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            Span<byte> hash = stackalloc byte[16]; // MD5 = 16 bytes
            MD5.HashData(Encoding.UTF8.GetBytes(input), hash);
            return Convert.ToHexString(hash);
        }
    }

    public enum PasswordVerifyResult
    {
        Failed = 0,
        Success = 1,
        SuccessRehashNeeded = 2
    }
}
