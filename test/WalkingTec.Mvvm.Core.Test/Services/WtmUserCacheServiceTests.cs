#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
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
            CoreProgram.DefaultJsonOption ??= new System.Text.Json.JsonSerializerOptions();
            CoreProgram.DefaultPostJsonOption ??= new System.Text.Json.JsonSerializerOptions();
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
        public async Task RemoveUserCacheAsync_DoesNotThrow()
        {
            await _service.RemoveUserCacheAsync("T1", "user1");
            // No exception = pass. Cache key with InstanceName prefix is removed internally.
        }

        [TestMethod]
        public async Task RemoveUserCacheAsync_MultipleUsers_DoesNotThrow()
        {
            await _service.RemoveUserCacheAsync("T1", "u1", "u2");
        }

        [TestMethod]
        public async Task RemoveUserCacheAsync_NullTenant_DoesNotThrow()
        {
            await _service.RemoveUserCacheAsync(null, "user1");
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

            await _service.RemoveUserCacheByRoleAsync(
                currentTenant: null, hasMainHost: true,
                dc: null, apiClient: apiMock.Object, "admin");

            apiMock.Verify(a => a.CallAPI<List<string>>(
                "mainhost", It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<string?>()), Times.Once);
        }

        [TestMethod]
        public async Task RemoveUserCacheByRoleAsync_NoMainHost_NoApiClient_DoesNotThrow()
        {
            await _service.RemoveUserCacheByRoleAsync(
                currentTenant: "T1", hasMainHost: false,
                dc: null, apiClient: null, "admin");
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

            await _service.RemoveUserCacheByGroupAsync(
                currentTenant: null, hasMainHost: true,
                dc: null, apiClient: apiMock.Object, "devgroup");

            apiMock.Verify(a => a.CallAPI<List<string>>(
                "mainhost", It.IsAny<string>(),
                It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<string?>()), Times.Once);
        }

        #endregion
    }
}
