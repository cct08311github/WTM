#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Services;

namespace WalkingTec.Mvvm.Core.Test.Services
{
    [TestClass]
    public class WtmUserCacheServiceTests
    {
        private MemoryDistributedCache _cache = null!;
        private WtmUserCacheService _service = null!;

        [TestInitialize]
        public void Setup()
        {
            _cache = new MemoryDistributedCache(
                Options.Create(new MemoryDistributedCacheOptions()));
            _service = new WtmUserCacheService(_cache);
        }

        #region Constructor

        [TestMethod]
        public void Ctor_NullCache_Throws()
        {
            Action act = () => new WtmUserCacheService(null!);
            act.Should().Throw<ArgumentNullException>();
        }

        #endregion

        #region RemoveUserCacheAsync

        [TestMethod]
        public async Task RemoveUserCacheAsync_RemovesMatchingKeys()
        {
            var key = $"{GlobalConstants.CacheKey.UserInfo}:user1$`$T1";
            _cache.SetString(key, "data");

            await _service.RemoveUserCacheAsync("T1", "user1");

            var result = _cache.GetString(key);
            result.Should().BeNull();
        }

        [TestMethod]
        public async Task RemoveUserCacheAsync_MultipleUsers()
        {
            var key1 = $"{GlobalConstants.CacheKey.UserInfo}:u1$`$T1";
            var key2 = $"{GlobalConstants.CacheKey.UserInfo}:u2$`$T1";
            _cache.SetString(key1, "d1");
            _cache.SetString(key2, "d2");

            await _service.RemoveUserCacheAsync("T1", "u1", "u2");

            _cache.GetString(key1).Should().BeNull();
            _cache.GetString(key2).Should().BeNull();
        }

        [TestMethod]
        public async Task RemoveUserCacheAsync_NullTenant()
        {
            var key = $"{GlobalConstants.CacheKey.UserInfo}:user1$`$";
            _cache.SetString(key, "data");

            await _service.RemoveUserCacheAsync(null, "user1");

            _cache.GetString(key).Should().BeNull();
        }

        #endregion

        #region RemoveUserCacheByRoleAsync

        [TestMethod]
        public async Task RemoveUserCacheByRoleAsync_MainHost_CallsApi()
        {
            var apiMock = new Mock<IWtmApiClient>();
            apiMock.Setup(a => a.CallAPI<List<string>>(
                    "mainhost",
                    It.Is<string>(u => u.Contains("GetUserByRole")),
                    It.IsAny<int?>(), It.IsAny<string?>(),
                    It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<string?>()))
                .ReturnsAsync(new ApiResult<List<string>> { Data = new List<string> { "user1" } });

            var key = $"{GlobalConstants.CacheKey.UserInfo}:user1$`$";
            _cache.SetString(key, "cached");

            await _service.RemoveUserCacheByRoleAsync(
                currentTenant: null, hasMainHost: true,
                dc: null, apiClient: apiMock.Object, "admin");

            _cache.GetString(key).Should().BeNull();
            apiMock.Verify(a => a.CallAPI<List<string>>(
                "mainhost", It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<string?>()), Times.Once);
        }

        [TestMethod]
        public async Task RemoveUserCacheByRoleAsync_NoMainHost_NoApiClient_DoesNotThrow()
        {
            // With tenant set, should query DC, but DC is null — should just skip
            await _service.RemoveUserCacheByRoleAsync(
                currentTenant: "T1", hasMainHost: false,
                dc: null, apiClient: null, "admin");

            // No exception = pass
        }

        #endregion

        #region RemoveUserCacheByGroupAsync

        [TestMethod]
        public async Task RemoveUserCacheByGroupAsync_MainHost_CallsApi()
        {
            var apiMock = new Mock<IWtmApiClient>();
            apiMock.Setup(a => a.CallAPI<List<string>>(
                    "mainhost",
                    It.Is<string>(u => u.Contains("GetUserByGroup")),
                    It.IsAny<int?>(), It.IsAny<string?>(),
                    It.IsAny<Dictionary<string, string>?>(),
                    It.IsAny<string?>()))
                .ReturnsAsync(new ApiResult<List<string>> { Data = new List<string> { "user2" } });

            var key = $"{GlobalConstants.CacheKey.UserInfo}:user2$`$";
            _cache.SetString(key, "cached");

            await _service.RemoveUserCacheByGroupAsync(
                currentTenant: null, hasMainHost: true,
                dc: null, apiClient: apiMock.Object, "devgroup");

            _cache.GetString(key).Should().BeNull();
        }

        #endregion
    }
}
