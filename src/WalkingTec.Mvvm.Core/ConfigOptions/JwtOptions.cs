using System;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using WalkingTec.Mvvm.Core.Auth;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>Result of comparing the JWT signing key in use at startup with a reloaded configuration.</summary>
    public enum JwtKeyReloadChange
    {
        /// <summary>The signing key is unchanged.</summary>
        None,
        /// <summary>The signing key changed to a value that passes the key check.</summary>
        ChangedKey,
        /// <summary>The signing key changed to an insecure value.</summary>
        ChangedInsecureKey
    }

    public class JwtOption
    {
        public string Issuer { get; set; } = "http://localhost";
        public string Audience { get; set; } = "http://localhost";
        public int Expires { get; set; } = 3600;

        /// <summary>
        /// Explicit opt-out of the startup key check (config: JwtOptions:AllowInsecureKey, default false).
        /// When true, an insecure signing key no longer aborts startup outside Development; a Critical
        /// message is logged on every start instead. Intended only as a transitional measure for systems
        /// that cannot replace the key immediately.
        /// </summary>
        public bool AllowInsecureKey { get; set; }

        /// <summary>
        /// Explicit opt-out of the required expiry claim for bearer tokens (config: JwtOptions:AllowTokensWithoutExpiry,
        /// default false). By default a bearer token without an "exp" claim is rejected. When true, tokens that carry no
        /// expiry are accepted again (tokens that carry an expiry are still rejected once it has passed). Intended only as
        /// a transitional measure for clients that still present tokens issued without an expiry; a Warning is logged on
        /// every start while it is enabled. Tokens issued by the built-in TokenService always carry an expiry and are not
        /// affected (custom ITokenService implementations may not). The setting is read at application startup; changing
        /// it requires an application restart. Besides the expiry requirement, bearer validation now also enforces HS256
        /// only and the "nbf" (not before) claim; this option does not relax either of them.
        /// </summary>
        public bool AllowTokensWithoutExpiry { get; set; }
        private const int MinKeyLength = 32;
        private const int MinDistinctChars = 5;

        // Values published in this repository (framework default and demo appsettings samples, incl. the demo placeholder).
        // Anyone can look them up, so they must never be used to sign tokens outside Development.
        private static readonly string[] KnownPublicKeys = new[]
        {
            "wtmwtmwtmwtmwtmwtm",
            "superSecretKey@345",
            "CHANGE-ME-development-only-placeholder-key"
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
        /// Guard used at application startup. Outside Development an insecure key aborts startup unless
        /// <see cref="AllowInsecureKey"/> is true, in which case a Critical message is logged instead;
        /// in Development only a warning is logged. The key value is never written to any message.
        /// Note: the early call in AddWtmContext has no logger, so the Critical message is emitted by the
        /// later call in UseWtmContext; an application that never calls UseWtmContext gets no log line.
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
            if (AllowInsecureKey)
            {
                logger?.LogCritical("The JwtOptions.SecurityKey startup check was explicitly bypassed via JwtOptions:AllowInsecureKey. The signing key is missing, shorter than 32 characters, too repetitive, or a publicly known sample value, so anyone who knows or guesses it can forge valid tokens. Supply a unique random key of at least 32 characters (for example via the JwtOptions__SecurityKey environment variable or a secret manager) and remove JwtOptions:AllowInsecureKey.");
                return;
            }
            throw new InvalidOperationException("JwtOptions.SecurityKey is missing, shorter than 32 characters, too repetitive, or a publicly known sample value, so the application refuses to start outside the Development environment. Supply a unique random key of at least 32 characters, for example via the JwtOptions__SecurityKey environment variable or a secret manager. If the key cannot be replaced immediately, JwtOptions:AllowInsecureKey=true starts the application anyway (not recommended).");
        }

        /// <summary>
        /// Decides what a configuration reload means for the JWT signing key. Detection only: nothing is
        /// thrown and no key in use is changed. Keys are compared in memory and never written to any message.
        /// </summary>
        public static JwtKeyReloadChange DescribeReloadChange(JwtOption startup, JwtOption current)
        {
            if (startup == null || current == null)
            {
                return JwtKeyReloadChange.None;
            }
            if (string.Equals(startup.SecurityKey, current.SecurityKey, StringComparison.Ordinal))
            {
                return JwtKeyReloadChange.None;
            }
            return current.IsInsecureKey() ? JwtKeyReloadChange.ChangedInsecureKey : JwtKeyReloadChange.ChangedKey;
        }

        /// <summary>
        /// Logs the outcome of <see cref="DescribeReloadChange"/>: a Warning when the key changed (a restart is required
        /// for token issuance and validation to agree), and a Critical message (Warning in Development) when the changed
        /// key is also insecure. The key value is never written to any message.
        /// </summary>
        public static void LogReloadChange(ILogger logger, JwtOption startup, JwtOption current, bool isDevelopment)
        {
            if (logger == null)
            {
                return;
            }
            switch (DescribeReloadChange(startup, current))
            {
                case JwtKeyReloadChange.ChangedKey:
                    logger.LogWarning("JwtOptions.SecurityKey changed after startup. A signing key change requires an application restart to take effect consistently; until then token issuance and token validation may disagree.");
                    break;
                case JwtKeyReloadChange.ChangedInsecureKey:
                    const string insecure = "JwtOptions.SecurityKey changed after startup to a value that is missing, shorter than 32 characters, too repetitive, or a publicly known sample value. A signing key change requires an application restart to take effect consistently (token issuance and validation may disagree until then), and the startup key check and JwtOptions:AllowInsecureKey are not re-applied at runtime. Supply a unique random key of at least 32 characters.";
                    if (isDevelopment)
                    {
                        logger.LogWarning(insecure);
                    }
                    else
                    {
                        logger.LogCritical(insecure);
                    }
                    break;
            }
        }
        public string LoginPath { get; set; }

        /// <summary>
        /// Build the parameters used by JwtBearer authentication: the same rules as <see cref="CreateValidationParameters"/>
        /// (signature, HS256, issuer, audience, required expiry, 30 seconds of clock skew), except that the expiry claim
        /// is optional when <see cref="AllowTokensWithoutExpiry"/> is true. A token that has an expiry is always checked
        /// against it, in both modes.
        /// </summary>
        public TokenValidationParameters CreateBearerValidationParameters()
        {
            var parameters = CreateValidationParameters();
            parameters.RequireExpirationTime = AllowTokensWithoutExpiry == false;
            return parameters;
        }

        /// <summary>
        /// Logs a Warning (never throws) when <see cref="AllowTokensWithoutExpiry"/> is enabled.
        /// </summary>
        public void LogExpiryPolicy(ILogger logger)
        {
            if (AllowTokensWithoutExpiry)
            {
                logger?.LogWarning("JwtOptions:AllowTokensWithoutExpiry is enabled: bearer tokens without an expiry claim are accepted and never expire. This is a transitional setting; make clients use tokens that carry an expiry and remove the option.");
            }
        }

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
