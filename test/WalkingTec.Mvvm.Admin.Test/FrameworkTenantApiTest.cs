using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using WalkingTec.Mvvm.Admin.Api;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc.Admin.ViewModels.FrameworkTenantVMs;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    [TestClass]
    public class FrameworkTenantApiTest
    {
        private FrameworkTenantController _controller;
        private string _seed;

        public FrameworkTenantApiTest()
        {
            _seed = Guid.NewGuid().ToString();
            _controller = MockController.CreateApi<FrameworkTenantController>(
                new Demo.DataContext(_seed, DBTypeEnum.Memory), "admin");
        }

        // ─── Read / Delete Operations ─────────────────────────────────
        // Note: Create and Edit are excluded because FrameworkTenantVM.DoAdd()/DoEdit()
        // call TenantOperation() → Wtm.CreateDC("default") which requires full connection
        // string configuration not available in MockController tests.

        [TestMethod]
        public void SearchTest()
        {
            var rv = _controller.Search(new FrameworkTenantSearcher());
            Assert.IsTrue(string.IsNullOrEmpty((rv as ContentResult)?.Content) == false);
        }

        [TestMethod]
        public void GetTest()
        {
            FrameworkTenant v = new FrameworkTenant();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                v.TCode = "T003";
                v.TName = "GetTestTenant";
                v.Enabled = true;
                context.Set<FrameworkTenant>().Add(v);
                context.SaveChanges();
            }

            var rv = _controller.Get(v.ID);
            Assert.IsNotNull(rv);
            Assert.AreEqual("T003", rv.Entity.TCode);
            Assert.AreEqual("GetTestTenant", rv.Entity.TName);
        }

        [TestMethod]
        public void BatchDeleteTest()
        {
            FrameworkTenant v1 = new FrameworkTenant();
            FrameworkTenant v2 = new FrameworkTenant();
            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                v1.TCode = "T004";
                v1.TName = "Tenant1";
                v1.Enabled = true;
                v2.TCode = "T005";
                v2.TName = "Tenant2";
                v2.Enabled = true;
                context.Set<FrameworkTenant>().Add(v1);
                context.Set<FrameworkTenant>().Add(v2);
                context.SaveChanges();
            }

            var rv = _controller.BatchDelete(new string[] { v1.ID.ToString(), v2.ID.ToString() });
            Assert.IsInstanceOfType(rv, typeof(OkObjectResult));

            using (var context = new Demo.DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(0, context.Set<FrameworkTenant>().Count());
            }
        }

        [TestMethod]
        public void BatchDeleteEmptyIds_ReturnsOk()
        {
            var rv = _controller.BatchDelete(new string[] { });
            Assert.IsInstanceOfType(rv, typeof(OkResult));
        }

        [TestMethod]
        public void ExportExcelTest()
        {
            var rv = _controller.ExportExcel(new FrameworkTenantSearcher());
            Assert.IsInstanceOfType(rv, typeof(FileResult));
        }

        [TestMethod]
        public void ExportExcelByIdsTest()
        {
            var rv = _controller.ExportExcelByIds(new string[] { });
            Assert.IsInstanceOfType(rv, typeof(FileResult));
        }

        // ─── Role Boundary: CanUseTenant() ────────────────────────────
        // CanUseTenant() returns true when:
        //   1. LoginUserInfo.CurrentTenant == null (main host user), OR
        //   2. User's tenant exists in AllTenant with Enabled && EnableSub
        // Returns false otherwise → all CRUD endpoints return BadRequest.

        [TestMethod]
        public void Search_TenantUserWithoutPermission_ReturnsBadRequest()
        {
            // Simulate a tenant user whose tenant is NOT in AllTenant list
            _controller.Wtm.LoginUserInfo.CurrentTenant = "unknown_tenant";
            _controller.Wtm.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant>());

            var rv = _controller.Search(new FrameworkTenantSearcher());
            Assert.IsInstanceOfType(rv, typeof(BadRequestObjectResult));
        }

        [TestMethod]
        public void Add_TenantUserWithoutPermission_ReturnsBadRequest()
        {
            _controller.Wtm.LoginUserInfo.CurrentTenant = "blocked_tenant";
            _controller.Wtm.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant>());

            FrameworkTenantVM vm = _controller.Wtm.CreateVM<FrameworkTenantVM>();
            vm.Entity = new FrameworkTenant { TCode = "X", TName = "X" };
            var rv = _controller.Add(vm);
            Assert.IsInstanceOfType(rv, typeof(BadRequestObjectResult));
        }

        [TestMethod]
        public void Edit_TenantUserWithoutPermission_ReturnsBadRequest()
        {
            _controller.Wtm.LoginUserInfo.CurrentTenant = "blocked_tenant";
            _controller.Wtm.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant>());

            FrameworkTenantVM vm = _controller.Wtm.CreateVM<FrameworkTenantVM>();
            vm.Entity = new FrameworkTenant { TCode = "X", TName = "X" };
            var rv = _controller.Edit(vm);
            Assert.IsInstanceOfType(rv, typeof(BadRequestObjectResult));
        }

        [TestMethod]
        public void BatchDelete_TenantUserWithoutPermission_ReturnsBadRequest()
        {
            _controller.Wtm.LoginUserInfo.CurrentTenant = "blocked_tenant";
            _controller.Wtm.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant>());

            var rv = _controller.BatchDelete(new string[] { Guid.NewGuid().ToString() });
            Assert.IsInstanceOfType(rv, typeof(BadRequestObjectResult));
        }

        [TestMethod]
        public void Search_MainHostUser_Succeeds()
        {
            // Main host user: CurrentTenant == null
            _controller.Wtm.LoginUserInfo.CurrentTenant = null;
            var rv = _controller.Search(new FrameworkTenantSearcher());
            // Should not be BadRequest (CanUseTenant returns true)
            Assert.IsFalse(rv is BadRequestObjectResult,
                "Main host user (CurrentTenant=null) should pass CanUseTenant check");
        }

        [TestMethod]
        public void Search_TenantUserWithSubEnabled_Succeeds()
        {
            // Tenant user whose tenant has Enabled=true AND EnableSub=true
            _controller.Wtm.LoginUserInfo.CurrentTenant = "sub_tenant";
            _controller.Wtm.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant>
            {
                new FrameworkTenant
                {
                    TCode = "sub_tenant",
                    TName = "Sub Tenant",
                    Enabled = true,
                    EnableSub = true
                }
            });

            var rv = _controller.Search(new FrameworkTenantSearcher());
            Assert.IsFalse(rv is BadRequestObjectResult,
                "Tenant user with Enabled+EnableSub should pass CanUseTenant check");
        }

        [TestMethod]
        public void Search_TenantUserWithSubDisabled_ReturnsBadRequest()
        {
            // Tenant exists but EnableSub=false → not allowed
            _controller.Wtm.LoginUserInfo.CurrentTenant = "nosub_tenant";
            _controller.Wtm.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant>
            {
                new FrameworkTenant
                {
                    TCode = "nosub_tenant",
                    TName = "NoSub Tenant",
                    Enabled = true,
                    EnableSub = false
                }
            });

            var rv = _controller.Search(new FrameworkTenantSearcher());
            Assert.IsInstanceOfType(rv, typeof(BadRequestObjectResult));
        }

        [TestMethod]
        public void Search_DisabledTenant_ReturnsBadRequest()
        {
            // Tenant exists but Enabled=false
            _controller.Wtm.LoginUserInfo.CurrentTenant = "disabled_t";
            _controller.Wtm.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant>
            {
                new FrameworkTenant
                {
                    TCode = "disabled_t",
                    TName = "Disabled",
                    Enabled = false,
                    EnableSub = true
                }
            });

            var rv = _controller.Search(new FrameworkTenantSearcher());
            Assert.IsInstanceOfType(rv, typeof(BadRequestObjectResult));
        }
    }
}
