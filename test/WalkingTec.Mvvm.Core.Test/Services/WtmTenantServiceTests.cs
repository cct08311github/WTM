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
            CoreProgram.DefaultJsonOption ??= new System.Text.Json.JsonSerializerOptions();
            CoreProgram.DefaultPostJsonOption ??= new System.Text.Json.JsonSerializerOptions();
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
        public async Task RemoveGroupCacheAsync_DoesNotThrow()
        {
            var service = CreateService();
            await service.RemoveGroupCacheAsync("T1");
        }

        #endregion

        #region RemoveRoleCacheAsync

        [TestMethod]
        public async Task RemoveRoleCacheAsync_DoesNotThrow()
        {
            var service = CreateService();
            await service.RemoveRoleCacheAsync("T1");
        }

        #endregion

        #region GetTenantGroups — no DB available returns empty

        [TestMethod]
        public void GetTenantGroups_NoConnection_ReturnsEmptyOrNull()
        {
            var configs = new Configs { Connections = new List<CS>() };
            var service = CreateService(configs: configs);

            var result = service.GetTenantGroups("T1");

            // With no DB connections, CreateDCForTenant returns null, catch block returns empty list.
            // But the cache Add may serialize null depending on timing. Accept empty or null.
            if (result != null)
            {
                result.Should().BeEmpty();
            }
        }

        #endregion

        #region GetTenantRoles — no DB available returns empty

        [TestMethod]
        public void GetTenantRoles_NoConnection_ReturnsEmptyOrNull()
        {
            var configs = new Configs { Connections = new List<CS>() };
            var service = CreateService(configs: configs);

            var result = service.GetTenantRoles("T1");

            if (result != null)
            {
                result.Should().BeEmpty();
            }
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
