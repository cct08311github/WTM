#nullable enable
using System.Collections.Generic;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc.Binders;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Tests for NewtonsoftJsonFormatterAttribute.Apply(ActionModel) and
    /// Apply(ControllerModel).  OnActionExecuted requires a full MVC pipeline
    /// and is not covered here.
    /// </summary>
    [TestClass]
    public class NewtonsoftJsonFormatterAttributeTests
    {
        private static NewtonsoftJsonFormatterAttribute MakeAttr() =>
            new NewtonsoftJsonFormatterAttribute();

        // ── Helper to build an ActionModel with a body-bound parameter ────────

        private static ActionModel MakeActionModel(bool withBodyParam)
        {
            // Use a real MethodInfo as the backing so ActionModel ctor is satisfied
            var mi = typeof(NewtonsoftJsonFormatterAttributeTests)
                .GetMethod(nameof(MakeActionModel), BindingFlags.NonPublic | BindingFlags.Static)!;
            var actionModel = new ActionModel(mi, new List<object>());

            if (withBodyParam)
            {
                var pi = mi.GetParameters()[0]; // bool withBodyParam
                var pModel = new ParameterModel(pi, new List<object>())
                {
                    BindingInfo = new BindingInfo
                    {
                        BindingSource = BindingSource.Body
                    }
                };
                actionModel.Parameters.Add(pModel);
            }

            return actionModel;
        }

        // ── Apply(ActionModel) ────────────────────────────────────────────────

        [TestMethod]
        public void Apply_action_sets_BinderType_for_body_bound_parameters()
        {
            var attr   = MakeAttr();
            var action = MakeActionModel(withBodyParam: true);

            attr.Apply(action);

            var p = action.Parameters[0];
            Assert.AreEqual(typeof(NewtonsoftJsonBodyModelBinder), p.BindingInfo!.BinderType,
                "Body-bound parameter should have BinderType set to NewtonsoftJsonBodyModelBinder");
        }

        [TestMethod]
        public void Apply_action_does_not_alter_non_body_parameters()
        {
            var attr   = MakeAttr();
            var action = MakeActionModel(withBodyParam: false);
            // Add a query-bound parameter
            var mi = typeof(NewtonsoftJsonFormatterAttributeTests)
                .GetMethod(nameof(MakeActionModel), BindingFlags.NonPublic | BindingFlags.Static)!;
            var pi     = mi.GetParameters()[0];
            var pModel = new ParameterModel(pi, new List<object>())
            {
                BindingInfo = new BindingInfo
                {
                    BindingSource = BindingSource.Query
                }
            };
            action.Parameters.Add(pModel);

            attr.Apply(action);

            Assert.IsNull(pModel.BindingInfo.BinderType,
                "Query-bound parameter's BinderType should remain null");
        }

        [TestMethod]
        public void Apply_action_with_null_BindingInfo_does_not_throw()
        {
            var attr   = MakeAttr();
            var action = MakeActionModel(withBodyParam: false);
            var mi     = typeof(NewtonsoftJsonFormatterAttributeTests)
                .GetMethod(nameof(MakeActionModel), BindingFlags.NonPublic | BindingFlags.Static)!;
            var pi     = mi.GetParameters()[0];
            // BindingInfo == null → should be silently skipped
            var pModel = new ParameterModel(pi, new List<object>())
            {
                BindingInfo = null
            };
            action.Parameters.Add(pModel);

            // Should not throw
            attr.Apply(action);
        }

        [TestMethod]
        public void Apply_action_empty_parameters_does_not_throw()
        {
            var attr   = MakeAttr();
            var action = MakeActionModel(withBodyParam: false);
            attr.Apply(action); // no parameters
        }

        // ── Apply(ControllerModel) ────────────────────────────────────────────

        [TestMethod]
        public void Apply_controller_delegates_to_each_action()
        {
            var attr       = MakeAttr();
            var typeInfo   = typeof(NewtonsoftJsonFormatterAttributeTests).GetTypeInfo();
            var controller = new ControllerModel(typeInfo, new List<object>());

            var action1 = MakeActionModel(withBodyParam: true);
            var action2 = MakeActionModel(withBodyParam: true);
            controller.Actions.Add(action1);
            controller.Actions.Add(action2);

            attr.Apply(controller);

            Assert.AreEqual(typeof(NewtonsoftJsonBodyModelBinder), action1.Parameters[0].BindingInfo!.BinderType);
            Assert.AreEqual(typeof(NewtonsoftJsonBodyModelBinder), action2.Parameters[0].BindingInfo!.BinderType);
        }

        [TestMethod]
        public void Apply_controller_with_no_actions_does_not_throw()
        {
            var attr       = MakeAttr();
            var typeInfo   = typeof(NewtonsoftJsonFormatterAttributeTests).GetTypeInfo();
            var controller = new ControllerModel(typeInfo, new List<object>());
            attr.Apply(controller); // no actions
        }

        // ── Attribute is ActionFilterAttribute ────────────────────────────────

        [TestMethod]
        public void Attribute_inherits_ActionFilterAttribute()
        {
            var attr = MakeAttr();
            Assert.IsInstanceOfType<ActionFilterAttribute>(attr);
        }

        // ── Attribute implements both conventions ─────────────────────────────

        [TestMethod]
        public void Attribute_implements_IControllerModelConvention()
        {
            var attr = MakeAttr();
            Assert.IsInstanceOfType<IControllerModelConvention>(attr);
        }

        [TestMethod]
        public void Attribute_implements_IActionModelConvention()
        {
            var attr = MakeAttr();
            Assert.IsInstanceOfType<IActionModelConvention>(attr);
        }
    }
}
