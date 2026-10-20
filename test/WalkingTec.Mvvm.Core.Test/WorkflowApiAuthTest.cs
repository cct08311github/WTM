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

        private static ApplicationModel ApplyRegisteredConventions(bool enableWorkflow, out ControllerModel other)
        {
            var app = new ApplicationModel();
            app.Controllers.Add(new ControllerModel(typeof(WorkflowApiController).GetTypeInfo(), new object[0]));
            app.Controllers.Add(new ControllerModel(typeof(WorkflowApiDisabledController).GetTypeInfo(), new object[0]));
            other = new ControllerModel(typeof(OtherApiController).GetTypeInfo(), new object[0]);
            app.Controllers.Add(other);
            foreach (var c in ConventionsFor(enableWorkflow))
            {
                c.Apply(app);
            }
            return app;
        }

        [TestMethod]
        public void WorkflowDisabled_KeepsStub_RemovesController_LeavesOthers()
        {
            var app = ApplyRegisteredConventions(false, out var other);
            Assert.AreEqual(1, app.Controllers.Count(x => x.ControllerType.AsType() == typeof(WorkflowApiDisabledController)));
            Assert.AreEqual(0, app.Controllers.Count(x => x.ControllerType.AsType() == typeof(WorkflowApiController)));
            Assert.IsTrue(app.Controllers.Contains(other));
            Assert.AreEqual(2, app.Controllers.Count);
        }

        [TestMethod]
        public void WorkflowEnabled_RemovesStub_KeepsController_LeavesOthers()
        {
            var app = ApplyRegisteredConventions(true, out var other);
            Assert.AreEqual(0, app.Controllers.Count(x => x.ControllerType.AsType() == typeof(WorkflowApiDisabledController)));
            Assert.AreEqual(1, app.Controllers.Count(x => x.ControllerType.AsType() == typeof(WorkflowApiController)));
            Assert.IsTrue(app.Controllers.Contains(other));
            Assert.AreEqual(2, app.Controllers.Count);
        }

        [TestMethod]
        public void Registration_AddsStubRemoval_OnlyWhenWorkflowIsEnabled()
        {
            Assert.AreEqual(0, ConventionsFor(false).OfType<WorkflowApiStubRemovalConvention>().Count());
            Assert.AreEqual(1, ConventionsFor(true).OfType<WorkflowApiStubRemovalConvention>().Count());
        }

        [TestMethod]
        public void StubRemovalConvention_WithoutStub_ChangesNothing()
        {
            var app = new ApplicationModel();
            app.Controllers.Add(new ControllerModel(typeof(OtherApiController).GetTypeInfo(), new object[0]));
            new WorkflowApiStubRemovalConvention().Apply(app);
            Assert.AreEqual(1, app.Controllers.Count);
        }

        [TestMethod]
        public void Stub_ReturnsNotFound_IsPublic_NotAuthorized_HiddenFromSwagger()
        {
            var t = typeof(WorkflowApiDisabledController);
            Assert.IsFalse(typeof(IBaseController).IsAssignableFrom(t), "stub must not use WTMContext / data context");
            Assert.IsNull(t.GetCustomAttribute<AuthorizeJwtWithCookieAttribute>(true));
            Assert.AreEqual(0, t.GetCustomAttributes(true).OfType<IAuthorizeData>().Count());
            Assert.IsNotNull(t.GetCustomAttribute<PublicAttribute>(true));
            var api = t.GetCustomAttribute<ApiExplorerSettingsAttribute>(true);
            Assert.IsNotNull(api);
            Assert.IsTrue(api.IgnoreApi);

            var m = t.GetMethod("NotAvailable");
            Assert.IsNotNull(m);
            Assert.IsNotNull(m.GetCustomAttribute<PublicAttribute>(true));
            Assert.IsNotNull(m.GetCustomAttribute<NoLogAttribute>(true));
            Assert.AreEqual(0, m.GetCustomAttributes(true).OfType<IAuthorizeData>().Count());
            var routes = m.GetCustomAttributes<RouteAttribute>().Select(x => x.Template).ToList();
            CollectionAssert.Contains(routes, "_WorkflowApi");
            CollectionAssert.Contains(routes, "_WorkflowApi/{**path}");
            var verbs = m.GetCustomAttribute<AcceptVerbsAttribute>().HttpMethods.ToList();
            foreach (var v in new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" })
            {
                CollectionAssert.Contains(verbs, v);
            }

            var result = new WorkflowApiDisabledController().NotAvailable("GetWorkflowUsers");
            Assert.IsInstanceOfType(result, typeof(NotFoundResult));
        }

        private class OtherApiController : ControllerBase
        {
        }
    }
}
