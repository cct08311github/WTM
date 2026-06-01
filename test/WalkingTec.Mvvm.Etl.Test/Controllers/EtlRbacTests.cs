#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Etl.Scheduling;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Etl.Test.Controllers;

/// <summary>
/// ETL 控制器 RBAC 授權測試 — 對應 Issue #423
///
/// WTM RBAC 在 PrivilegeFilter（middleware）執行，非 controller 內部。
/// 因此測試策略為：
/// 1. Attribute 存在性測試 — 確認所有高風險 action 都有 [ActionDescription]，
///    使 RBAC 系統能識別並保護它們。
/// 2. Wtm.IsAccessable() 行為測試 — 直接測試 PrivilegeFilter 使用的授權邏輯，
///    驗證未登入 / 無特權使用者無法存取 ETL 端點。
/// </summary>
[TestClass]
public class EtlRbacTests
{
    // ─── Helper: ETL 高風險操作名稱 ─────────────────────────────────────────────

    private static readonly string[] HighRiskJobActions =
    {
        "TriggerNow", "Pause", "Resume", "Abort", "SkipNext", "Reschedule",
        "Create", "Edit", "Delete"
    };

    private static readonly string[] MonitorActions = { "Running", "Progress" };
    private static readonly string[] RunLogActions  = { "Index", "Search", "Rerun" };

    // ─── 1. Attribute 存在性測試 ────────────────────────────────────────────────

