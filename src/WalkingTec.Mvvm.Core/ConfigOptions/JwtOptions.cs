using System;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using WalkingTec.Mvvm.Core.Auth;

namespace WalkingTec.Mvvm.Core
{
    public class JwtOption
    {
        public string Issuer { get; set; } = "http://localhost";
        public string Audience { get; set; } = "http://localhost";
        public int Expires { get; set; } = 3600;
        private const int MinKeyLength = 32;
        private const int MinDistinctChars = 5;

        // Values published in this repository (framework default and demo appsettings samples).
        // Anyone can look them up, so they must never be used to sign tokens outside Development.
        private static readonly string[] KnownPublicKeys = new[]
        {
            "wtmwtmwtmwtmwtmwtm",
            "superSecretKey@345"
        };

        private const string DefaultKey = "wtmwtmwtmwtmwtmwtm";

        private string _securiteKey = DefaultKey;
        private bool _keyAssigned;
        private bool _wasPadded;

        public string SecurityKey { get
            {
                return _securiteKey;
            }
            set {
                _keyAssigned = true;
                _securiteKey = value ?? string.Empty;
                _wasPadded = _securiteKey.Length < MinKeyLength;
                if (_wasPadded)
                {
                    var count = MinKeyLength - _securiteKey.Length;
                    for (int i = 0; i < count; i++)
                    {
                        _securiteKey += "x";
                    }
                }

            }
        }

        /// <summary>
        /// Returns true when the signing key is not safe for production use: it was never configured
        /// (framework default), it was empty or shorter than 32 characters before padding, it has
        /// fewer than 5 distinct characters, or it equals a value that is published in this repository
        /// (compared case-insensitively, optionally followed by any number of 'x' padding characters).
        /// </summary>
        public bool IsInsecureKey()
        {
            if (_keyAssigned == false || _wasPadded)
            {
                return true;
            }
            var key = (_securiteKey ?? string.Empty).Trim();
            if (key.Length == 0 || key.Distinct().Count() < MinDistinctChars)
            {
                return true;
            }
            foreach (var known in KnownPublicKeys)
            {
                if (key.StartsWith(known, StringComparison.OrdinalIgnoreCase)
                    && key.Substring(known.Length).All(c => c == 'x' || c == 'X'))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Guard used at application startup. Outside Development an insecure key aborts startup;
        /// in Development only a warning is logged. The key value is never written to any message.
        /// </summary>
        public void EnsureSecureKey(bool isDevelopment, ILogger logger)
        {
            if (IsInsecureKey() == false)
            {
                return;
            }
            if (isDevelopment)
            {
                logger?.LogWarning("JwtOptions.SecurityKey is missing, shorter than 32 characters, too repetitive, or a publicly known sample value. This is tolerated in Development only; supply a unique random key of at least 32 characters (for example via the JwtOptions__SecurityKey environment variable or a secret manager) before deploying.");
                return;
            }
            throw new InvalidOperationException("JwtOptions.SecurityKey is missing, shorter than 32 characters, too repetitive, or a publicly known sample value, so the application refuses to start outside the Development environment. Supply a unique random key of at least 32 characters, for example via the JwtOptions__SecurityKey environment variable or a secret manager.");
        }
        public string LoginPath { get; set; }

        /// <summary>
        /// Build the parameters used to validate tokens issued with these options
        /// (signature, algorithm, issuer, audience and lifetime are all required).
        /// </summary>
        public TokenValidationParameters CreateValidationParameters()
        {
            return new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = Issuer,
                ValidateAudience = true,
                ValidAudience = Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecurityKey)),
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = AuthConstants.JwtClaimTypes.Name,
                RoleClaimType = AuthConstants.JwtClaimTypes.Role
            };
        }
    }
}
