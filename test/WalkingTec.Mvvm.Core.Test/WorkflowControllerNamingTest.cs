using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Asp.Versioning.Conventions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>A controller whose name ends in a digit, like the Vue3Demo SchoolVue3 (#294).</summary>
    [ApiController]
    [Route("api/[controller]")]
    public class Xxx3Controller : ControllerBase
    {
        [HttpGet("[action]")]
        public IActionResult Ping() => Ok();
    }

    /// <summary>The same for an MVC style controller (not an [ApiController]).</summary>
    public class Yyy7Controller : Controller
    {
        public IActionResult Index() => Content("x");
    }

    /// <summary>
    /// #294: with EnableWorkflow=true AddWtmWorkflow registers Elsa's API versioning, whose default controller name convention strips the
    /// trailing digits of every [ApiController] name (SchoolVue3 became SchoolVue). Enabling workflow must not change the route or
    /// controller name of controllers that are not Elsa's.
    /// </summary>
    [TestClass]
    public class WorkflowControllerNamingTest
    {
        private class TypesPart : ApplicationPart, IApplicationPartTypeProvider
        {
            private readonly Type[] _types;
            public TypesPart(params Type[] types) { _types = types; }
            public override string Name => "WorkflowControllerNamingTest";
            public IEnumerable<TypeInfo> Types => _types.Select(x => x.GetTypeInfo());
        }

        private static IConfiguration WorkflowConfig()
        {
            return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["EnableWorkflow"] = "true",
                ["Connections:0:Key"] = "default",
                ["Connections:0:DbType"] = "Memory",
                ["Connections:0:Value"] = "wf-naming-" + Guid.NewGuid(),
            }).Build();
        }

        private static IReadOnlyList<ControllerActionDescriptor> Describe(bool workflow, Action<IServiceCollection> before = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(new Mock<IWebHostEnvironment>().Object);
            services.AddSingleton<IHostEnvironment>(sp => sp.GetRequiredService<IWebHostEnvironment>());
            services.AddSingleton(new System.Diagnostics.DiagnosticListener("test"));
            services.AddSingleton<System.Diagnostics.DiagnosticSource>(sp => sp.GetRequiredService<System.Diagnostics.DiagnosticListener>());
            before?.Invoke(services);
            if (workflow)
            {
                services.AddWtmWorkflow(WorkflowConfig());
            }
            services.AddMvc().ConfigureApplicationPartManager(m =>
            {
                m.ApplicationParts.Clear();
                m.ApplicationParts.Add(new TypesPart(typeof(Xxx3Controller), typeof(Yyy7Controller)));
                if (workflow)
                {
                    m.ApplicationParts.Add(new AssemblyPart(typeof(Elsa.Server.Api.Endpoints.Activities.List).Assembly));
                }
            });
            var sp = services.BuildServiceProvider();
            return sp.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
                .OfType<ControllerActionDescriptor>().ToList();
        }

        private static string RouteController(IEnumerable<ControllerActionDescriptor> all, Type controller)
        {
            return all.Single(x => x.ControllerTypeInfo.AsType() == controller && x.ActionName != null).RouteValues["controller"];
        }

        [TestMethod]
        public void WithoutWorkflow_ControllerNamesEndingInADigitAreKept()
        {
            var all = Describe(false);
            Assert.AreEqual("Xxx3", RouteController(all, typeof(Xxx3Controller)));
            Assert.AreEqual("Yyy7", RouteController(all, typeof(Yyy7Controller)));
        }

        [TestMethod]
        public void WithWorkflow_ApiControllerNamesEndingInADigitAreKept()
        {
            var all = Describe(true);
            Assert.AreEqual("Xxx3", RouteController(all, typeof(Xxx3Controller)));
            var action = all.Single(x => x.ControllerTypeInfo.AsType() == typeof(Xxx3Controller));
            Assert.AreEqual("api/Xxx3/Ping", action.AttributeRouteInfo.Template);
        }

        [TestMethod]
        public void WithWorkflow_MvcControllerNamesEndingInADigitAreKept()
        {
            var all = Describe(true);
            Assert.AreEqual("Yyy7", RouteController(all, typeof(Yyy7Controller)));
        }

        [TestMethod]
        public void WithWorkflow_ElsaControllersKeepTheirNamesAndVersionedRoutes()
        {
            var all = Describe(true);
            var elsa = all.Where(x => x.ControllerTypeInfo.FullName.StartsWith(WorkflowApiSecurity.ApiNamespacePrefix, StringComparison.Ordinal)).ToList();
            Assert.IsTrue(elsa.Count > 20, "the Elsa API endpoints are registered");

            // Elsa's controller is a class named like its action group: List in Elsa.Server.Api.Endpoints.Activities
            var activities = elsa.Single(x => x.ControllerTypeInfo.AsType() == typeof(Elsa.Server.Api.Endpoints.Activities.List));
            Assert.AreEqual("List", activities.RouteValues["controller"]);
            Assert.AreEqual("v{version:apiVersion}/activities", activities.AttributeRouteInfo.Template);
            // none of Elsa's controllers lost anything: the route value is the class name
            foreach (var d in elsa)
            {
                Assert.AreEqual(d.ControllerTypeInfo.Name, d.RouteValues["controller"], d.ControllerTypeInfo.FullName);
            }
            // API versioning is still applied to them
            Assert.IsTrue(elsa.All(x => x.EndpointMetadata.OfType<Asp.Versioning.ApiVersionMetadata>().Any()), "Elsa endpoints keep their API version metadata");
        }

        [TestMethod]
        public void WithWorkflow_ACustomControllerNameConventionRegisteredByTheApplicationIsNotReplaced()
        {
            var custom = new CustomConvention();
            var all = Describe(true, services => services.AddSingleton<IControllerNameConvention>(custom));
            Assert.AreEqual("Xxx3-custom", RouteController(all, typeof(Xxx3Controller)));
        }

        private class CustomConvention : IControllerNameConvention
        {
            public string NormalizeName(string controllerName) => controllerName + "-custom";
            public string GroupName(string controllerName) => controllerName;
        }
    }
}
