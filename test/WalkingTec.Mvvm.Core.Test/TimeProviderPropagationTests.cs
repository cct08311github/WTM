using System;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class TimeProviderPropagationTests
    {
        private sealed class FixedTimeProvider : TimeProvider
        {
            private readonly DateTimeOffset _utcNow;

            public FixedTimeProvider(DateTimeOffset utcNow)
            {
                _utcNow = utcNow;
            }

            public override DateTimeOffset GetUtcNow() => _utcNow;

            public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        }

        [TestMethod]
        public void Setting_WtmContext_DC_propagates_TimeProvider_to_EmptyContext()
        {
            var fixedProvider = new FixedTimeProvider(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
            var wtm = new WTMContext(null, new GlobalData(), timeProvider: fixedProvider);
            var dc = new EmptyContext("Data Source=file:tp_setter?mode=memory&cache=shared", DBTypeEnum.SQLite);

            wtm.DC = dc;

            Assert.AreSame(fixedProvider, dc.TimeProvider);
            dc.Dispose();
        }

        [TestMethod]
        public void FrameworkTenant_CreateDC_propagates_WtmContext_TimeProvider()
        {
            var fixedProvider = new FixedTimeProvider(new DateTimeOffset(2031, 6, 7, 8, 9, 10, TimeSpan.Zero));
            var config = new Configs();
            var monitor = new Mock<IOptionsMonitor<Configs>>();
            monitor.Setup(x => x.CurrentValue).Returns(config);
            var wtm = new WTMContext(monitor.Object, new GlobalData(), timeProvider: fixedProvider);
            var tenant = new FrameworkTenant
            {
                TCode = "TENANT_A",
                TDb = "Data Source=file:tp_tenant?mode=memory&cache=shared",
                TDbType = DBTypeEnum.SQLite,
                DbContext = "DataContext"
            };

            var dc = tenant.CreateDC(wtm);

            Assert.IsNotNull(dc);
            Assert.IsInstanceOfType(dc, typeof(EmptyContext));
            Assert.AreSame(fixedProvider, ((EmptyContext)dc).TimeProvider);
            dc.Dispose();
        }
    }
}
