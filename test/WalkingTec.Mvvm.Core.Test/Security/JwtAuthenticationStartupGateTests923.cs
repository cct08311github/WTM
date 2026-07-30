#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// Issue #923: startup-gate behaviour of
    /// <see cref="FrameworkServiceExtension.AddWtmAuthentication"/> across the population
    /// table in the design decision — unset / factory-default / demo key / short custom key
    /// / strong key, crossed with Production-like vs Development, plus the fail-closed
    /// default when neither <see cref="IWebHostEnvironment"/> nor an environment variable is
    /// available.
    /// </summary>
    [TestClass]
    public class JwtAuthenticationStartupGateTests923
    {
        // ─── Helpers ────────────────────────────────────────────────────────────────

        private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        private static readonly Dictionary<string, string?> ValidConnection = new()
        {
            ["Connections:0:Key"] = "default",
            ["Connections:0:Value"] = "Data Source=:memory:",
            ["Connections:0:Enabled"] = "true",
        };

        private static Dictionary<string, string?> ConfigWith(string? securityKey)
        {
            var values = new Dictionary<string, string?>(ValidConnection);
            if (securityKey != null)
            {
                values["JwtOptions:SecurityKey"] = securityKey;
            }
            return values;
        }

        private static IWebHostEnvironment MakeEnv(string environmentName)
        {
            var mock = new Mock<IWebHostEnvironment>();
            mock.Setup(e => e.EnvironmentName).Returns(environmentName);
            return mock.Object;
        }

        /// <summary>
        /// Clears both environment-variable fallbacks so a test can prove the fail-closed
        /// default with NEITHER an <see cref="IWebHostEnvironment"/> nor an environment
        /// variable available. Restores the original values afterwards regardless of
        /// outcome — these are process-wide, so leaking a change here would be flaky for
        /// whatever test runs next in the same process.
        /// </summary>
        private static IDisposable ClearEnvironmentVariables()
        {
            var originalAspnet = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            var originalDotnet = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", null);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", null);
            return new RestoreEnvironmentVariables(originalAspnet, originalDotnet);
        }

        private sealed class RestoreEnvironmentVariables : IDisposable
        {
            private readonly string? _aspnet;
            private readonly string? _dotnet;
            public RestoreEnvironmentVariables(string? aspnet, string? dotnet)
            {
                _aspnet = aspnet;
                _dotnet = dotnet;
            }
            public void Dispose()
            {
                Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", _aspnet);
                Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", _dotnet);
            }
        }

        // ─── (a) unset key ──────────────────────────────────────────────────────────

        [TestMethod]
        public void AddWtmAuthentication_UnsetKey_ProductionLikeEnvironment_Throws()
        {
            var services = new ServiceCollection();
            services.AddSingleton(MakeEnv("Production"));
            var config = BuildConfig(ConfigWith(null));

            Assert.ThrowsException<InvalidOperationException>(
                () => services.AddWtmAuthentication(config));
        }

        [TestMethod]
        public void AddWtmAuthentication_UnsetKey_DevelopmentEnvironment_DoesNotThrow()
        {
            var services = new ServiceCollection();
            services.AddSingleton(MakeEnv("Development"));
            var config = BuildConfig(ConfigWith(null));

            services.AddWtmAuthentication(config); // must not throw
        }

        [TestMethod]
        public void AddWtmAuthentication_UnsetKey_NoEnvironmentInfoAtAll_FailsClosed_Throws()
        {
            using var _ = ClearEnvironmentVariables();
            var services = new ServiceCollection(); // no IWebHostEnvironment registered
            var config = BuildConfig(ConfigWith(null));

            Assert.ThrowsException<InvalidOperationException>(
                () => services.AddWtmAuthentication(config),
                "#923: with no IWebHostEnvironment and no ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT, " +
                "the environment must be treated as non-Development (fail closed), not Development.");
        }

        [TestMethod]
        public void AddWtmAuthentication_UnsetKey_NoWebHostEnvironment_ButAspNetCoreEnvironmentVarIsDevelopment_DoesNotThrow()
        {
            using var _ = ClearEnvironmentVariables();
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            var services = new ServiceCollection(); // no IWebHostEnvironment registered
            var config = BuildConfig(ConfigWith(null));

            services.AddWtmAuthentication(config); // must not throw — env-var fallback path
        }

        // ─── (b) well-known default set explicitly ─────────────────────────────────

        [TestMethod]
        public void AddWtmAuthentication_ExplicitWellKnownDefault_ProductionLikeEnvironment_Throws()
        {
            var services = new ServiceCollection();
            services.AddSingleton(MakeEnv("Production"));
            var config = BuildConfig(ConfigWith(JwtOption.WellKnownDefaultKey));

            Assert.ThrowsException<InvalidOperationException>(
                () => services.AddWtmAuthentication(config));
        }

        // ─── (c) demo key ───────────────────────────────────────────────────────────

        [TestMethod]
        public void AddWtmAuthentication_DemoKeySuper_ProductionLikeEnvironment_Throws()
        {
            // #923's headline fix: this used to boot successfully (the P0 defect).
            var services = new ServiceCollection();
            services.AddSingleton(MakeEnv("Production"));
            var config = BuildConfig(ConfigWith("super"));

            Assert.ThrowsException<InvalidOperationException>(
                () => services.AddWtmAuthentication(config));
        }

        [TestMethod]
        public void AddWtmAuthentication_DemoKeySuperSecretKey345_ProductionLikeEnvironment_Throws()
        {
            var services = new ServiceCollection();
            services.AddSingleton(MakeEnv("Production"));
            var config = BuildConfig(ConfigWith("superSecretKey@345"));

            Assert.ThrowsException<InvalidOperationException>(
                () => services.AddWtmAuthentication(config));
        }

        [TestMethod]
        public void AddWtmAuthentication_DemoKeySuper_DevelopmentEnvironment_DoesNotThrow()
        {
            var services = new ServiceCollection();
            services.AddSingleton(MakeEnv("Development"));
            var config = BuildConfig(ConfigWith("super"));

            services.AddWtmAuthentication(config); // must not throw — replaced with ephemeral key
        }

        // ─── (d) short custom key ───────────────────────────────────────────────────

        [TestMethod]
        public void AddWtmAuthentication_ShortCustomKey_ProductionLikeEnvironment_Throws()
        {
            var services = new ServiceCollection();
            services.AddSingleton(MakeEnv("Production"));
            var config = BuildConfig(ConfigWith("CustomKey12345")); // 14 chars, not a known default

            Assert.ThrowsException<InvalidOperationException>(
                () => services.AddWtmAuthentication(config));
        }

        [TestMethod]
        public void AddWtmAuthentication_ShortCustomKey_DevelopmentEnvironment_AlsoThrows()
        {
            // #923 design: Development is deliberately STRICT here too — an operator who set
            // a short key believed it was supported; that false belief must surface locally,
            // not just in production.
            var services = new ServiceCollection();
            services.AddSingleton(MakeEnv("Development"));
            var config = BuildConfig(ConfigWith("CustomKey12345"));

            Assert.ThrowsException<InvalidOperationException>(
                () => services.AddWtmAuthentication(config));
        }

        // ─── (e) strong key ─────────────────────────────────────────────────────────

        [TestMethod]
        public void AddWtmAuthentication_StrongKey_ProductionLikeEnvironment_DoesNotThrow()
        {
            var services = new ServiceCollection();
            services.AddSingleton(MakeEnv("Production"));
            var config = BuildConfig(ConfigWith(JwtTestKeys.StrongCustomKey));

            services.AddWtmAuthentication(config); // must not throw
        }

        // ─── Wiring: config.Get<Configs>() snapshot vs IOptionsMonitor<Configs> ────────

        /// <summary>
        /// Issue #923: <c>AddWtmAuthentication</c> builds the JwtBearer handler's
        /// <c>IssuerSigningKey</c> from a <c>config.Get&lt;Configs&gt;()</c> snapshot, while
        /// <c>TokenService</c> (and anything else resolving JwtOption via DI) reads
        /// <c>IOptionsMonitor&lt;Configs&gt;.CurrentValue</c> — a SEPARATE, independently
        /// bound object graph. When the Development ephemeral-key carve-out generates a
        /// random key, it must be pushed into BOTH graphs (via <c>services.PostConfigure
        /// &lt;Configs&gt;</c>) or the app signs with one key and validates with another.
        /// This test proves the two graphs agree, byte-for-byte, on the SAME generated key.
        /// </summary>
        [TestMethod]
        public void AddWtmAuthentication_DevelopmentEphemeralKey_IsWiredIntoBothConfigsObjectGraphs()
        {
            var services = new ServiceCollection();
            services.AddSingleton(MakeEnv("Development"));
            var config = BuildConfig(ConfigWith(null)); // unset -> triggers ephemeral-key path

            // Mirrors AddWtmContext's own services.Configure<Configs>(config) binding — the
            // SEPARATE object graph IOptionsMonitor<Configs> resolves from.
            services.Configure<Configs>(config);

            services.AddWtmAuthentication(config);

            using var provider = services.BuildServiceProvider();

            // #931 item 1: compare EffectiveSecurityKey (the actual HMAC signing material both
            // sides use), not SecurityKey (the raw, unpadded value) — the generated ephemeral
            // key is always >= 32 bytes already, so the two happen to be identical for THIS
            // value, but EffectiveSecurityKey is the semantically correct comparison.
            var monitorKey = provider.GetRequiredService<IOptionsMonitor<Configs>>()
                .CurrentValue.JwtOptions.EffectiveSecurityKey;

            var bearerOptions = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(JwtBearerDefaults.AuthenticationScheme);
            var bearerKeyBytes = ((SymmetricSecurityKey)bearerOptions.TokenValidationParameters.IssuerSigningKey).Key;

            Assert.IsFalse(string.IsNullOrEmpty(monitorKey), "IOptionsMonitor<Configs> must have a non-empty ephemeral key");
            CollectionAssert.AreEqual(
                Encoding.UTF8.GetBytes(monitorKey),
                bearerKeyBytes,
                "#923: the JwtBearer handler's IssuerSigningKey and IOptionsMonitor<Configs>'s " +
                "JwtOptions.SecurityKey must be byte-identical — otherwise the app signs tokens " +
                "with one key and validates them with another, rejecting every token it issues.");
        }

        // ─── PostConfigure must not clobber an operator-supplied code-delegate key (review) ──

        /// <summary>
        /// Post-implementation review finding: the PostConfigure delegate registered by the
        /// Development ephemeral-key carve-out must be CONDITIONAL on the merged
        /// <see cref="Configs"/> still being weak by the time it runs — not unconditional.
        /// A host that ALSO registers its own <c>services.Configure&lt;Configs&gt;(o =>
        /// o.JwtOptions.SecurityKey = "...")</c> code delegate (e.g. reading a real key from a
        /// secret manager, with nothing in raw <see cref="IConfiguration"/>) has that value
        /// already applied to the SAME <see cref="Configs"/> instance by the time
        /// PostConfigure runs, because every <c>Configure</c> action runs before every
        /// <c>PostConfigure</c> action regardless of registration order. An unconditional
        /// overwrite would silently discard that real key and replace it with a fresh random
        /// one on every restart, with no diagnostic explaining why every previously issued
        /// token stopped working.
        /// </summary>
        [TestMethod]
        public void AddWtmAuthentication_DevelopmentCodeDelegateStrongKey_IOptionsMonitorResolvesOperatorKey_NotGeneratedKey()
        {
            var services = new ServiceCollection();
            services.AddSingleton(MakeEnv("Development"));
            var config = BuildConfig(ConfigWith(null)); // nothing in raw IConfiguration -> AddWtmAuthentication sees a weak key

            // #931 item 2: was a fixed literal, publicly readable via test/'s mirror sync.
            var operatorKey = JwtTestKeys.StrongCustomKey;

            // Mirrors AddWtmContext's own services.Configure<Configs>(config) binding (does NOT
            // set SecurityKey — absent from `config`) plus a host's OWN code-delegate
            // registration supplying the real key. Registration order relative to
            // AddWtmAuthentication does not matter for Configure-vs-Configure ordering here;
            // what matters is that both run before PostConfigure.
            services.Configure<Configs>(config);
            services.Configure<Configs>(o => o.JwtOptions.SecurityKey = operatorKey);

            services.AddWtmAuthentication(config);

            using var provider = services.BuildServiceProvider();

            var monitorKey = provider.GetRequiredService<IOptionsMonitor<Configs>>()
                .CurrentValue.JwtOptions.SecurityKey;

            Assert.AreEqual(operatorKey, monitorKey,
                "#923 review: PostConfigure must be conditional on IsWeakSigningKey() still being " +
                "true when it runs. An unconditional overwrite here means IOptionsMonitor<Configs> " +
                "resolves to a freshly generated random key instead of the operator's own " +
                "code-delegate-supplied key, silently discarding it on every process start.");
        }

        /// <summary>
        /// Companion to the test above, checked at the SAME time rather than as a separate
        /// assumption: even with the PostConfigure guard now conditional, the JwtBearer
        /// handler's own <c>IssuerSigningKey</c> is built earlier in <c>AddWtmAuthentication</c>
        /// from the LOCAL <c>config.Get&lt;Configs&gt;()</c> snapshot, which — unlike
        /// <c>IOptionsMonitor&lt;Configs&gt;</c> — can never observe a
        /// <c>services.Configure&lt;Configs&gt;</c> code delegate at all. That local snapshot
        /// was already mutated to the generated ephemeral key before PostConfigure ever runs,
        /// and nothing in this fix touches that line. So in exactly this scenario (Development,
        /// no key in raw configuration, real key supplied only via code delegate),
        /// <c>TokenService</c> (via <c>IOptionsMonitor&lt;Configs&gt;</c>, per the test
        /// above) signs with the operator's real key while the JwtBearer handler validates
        /// against the generated one — every login now fails outright, which this test pins
        /// as the CURRENT, KNOWN behaviour rather than leaving it undiscovered. This is the
        /// #753-family split-brain read (AddWtmAuthentication reads config.Get&lt;Configs&gt;(),
        /// never the DI-merged graph); closing it means changing what AddWtmAuthentication
        /// reads from, out of scope for #923's own fix.
        /// </summary>
        [TestMethod]
        public void AddWtmAuthentication_DevelopmentCodeDelegateStrongKey_JwtBearerHandlerStillUsesGeneratedKey_NotOperatorKey()
        {
            var services = new ServiceCollection();
            services.AddSingleton(MakeEnv("Development"));
            var config = BuildConfig(ConfigWith(null));

            // #931 item 2: was a fixed literal, publicly readable via test/'s mirror sync.
            var operatorKey = JwtTestKeys.StrongCustomKey;

            services.Configure<Configs>(config);
            services.Configure<Configs>(o => o.JwtOptions.SecurityKey = operatorKey);

            services.AddWtmAuthentication(config);

            using var provider = services.BuildServiceProvider();

            var monitorKey = provider.GetRequiredService<IOptionsMonitor<Configs>>()
                .CurrentValue.JwtOptions.SecurityKey;
            var bearerOptions = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(JwtBearerDefaults.AuthenticationScheme);
            var bearerKeyBytes = ((SymmetricSecurityKey)bearerOptions.TokenValidationParameters.IssuerSigningKey).Key;

            Assert.AreEqual(operatorKey, monitorKey,
                "sanity check: this test assumes the fix above already holds");
            CollectionAssert.AreNotEqual(
                Encoding.UTF8.GetBytes(operatorKey),
                bearerKeyBytes,
                "#753-family residual gap, deliberately pinned rather than silently present: " +
                "config.Get<Configs>() cannot observe a services.Configure<Configs> code " +
                "delegate, so the JwtBearer handler's IssuerSigningKey is still the generated " +
                "ephemeral key here even though IOptionsMonitor<Configs> now correctly resolves " +
                "the operator's real key — TokenService signs with one key, the handler " +
                "validates with another, and every login in this exact configuration fails. " +
                "If this assertion ever starts failing (i.e. the two keys now agree), that means " +
                "someone changed what AddWtmAuthentication reads from and this gap has likely " +
                "been closed — update this test's intent, don't just delete it.");
        }

        // ─── #931 item 3: PostConfigure must not substitute for a too-short code-delegate key ──

        /// <summary>
        /// #931 item 3: the ephemeral-key PostConfigure delegate must only substitute for
        /// <see cref="JwtOption.WeakReasonUnset"/> / <see cref="JwtOption.WeakReasonKnownPublicKey"/>
        /// — NOT for <see cref="JwtOption.WeakReasonTooShort"/>. Scenario: raw configuration
        /// has no key (the ephemeral branch starts), then a host ALSO registers
        /// <c>services.Configure&lt;Configs&gt;(o => o.JwtOptions.SecurityKey = "short")</c> —
        /// a too-short CUSTOM key, via a second Configure source evaluated before
        /// PostConfigure runs. The design's row (d) says a too-short custom key must be
        /// rejected in EVERY environment, Development included, with no ephemeral fallback;
        /// if PostConfigure silently replaced it with the generated key here, this second
        /// Configure source would launder exactly the case row (d) exists to reject. Asserts
        /// the short key survives PostConfigure unchanged — <see cref="TokenService"/>'s own
        /// constructor guard (item 5, tested separately) is what actually turns this into a
        /// visible failure.
        /// </summary>
        [TestMethod]
        public void AddWtmAuthentication_DevelopmentCodeDelegateShortKey_PostConfigureDoesNotSubstituteEphemeralKey()
        {
            var services = new ServiceCollection();
            services.AddSingleton(MakeEnv("Development"));
            var config = BuildConfig(ConfigWith(null));

            const string shortKey = "tooShort123"; // < 32 bytes, not blocklisted -> WeakReasonTooShort

            services.Configure<Configs>(config);
            services.Configure<Configs>(o => o.JwtOptions.SecurityKey = shortKey);

            services.AddWtmAuthentication(config);

            using var provider = services.BuildServiceProvider();

            var monitorKey = provider.GetRequiredService<IOptionsMonitor<Configs>>()
                .CurrentValue.JwtOptions.SecurityKey;

            Assert.AreEqual(shortKey, monitorKey,
                "#931 item 3: PostConfigure must not substitute the ephemeral key for a " +
                "too-short custom key — design row (d) requires rejection in every " +
                "environment, not a silent ephemeral fallback for a second Configure source.");
        }

        // ─── #931 item 6: the generated ephemeral key must be unpredictable, not just present ──

        /// <summary>
        /// #931 item 6: every OTHER test in this file that touches the Development
        /// ephemeral-key path only asserts that SOME 32+-byte key was generated and wired
        /// consistently — replacing <c>RandomNumberGenerator.GetBytes(32)</c> with
        /// <c>new byte[32]</c> (an all-zero, entirely predictable key) would satisfy every one
        /// of them. This test binds directly to unpredictability: two INDEPENDENT
        /// <c>AddWtmAuthentication</c> calls (separate <see cref="ServiceCollection"/>s, no
        /// shared state) must not produce the same generated key, and neither may be the
        /// all-zero constant.
        /// </summary>
        [TestMethod]
        public void AddWtmAuthentication_DevelopmentEphemeralKey_IsNotConstant_AndDiffersAcrossIndependentCalls()
        {
            string GenerateOnce()
            {
                var services = new ServiceCollection();
                services.AddSingleton(MakeEnv("Development"));
                var config = BuildConfig(ConfigWith(null));
                services.Configure<Configs>(config);
                services.AddWtmAuthentication(config);
                using var provider = services.BuildServiceProvider();
                return provider.GetRequiredService<IOptionsMonitor<Configs>>()
                    .CurrentValue.JwtOptions.SecurityKey;
            }

            var key1 = GenerateOnce();
            var key2 = GenerateOnce();

            Assert.AreNotEqual(key1, key2,
                "#931: two independently generated ephemeral keys must differ — a constant " +
                "signing key (e.g. an all-zero byte array) would make every OTHER test in " +
                "this file that only checks 'some key was generated and wired consistently' " +
                "pass while shipping a predictable key.");

            var allZeroKeyBase64 = Convert.ToBase64String(new byte[32]);
            Assert.AreNotEqual(allZeroKeyBase64, key1, "generated key must not be the all-zero constant");
            Assert.AreNotEqual(allZeroKeyBase64, key2, "generated key must not be the all-zero constant");
        }
    }
}
