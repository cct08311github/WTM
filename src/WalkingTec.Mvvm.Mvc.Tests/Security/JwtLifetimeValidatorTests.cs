using System;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using WalkingTec.Mvvm.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Mvc.Tests.Security
{
    /// <summary>
    /// Unit tests for <see cref="FrameworkServiceExtension.ValidateJwtLifetime"/>.
    ///
    /// Covers Issue #24 requirements:
    /// - Tokens with no <c>exp</c> claim must be rejected (previously accepted forever)
    /// - Expired tokens are rejected
    /// - Valid (not-yet-expired) tokens are accepted
    /// - Pre-<c>nbf</c> tokens (future not-before) are rejected
    /// - Tokens whose <c>nbf</c> is in the past are accepted
    /// </summary>
    [TestClass]
    public class JwtLifetimeValidatorTests
    {
        // Default params — ClockSkew is left at zero here so edge cases are sharp.
        private static readonly TokenValidationParameters DefaultParams =
            new() { ClockSkew = TimeSpan.Zero };

        // ─── No-exp rejection ───────────────────────────────────────────────

        [TestMethod]
        public void LifetimeValidator_RejectsToken_WithNoExp()
        {
            // expires == null must be rejected — previously the old implementation
            // returned true here, effectively making no-exp tokens valid forever.
            var result = FrameworkServiceExtension.ValidateJwtLifetime(
                notBefore: null,
                expires:   null,
                securityToken: null!,
                validationParameters: DefaultParams);

            result.Should().BeFalse("a token without an exp claim must never be accepted");
        }

        // ─── Expired token ──────────────────────────────────────────────────

        [TestMethod]
        public void LifetimeValidator_RejectsToken_AlreadyExpired()
        {
            var result = FrameworkServiceExtension.ValidateJwtLifetime(
                notBefore: null,
                expires:   DateTime.UtcNow.AddSeconds(-60),
                securityToken: null!,
                validationParameters: DefaultParams);

            result.Should().BeFalse("a token expired 60 seconds ago must be rejected");
        }

        // ─── Valid token ────────────────────────────────────────────────────

        [TestMethod]
        public void LifetimeValidator_AcceptsToken_NotYetExpired()
        {
            var result = FrameworkServiceExtension.ValidateJwtLifetime(
                notBefore: null,
                expires:   DateTime.UtcNow.AddMinutes(30),
                securityToken: null!,
                validationParameters: DefaultParams);

            result.Should().BeTrue("a token expiring in 30 minutes must be accepted");
        }

        // ─── Pre-nbf (future not-before) rejection ──────────────────────────

        [TestMethod]
        public void LifetimeValidator_RejectsToken_BeforeNbf()
        {
            // nbf is 60 seconds in the future — token should not be usable yet.
            var result = FrameworkServiceExtension.ValidateJwtLifetime(
                notBefore: DateTime.UtcNow.AddSeconds(60),
                expires:   DateTime.UtcNow.AddHours(1),
                securityToken: null!,
                validationParameters: DefaultParams);

            result.Should().BeFalse("a token whose nbf is in the future must be rejected");
        }

        // ─── Past nbf acceptance ────────────────────────────────────────────

        [TestMethod]
        public void LifetimeValidator_AcceptsToken_NbfInPast()
        {
            var result = FrameworkServiceExtension.ValidateJwtLifetime(
                notBefore: DateTime.UtcNow.AddMinutes(-5),
                expires:   DateTime.UtcNow.AddMinutes(55),
                securityToken: null!,
                validationParameters: DefaultParams);

            result.Should().BeTrue("a token whose nbf is 5 minutes in the past must be accepted");
        }

        // ─── ClockSkew tolerance ────────────────────────────────────────────

        [TestMethod]
        public void LifetimeValidator_RespectsClockSkew_ForExpiry()
        {
            // Token expired 3 seconds ago; with a 5-second skew it should still pass.
            var paramsWithSkew = new TokenValidationParameters
            {
                ClockSkew = TimeSpan.FromSeconds(5)
            };

            var result = FrameworkServiceExtension.ValidateJwtLifetime(
                notBefore: null,
                expires:   DateTime.UtcNow.AddSeconds(-3),
                securityToken: null!,
                validationParameters: paramsWithSkew);

            result.Should().BeTrue("within the 5-second clock-skew window an 'expired' token is still valid");
        }

        [TestMethod]
        public void LifetimeValidator_RejectsToken_ExpiredBeyondClockSkew()
        {
            // Token expired 10 seconds ago; with a 5-second skew it must be rejected.
            var paramsWithSkew = new TokenValidationParameters
            {
                ClockSkew = TimeSpan.FromSeconds(5)
            };

            var result = FrameworkServiceExtension.ValidateJwtLifetime(
                notBefore: null,
                expires:   DateTime.UtcNow.AddSeconds(-10),
                securityToken: null!,
                validationParameters: paramsWithSkew);

            result.Should().BeFalse("10 seconds ago is outside the 5-second skew window");
        }
    }
}
