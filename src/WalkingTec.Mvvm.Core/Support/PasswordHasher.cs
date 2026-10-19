using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Outcome of <see cref="PasswordHasher.Verify"/>.
    /// </summary>
    public enum PasswordVerifyResult
    {
        /// <summary>The password does not match, or the stored value is not a recognised format.</summary>
        Failed = 0,

        /// <summary>The password matches and the stored hash is current.</summary>
        Success = 1,

        /// <summary>The password matches, but the stored hash uses an older format or fewer iterations than configured and should be replaced by <see cref="PasswordHasher.Hash"/>.</summary>
        SuccessRehashNeeded = 2
    }

    /// <summary>
    /// Creates and verifies password hashes. Two stored formats are understood:
    /// <list type="bullet">
    /// <item><description>legacy MD5: 32 hex characters (the output of <see cref="Utils.GetMD5String"/>), compared case-insensitively;</description></item>
    /// <item><description>PBKDF2: <c>$pbkdf2-sha256$iterations$saltBase64$hashBase64</c> (16 byte random salt, 32 byte hash, HMAC-SHA256).</description></item>
    /// </list>
    /// <para>
    /// With the default options (<see cref="PasswordAlgorithm.Md5"/>) <see cref="Hash"/> returns exactly
    /// <see cref="Utils.GetMD5String"/>, so stored data and login behaviour are unchanged.
    /// </para>
    /// <para>
    /// Safety guard: the PBKDF2 format is longer than the 32 characters of the default <c>FrameworkUserBase.Password</c> column
    /// (91 characters with the default iteration count). When <see cref="PasswordAlgorithm.Pbkdf2Sha256"/> is configured but the
    /// known maximum length of the password column is smaller than that, the hasher does not produce PBKDF2 hashes (a database
    /// would truncate or reject them): <see cref="Algorithm"/> falls back to <see cref="PasswordAlgorithm.Md5"/> and a Warning is
    /// logged once when the hasher is created. Existing PBKDF2 hashes are still verified, and no rehash is requested.
    /// </para>
    /// </summary>
    public class PasswordHasher
    {
        /// <summary>Prefix of the PBKDF2 storage format.</summary>
        public const string Pbkdf2Prefix = "$pbkdf2-sha256$";

        private const int SaltSize = 16;
        private const int HashSize = 32;
        // upper bound for the iteration count read from a stored hash, so a corrupted value cannot stall a login
        private const int MaxStoredIterations = 10000000;
        private const string PasswordPropertyName = "Password";

        private static readonly Lazy<PasswordHasher> _default = new Lazy<PasswordHasher>(() => new PasswordHasher(new PasswordOption()));

        /// <summary>
        /// A hasher with the default options (MD5). For code that has no <see cref="WTMContext"/>, such as seed data.
        /// Hashes it creates are upgraded at login when a stronger algorithm is configured.
        /// </summary>
        public static PasswordHasher Default => _default.Value;

        /// <summary>The algorithm requested in the options.</summary>
        public PasswordAlgorithm ConfiguredAlgorithm { get; }

        /// <summary>The algorithm actually used by <see cref="Hash"/>; differs from <see cref="ConfiguredAlgorithm"/> only when the safety guard applies.</summary>
        public PasswordAlgorithm Algorithm { get; }

        /// <summary>PBKDF2 iteration count used for new hashes.</summary>
        public int Iterations { get; }

        /// <summary>True when PBKDF2 was configured but the safety guard forced MD5.</summary>
        public bool FellBackToMd5 => ConfiguredAlgorithm == PasswordAlgorithm.Pbkdf2Sha256 && Algorithm == PasswordAlgorithm.Md5;

        /// <summary>
        /// Creates a hasher.
        /// </summary>
        /// <param name="options">Password options; null means defaults (MD5).</param>
        /// <param name="maxStoredLength">Known maximum length of the password column, or null when unknown or unlimited.</param>
        /// <param name="logger">Receives the one-time Warning when the safety guard applies.</param>
        public PasswordHasher(PasswordOption options, int? maxStoredLength = null, ILogger logger = null)
        {
            options = options ?? new PasswordOption();
            ConfiguredAlgorithm = options.Algorithm;
            Iterations = options.Pbkdf2Iterations >= 1 ? options.Pbkdf2Iterations : PasswordOption.DefaultPbkdf2Iterations;
            Algorithm = ConfiguredAlgorithm;
            if (ConfiguredAlgorithm == PasswordAlgorithm.Pbkdf2Sha256 && maxStoredLength.HasValue)
            {
                var needed = Pbkdf2OutputLength(Iterations);
                if (maxStoredLength.Value < needed)
                {
                    Algorithm = PasswordAlgorithm.Md5;
                    logger?.LogWarning("PasswordOptions:Algorithm is {Configured}, but the password column allows only {MaxLength} characters and the PBKDF2 format needs {Needed}. New passwords keep using MD5 until the column is widened.", ConfiguredAlgorithm, maxStoredLength.Value, needed);
                }
            }
        }

        /// <summary>
        /// Hashes a password with <see cref="Algorithm"/>. For MD5 this is exactly <see cref="Utils.GetMD5String"/> (null gives an empty string).
        /// </summary>
        public string Hash(string password)
        {
            if (Algorithm == PasswordAlgorithm.Md5)
            {
                return Utils.GetMD5String(password);
            }
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password ?? string.Empty, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
            return string.Concat(Pbkdf2Prefix, Iterations.ToString(CultureInfo.InvariantCulture), "$", Convert.ToBase64String(salt), "$", Convert.ToBase64String(hash));
        }

        /// <summary>
        /// Checks a password against a stored hash in either supported format. A null or empty stored value, a null password and
        /// an unrecognised stored format all give <see cref="PasswordVerifyResult.Failed"/>.
        /// </summary>
        public PasswordVerifyResult Verify(string stored, string password)
        {
            if (string.IsNullOrEmpty(stored) || password == null)
            {
                return PasswordVerifyResult.Failed;
            }
            if (IsLegacyMd5(stored))
            {
                var expected = Encoding.ASCII.GetBytes(Utils.GetMD5String(password));
                var actual = Encoding.ASCII.GetBytes(stored.ToUpperInvariant());
                if (CryptographicOperations.FixedTimeEquals(expected, actual) == false)
                {
                    return PasswordVerifyResult.Failed;
                }
                return Algorithm == PasswordAlgorithm.Pbkdf2Sha256 ? PasswordVerifyResult.SuccessRehashNeeded : PasswordVerifyResult.Success;
            }
            if (TryParsePbkdf2(stored, out var iterations, out var salt, out var expectedHash))
            {
                var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
                if (CryptographicOperations.FixedTimeEquals(actualHash, expectedHash) == false)
                {
                    return PasswordVerifyResult.Failed;
                }
                return Algorithm == PasswordAlgorithm.Pbkdf2Sha256 && iterations < Iterations ? PasswordVerifyResult.SuccessRehashNeeded : PasswordVerifyResult.Success;
            }
            return PasswordVerifyResult.Failed;
        }

        /// <summary>
        /// Does the work of one hash and discards it, so a login for an unknown account costs about the same as one for a known account.
        /// </summary>
        public void BurnTime(string password)
        {
            Hash(password);
        }

        /// <summary>
        /// Length of a PBKDF2 hash string created with the given iteration count.
        /// </summary>
        public static int Pbkdf2OutputLength(int iterations)
        {
            return Pbkdf2Prefix.Length
                + iterations.ToString(CultureInfo.InvariantCulture).Length
                + 1 + Base64Length(SaltSize)
                + 1 + Base64Length(HashSize);
        }

        /// <summary>
        /// Reads the maximum length of the user password column: from the EF model when <paramref name="dc"/> is a DbContext that
        /// maps the property, otherwise from <c>[StringLength]</c> / <c>[MaxLength]</c> on the user type. Null means unknown or unlimited.
        /// </summary>
        public static int? GetPasswordMaxLength(Type userType, object dc)
        {
            userType = userType ?? typeof(FrameworkUserBase);
            if (dc is DbContext dbContext)
            {
                try
                {
                    var prop = dbContext.Model.FindEntityType(userType)?.FindProperty(PasswordPropertyName);
                    if (prop != null)
                    {
                        return prop.GetMaxLength();
                    }
                }
                catch
                {
                    // fall back to the attributes
                }
            }
            var pi = userType.GetProperty(PasswordPropertyName, BindingFlags.Public | BindingFlags.Instance);
            if (pi == null)
            {
                return null;
            }
            var sl = pi.GetCustomAttribute<StringLengthAttribute>();
            if (sl != null)
            {
                return sl.MaximumLength;
            }
            var ml = pi.GetCustomAttribute<MaxLengthAttribute>();
            if (ml != null && ml.Length > 0)
            {
                return ml.Length;
            }
            return null;
        }

        private static int Base64Length(int bytes)
        {
            return (bytes + 2) / 3 * 4;
        }

        private static bool IsLegacyMd5(string stored)
        {
            if (stored.Length != 32)
            {
                return false;
            }
            foreach (var c in stored)
            {
                var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (hex == false)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryParsePbkdf2(string stored, out int iterations, out byte[] salt, out byte[] hash)
        {
            iterations = 0;
            salt = null;
            hash = null;
            if (stored.StartsWith(Pbkdf2Prefix, StringComparison.Ordinal) == false)
            {
                return false;
            }
            var parts = stored.Substring(Pbkdf2Prefix.Length).Split('$');
            if (parts.Length != 3)
            {
                return false;
            }
            if (int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out iterations) == false || iterations < 1 || iterations > MaxStoredIterations)
            {
                return false;
            }
            try
            {
                salt = Convert.FromBase64String(parts[1]);
                hash = Convert.FromBase64String(parts[2]);
            }
            catch (FormatException)
            {
                return false;
            }
            return salt.Length >= 8 && hash.Length >= 16 && hash.Length <= 64;
        }
    }
}