    [TestMethod]
    public void EtlJobController_all_high_risk_actions_have_ActionDescription()
    {
        var type = typeof(_EtlJobController);
        foreach (var name in HighRiskJobActions)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                              .Where(m => m.Name == name);
            Assert.IsTrue(methods.Any(),
                $"_EtlJobController 缺少 {name} 方法");

            // At least one overload must carry [ActionDescription]
            var hasAttr = methods.Any(m =>
                m.IsDefined(typeof(ActionDescriptionAttribute), false) ||
                type.IsDefined(typeof(ActionDescriptionAttribute), false));
            Assert.IsTrue(hasAttr,
                $"_EtlJobController.{name}（或其 Controller）缺少 [ActionDescription]，無法被 RBAC 識別");
        }
    }

    [TestMethod]
    public void EtlMonitorController_actions_have_ActionDescription()
    {
        var type = typeof(_EtlMonitorController);
        Assert.IsTrue(type.IsDefined(typeof(ActionDescriptionAttribute), false),
            "_EtlMonitorController 本身缺少 [ActionDescription]");

        foreach (var name in MonitorActions)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                              .Where(m => m.Name == name);
            Assert.IsTrue(methods.Any(),
                $"_EtlMonitorController 缺少 {name} 方法");
        }
    }

    [TestMethod]
    public void EtlRunLogController_high_risk_action_Rerun_has_ActionDescription()
    {
        var type = typeof(_EtlRunLogController);
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                          .Where(m => m.Name == "Rerun");
        Assert.IsTrue(methods.Any(),
            "_EtlRunLogController 缺少 Rerun 方法");

        var hasAttr = methods.Any(m =>
            m.IsDefined(typeof(ActionDescriptionAttribute), false) ||
            type.IsDefined(typeof(ActionDescriptionAttribute), false));
        Assert.IsTrue(hasAttr,
            "_EtlRunLogController.Rerun（或其 Controller）缺少 [ActionDescription]");
    }

    // ─── 2. Wtm.IsAccessable() — 無 FunctionPrivileges 使用者被拒 (→ 403) ──────
    // Note: LoginUserInfo == null causes PrivilegeFilter to return 401; that code
    // path is exercised by PrivilegeFilter itself (middleware level). Here we test
    // the FunctionPrivileges == null branch (403), which is the same IsAccessable()
    // logic used in both cases and is testable directly.

    [TestMethod]
    public void IsAccessable_returns_false_for_etl_url_when_user_has_empty_privileges()
    {
        // Explicit empty list (rather than null) — user is logged in but has no grants.
        // PrivilegeFilter maps this to HTTP 403.
        var wtm = MockWtmContext.CreateWtmContext();
        wtm.LoginUserInfo!.FunctionPrivileges = new System.Collections.Generic.List<WalkingTec.Mvvm.Core.Support.Json.SimpleFunctionPri>();

        Assert.IsFalse(wtm.IsAccessable("/_EtlJob/TriggerNow"),
            "無授權使用者不應能存取 /_EtlJob/TriggerNow");
        Assert.IsFalse(wtm.IsAccessable("/_EtlJob/Abort"),
            "無授權使用者不應能存取 /_EtlJob/Abort");
        Assert.IsFalse(wtm.IsAccessable("/_EtlMonitor/Running"),
            "無授權使用者不應能存取 /_EtlMonitor/Running");
        Assert.IsFalse(wtm.IsAccessable("/_EtlRunLog/Rerun"),
            "無授權使用者不應能存取 /_EtlRunLog/Rerun");
    }

    // ─── 3. Wtm.IsAccessable() — 登入但無特權使用者被拒 (→ 403) ────────────────

    [TestMethod]
    public void IsAccessable_returns_false_for_etl_url_when_user_has_no_function_privileges()
    {
        // FunctionPrivileges == null means user has no page-level grants.
        // PrivilegeFilter maps this to HTTP 403.
        var wtm = MockWtmContext.CreateWtmContext();
        // MockWtmContext sets LoginUserInfo with ITCode but no FunctionPrivileges
        Assert.IsNull(wtm.LoginUserInfo?.FunctionPrivileges,
            "MockWtmContext should create user with null FunctionPrivileges");

        Assert.IsFalse(wtm.IsAccessable("/_EtlJob/TriggerNow"),
            "無特權使用者不應能存取 /_EtlJob/TriggerNow");
        Assert.IsFalse(wtm.IsAccessable("/_EtlJob/Pause"),
            "無特權使用者不應能存取 /_EtlJob/Pause");
        Assert.IsFalse(wtm.IsAccessable("/_EtlJob/Resume"),
            "無特權使用者不應能存取 /_EtlJob/Resume");
        Assert.IsFalse(wtm.IsAccessable("/_EtlJob/Abort"),
            "無特權使用者不應能存取 /_EtlJob/Abort");
        Assert.IsFalse(wtm.IsAccessable("/_EtlJob/Delete"),
            "無特權使用者不應能存取 /_EtlJob/Delete");
        Assert.IsFalse(wtm.IsAccessable("/_EtlMonitor/Running"),
            "無特權使用者不應能存取 /_EtlMonitor/Running");
        Assert.IsFalse(wtm.IsAccessable("/_EtlRunLog/Rerun"),
            "無特權使用者不應能存取 /_EtlRunLog/Rerun");
    }

    // ─── 4. Wtm.IsAccessable() — ETL URL 不在 AllAccessUrls（非 public）──────

    [TestMethod]
    public void ETL_urls_are_not_in_AllAccessUrls_by_default()
    {
        var wtm = MockWtmContext.CreateWtmContext();
        var allPublic = wtm.GlobaInfo.AllAccessUrls ?? new System.Collections.Generic.List<string>();

        var etlUrls = new[]
        {
            "/_EtlJob/TriggerNow",
            "/_EtlJob/Abort",
            "/_EtlJob/Delete",
            "/_EtlMonitor/Running",
            "/_EtlRunLog/Rerun",
        };

        foreach (var url in etlUrls)
        {
            Assert.IsFalse(allPublic.Contains(url, StringComparer.OrdinalIgnoreCase),
                $"ETL 端點 {url} 不應在 AllAccessUrls（公開）列表中");
        }
    }

    // ─── 5. IsQuickDebug 預設為 false（確保 RBAC 預設啟用）────────────────────

    [TestMethod]
    public void MockWtmContext_IsQuickDebug_is_false_by_default()
    {
        var wtm = MockWtmContext.CreateWtmContext();
        Assert.IsFalse(wtm.ConfigInfo?.IsQuickDebug ?? false,
            "測試環境 IsQuickDebug 預設應為 false，否則 RBAC 全部繞過");
    }

    // ─── 6. OnActionExecuting — 控制器層 Admin/ETLAdmin 角色守衛（#523）────────

    private static _EtlJobController CreateEtlControllerWithRoles(params string[] roleCodes)
    {
        var controller = new _EtlJobController(null!, NullLogger<_EtlJobController>.Instance);
        controller.Wtm = MockWtmContext.CreateWtmContext();
        controller.Wtm.LoginUserInfo!.Roles = roleCodes
            .Select(r => new SimpleRole { RoleCode = r })
            .ToList();
        return controller;
    }

    private static ActionExecutingContext MakeActionContext(_EtlJobController controller)
    {
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller);
    }

    [TestMethod]
    public void OnActionExecuting_Admin_role_is_allowed()
    {
        var controller = CreateEtlControllerWithRoles("Admin");
        var context = MakeActionContext(controller);
        controller.OnActionExecuting(context);
        Assert.IsNull(context.Result, "Admin 角色應允許通過，context.Result 應為 null");
    }

    [TestMethod]
    public void OnActionExecuting_ETLAdmin_role_is_allowed()
    {
        var controller = CreateEtlControllerWithRoles("ETLAdmin");
        var context = MakeActionContext(controller);
        controller.OnActionExecuting(context);
        Assert.IsNull(context.Result, "ETLAdmin 角色應允許通過，context.Result 應為 null");
    }

    [TestMethod]
    public void OnActionExecuting_Admin_check_is_case_insensitive()
    {
        var controller = CreateEtlControllerWithRoles("admin");
        var context = MakeActionContext(controller);
        controller.OnActionExecuting(context);
        Assert.IsNull(context.Result, "admin（小寫）應視同 Admin，允許通過");
    }

    [TestMethod]
    public void OnActionExecuting_non_admin_role_is_forbidden()
    {
        var controller = CreateEtlControllerWithRoles("Manager");
        var context = MakeActionContext(controller);
        controller.OnActionExecuting(context);
        Assert.IsInstanceOfType(context.Result, typeof(ForbidResult),
            "非 Admin/ETLAdmin 角色應回傳 ForbidResult");
    }

    [TestMethod]
    public void OnActionExecuting_no_roles_is_forbidden()
    {
        var controller = CreateEtlControllerWithRoles();
        var context = MakeActionContext(controller);
        controller.OnActionExecuting(context);
        Assert.IsInstanceOfType(context.Result, typeof(ForbidResult),
            "無角色使用者應回傳 ForbidResult");
    }

    // ─── 7. IsQuickDebug bypass — 對應 Issue #638 ───────────────────────────────

    [TestMethod]
    public void OnActionExecuting_IsQuickDebug_true_bypasses_role_check()
    {
        // Arrange: non-Admin user (would normally be forbidden)
        var controller = CreateEtlControllerWithRoles("001");   // default demo admin code
        controller.Wtm!.ConfigInfo!.IsQuickDebug = true;
        var context = MakeActionContext(controller);

        // Act
        controller.OnActionExecuting(context);

        // Assert: IsQuickDebug must bypass the custom role gate, matching WTM convention
        Assert.IsNull(context.Result,
            "IsQuickDebug=true 時應繞過 ETL 角色守衛，context.Result 應為 null");
    }

    [TestMethod]
    public void OnActionExecuting_IsQuickDebug_false_still_enforces_role_check()
    {
        // Arrange: non-Admin user with IsQuickDebug explicitly false
        var controller = CreateEtlControllerWithRoles("001");
        controller.Wtm!.ConfigInfo!.IsQuickDebug = false;
        var context = MakeActionContext(controller);

        // Act
        controller.OnActionExecuting(context);

        // Assert: role check still runs, non-Admin is forbidden
        Assert.IsInstanceOfType(context.Result, typeof(ForbidResult),
            "IsQuickDebug=false 時仍應執行角色檢查");
    }

}
