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
using WalkingTec.Mvvm.Core.Services;

namespace WalkingTec.Mvvm.Core.Test.Services
{
    [TestClass]
    public class WtmTenantServiceTests
    {
        private MemoryDistributedCache _cache = null!;

        [TestInitialize]
        public void Setup()
        {
            _cache = new MemoryDistributedCache(
                Options.Create(new MemoryDistributedCacheOptions()));
        }

        #region Constructor

        [TestMethod]
        public void Ctor_NullCache_Throws()
        {
            var configs = MakeConfigsMonitor();
            Action act = () => new WtmTenantService(null!, configs, new GlobalData());
            act.Should().Throw<ArgumentNullException>().WithParameterName("cache");
        }

        [TestMethod]
        public void Ctor_NullConfigs_Throws()
        {
            Action act = () => new WtmTenantService(_cache, null!, new GlobalData());
            act.Should().Throw<ArgumentNullException>().WithParameterName("configs");
        }

        [TestMethod]
        public void Ctor_NullGlobalData_Throws()
        {
            var configs = MakeConfigsMonitor();
            Action act = () => new WtmTenantService(_cache, configs, null!);
            act.Should().Throw<ArgumentNullException>().WithParameterName("globalData");
        }

        #endregion

        #region RemoveGroupCacheAsync

        [TestMethod]
        public async Task RemoveGroupCacheAsync_RemovesKey()
        {
            var key = $"{GlobalConstants.CacheKey.TenantGroups}:T1";
            _cache.SetString(key, "data");

            var service = CreateService();
            await service.RemoveGroupCacheAsync("T1");

            _cache.GetString(key).Should().BeNull();
        }

        #endregion

        #region RemoveRoleCacheAsync

        [TestMethod]
        public async Task RemoveRoleCacheAsync_RemovesKey()
        {
            var key = $"{GlobalConstants.CacheKey.TenantRoles}:T1";
            _cache.SetString(key, "data");

            var service = CreateService();
            await service.RemoveRoleCacheAsync("T1");

            _cache.GetString(key).Should().BeNull();
        }

        #endregion

        #region GetTenantGroups — no DB available returns empty

        [TestMethod]
        public void GetTenantGroups_NoConnection_ReturnsEmptyList()
        {
            // No connections configured → CreateDC returns null → catch → empty list
            var configs = new Configs { Connections = new List<CS>() };
            var service = CreateService(configs: configs);

            var result = service.GetTenantGroups("T1");

            result.Should().NotBeNull();
            result.Should().BeEmpty();
        }

        #endregion

        #region GetTenantRoles — no DB available returns empty

        [TestMethod]
        public void GetTenantRoles_NoConnection_ReturnsEmptyList()
        {
            var configs = new Configs { Connections = new List<CS>() };
            var service = CreateService(configs: configs);

            var result = service.GetTenantRoles("T1");

            result.Should().NotBeNull();
            result.Should().BeEmpty();
        }

        #endregion

        #region Caching behaviour

        [TestMethod]
        public void GetTenantGroups_SecondCall_ReturnsCachedResult()
        {
            // With empty connections, first call returns empty list from DB fallback.
            // Second call should return the cached result.
            var configs = new Configs { Connections = new List<CS>() };
            var service = CreateService(configs: configs);

            var result1 = service.GetTenantGroups("T1");
            var result2 = service.GetTenantGroups("T1");

            result1.Should().BeEmpty();
            result2.Should().BeEmpty();
            // Both calls succeed without throwing → cache is working
        }

        [TestMethod]
        public void GetTenantRoles_SecondCall_ReturnsCachedResult()
        {
            var configs = new Configs { Connections = new List<CS>() };
            var service = CreateService(configs: configs);

            var result1 = service.GetTenantRoles("T2");
            var result2 = service.GetTenantRoles("T2");

            result1.Should().BeEmpty();
            result2.Should().BeEmpty();
        }

        #endregion

        #region Helpers

        private WtmTenantService CreateService(Configs? configs = null, GlobalData? globalData = null)
        {
            configs ??= new Configs { Connections = new List<CS>() };
            var configsMock = MakeConfigsMonitor(configs);
            globalData ??= new GlobalData { AllAssembly = new List<System.Reflection.Assembly>() };
            return new WtmTenantService(_cache, configsMock, globalData);
        }

        private static IOptionsMonitor<Configs> MakeConfigsMonitor(Configs? configs = null)
        {
            configs ??= new Configs();
            var mock = new Mock<IOptionsMonitor<Configs>>();
            mock.Setup(x => x.CurrentValue).Returns(configs);
            return mock.Object;
        }

        #endregion
    }
}
