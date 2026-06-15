#nullable enable
using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Test.Services
{
    [TestClass]
    public class WtmAuthorizationServiceTests
    {
        private WtmAuthorizationService _service = null!;

        [TestInitialize]
        public void Setup()
        {
            _service = new WtmAuthorizationService();
        }

        #region IsAccessable — early return paths

        [TestMethod]
        public void IsAccessable_QuickDebug_ReturnsTrue()
        {
            var config = new Configs { IsQuickDebug = true };
            var gd = MakeGlobalData();

            _service.IsAccessable("/admin/secret", null, config, gd).Should().BeTrue();
        }

        [TestMethod]
        public void IsAccessable_NullUrl_ReturnsTrue()
        {
            var config = new Configs { IsQuickDebug = false };
            var gd = MakeGlobalData();

            _service.IsAccessable(null, null, config, gd).Should().BeTrue();
        }

        [TestMethod]
        public void IsAccessable_EmptyUrl_ReturnsTrue()
        {
            var config = new Configs { IsQuickDebug = false };
            var gd = MakeGlobalData();

            _service.IsAccessable("", null, config, gd).Should().BeTrue();
        }

        #endregion

        #region IsAccessable — public URL

        [TestMethod]
        public void IsAccessable_PublicUrl_ReturnsTrue()
        {
            var menuId = Guid.NewGuid();
            var gd = MakeGlobalData(new List<SimpleMenu>
            {
                new SimpleMenu { ID = menuId, Url = "/public/page", IsPublic = true }
            });

            var config = new Configs { IsQuickDebug = false };

            _service.IsAccessable("/public/page", null, config, gd).Should().BeTrue();
        }

        #endregion

        #region IsAccessable — AllAccessUrls

        [TestMethod]
        public void IsAccessable_InAllAccessUrls_ReturnsTrue()
        {
            var gd = MakeGlobalData();
            gd.AllAccessUrls = new List<string> { "/api/open" };
            var config = new Configs { IsQuickDebug = false };

            _service.IsAccessable("/api/open", null, config, gd).Should().BeTrue();
        }

        [TestMethod]
        public void IsAccessable_RootSlash_NotMatchedByAllAccessUrls()
        {
            var gd = MakeGlobalData();
            gd.AllAccessUrls = new List<string> { "/" };
            var config = new Configs { IsQuickDebug = false };

            // "/" in AllAccessUrls is explicitly skipped (au != "/")
            _service.IsAccessable("/some/page", null, config, gd).Should().BeFalse();
        }

        #endregion

        #region IsAccessable — no function privileges

        [TestMethod]
        public void IsAccessable_NoFunctionPrivileges_ReturnsFalse()
        {
            var gd = MakeGlobalData();
            var config = new Configs { IsQuickDebug = false };
            var user = new LoginUserInfo { ITCode = "user1", FunctionPrivileges = null };

            _service.IsAccessable("/protected/page", user, config, gd).Should().BeFalse();
        }

        #endregion

        #region IsAccessable — menu-based privilege check

        [TestMethod]
        public void IsAccessable_MatchingMenuPrivilege_ReturnsTrue()
        {
            var menuId = Guid.NewGuid();
            var gd = MakeGlobalData(new List<SimpleMenu>
            {
                new SimpleMenu { ID = menuId, Url = "/admin/users" }
            });
            var config = new Configs { IsQuickDebug = false };
            var user = new LoginUserInfo
            {
                ITCode = "admin",
                FunctionPrivileges = new List<SimpleFunctionPri>
                {
                    new SimpleFunctionPri { MenuItemId = menuId, Allowed = true }
                }
            };

            _service.IsAccessable("/admin/users", user, config, gd).Should().BeTrue();
        }

        [TestMethod]
        public void IsAccessable_MenuExistsButNoPrivilege_ReturnsFalse()
        {
            var menuId = Guid.NewGuid();
            var gd = MakeGlobalData(new List<SimpleMenu>
            {
                new SimpleMenu { ID = menuId, Url = "/admin/users" }
            });
            var config = new Configs { IsQuickDebug = false };
            var user = new LoginUserInfo
            {
                ITCode = "guest",
                FunctionPrivileges = new List<SimpleFunctionPri>
                {
                    // Different menu ID → no match
                    new SimpleFunctionPri { MenuItemId = Guid.NewGuid(), Allowed = true }
                }
            };

            _service.IsAccessable("/admin/users", user, config, gd).Should().BeFalse();
        }

        [TestMethod]
        public void IsAccessable_NoMenuFound_ReturnsFalse()
        {
            var gd = MakeGlobalData(new List<SimpleMenu>()); // empty
            var config = new Configs { IsQuickDebug = false };
            var user = new LoginUserInfo
            {
                ITCode = "user1",
                FunctionPrivileges = new List<SimpleFunctionPri>()
            };

            _service.IsAccessable("/unknown/path", user, config, gd).Should().BeFalse();
        }

        #endregion

        #region IsAccessable — hash URL

        [TestMethod]
        public void IsAccessable_HashUrl_ReturnsTrue()
        {
            var gd = MakeGlobalData();
            var config = new Configs { IsQuickDebug = false };
            var user = new LoginUserInfo
            {
                ITCode = "user1",
                FunctionPrivileges = new List<SimpleFunctionPri>()
            };

            _service.IsAccessable("#/temp/js/location", user, config, gd).Should().BeTrue();
        }

        #endregion

        #region IsAccessable — tenant [HostOnly]

        [TestMethod]
        public void IsAccessable_TenantHostOnly_ReturnsFalse()
        {
            var gd = MakeGlobalData();
            gd.AllMainTenantOnlyUrls = new List<string> { "/admin/hostonly" };
            var config = new Configs { IsQuickDebug = false, EnableTenant = true };
            var user = new LoginUserInfo
            {
                ITCode = "tenantuser",
                TenantCode = "T001",
                FunctionPrivileges = new List<SimpleFunctionPri>()
            };

            _service.IsAccessable("/admin/hostonly", user, config, gd).Should().BeFalse();
        }

        [TestMethod]
        public void IsAccessable_HostUser_CanAccessHostOnly()
        {
            var menuId = Guid.NewGuid();
            var gd = MakeGlobalData();
            gd.AllMainTenantOnlyUrls = new List<string> { "/admin/hostonly" };
            gd.SetMenuGetFunc(() => new List<SimpleMenu>
            {
                new SimpleMenu { ID = menuId, Url = "/admin/hostonly" }
            });
            var config = new Configs { IsQuickDebug = false, EnableTenant = true };
            var user = new LoginUserInfo
            {
                ITCode = "hostadmin",
                TenantCode = null, // host user, not a tenant
                FunctionPrivileges = new List<SimpleFunctionPri>
                {
                    new SimpleFunctionPri { MenuItemId = menuId, Allowed = true }
                }
            };

            _service.IsAccessable("/admin/hostonly", user, config, gd).Should().BeTrue();
        }

        #endregion

        #region IsAccessable — tenant menu not allowed

        [TestMethod]
        public void IsAccessable_TenantMenuNotAllowed_ReturnsFalse()
        {
            var menuId = Guid.NewGuid();
            var gd = MakeGlobalData(new List<SimpleMenu>
            {
                new SimpleMenu { ID = menuId, Url = "/admin/config", TenantAllowed = false }
            });
            var config = new Configs { IsQuickDebug = false };
            var user = new LoginUserInfo
            {
                ITCode = "tenantuser",
                CurrentTenant = "T001",
                FunctionPrivileges = new List<SimpleFunctionPri>
                {
                    new SimpleFunctionPri { MenuItemId = menuId, Allowed = true }
                }
            };

            _service.IsAccessable("/admin/config", user, config, gd).Should().BeFalse();
        }

        #endregion

        #region IsUrlPublic

        [TestMethod]
        public void IsUrlPublic_PublicMenu_ReturnsTrue()
        {
            var gd = MakeGlobalData(new List<SimpleMenu>
            {
                new SimpleMenu { ID = Guid.NewGuid(), Url = "/home/index", IsPublic = true }
            });

            _service.IsUrlPublic("/home/index", gd).Should().BeTrue();
        }

        [TestMethod]
        public void IsUrlPublic_NonPublicMenu_ReturnsFalse()
        {
            var gd = MakeGlobalData(new List<SimpleMenu>
            {
                new SimpleMenu { ID = Guid.NewGuid(), Url = "/admin/panel", IsPublic = false }
            });

            _service.IsUrlPublic("/admin/panel", gd).Should().BeFalse();
        }

        [TestMethod]
        public void IsUrlPublic_NoMenuFound_ReturnsFalse()
        {
            var gd = MakeGlobalData();

            _service.IsUrlPublic("/unknown", gd).Should().BeFalse();
        }

        [TestMethod]
        public void IsUrlPublic_HashUrl_ReturnsTrue()
        {
            var gd = MakeGlobalData();

            _service.IsUrlPublic("#something", gd).Should().BeTrue();
        }

        [TestMethod]
        public void IsUrlPublic_NullUrl_ReturnsFalse()
        {
            var gd = MakeGlobalData();

            _service.IsUrlPublic(null, gd).Should().BeFalse();
        }

        #endregion

        #region MatchUrl — NonBacktracking fallback (M20)

        [TestMethod]
        public void IsAccessable_PatternUnsupportedByNonBacktracking_FallsBackAndDoesNotThrow()
        {
            // Regex patterns that use backreferences, look-aheads, or other constructs
            // unsupported by RegexOptions.NonBacktracking throw NotSupportedException at
            // CONSTRUCTION time (not at match time). Before the fix the exception was
            // uncaught and surfaced as a 500. After the fix the code falls back to a
            // compiled regex without NonBacktracking and returns a valid result.
            //
            // A lookahead "(?=...)" triggers NotSupportedException with NonBacktracking.
            var patternWithLookahead = "/admin(?=/users)";

            var gd = MakeGlobalData();
            // Put the problematic pattern into AllAccessUrls so MatchUrl is invoked.
            gd.AllAccessUrls = new List<string> { patternWithLookahead };
            var config = new Configs { IsQuickDebug = false };

            // Must not throw — should fall back gracefully to compiled regex.
            Action act = () => _service.IsAccessable("/admin/users", null, config, gd);
            act.Should().NotThrow("NonBacktracking-unsupported patterns must fall back to compiled regex, not throw 500");
        }

        [TestMethod]
        public void IsAccessable_ValidPattern_StillMatchesAfterCachingFallback()
        {
            // Ensure normal patterns still work even when the cache may have been seeded
            // from a previous test that triggered the fallback.
            var menuId = Guid.NewGuid();
            var gd = MakeGlobalData(new List<SimpleMenu>
            {
                new SimpleMenu { ID = menuId, Url = "/api/data" }
            });
            gd.AllAccessUrls = new List<string> { "/api/data" };
            var config = new Configs { IsQuickDebug = false };

            // A plain pattern should match without error.
            var result = _service.IsAccessable("/api/data", null, config, gd);
            result.Should().BeTrue("a valid AllAccessUrls pattern must still match after the fallback fix");
        }

        #endregion

        #region PrivilegeFilter — attribute-route URL normalization

        [TestMethod]
        public void PrivilegeFilter_AttributeRoute_ConstraintStripped_ProducesMenuFormUrl()
        {
            var pattern = @"\{(\w+)(?::[^}{]+)?(?:\?)?\}";
            var replacement = "{$1}";

            System.Text.RegularExpressions.Regex.Replace("{id:guid}", pattern, replacement)
                .Should().Be("{id}", "guid constraint should be stripped from route parameter");

            System.Text.RegularExpressions.Regex.Replace(
                    "api/_workflow/definitions/{code}/publish", pattern, replacement)
                .Should().Be("api/_workflow/definitions/{code}/publish",
                    "a parameter with no constraint should be left unchanged");

            System.Text.RegularExpressions.Regex.Replace("{id:guid}/approve", pattern, replacement)
                .Should().Be("{id}/approve",
                    "guid constraint should be stripped even when followed by a path segment");
        }

        [TestMethod]
        public void WtmAuthorizationService_WorkflowPublishUrl_WithoutPrivilege_ReturnsFalse()
        {
            // Prove that when BaseUrl resolves to the template URL, RBAC correctly
            // denies a user who has no matching function privilege for that URL.
            var menuId = Guid.NewGuid();
            var gd = MakeGlobalData(new List<SimpleMenu>
            {
                new SimpleMenu { ID = menuId, Url = "/api/_workflow/definitions/{code}/publish" }
            });
            var config = new Configs { IsQuickDebug = false };
            var user = new LoginUserInfo
            {
                ITCode = "regular_user",
                FunctionPrivileges = new List<SimpleFunctionPri>
                {
                    // Different menu ID → no privilege match for the workflow publish URL
                    new SimpleFunctionPri { MenuItemId = Guid.NewGuid(), Allowed = true }
                }
            };

            _service.IsAccessable("/api/_workflow/definitions/{code}/publish", user, config, gd)
                .Should().BeFalse("a user without privilege for this URL must be denied");
        }

        [TestMethod]
        public void WtmAuthorizationService_EmptyUrl_FailsOpen_BaselineBehavior()
        {
            // This is the existing behavior. PrivilegeFilter must not reach this path
            // for gated authenticated actions.
            var config = new Configs { IsQuickDebug = false };
            var gd = MakeGlobalData();

            _service.IsAccessable("", null, config, gd)
                .Should().BeTrue("empty URL currently returns true — the defense-in-depth guard in PrivilegeFilter prevents reaching this code path for authenticated gated actions");
        }

        #endregion

        #region Helpers

        private static GlobalData MakeGlobalData(List<SimpleMenu>? menus = null)
        {
            var gd = new GlobalData
            {
                AllAccessUrls = new List<string>(),
                AllMainTenantOnlyUrls = new List<string>(),
                AllModule = new List<SimpleModule>(),
                AllAssembly = new List<System.Reflection.Assembly>()
            };
            gd.SetMenuGetFunc(() => menus ?? new List<SimpleMenu>());
            return gd;
        }

        #endregion
    }
}
