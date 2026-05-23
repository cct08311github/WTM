using System.Collections.Generic;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;
using DUWENINK.Captcha;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// Verifies the admin runtime check added to _FrameworkController in #30.
    ///
    /// CallerIsAdmin() is internal; this assembly has InternalsVisibleTo access
    /// via WalkingTec.Mvvm.Mvc.csproj. We construct the controller with a
    /// synthesized WTMContext whose LoginUserInfo.Roles is set to controlled values.
    /// </summary>
    [TestClass]
    public class BatchAssignRolesRbacTests
    {
        // ─── helpers ──────────────────────────────────────────────────────────

        private static _FrameworkController CreateController(List<SimpleRole>? roles)
        {
            var mockSecurityCode = new Mock<ISecurityCodeHelper>();
            var controller = new _FrameworkController(mockSecurityCode.Object);
            var wtm = MockWtmContext.CreateWtmContext();
            wtm.LoginUserInfo = new LoginUserInfo
            {
                ITCode = "testuser",
                Roles = roles
            };
            controller.Wtm = wtm;
            return controller;
        }

        private static List<SimpleRole> RolesWithCode(string roleCode) =>
            new List<SimpleRole> { new SimpleRole { RoleCode = roleCode } };

        // ─── CallerIsAdmin tests ───────────────────────────────────────────────

        [TestMethod]
        public void CallerIsAdmin_ReturnsTrue_WhenRoleCodeIsAdmin()
        {
            var controller = CreateController(RolesWithCode("Admin"));
            controller.CallerIsAdmin().Should().BeTrue(
                "a user with RoleCode == \"Admin\" should be recognised as an administrator");
        }

        [TestMethod]
        public void CallerIsAdmin_ReturnsTrue_WhenRoleCodeIsAdminMixedCase_Lower()
        {
            var controller = CreateController(RolesWithCode("admin"));
            controller.CallerIsAdmin().Should().BeTrue(
                "role code comparison must be case-insensitive; \"admin\" must match");
        }

        [TestMethod]
        public void CallerIsAdmin_ReturnsTrue_WhenRoleCodeIsAdminMixedCase_Upper()
        {
            var controller = CreateController(RolesWithCode("ADMIN"));
            controller.CallerIsAdmin().Should().BeTrue(
                "role code comparison must be case-insensitive; \"ADMIN\" must match");
        }

        [TestMethod]
        public void CallerIsAdmin_ReturnsFalse_WhenRolesDoesNotIncludeAdmin()
        {
            var controller = CreateController(RolesWithCode("Operator"));
            controller.CallerIsAdmin().Should().BeFalse(
                "a user without Admin role must be rejected");
        }

        [TestMethod]
        public void CallerIsAdmin_ReturnsFalse_WhenLoginUserInfoIsNull()
        {
            // Setting Wtm to null exercises the Wtm?.LoginUserInfo?.Roles null-propagation
            // chain in CallerIsAdmin(). WTMContext.LoginUserInfo getter may reload from cache
            // in the mock, so we test the null-Wtm path rather than null-LoginUserInfo directly.
            var mockSecurityCode = new Mock<ISecurityCodeHelper>();
            var controller = new _FrameworkController(mockSecurityCode.Object);
            controller.Wtm = null!;

            controller.CallerIsAdmin().Should().BeFalse(
                "when Wtm is null there is no authenticated user context — must not be admin");
        }

        [TestMethod]
        public void CallerIsAdmin_ReturnsFalse_WhenRolesIsNull()
        {
            var controller = CreateController(roles: null);
            controller.CallerIsAdmin().Should().BeFalse(
                "null Roles list means no role assigned — must not be admin");
        }
    }
}
