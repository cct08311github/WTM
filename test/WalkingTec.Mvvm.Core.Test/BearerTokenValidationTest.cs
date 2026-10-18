using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Mvc.Auth;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class BearerTokenValidationTest
    {
        private const string TestKey = "bearer-token-test-key-0123456789-abcdef-0123456789-abcdef-0123456789-xyz";
        private const string OtherKey = "another-test-key-that-is-not-the-same-0123456789-abcdef-0123456789";
        private const string TestIssuer = "http://issuer.test";
        private const string TestAudience = "http://audience.test";

        private class CapturingLogger : ILogger
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = new List<(LogLevel, string)>();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }

        private static Dictionary<string, string> JwtSettings(bool allowTokensWithoutExpiry)
        {
            var settings = new Dictionary<string, string>
            {
                ["JwtOptions:Issuer"] = TestIssuer,
                ["JwtOptions:Audience"] = TestAudience,
                ["JwtOptions:SecurityKey"] = TestKey
            };
            if (allowTokensWithoutExpiry)
            {
                settings["JwtOptions:AllowTokensWithoutExpiry"] = "true";
            }
            return settings;
        }

        // Builds the options the real JwtBearer handler uses, through AddWtmAuthentication.
        private static JwtBearerOptions BuildBearerOptions(bool allowTokensWithoutExpiry = false)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(JwtSettings(allowTokensWithoutExpiry)).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWtmAuthentication(config);
            using var provider = services.BuildServiceProvider();
            return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        }

        private static string CreateToken(
            string key = TestKey,
            string algorithm = SecurityAlgorithms.HmacSha256,
            string issuer = TestIssuer,
            string audience = TestAudience,
            DateTime? expires = null,
            bool withExpiration = true,
            DateTime? notBefore = null)
        {
            var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), algorithm);
            var claims = new List<Claim>
            {
                new Claim(AuthConstants.JwtClaimTypes.Subject, "admin"),
                new Claim(AuthConstants.JwtClaimTypes.Name, "Admin User"),
                new Claim(AuthConstants.JwtClaimTypes.Role, "manager")
            };
            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                notBefore: notBefore,
                expires: withExpiration ? (expires ?? DateTime.UtcNow.AddMinutes(30)) : (DateTime?)null,
                signingCredentials: credentials);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private static bool IsValid(string token, TokenValidationParameters parameters, out ClaimsPrincipal principal)
        {
            principal = null;
            try
            {
                principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token, parameters, out _);
                return true;
            }
            catch (SecurityTokenException)
            {
                return false;
            }
        }

        private static bool IsValid(string token, TokenValidationParameters parameters)
        {
            return IsValid(token, parameters, out _);
        }

        // JwtBearer in ASP.NET Core 8+ validates with JsonWebTokenHandler by default, so check that path too.
        private static async Task<bool> IsValidWithJsonWebTokenHandlerAsync(string token, TokenValidationParameters parameters)
        {
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
            return result.IsValid;
        }

        [TestMethod]
        public void AllowTokensWithoutExpiryDefaultsToFalse()
        {
            Assert.IsFalse(new JwtOption().AllowTokensWithoutExpiry);
            Assert.IsTrue(BuildBearerOptions().TokenValidationParameters.RequireExpirationTime);
        }

        [TestMethod]
        public void JwtBearerEventsArePreserved()
        {
            foreach (var allow in new[] { false, true })
            {
                var events = BuildBearerOptions(allow).Events;
                Assert.IsNotNull(events, $"allow={allow}");
                Assert.IsNotNull(events.OnMessageReceived, $"allow={allow}");
                Assert.IsNotNull(events.OnTokenValidated, $"allow={allow}");
            }
        }

        [TestMethod]
        public async Task TokenIssuedByTokenServiceIsAccepted()
        {
            var configs = new Configs { JwtOptions = new JwtOption { Issuer = TestIssuer, Audience = TestAudience, SecurityKey = TestKey } };
            var monitor = new Mock<IOptionsMonitor<Configs>>();
            monitor.Setup(m => m.CurrentValue).Returns(configs);
            var issued = await new TokenService(monitor.Object).IssueTokenAsync(new LoginUserInfo { ITCode = "admin" });

            var parameters = BuildBearerOptions().TokenValidationParameters;
            Assert.IsTrue(IsValid(issued.AccessToken, parameters, out var principal));
            Assert.AreEqual("admin", principal.FindFirst(AuthConstants.JwtClaimTypes.Subject)?.Value);
            Assert.IsTrue(await IsValidWithJsonWebTokenHandlerAsync(issued.AccessToken, parameters));
        }

        [TestMethod]
        public async Task TokenWithoutExpiryIsRejectedByDefault()
        {
            var parameters = BuildBearerOptions().TokenValidationParameters;
            var token = CreateToken(withExpiration: false);
            Assert.IsFalse(IsValid(token, parameters));
            Assert.IsFalse(await IsValidWithJsonWebTokenHandlerAsync(token, parameters));
        }

        [TestMethod]
        public async Task TokenWithoutExpiryIsAcceptedWhenExplicitlyAllowed()
        {
            var parameters = BuildBearerOptions(allowTokensWithoutExpiry: true).TokenValidationParameters;
            var token = CreateToken(withExpiration: false);
            Assert.IsTrue(IsValid(token, parameters));
            Assert.IsTrue(await IsValidWithJsonWebTokenHandlerAsync(token, parameters));
            // a token that does carry an expiry keeps working in this mode
            Assert.IsTrue(IsValid(CreateToken(), parameters));
        }

        [TestMethod]
        public async Task ExpiredTokenIsRejectedInBothModes()
        {
            var expired = CreateToken(expires: DateTime.UtcNow.AddMinutes(-10), notBefore: DateTime.UtcNow.AddMinutes(-20));
            foreach (var allow in new[] { false, true })
            {
                var parameters = BuildBearerOptions(allow).TokenValidationParameters;
                Assert.IsFalse(IsValid(expired, parameters), $"allow={allow}");
                Assert.IsFalse(await IsValidWithJsonWebTokenHandlerAsync(expired, parameters), $"allow={allow}");
            }
        }

        [TestMethod]
        public async Task ExpiryJustPastIsRejectedBeyondClockSkewInBothModes()
        {
            // 30s clock skew: 10s past is tolerated, 2 minutes past is not
            var withinSkew = CreateToken(expires: DateTime.UtcNow.AddSeconds(-10), notBefore: DateTime.UtcNow.AddMinutes(-5));
            var beyondSkew = CreateToken(expires: DateTime.UtcNow.AddMinutes(-2), notBefore: DateTime.UtcNow.AddMinutes(-5));
            foreach (var allow in new[] { false, true })
            {
                var parameters = BuildBearerOptions(allow).TokenValidationParameters;
                Assert.AreEqual(TimeSpan.FromSeconds(30), parameters.ClockSkew);
                Assert.IsTrue(IsValid(withinSkew, parameters), $"allow={allow}");
                Assert.IsTrue(await IsValidWithJsonWebTokenHandlerAsync(withinSkew, parameters), $"allow={allow}");
                Assert.IsFalse(IsValid(beyondSkew, parameters), $"allow={allow}");
                Assert.IsFalse(await IsValidWithJsonWebTokenHandlerAsync(beyondSkew, parameters), $"allow={allow}");
            }
        }

        [TestMethod]
        public async Task FutureNotBeforeIsRejectedInBothModes()
        {
            var token = CreateToken(notBefore: DateTime.UtcNow.AddMinutes(10), expires: DateTime.UtcNow.AddMinutes(30));
            foreach (var allow in new[] { false, true })
            {
                var parameters = BuildBearerOptions(allow).TokenValidationParameters;
                Assert.IsFalse(IsValid(token, parameters), $"allow={allow}");
                Assert.IsFalse(await IsValidWithJsonWebTokenHandlerAsync(token, parameters), $"allow={allow}");
            }
        }

        [TestMethod]
        public async Task WrongKeyIssuerAudienceAndAlgorithmAreRejectedInBothModes()
        {
            var bad = new Dictionary<string, string>
            {
                ["wrong key"] = CreateToken(key: OtherKey),
                ["wrong issuer"] = CreateToken(issuer: "http://other-issuer.test"),
                ["wrong audience"] = CreateToken(audience: "http://other-audience.test"),
                ["HS512"] = CreateToken(algorithm: SecurityAlgorithms.HmacSha512),
                ["HS384"] = CreateToken(algorithm: SecurityAlgorithms.HmacSha384)
            };
            foreach (var allow in new[] { false, true })
            {
                var parameters = BuildBearerOptions(allow).TokenValidationParameters;
                foreach (var item in bad)
                {
                    Assert.IsFalse(IsValid(item.Value, parameters), $"{item.Key}, allow={allow}");
                    Assert.IsFalse(await IsValidWithJsonWebTokenHandlerAsync(item.Value, parameters), $"{item.Key}, allow={allow}");
                }
            }
        }

        [TestMethod]
        public async Task UnsignedTokenIsRejectedInBothModes()
        {
            var unsigned = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuer: TestIssuer,
                audience: TestAudience,
                claims: new[] { new Claim(AuthConstants.JwtClaimTypes.Subject, "admin") },
                expires: DateTime.UtcNow.AddMinutes(30)));
            foreach (var allow in new[] { false, true })
            {
                var parameters = BuildBearerOptions(allow).TokenValidationParameters;
                Assert.IsFalse(IsValid(unsigned, parameters), $"allow={allow}");
                Assert.IsFalse(await IsValidWithJsonWebTokenHandlerAsync(unsigned, parameters), $"allow={allow}");
            }
        }

        [TestMethod]
        public void NameAndRoleClaimTypesArePreserved()
        {
            foreach (var allow in new[] { false, true })
            {
                var parameters = BuildBearerOptions(allow).TokenValidationParameters;
                Assert.AreEqual(AuthConstants.JwtClaimTypes.Name, parameters.NameClaimType);
                Assert.AreEqual(AuthConstants.JwtClaimTypes.Role, parameters.RoleClaimType);

                // claim types are not remapped by the handler, as before
                Assert.IsTrue(IsValid(CreateToken(), parameters, out var principal));
                var identity = (ClaimsIdentity)principal.Identity;
                Assert.AreEqual("Admin User", identity.Name);
                Assert.IsTrue(principal.IsInRole("manager"));
            }
        }

        [TestMethod]
        public void BearerParametersMatchSharedParametersExceptExpiryRequirement()
        {
            var strictOption = new JwtOption { Issuer = TestIssuer, Audience = TestAudience, SecurityKey = TestKey };
            var shared = strictOption.CreateValidationParameters();
            var bearer = strictOption.CreateBearerValidationParameters();
            Assert.IsTrue(bearer.RequireExpirationTime);
            Assert.AreEqual(shared.ClockSkew, bearer.ClockSkew);
            Assert.AreEqual(shared.RequireSignedTokens, bearer.RequireSignedTokens);
            CollectionAssert.AreEqual(shared.ValidAlgorithms.ToList(), bearer.ValidAlgorithms.ToList());
            CollectionAssert.AreEqual(new[] { SecurityAlgorithms.HmacSha256 }, bearer.ValidAlgorithms.ToList());

            var loose = new JwtOption { Issuer = TestIssuer, Audience = TestAudience, SecurityKey = TestKey, AllowTokensWithoutExpiry = true };
            Assert.IsFalse(loose.CreateBearerValidationParameters().RequireExpirationTime);
            // the shared parameters used by the remote token path are not affected by the option
            Assert.IsTrue(loose.CreateValidationParameters().RequireExpirationTime);
        }

        [TestMethod]
        public void StartupWarningIsLoggedOnlyWhenAllowed()
        {
            var off = new CapturingLogger();
            new JwtOption().LogExpiryPolicy(off);
            Assert.AreEqual(0, off.Entries.Count);

            var on = new CapturingLogger();
            var option = new JwtOption { AllowTokensWithoutExpiry = true, SecurityKey = TestKey };
            option.LogExpiryPolicy(on);
            Assert.AreEqual(1, on.Entries.Count);
            Assert.AreEqual(LogLevel.Warning, on.Entries[0].Level);
            Assert.IsTrue(on.Entries[0].Message.Contains("JwtOptions:AllowTokensWithoutExpiry"));
            Assert.IsFalse(on.Entries[0].Message.Contains(TestKey));

            // never throws without a logger
            option.LogExpiryPolicy(null);
        }
    }
}
