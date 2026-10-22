using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// /_Framework/RemoveUserCacheByAccount, ByRole and ByGroup are for administrators: the account must hold the administrator role.
    /// </summary>
    [TestClass]
    public class FrameworkCacheEndpointAdministratorTest
    {
        private static LoginUserInfo User(params string[] roleCodes)
        {
            var roles = new List<SimpleRole>();
            foreach (var code in roleCodes)
            {
                roles.Add(new SimpleRole { RoleCode = code });
            }
            return new LoginUserInfo { ITCode = "someone", Roles = roles };
        }

        [TestMethod]
        public void TheAdministratorRole_IsAdmitted()
        {
            Assert.IsTrue(_FrameworkController.IsAdministrator(User("001")));
            Assert.IsTrue(_FrameworkController.IsAdministrator(User("900", "001")));
        }

        [TestMethod]
        public void OtherRoles_AndNoRoles_AreRefused()
        {
            Assert.IsFalse(_FrameworkController.IsAdministrator(User("900")));
            Assert.IsFalse(_FrameworkController.IsAdministrator(User()));
            Assert.IsFalse(_FrameworkController.IsAdministrator(new LoginUserInfo { ITCode = "someone" }));
        }

        [TestMethod]
        public void NobodySignedIn_IsRefused()
        {
            Assert.IsFalse(_FrameworkController.IsAdministrator(null));
        }
    }
}
