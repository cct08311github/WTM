#nullable enable
using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Services;

namespace WalkingTec.Mvvm.Core.Test.Services
{
    [TestClass]
    public class WtmDataContextFactoryTests
    {
        #region Constructor validation

        [TestMethod]
        public void Ctor_NullConfigs_ThrowsArgumentNullException()
        {
            Action act = () => new WtmDataContextFactory(null!, new GlobalData());
            act.Should().Throw<ArgumentNullException>().WithParameterName("configs");
        }

        [TestMethod]
        public void Ctor_NullGlobalData_ThrowsArgumentNullException()
        {
            var configsMock = new Mock<IOptionsMonitor<Configs>>();
            configsMock.Setup(x => x.CurrentValue).Returns(new Configs());

            Action act = () => new WtmDataContextFactory(configsMock.Object, null!);
            act.Should().Throw<ArgumentNullException>().WithParameterName("globalData");
        }

        #endregion

        #region Default connection string resolution

        [TestMethod]
        public void CreateDC_DefaultCS_UsesDefaultConnection()
        {
            var factory = CreateFactory(configs: MakeConfigs("default", DBTypeEnum.SQLite,
                $"Data Source=file:memdb_{Guid.NewGuid():N}?mode=memory&cache=shared"));

            var dc = factory.CreateDC();

            dc.Should().NotBeNull();
            dc!.CSName.Should().Be("default");
        }

        [TestMethod]
        public void CreateDC_SpecificCsKey_UsesNamedConnection()
        {
            var configs = MakeConfigs("default", DBTypeEnum.SQLite,
                $"Data Source=file:memdb_{Guid.NewGuid():N}?mode=memory&cache=shared");
            configs.Connections.Add(new CS
            {
                Key = "secondary",
                Value = $"Data Source=file:memdb_{Guid.NewGuid():N}?mode=memory&cache=shared",
                DbType = DBTypeEnum.SQLite,
                Enabled = true
            });
            var factory = CreateFactory(configs: configs);

            var dc = factory.CreateDC(cskey: "secondary");

            dc.Should().NotBeNull();
            dc!.CSName.Should().Be("secondary");
        }

        #endregion

        #region Log connection

        [TestMethod]
        public void CreateDC_LogConnection_UsesDefaultLogIfExists()
        {
            var configs = MakeConfigs("default", DBTypeEnum.SQLite,
                $"Data Source=file:memdb_{Guid.NewGuid():N}?mode=memory&cache=shared");
            configs.Connections.Add(new CS
            {
                Key = "defaultlog",
                Value = $"Data Source=file:memdb_{Guid.NewGuid():N}?mode=memory&cache=shared",
                DbType = DBTypeEnum.SQLite,
                Enabled = true
            });
            var factory = CreateFactory(configs: configs);

            var dc = factory.CreateDC(isLog: true);

            dc.Should().NotBeNull();
            dc!.CSName.Should().Be("defaultlog");
        }

        [TestMethod]
        public void CreateDC_LogConnection_FallsBackToDefaultIfNoLogCS()
        {
            var configs = MakeConfigs("default", DBTypeEnum.SQLite,
                $"Data Source=file:memdb_{Guid.NewGuid():N}?mode=memory&cache=shared");
            var factory = CreateFactory(configs: configs);

            var dc = factory.CreateDC(isLog: true);

            dc.Should().NotBeNull();
            dc!.CSName.Should().Be("default");
        }

        #endregion

        #region Disabled connection

        [TestMethod]
        public void CreateDC_DisabledConnection_ThrowsInvalidOperationException()
        {
            var configs = MakeConfigs("default", DBTypeEnum.SQLite,
                $"Data Source=file:memdb_{Guid.NewGuid():N}?mode=memory&cache=shared");
            configs.Connections[0].Enabled = false;
            var factory = CreateFactory(configs: configs);

            Action act = () => factory.CreateDC();

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*disabled*");
        }

        #endregion

        #region User code assignment

        [TestMethod]
        public void CreateDC_SetsCurrentUserCode()
        {
            var factory = CreateFactory(configs: MakeConfigs("default", DBTypeEnum.SQLite,
                $"Data Source=file:memdb_{Guid.NewGuid():N}?mode=memory&cache=shared"));

            var dc = factory.CreateDC(userCode: "testuser");

            dc.Should().NotBeNull();
            dc!.CurrentUserCode.Should().Be("testuser");
        }

