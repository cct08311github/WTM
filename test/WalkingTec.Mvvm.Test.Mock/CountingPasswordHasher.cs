using System;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Test.Mock
{
    /// <summary>
    /// A <see cref="PasswordHasher"/> that counts how often the expensive PBKDF2 step ran, and can run a callback inside it.
    /// </summary>
    public class CountingPasswordHasher : PasswordHasher
    {
        public CountingPasswordHasher(PasswordOption options, int? maxStoredLength = null) : base(options, maxStoredLength)
        {
        }

        /// <summary>Number of PBKDF2 derivations so far.</summary>
        public int Derivations { get; private set; }

        /// <summary>Runs on each derivation, before it is computed; receives the 1 based number of the derivation.</summary>
        public Action<int> OnDerive { get; set; }

        protected override byte[] DeriveKey(string password, byte[] salt, int iterations, int length)
        {
            Derivations++;
            OnDerive?.Invoke(Derivations);
            return base.DeriveKey(password, salt, iterations, length);
        }
    }
}
