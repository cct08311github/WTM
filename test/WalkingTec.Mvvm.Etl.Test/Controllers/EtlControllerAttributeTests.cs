#nullable enable
using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Etl.Test.Controllers;

/// <summary>
/// Regression tests for issue #635.
/// ETL controllers inherit BaseController (convention routing) and must NOT carry
/// [ApiController], which forces attribute-only routing and crashes the host at startup.
/// </summary>
[TestClass]
public class EtlControllerAttributeTests
{
    private static readonly Type[] EtlControllerTypes =
    [
        typeof(_EtlJobController),
        typeof(_EtlMonitorController),
        typeof(_EtlRunLogController),
    ];

    [TestMethod]
    public void EtlControllers_inherit_BaseController_not_BaseApiController()
    {
        foreach (var t in EtlControllerTypes)
        {
            Assert.IsTrue(
                typeof(BaseController).IsAssignableFrom(t),
                $"{t.Name} must inherit BaseController");
            Assert.IsFalse(
                typeof(BaseApiController).IsAssignableFrom(t),
                $"{t.Name} must NOT inherit BaseApiController");
        }
    }

    [TestMethod]
    [Description("Regression: #635 — [ApiController] on a convention-routed BaseController crashes the host at startup.")]
    public void EtlControllers_do_not_carry_ApiControllerAttribute()
    {
        foreach (var t in EtlControllerTypes)
        {
            var hasApiCtrl = t.GetCustomAttributes(typeof(ApiControllerAttribute), inherit: true).Any();
            Assert.IsFalse(
                hasApiCtrl,
                $"{t.Name} must not have [ApiController]. " +
                "BaseController uses convention routing; [ApiController] requires attribute routing and crashes the host.");
        }
    }
}
