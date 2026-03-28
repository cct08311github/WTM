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

        #region RefreshTokenAsync

        [TestMethod]
        public async Task RefreshTokenAsync_NullUser_ReturnsNull()
        {
            var tokenMock = new Mock<ITokenService>();
            var result = await _service.RefreshTokenAsync(null, tokenMock.Object, null, false);
            result.Should().BeNull();
        }

        [TestMethod]
        public async Task RefreshTokenAsync_LocalUser_IssuesNewToken()
        {
            var tokenMock = new Mock<ITokenService>();
            tokenMock.Setup(t => t.IssueTokenAsync(It.IsAny<LoginUserInfo>(), It.IsAny<string?>()))
                .ReturnsAsync(new Token { AccessToken = "new-token" });

            var user = new LoginUserInfo
            {
                ITCode = "user1",
                TenantCode = "T1",
                RemoteToken = "old-token"
            };

            var result = await _service.RefreshTokenAsync(user, tokenMock.Object, null, false);

            result.Should().NotBeNull();
            result!.AccessToken.Should().Be("new-token");
            tokenMock.Verify(t => t.IssueTokenAsync(
                It.Is<LoginUserInfo>(l => l.ITCode == "user1" && l.RemoteToken == "old-token"),
                It.IsAny<string?>()), Times.Once);
        }

        [TestMethod]
        public async Task RefreshTokenAsync_MainHost_CallsRemoteRefresh()
        {
            var apiMock = new Mock<IWtmApiClient>();
            apiMock.Setup(a => a.CallAPI<Token>(
                    "mainhost",
                    It.Is<string>(u => u.Contains("RefreshToken")),
                    HttpMethodEnum.POST,
                    It.IsAny<object?>(),
                    It.IsAny<int?>(), It.IsAny<string?>(),
                    It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<string?>()))
                .ReturnsAsync(new ApiResult<Token>
                {
                    Data = new Token { AccessToken = "remote-refreshed" }
                });

            var tokenMock = new Mock<ITokenService>();
            tokenMock.Setup(t => t.IssueTokenAsync(It.IsAny<LoginUserInfo>(), It.IsAny<string?>()))
                .ReturnsAsync(new Token { AccessToken = "local-issued" });

            var user = new LoginUserInfo
            {
                ITCode = "user1",
                CurrentTenant = null // host user
            };

            var result = await _service.RefreshTokenAsync(
                user, tokenMock.Object, apiMock.Object, hasMainHost: true);

            result.Should().NotBeNull();
            result!.AccessToken.Should().Be("local-issued");
            tokenMock.Verify(t => t.IssueTokenAsync(
                It.Is<LoginUserInfo>(l => l.RemoteToken == "remote-refreshed"),
                It.IsAny<string?>()), Times.Once);
        }

        #endregion
    }
}
