using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;

namespace WalkingTec.Mvvm.Core
{
    public static class PasswordHashHelper
    {
        private static readonly PasswordHasher<string> _hasher = new();
        private static readonly Regex _md5Pattern =
            new(@"^[0-9A-F]{32}$", RegexOptions.Compiled);

        public static string HashPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return string.Empty;
            return _hasher.HashPassword(string.Empty, password);
        }

        public static PasswordVerifyResult VerifyPassword(
            string storedHash, string password)
        {
            if (string.IsNullOrEmpty(storedHash) ||
                string.IsNullOrEmpty(password))
                return PasswordVerifyResult.Failed;

            if (IsLegacyMD5Hash(storedHash))
            {
                var md5 = ComputeMD5(password);
                return string.Equals(storedHash, md5, StringComparison.Ordinal)
                    ? PasswordVerifyResult.SuccessRehashNeeded
                    : PasswordVerifyResult.Failed;
            }

            var result = _hasher.VerifyHashedPassword(
                string.Empty, storedHash, password);
            return result switch
            {
                PasswordVerificationResult.Success
                    => PasswordVerifyResult.Success,
                PasswordVerificationResult.SuccessRehashNeeded
                    => PasswordVerifyResult.SuccessRehashNeeded,
                _ => PasswordVerifyResult.Failed
            };
        }

        public static bool IsLegacyMD5Hash(string hash)
        {
            if (string.IsNullOrEmpty(hash) || hash.Length != 32) return false;
            return _md5Pattern.IsMatch(hash);
        }

        internal static string ComputeMD5(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            byte[] buffer = Encoding.UTF8.GetBytes(input);
            byte[] hash = MD5.HashData(buffer);
            var sb = new StringBuilder(32);
            foreach (byte b in hash) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }
    }

    public enum PasswordVerifyResult
    {
        Failed = 0,
        Success = 1,
        SuccessRehashNeeded = 2
    }
}
