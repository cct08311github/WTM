using System;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc.Tests.Fixtures;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Mvc.Tests.Security
{
    /// <summary>
    /// Tests that issued JWT access tokens contain correct claims, are properly
    /// signed, and respect configured expiration boundaries.
    ///
    /// Covers issue #232 requirements:
    /// - Validate signature errors (tampered tokens fail validation)
    /// - Validate permission claims boundaries (itcode, tenant, jti present)
    /// - Validate expired tokens (expiration matches config)
    /// </summary>
    [TestClass]
    public class JwtClaimsValidationTests
    {
        private readonly TokenTestFixture _fixture = new();

        // #931 item 2: was a second hardcoded copy of the SAME literal TokenTestFixture used
        // to hardcode ("WTM_Test_Key_AtLeast_32_Characters!!") — now reads the fixture's own
        // randomly-generated key instead, since this file needs to build TokenValidationParameters
        // against tokens THAT fixture's TokenService actually signs.
        private string TestSecurityKey => _fixture.GeneratedSecurityKey;
        private const string TestIssuer = "WTM_Test";
        private const string TestAudience = "WTM_Test";

        // ─── Claims Content ─────────────────────────────────────────────────────

        [TestMethod]
        public async Task IssuedToken_ContainsItCodeClaim()
        {
            var user = CreateUser("alice");
            var token = await _fixture.TokenService.IssueTokenAsync(user);

            var jwt = ParseJwt(token.AccessToken!);
            jwt.Claims.Should().Contain(c => c.Type == "itcode" && c.Value == "alice");
        }

        [TestMethod]
        public async Task IssuedToken_ContainsJtiClaim()
        {
            var user = CreateUser("bob");
            var token = await _fixture.TokenService.IssueTokenAsync(user);

            var jwt = ParseJwt(token.AccessToken!);
            jwt.Claims.Should().Contain(c => c.Type == JwtRegisteredClaimNames.Jti);
            var jti = jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
            jti.Should().NotBeNullOrEmpty();
        }

        [TestMethod]
        public async Task IssuedToken_JtiIsUniquePerIssuance()
        {
            var user = CreateUser("charlie");
            var token1 = await _fixture.TokenService.IssueTokenAsync(user);
            var token2 = await _fixture.TokenService.IssueTokenAsync(user);

            var jti1 = ParseJwt(token1.AccessToken!).Claims.First(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
            var jti2 = ParseJwt(token2.AccessToken!).Claims.First(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
            jti1.Should().NotBe(jti2, "each issuance must have a unique JTI to prevent replay");
        }

        [TestMethod]
        public async Task IssuedToken_WithTenant_ContainsTenantClaim()
        {
            var user = CreateUser("dave", tenantCode: "tenant_a");
            var token = await _fixture.TokenService.IssueTokenAsync(user);

            var jwt = ParseJwt(token.AccessToken!);
            jwt.Claims.Should().Contain(c => c.Type == "tenant" && c.Value == "tenant_a");
        }

        [TestMethod]
        public async Task IssuedToken_WithoutTenant_OmitsTenantClaim()
        {
            var user = CreateUser("eve");
            var token = await _fixture.TokenService.IssueTokenAsync(user);

            var jwt = ParseJwt(token.AccessToken!);
            jwt.Claims.Should().NotContain(c => c.Type == "tenant");
        }

        // ─── Expiration Boundaries ──────────────────────────────────────────────

        [TestMethod]
        public async Task IssuedToken_ExpirationMatchesConfiguredLifetime()
        {
            var before = DateTime.UtcNow;
            var user = CreateUser("frank");
            var token = await _fixture.TokenService.IssueTokenAsync(user);
            var after = DateTime.UtcNow;

            var jwt = ParseJwt(token.AccessToken!);
            jwt.ValidTo.Should().BeAfter(before.AddSeconds(3599));
            jwt.ValidTo.Should().BeBefore(after.AddSeconds(3601));
        }

        [TestMethod]
        public async Task IssuedToken_ExpiresInMatchesConfiguredSeconds()
        {
            var user = CreateUser("grace");
            var token = await _fixture.TokenService.IssueTokenAsync(user);

            token.ExpiresIn.Should().Be(3600);
            token.TokenType.Should().Be("Bearer");
        }

        // ─── Signature Validation ───────────────────────────────────────────────

        [TestMethod]
        public async Task IssuedToken_ValidatesWithCorrectKey()
        {
            var user = CreateUser("heidi");
            var token = await _fixture.TokenService.IssueTokenAsync(user);

            var validationParams = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = TestIssuer,
                ValidateAudience = true,
                ValidAudience = TestAudience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSecurityKey)),
                ValidateLifetime = true
            };

            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token.AccessToken, validationParams, out var validatedToken);

            principal.Should().NotBeNull();
            validatedToken.Should().NotBeNull();
        }

        [TestMethod]
        public async Task IssuedToken_FailsValidationWithWrongKey()
        {
            var user = CreateUser("ivan");
            var token = await _fixture.TokenService.IssueTokenAsync(user);

            var validationParams = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = TestIssuer,
                ValidateAudience = true,
                ValidAudience = TestAudience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes("WRONG_KEY_THAT_IS_32_CHARS_LONG!!")),
                ValidateLifetime = true
            };

            var handler = new JwtSecurityTokenHandler();
            var act = () => handler.ValidateToken(token.AccessToken, validationParams, out _);
            act.Should().Throw<SecurityTokenSignatureKeyNotFoundException>();
        }

        [TestMethod]
        public async Task IssuedToken_FailsValidationWithWrongIssuer()
        {
            var user = CreateUser("judy");
            var token = await _fixture.TokenService.IssueTokenAsync(user);

            var validationParams = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "Wrong_Issuer",
                ValidateAudience = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSecurityKey)),
                ValidateLifetime = false
            };

            var handler = new JwtSecurityTokenHandler();
            var act = () => handler.ValidateToken(token.AccessToken, validationParams, out _);
            act.Should().Throw<SecurityTokenInvalidIssuerException>();
        }

        [TestMethod]
        public async Task IssuedToken_FailsValidationWithWrongAudience()
        {
            var user = CreateUser("karl");
            var token = await _fixture.TokenService.IssueTokenAsync(user);

            var validationParams = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = true,
                ValidAudience = "Wrong_Audience",
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSecurityKey)),
                ValidateLifetime = false
            };

            var handler = new JwtSecurityTokenHandler();
            var act = () => handler.ValidateToken(token.AccessToken, validationParams, out _);
            act.Should().Throw<SecurityTokenInvalidAudienceException>();
        }

        [TestMethod]
        public async Task TamperedToken_FailsSignatureValidation()
        {
            var user = CreateUser("lisa");
            var token = await _fixture.TokenService.IssueTokenAsync(user);

            // Tamper with the payload by flipping a character
            var parts = token.AccessToken!.Split('.');
            var tamperedPayload = parts[1][..^1] + (parts[1][^1] == 'A' ? 'B' : 'A');
            var tamperedToken = $"{parts[0]}.{tamperedPayload}.{parts[2]}";

            var validationParams = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSecurityKey)),
                ValidateLifetime = false
            };

            var handler = new JwtSecurityTokenHandler();
            var act = () => handler.ValidateToken(tamperedToken, validationParams, out _);
            // Tampered payload may cause Base64 decode failure (ArgumentException)
            // or signature mismatch (SecurityTokenException) — either indicates rejection.
            act.Should().Throw<Exception>()
                .Which.Should().Match<Exception>(e =>
                    e is SecurityTokenException || e is ArgumentException);
        }

        // ─── Token Structure ────────────────────────────────────────────────────

        [TestMethod]
        public async Task IssuedToken_HasBearerTypeAndRefreshToken()
        {
            var user = CreateUser("mike");
            var token = await _fixture.TokenService.IssueTokenAsync(user);

            token.AccessToken.Should().NotBeNullOrEmpty();
            token.RefreshToken.Should().NotBeNullOrEmpty();
            token.TokenType.Should().Be("Bearer");
            token.ExpiresIn.Should().BeGreaterThan(0);
        }

        [TestMethod]
        public async Task IssuedToken_AccessTokenIsThreePartJwt()
        {
            var user = CreateUser("nina");
            var token = await _fixture.TokenService.IssueTokenAsync(user);

            var parts = token.AccessToken!.Split('.');
            parts.Should().HaveCount(3, "JWT should have header.payload.signature");
        }

        // ─── Helpers ────────────────────────────────────────────────────────────

        private static LoginUserInfo CreateUser(string itCode, string? tenantCode = null)
        {
            return new LoginUserInfo
            {
                ITCode = itCode,
                TenantCode = tenantCode,
                Name = $"Test_{itCode}"
            };
        }

        private static JwtSecurityToken ParseJwt(string token)
        {
            var handler = new JwtSecurityTokenHandler();
            return handler.ReadJwtToken(token);
        }

        [TestCleanup]
        public void Cleanup() => _fixture?.Dispose();
    }
}
