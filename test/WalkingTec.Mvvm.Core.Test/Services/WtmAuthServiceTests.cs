#nullable enable
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Test.Services
{
    [TestClass]
    public class WtmAuthServiceTests
    {
        private WtmAuthService _service = null!;

        [TestInitialize]
        public void Setup()
        {
            _service = new WtmAuthService();
        }

        #region VerifyPassword

        [TestMethod]
        public void VerifyPassword_CorrectPassword_ReturnsSuccess()
        {
            var hash = PasswordHashHelper.HashPassword("test123");
            var result = _service.VerifyPassword(hash, "test123");
            result.Should().NotBe(PasswordVerifyResult.Failed);
        }

        [TestMethod]
        public void VerifyPassword_WrongPassword_ReturnsFailed()
        {
            var hash = PasswordHashHelper.HashPassword("test123");
            var result = _service.VerifyPassword(hash, "wrongpassword");
            result.Should().Be(PasswordVerifyResult.Failed);
        }

        [TestMethod]
        public void VerifyPassword_NullHash_ReturnsFailed()
        {
            var result = _service.VerifyPassword(null, "test");
            result.Should().Be(PasswordVerifyResult.Failed);
        }

        #endregion

        #region HashPassword

        [TestMethod]
        public void HashPassword_ReturnsNonEmptyString()
        {
            var hash = _service.HashPassword("mypassword");
            hash.Should().NotBeNullOrEmpty();
        }

        [TestMethod]
        public void HashPassword_DifferentCallsProduceDifferentHashes()
        {
            var hash1 = _service.HashPassword("same");
            var hash2 = _service.HashPassword("same");
            // PBKDF2 with random salt produces different hashes
            hash1.Should().NotBe(hash2);
        }

        #endregion

        #region AuthenticateViaRemoteHostAsync

        [TestMethod]
        public async Task AuthenticateViaRemoteHost_WithToken_CallsCheckUserInfo()
        {
            var apiMock = new Mock<IWtmApiClient>();
            apiMock.Setup(a => a.CallAPI<LoginUserInfo>(
                    "mainhost",
                    It.Is<string>(u => u.Contains("checkuserinfo")),
                    HttpMethodEnum.GET,
                    It.IsAny<object?>(),
                    It.IsAny<int?>(), It.IsAny<string?>(),
                    It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<string?>()))
                .ReturnsAsync(new ApiResult<LoginUserInfo>
                {
                    Data = new LoginUserInfo { ITCode = "remoteuser" }
                });

            var result = await _service.AuthenticateViaRemoteHostAsync(
                apiMock.Object, "existing-token", null, null);

            result.Should().NotBeNull();
            result!.ITCode.Should().Be("remoteuser");
            result.RemoteToken.Should().Be("existing-token");
        }

        [TestMethod]
        public async Task AuthenticateViaRemoteHost_WithPassword_LoginsThenChecks()
        {
            var apiMock = new Mock<IWtmApiClient>();
            // First call: login
            apiMock.Setup(a => a.CallAPI<Token>(
                    "mainhost",
                    It.Is<string>(u => u.Contains("loginjwt")),
                    HttpMethodEnum.POST,
                    It.IsAny<object?>(),
                    It.IsAny<int?>(), It.IsAny<string?>(),
                    It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<string?>()))
                .ReturnsAsync(new ApiResult<Token>
                {
                    Data = new Token { AccessToken = "new-access-token" }
                });
            // Second call: check user info
            apiMock.Setup(a => a.CallAPI<LoginUserInfo>(
                    "mainhost",
                    It.Is<string>(u => u.Contains("checkuserinfo")),
                    HttpMethodEnum.GET,
                    It.IsAny<object?>(),
                    It.IsAny<int?>(), It.IsAny<string?>(),
                    It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<string?>()))
                .ReturnsAsync(new ApiResult<LoginUserInfo>
                {
                    Data = new LoginUserInfo { ITCode = "loginuser" }
                });

            var result = await _service.AuthenticateViaRemoteHostAsync(
                apiMock.Object, null, "admin", "pass123");

            result.Should().NotBeNull();
            result!.ITCode.Should().Be("loginuser");
            result.RemoteToken.Should().Be("new-access-token");
        }

        [TestMethod]
        public async Task AuthenticateViaRemoteHost_NoTokenNoPassword_ReturnsNull()
        {
            var apiMock = new Mock<IWtmApiClient>();

            var result = await _service.AuthenticateViaRemoteHostAsync(
                apiMock.Object, null, null, null);

            result.Should().BeNull();
        }

        [TestMethod]
        public async Task AuthenticateViaRemoteHost_LoginFails_ReturnsNull()
        {
            var apiMock = new Mock<IWtmApiClient>();
            apiMock.Setup(a => a.CallAPI<Token>(
                    "mainhost", It.IsAny<string>(), HttpMethodEnum.POST,
                    It.IsAny<object?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                    It.IsAny<Dictionary<string, string>?>(), It.IsAny<string?>()))
                .ReturnsAsync(new ApiResult<Token> { Data = new Token { AccessToken = null } });

            var result = await _service.AuthenticateViaRemoteHostAsync(
                apiMock.Object, null, "admin", "wrongpass");

            result.Should().BeNull();
        }

        #endregion

        #region RefreshTokenAsync (obsolete 4-arg overload — #721: must always reject)

        [TestMethod]
        public async Task RefreshTokenAsync_ObsoleteOverload_NullUser_ReturnsNull()
        {
            var tokenMock = new Mock<ITokenService>();
#pragma warning disable CS0618 // intentionally exercising the deprecated overload
            var result = await _service.RefreshTokenAsync(null, tokenMock.Object, null, false);
#pragma warning restore CS0618
            result.Should().BeNull();
        }

        [TestMethod]
        public async Task RefreshTokenAsync_ObsoleteOverload_ValidUser_StillRejectsAndNeverReissuesFromIdentity()
        {
            // SECURITY (#721) regression: the obsolete overload used to reissue a fresh
            // token pair purely from LoginUserInfo identity, with zero refresh-token
            // validation. It must now unconditionally reject — proving the bypass is gone
            // even when called with a fully-populated, plausible-looking user.
            var tokenMock = new Mock<ITokenService>();
            tokenMock.Setup(t => t.IssueTokenAsync(It.IsAny<LoginUserInfo>(), It.IsAny<string?>()))
                .ReturnsAsync(new Token { AccessToken = "should-never-be-issued" });

            var user = new LoginUserInfo
            {
                ITCode = "user1",
                TenantCode = "T1",
                RemoteToken = "old-token"
            };

#pragma warning disable CS0618 // intentionally exercising the deprecated overload
            var result = await _service.RefreshTokenAsync(user, tokenMock.Object, null, false);
#pragma warning restore CS0618

            result.Should().BeNull();
            tokenMock.Verify(t => t.IssueTokenAsync(It.IsAny<LoginUserInfo>(), It.IsAny<string?>()), Times.Never);
        }

        [TestMethod]
        public async Task RefreshTokenAsync_ObsoleteOverload_MainHostUser_StillRejectsAndNeverForwardsEmptyBody()
        {
            var apiMock = new Mock<IWtmApiClient>();
            var tokenMock = new Mock<ITokenService>();

            var user = new LoginUserInfo { ITCode = "user1", CurrentTenant = null };

#pragma warning disable CS0618 // intentionally exercising the deprecated overload
            var result = await _service.RefreshTokenAsync(
                user, tokenMock.Object, apiMock.Object, hasMainHost: true);
#pragma warning restore CS0618

            result.Should().BeNull();
            apiMock.Verify(a => a.CallAPI<Token>(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<HttpMethodEnum>(),
                It.IsAny<object?>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<string?>()), Times.Never);
        }

        #endregion

        #region RefreshTokenAsync (new secure overload — validates the presented token, #721)

        [TestMethod]
        public async Task RefreshTokenAsync_NullOrEmptyToken_ReturnsNull()
        {
            var tokenMock = new Mock<ITokenService>();

            var result = await _service.RefreshTokenAsync(null, null, tokenMock.Object, null, false);
            result.Should().BeNull();

            var result2 = await _service.RefreshTokenAsync(string.Empty, null, tokenMock.Object, null, false);
            result2.Should().BeNull();

            tokenMock.Verify(t => t.RefreshTokenAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        }

        [TestMethod]
        public async Task RefreshTokenAsync_LocalHost_ValidatesPresentedTokenViaTokenService()
        {
            var tokenMock = new Mock<ITokenService>();
            tokenMock.Setup(t => t.RefreshTokenAsync("presented-refresh-token", It.IsAny<string?>()))
                .ReturnsAsync(new Token { AccessToken = "rotated-access-token", RefreshToken = "rotated-refresh-token" });

            var user = new LoginUserInfo { ITCode = "user1", TenantCode = "T1" };

            var result = await _service.RefreshTokenAsync(
                "presented-refresh-token", user, tokenMock.Object, null, hasMainHost: false);

            result.Should().NotBeNull();
            result!.AccessToken.Should().Be("rotated-access-token");
            tokenMock.Verify(t => t.RefreshTokenAsync("presented-refresh-token", It.IsAny<string?>()), Times.Once);
            tokenMock.Verify(t => t.IssueTokenAsync(It.IsAny<LoginUserInfo>(), It.IsAny<string?>()), Times.Never);
        }

        [TestMethod]
        public async Task RefreshTokenAsync_LocalHost_BogusToken_TokenServiceRejects_ReturnsNull()
        {
            // Simulates ITokenService.RefreshTokenAsync's real behavior for a
            // never-issued/expired/already-rotated token: it returns null. The bogus
            // token must never fall back to identity-based reissue.
            var tokenMock = new Mock<ITokenService>();
            tokenMock.Setup(t => t.RefreshTokenAsync("bogus-token", It.IsAny<string?>()))
                .ReturnsAsync((Token?)null);

            var user = new LoginUserInfo { ITCode = "user1" };

            var result = await _service.RefreshTokenAsync(
                "bogus-token", user, tokenMock.Object, null, hasMainHost: false);

            result.Should().BeNull();
            tokenMock.Verify(t => t.IssueTokenAsync(It.IsAny<LoginUserInfo>(), It.IsAny<string?>()), Times.Never);
        }

        [TestMethod]
        public async Task RefreshTokenAsync_Federation_ForwardsRealPresentedTokenToHardenedMainhostEndpoint()
        {
            var apiMock = new Mock<IWtmApiClient>();
            apiMock.Setup(a => a.CallAPI<Token>(
                    "mainhost",
                    "/api/_account/refreshtoken",
                    HttpMethodEnum.POST,
                    It.Is<object?>(o => o != null && (string)o.GetType().GetProperty("RefreshToken")!.GetValue(o)! == "presented-refresh-token"),
                    It.IsAny<int?>(), It.IsAny<string?>(),
                    It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<string?>()))
                .ReturnsAsync(new ApiResult<Token>
                {
                    Data = new Token { AccessToken = "remote-rotated" }
                });

            var tokenMock = new Mock<ITokenService>();
            var user = new LoginUserInfo { ITCode = "user1", CurrentTenant = null };

            var result = await _service.RefreshTokenAsync(
                "presented-refresh-token", user, tokenMock.Object, apiMock.Object, hasMainHost: true);

            result.Should().NotBeNull();
            result!.AccessToken.Should().Be("remote-rotated");
            apiMock.VerifyAll();
            // Never a locally-issued, identity-based token — validation happens on the mainhost.
            tokenMock.Verify(t => t.IssueTokenAsync(It.IsAny<LoginUserInfo>(), It.IsAny<string?>()), Times.Never);
        }

        [TestMethod]
        public async Task RefreshTokenAsync_Federation_NoApiClient_ReturnsNull()
        {
            var tokenMock = new Mock<ITokenService>();
            var user = new LoginUserInfo { ITCode = "user1", CurrentTenant = null };

            var result = await _service.RefreshTokenAsync(
                "presented-refresh-token", user, tokenMock.Object, null, hasMainHost: true);

            result.Should().BeNull();
        }

        #endregion
    }
}
