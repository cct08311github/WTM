namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Algorithm used to store newly created passwords.
    /// </summary>
    public enum PasswordAlgorithm
    {
        /// <summary>
        /// Unsalted MD5, 32 uppercase hex characters (the historical format and the default).
        /// </summary>
        Md5 = 0,

        /// <summary>
        /// Salted PBKDF2 with HMAC-SHA256, stored as <c>$pbkdf2-sha256$iterations$salt$hash</c>.
        /// </summary>
        Pbkdf2Sha256 = 1
    }

    /// <summary>
    /// Password storage settings (config section: PasswordOptions).
    /// </summary>
    public class PasswordOption
    {
        /// <summary>
        /// Default PBKDF2 iteration count.
        /// </summary>
        public const int DefaultPbkdf2Iterations = 600000;

        /// <summary>
        /// Algorithm used when a password is hashed (config: PasswordOptions:Algorithm, default Md5).
        /// Stored hashes in either format are always accepted at login, whatever this is set to.
        /// <see cref="PasswordAlgorithm.Pbkdf2Sha256"/> only takes effect when the password column of the user
        /// table can hold the longer PBKDF2 format; otherwise MD5 stays in use and a Warning is logged (see <see cref="PasswordHasher"/>).
        /// </summary>
        public PasswordAlgorithm Algorithm { get; set; } = PasswordAlgorithm.Md5;

        /// <summary>
        /// PBKDF2 iteration count for new hashes (config: PasswordOptions:Pbkdf2Iterations, default 600000).
        /// Values outside 100000..2000000 are clamped to that range (a Warning is logged). A stored PBKDF2 hash with fewer
        /// iterations than the effective count is upgraded at the next successful login.
        /// </summary>
        public int Pbkdf2Iterations { get; set; } = DefaultPbkdf2Iterations;
    }
}
