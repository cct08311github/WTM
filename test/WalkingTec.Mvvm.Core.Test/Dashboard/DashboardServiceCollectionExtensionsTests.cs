using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Dashboard;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    [TestClass]
    public class DashboardServiceCollectionExtensionsTests
    {
        [TestMethod]
        public void AddWtmDashboard_registers_IDashboardService()
        {
            var services = new ServiceCollection();
            services.AddWtmDashboard();
            var provider = services.BuildServiceProvider();
            var svc = provider.GetService<IDashboardService>();
            Assert.IsNotNull(svc);
        }
    }
}