        #endregion

        #region Tenant code

        [TestMethod]
        public void CreateDC_TenantWithoutDB_ReturnsDefaultDCWithTenantCode()
        {
            var configs = MakeConfigs("default", DBTypeEnum.SQLite,
                $"Data Source=file:memdb_{Guid.NewGuid():N}?mode=memory&cache=shared");
            var gd = new GlobalData
            {
                AllTenant = new List<FrameworkTenant>
                {
                    new FrameworkTenant { TCode = "T001", TDomain = "t001.example.com" }
                },
                AllAssembly = new List<System.Reflection.Assembly>()
            };
            var factory = CreateFactory(configs: configs, globalData: gd);

            // Tenant doesn't have its own DB (IsUsingDB == false),
            // so a default DC is returned with tenant code set
            var dc = factory.CreateDC(currentTenant: "T001");

            dc.Should().NotBeNull();
            dc!.TenantCode.Should().Be("T001");
        }

        [TestMethod]
        public void CreateDC_DomainBasedTenantResolution()
        {
            var configs = MakeConfigs("default", DBTypeEnum.SQLite,
                $"Data Source=file:memdb_{Guid.NewGuid():N}?mode=memory&cache=shared");
            var gd = new GlobalData
            {
                AllTenant = new List<FrameworkTenant>
                {
                    new FrameworkTenant { TCode = "DOMTENANT", TDomain = "tenant.example.com" }
                },
                AllAssembly = new List<System.Reflection.Assembly>()
            };
            var factory = CreateFactory(configs: configs, globalData: gd);

            var dc = factory.CreateDC(refererDomain: "tenant.example.com");

            dc.Should().NotBeNull();
            dc!.TenantCode.Should().Be("DOMTENANT");
        }

        #endregion

        #region TimeProvider

        [TestMethod]
        public void CreateDC_SetsTimeProvider_OnEmptyContext()
        {
            var fakeTime = new FakeTimeProvider();
            var factory = CreateFactory(
                configs: MakeConfigs("default", DBTypeEnum.SQLite,
                    $"Data Source=file:memdb_{Guid.NewGuid():N}?mode=memory&cache=shared"),
                timeProvider: fakeTime);

            var dc = factory.CreateDC();

            if (dc is EmptyContext ec)
            {
                ec.TimeProvider.Should().BeSameAs(fakeTime);
            }
        }

        #endregion

        #region Debug flag

        [TestMethod]
        public void CreateDC_QuickDebug_SetsIsDebugOnDC()
        {
            var configs = MakeConfigs("default", DBTypeEnum.SQLite,
                $"Data Source=file:memdb_{Guid.NewGuid():N}?mode=memory&cache=shared");
            configs.IsQuickDebug = true;
            var factory = CreateFactory(configs: configs);

            var dc = factory.CreateDC();

            dc.Should().NotBeNull();
            dc!.IsDebug.Should().BeTrue();
        }

        #endregion

        #region Null connection

        [TestMethod]
        public void CreateDC_NoMatchingCS_ReturnsNull()
        {
            var configs = new Configs { Connections = new List<CS>() };
            var factory = CreateFactory(configs: configs);

            var dc = factory.CreateDC();

            dc.Should().BeNull();
        }

        #endregion

        #region Helpers

        private static WtmDataContextFactory CreateFactory(
            Configs? configs = null,
            GlobalData? globalData = null,
            ILoggerFactory? loggerFactory = null,
            TimeProvider? timeProvider = null)
        {
            configs ??= new Configs();
            var configsMock = new Mock<IOptionsMonitor<Configs>>();
            configsMock.Setup(x => x.CurrentValue).Returns(configs);

            globalData ??= new GlobalData
            {
                AllAssembly = new List<System.Reflection.Assembly>()
            };

            return new WtmDataContextFactory(configsMock.Object, globalData, loggerFactory, timeProvider);
        }

        private static Configs MakeConfigs(string key, DBTypeEnum dbType, string connectionString)
        {
            return new Configs
            {
                Connections = new List<CS>
                {
                    new CS
                    {
                        Key = key,
                        Value = connectionString,
                        DbType = dbType,
                        Enabled = true
                    }
                }
            };
        }

        #endregion
    }
}
