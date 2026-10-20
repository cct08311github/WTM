using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// #13: the workflow lookup endpoints must need a signed-in user, and _WorkflowApi must not exist when workflow is disabled.
    /// </summary>
    [TestClass]
    public class WorkflowApiAuthTest
    {
        private static readonly string[] LookupActions =
        {
            "GetWorkflowUsers", "GetWorkflowGroups", "GetWorkflowGroupManagers", "GetWorkflowMyGroupManagers", "GetWorkflowRoles"
        };

        [TestMethod]
        public void LookupActions_AreNotAnonymous()
        {
            foreach (var name in LookupActions)
            {
                var m = typeof(WorkflowApiController).GetMethod(name);
                Assert.IsNotNull(m, name);
                Assert.IsNull(m.GetCustomAttribute<PublicAttribute>(true), name + " must not be [Public]");
                Assert.AreEqual(0, m.GetCustomAttributes(true).OfType<IAllowAnonymous>().Count(), name + " must not allow anonymous");
                Assert.IsNotNull(m.GetCustomAttribute<NoLogAttribute>(true), name + " keeps [NoLog]");
            }
        }

        [TestMethod]
        public void Controller_RequiresAuthentication_AndNoActionAllowsAnonymous()
        {
            var t = typeof(WorkflowApiController);
            Assert.IsNotNull(t.GetCustomAttribute<AuthorizeJwtWithCookieAttribute>(true));
            Assert.IsNotNull(t.GetCustomAttribute<AllRightsAttribute>(true));
            Assert.AreEqual(0, t.GetCustomAttributes(true).OfType<IAllowAnonymous>().Count());
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.AreEqual(0, m.GetCustomAttributes(true).OfType<IAllowAnonymous>().Count(), m.Name + " must not allow anonymous");
            }
        }

        [TestMethod]
        public void DisabledConvention_RemovesWorkflowApiController_OnlyThat()
        {
            var app = new ApplicationModel();
            var workflow = new ControllerModel(typeof(WorkflowApiController).GetTypeInfo(), new object[0]);
            var other = new ControllerModel(typeof(OtherApiController).GetTypeInfo(), new object[0]);
            app.Controllers.Add(workflow);
            app.Controllers.Add(other);

            new WorkflowApiDisabledConvention().Apply(app);

            Assert.AreEqual(1, app.Controllers.Count);
            Assert.AreSame(other, app.Controllers[0]);
        }

        [TestMethod]
        public void DisabledConvention_WithoutWorkflowController_ChangesNothing()
        {
            var app = new ApplicationModel();
            app.Controllers.Add(new ControllerModel(typeof(OtherApiController).GetTypeInfo(), new object[0]));

            new WorkflowApiDisabledConvention().Apply(app);

            Assert.AreEqual(1, app.Controllers.Count);
        }

        private static System.Collections.Generic.IList<IApplicationModelConvention> ConventionsFor(bool enableWorkflow)
        {
            var services = new ServiceCollection();
            services.AddOptions();
            WorkflowApiSecurity.AddWorkflowApiConvention(services, new Configs { EnableWorkflow = enableWorkflow });
            using var sp = services.BuildServiceProvider();
            return sp.GetRequiredService<IOptions<MvcOptions>>().Value.Conventions;
        }

        [TestMethod]
        public void Registration_AddsConvention_OnlyWhenWorkflowIsDisabled()
        {
            Assert.AreEqual(1, ConventionsFor(false).OfType<WorkflowApiDisabledConvention>().Count());
            Assert.AreEqual(0, ConventionsFor(true).OfType<WorkflowApiDisabledConvention>().Count());
        }

        private class OtherApiController : ControllerBase
        {
        }
    }
}
