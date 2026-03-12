using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Mvc.Auth;

namespace WalkingTec.Mvvm.Core.Test
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public class TokenServiceTests
    {
        private Mock<IOptionsMonitor<Configs>> _configsMock;
        private Mock<IServiceProvider> _spMock;
        private JwtOption _jwtOption;
        private TokenService _tokenService;

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestInitialize]
        public void Setup()
        {
            _configsMock = new Mock<IOptionsMonitor<Configs>>();
            _spMock = new Mock<IServiceProvider>();
            
            _jwtOption = new JwtOption
            {
                Issuer = "test_issuer",
                Audience = "test_audience",
                Expires = 3600,
                SecurityKey = "wtm_very_secret_key_1234567890123" // 32+ chars
            };

            var configs = new Configs { JwtOptions = _jwtOption };
            _configsMock.Setup(x => x.CurrentValue).Returns(configs);

            // Mock service scope for CreateRefreshTokenAsync
            var scopeMock = new Mock<IServiceScope>();
            var scopeFactoryMock = new Mock<IServiceScopeFactory>();
            scopeFactoryMock.Setup(x => x.CreateScope()).Returns(scopeMock.Object);
            _spMock.Setup(x => x.GetService(typeof(IServiceScopeFactory))).Returns(scopeFactoryMock.Object);
            scopeMock.Setup(x => x.ServiceProvider).Returns(_spMock.Object);
            
            _tokenService = new TokenService(_configsMock.Object, _spMock.Object);
        }

        private TokenValidationParameters GetValidationParameters()
        {
            return new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _jwtOption.Issuer,
                ValidateAudience = true,
                ValidAudience = _jwtOption.Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOption.SecurityKey)),
                ClockSkew = TimeSpan.Zero
            };
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public async Task ValidateToken_Expired_ShouldThrowException()
        {
            // Arrange
            _jwtOption.Expires = -1; // Expires immediately
            var user = new LoginUserInfo { ITCode = "test_user" };

            // Act
            var token = await _tokenService.IssueTokenAsync(user);

            // Assert
            var handler = new JwtSecurityTokenHandler();
            var act = () => handler.ValidateToken(token.AccessToken, GetValidationParameters(), out _);
            act.Should().Throw<SecurityTokenExpiredException>();
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public async Task ValidateToken_SignatureError_ShouldThrowException()
        {
            // Arrange
            var user = new LoginUserInfo { ITCode = "test_user" };
            var token = await _tokenService.IssueTokenAsync(user);

            // Tamper with token signature
            var parts = token.AccessToken.Split('.');
            parts[2] = parts[2] == "tampered" ? "different" : "tampered"; // make it invalid
            var tamperedToken = string.Join(".", parts);

            // Act & Assert
            var handler = new JwtSecurityTokenHandler();
            var act = () => handler.ValidateToken(tamperedToken, GetValidationParameters(), out _);
            act.Should().Throw<SecurityTokenInvalidSignatureException>();
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public async Task PermissionClaimsBoundaries_MissingITCode_ShouldThrowArgumentException()
        {
            // Arrange
            var user = new LoginUserInfo { ITCode = null };

            // Act
            var act = () => _tokenService.IssueTokenAsync(user);

            // Assert
            await act.Should().ThrowAsync<ArgumentException>().WithMessage("*ITCode*");
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public async Task PermissionClaimsBoundaries_IncludesExpectedClaims()
        {
            // Arrange
            var user = new LoginUserInfo 
            { 
                ITCode = "test_user",
                Name = "Test Name",
                TenantCode = "tenant1",
                RemoteToken = "rt1"
            };

            // Act
            var token = await _tokenService.IssueTokenAsync(user);

            // Assert
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token.AccessToken, GetValidationParameters(), out _);
            
            principal.HasClaim(c => c.Type == AuthConstants.JwtClaimTypes.Subject && c.Value == "test_user").Should().BeTrue();
            principal.HasClaim(c => c.Type == AuthConstants.JwtClaimTypes.TenantCode && c.Value == "tenant1").Should().BeTrue();
            principal.HasClaim(c => c.Type == AuthConstants.JwtClaimTypes.RToken && c.Value == "rt1").Should().BeTrue();
            
            // Check Name claim
            principal.HasClaim(c => c.Type == AuthConstants.JwtClaimTypes.Name && c.Value == "Test Name").Should().BeTrue();
        }
    }
}