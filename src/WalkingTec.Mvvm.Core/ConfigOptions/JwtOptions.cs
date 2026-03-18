#nullable enable
using System;
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

        private string _securiteKey = WellKnownDefaultKey;

        public string SecurityKey
        {
            get => _securiteKey;
            set
            {
                _securiteKey = value;
                if (_securiteKey.Length < 32)
                {
                    var count = 32 - _securiteKey.Length;
                    for (int i = 0; i < count; i++)
                        _securiteKey += "x";
                }
            }
        }

        public string? LoginPath { get; set; }

        /// <summary>
        /// Returns <c>true</c> when <see cref="SecurityKey"/> is still the well-known
        /// default value (or the default padded with 'x'). Either form is publicly known
        /// and must be rejected at startup.
        /// </summary>
        public bool IsDefaultOrWeakKey()
        {
            // Backing field == default (never set via config)
            if (_securiteKey == WellKnownDefaultKey) return true;

            // Key was explicitly set to the well-known default, which pads it with 'x':
            // "wtmwtmwtmwtmwtmwtm" + "xxxxxxxxxxxxxx" (14 x's) = 32 chars
            var paddedDefault = WellKnownDefaultKey.PadRight(32, 'x');
            if (_securiteKey == paddedDefault) return true;

            return false;
        }
    }
}
