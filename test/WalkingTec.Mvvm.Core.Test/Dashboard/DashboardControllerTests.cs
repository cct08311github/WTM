#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Dashboard;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    [TestClass]
    public class DashboardControllerTests
    {
        private Mock<IDashboardService> _service = null!;
        private _DashboardController _controller = null!;

        [TestInitialize]
        public void Init()
        {
            _service = new Mock<IDashboardService>();
            var opts = Options.Create(new DashboardOptions());
            _controller = new _DashboardController(_service.Object, opts)
            {
                Wtm = MockWtmContext.CreateWtmContext()
            };
            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };
        }

        private void SetUser(string userId, params string[] roles)
        {
            _controller.Wtm.LoginUserInfo = new LoginUserInfo { ITCode = userId };
            _controller.Wtm.LoginUserInfo.Roles = new List<SimpleRole>();
            foreach (var r in roles)
            {
                _controller.Wtm.LoginUserInfo.Roles.Add(new SimpleRole { RoleCode = r });
            }
        }

        [TestMethod]
        public async Task List_returns_200_with_empty_list()
        {
            SetUser("bob");
            _service.Setup(x => x.ListAsync("bob", It.IsAny<string[]>()))
                .ReturnsAsync(new List<DashboardSummary>());

            var result = await _controller.List() as OkObjectResult;

            result.Should().NotBeNull();
            result!.Value.Should().BeOfType<List<DashboardSummary>>();
            ((List<DashboardSummary>)result.Value!).Should().BeEmpty();
        }

        [TestMethod]
        public async Task Create_returns_id()
        {
            SetUser("bob");
            _service.Setup(x => x.CreateAsync(It.IsAny<DashboardDefinition>()))
                .ReturnsAsync("new-id");

            var result = await _controller.Create(new DashboardDefinition()) as OkObjectResult;

            result.Should().NotBeNull();
            result!.Value.Should().Be("new-id");
        }

        [TestMethod]
        public async Task Get_returns_404_for_unknown()
        {
            _service.Setup(x => x.GetAsync("unknown")).ReturnsAsync((DashboardDefinition?)null);

            var result = await _controller.Get("unknown") as NotFoundResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task Get_returns_403_when_access_denied()
        {
            SetUser("alice");
            var def = new DashboardDefinition { Id = "id1", Owner = "bob" };
            _service.Setup(x => x.GetAsync("id1")).ReturnsAsync(def);
            _service.Setup(x => x.CanAccess(def, "alice", It.IsAny<string[]>())).Returns(false);

            var result = await _controller.Get("id1") as ForbidResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task Update_returns_403_for_non_owner()
        {
            SetUser("alice");
            var existing = new DashboardDefinition { Id = "id1", Owner = "bob" };
            var update = new DashboardDefinition { Id = "id1" };
            _service.Setup(x => x.GetAsync("id1")).ReturnsAsync(existing);
            _service.Setup(x => x.CanEdit(existing, "alice", It.IsAny<string[]>())).Returns(false);

            var result = await _controller.Update("id1", update) as ForbidResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task Delete_returns_404_for_unknown()
        {
            _service.Setup(x => x.GetAsync("unknown")).ReturnsAsync((DashboardDefinition?)null);

            var result = await _controller.Delete("unknown") as NotFoundResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task Delete_returns_403_for_non_owner()
        {
            SetUser("alice");
            var existing = new DashboardDefinition { Id = "id1", Owner = "bob" };
            _service.Setup(x => x.GetAsync("id1")).ReturnsAsync(existing);
            _service.Setup(x => x.CanEdit(existing, "alice", It.IsAny<string[]>())).Returns(false);

            var result = await _controller.Delete("id1") as ForbidResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task GetWidgetData_returns_404_for_unknown_dashboard()
        {
            _service.Setup(x => x.GetAsync("unknown")).ReturnsAsync((DashboardDefinition?)null);

            var result = await _controller.GetWidgetData("unknown", "w1", CancellationToken.None) as NotFoundResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task GetWidgetData_returns_404_for_unknown_widget()
        {
            SetUser("bob");
            var def = new DashboardDefinition { Id = "id1", Owner = "bob" };
            _service.Setup(x => x.GetAsync("id1")).ReturnsAsync(def);
            _service.Setup(x => x.CanAccess(def, "bob", It.IsAny<string[]>())).Returns(true);

            var result = await _controller.GetWidgetData("id1", "w1", CancellationToken.None) as NotFoundResult;

            result.Should().NotBeNull();
        }

        [TestMethod]
        public async Task GetWidgetData_returns_403_when_dashboard_access_denied()
        {
            SetUser("alice");
            var def = new DashboardDefinition { Id = "id1", Owner = "bob" };
            _service.Setup(x => x.GetAsync("id1")).ReturnsAsync(def);
            _service.Setup(x => x.CanAccess(def, "alice", It.IsAny<string[]>())).Returns(false);

            var result = await _controller.GetWidgetData("id1", "w1", CancellationToken.None) as ForbidResult;

            result.Should().NotBeNull();
        }
    }
}