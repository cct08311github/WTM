#nullable enable
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Opt-in fail-fast config validation for critical WTM options.
    /// Call <see cref="AddWtmConfigValidation"/> in Program.cs after
    /// <c>AddWtmContext()</c> to have <see cref="OptionsValidationException"/>
    /// thrown at host startup when required settings are missing or invalid.
    /// Not calling this method preserves the existing behaviour (no validation).
    /// Introduced by Issue #422.
    /// </summary>
    /// <remarks>
    /// Usage:
    /// <code>
    /// builder.Services.AddWtmContext(builder.Configuration);
    /// builder.Services.AddWtmConfigValidation();   // opt-in
    /// var app = builder.Build();                    // throws here if config is invalid
    /// </code>
    /// </remarks>
    public static class WtmConfigValidationExtension
    {
        /// <summary>
        /// Returns <c>true</c> when the user has configured at least one JWT property
        /// away from its factory default, indicating that JWT authentication is in use.
        /// When all three properties are still at their shipped defaults the app is not
        /// using JWT and the JWT validators are skipped to avoid false-positives.
        /// </summary>
        /// <remarks>
        /// Issue #923: deliberately uses <see cref="JwtOption.IsFactoryDefaultKey"/>, NOT
        /// <see cref="JwtOption.IsWeakSigningKey"/>. This predicate only answers "has the
        /// operator touched SecurityKey at all" — widening it to the weak-key check would
        /// make a demo key like <c>"super"</c> (which is non-default, so activation should
        /// be considered true) evaluate to <c>IsJwtActive == false</c> whenever Issuer/
        /// Audience are still localhost, which would SKIP the SecurityKey validator below —
        /// the exact case it exists to catch. See
        /// <c>WtmConfigValidationTests.AddWtmConfigValidation_demo_security_key_super_with_localhost_issuer_audience_throws_on_start</c>.
        /// </remarks>
        private static bool IsJwtActive(JwtOption jwt) =>
            !jwt.IsFactoryDefaultKey()          // SecurityKey was changed to something non-default
            || jwt.Issuer   != "http://localhost"
            || jwt.Audience != "http://localhost";

        /// <summary>
        /// Registers <see cref="ValidateOnStart"/> validators for the critical
        /// WTM option classes (<see cref="Configs"/>, JWT via <see cref="JwtOption"/>
        /// nested inside <see cref="Configs.JwtOptions"/>).
        /// The host will throw <see cref="OptionsValidationException"/> during
        /// <c>IHost.StartAsync()</c> if any rule fails.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> to configure.</param>
        /// <returns>The same <paramref name="services"/> for chaining.</returns>
        public static IServiceCollection AddWtmConfigValidation(
            this IServiceCollection services)
        {
            // ── Configs (connection strings + JWT coherence) ──────────────────────
            // JWT is validated via the nested Configs.JwtOptions property so that
            // the bound IOptions<Configs> (populated by AddWtmContext) is used, not
            // a standalone IOptions<JwtOption> that would always resolve to CLR defaults.
            services
                .AddOptions<Configs>()
                .Validate(
                    configs =>
                        configs.Connections != null &&
                        configs.Connections.Any(c =>
                            c.Enabled &&
                            !string.IsNullOrWhiteSpace(c.Value)),
                    "WTM configuration error: at least one enabled, non-empty connection string " +
                    "must be present in the 'Connections' section of appsettings.json.")
                .Validate(c =>
                {
                    var jwt = c.JwtOptions;
                    if (!IsJwtActive(jwt)) return true;  // JWT not in use; skip
                    // Issue #923: the actual security invariant — too short, unset, or a
                    // publicly known demo/placeholder key — not just "is it the CLR default".
                    return !jwt.IsWeakSigningKey(out _);
                }, "WTM JWT configuration error: 'JwtOptions.SecurityKey' is unset, too short, or a publicly known placeholder value shipped with WTM. Set a strong, unique key (>= 32 bytes) in appsettings.json. Generate one with: openssl rand -base64 32")
                .Validate(c =>
                {
                    var jwt = c.JwtOptions;
                    if (!IsJwtActive(jwt)) return true;
                    return !string.IsNullOrWhiteSpace(jwt.Issuer);
                }, "WTM JWT configuration error: 'JwtOptions.Issuer' must not be empty.")
                .Validate(c =>
                {
                    var jwt = c.JwtOptions;
                    if (!IsJwtActive(jwt)) return true;
                    return !string.IsNullOrWhiteSpace(jwt.Audience);
                }, "WTM JWT configuration error: 'JwtOptions.Audience' must not be empty.")
                .ValidateOnStart();

            return services;
        }
    }
}
