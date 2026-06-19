#nullable enable
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Helper;

namespace WalkingTec.Mvvm.Core.Test.Helper
{
    /// <summary>
    /// Regression tests for Issue #381 fix #12:
    /// PropertyHelper.GetPropertyName had null-forgiving casts (!) on the
    /// get_Item branch that caused NRE when the indexer argument was not a
    /// captured-variable MemberExpression, or when Object was not a
    /// MemberExpression. The fix replaces them with explicit null checks
    /// plus <c>break</c>.
    /// </summary>
    [TestClass]
    public class PropertyHelperIndexerTests
    {
        private class Container
        {
            public Dictionary<string, string?> Items { get; set; } = new();
            public string Name { get; set; } = "";
        }

        // ── Happy-path tests — must still work after the fix ─────────────────

        /// <summary>
        /// Simple property access must still return the property name.
        /// </summary>
        [TestMethod]
        public void GetPropertyName_SimpleMemberAccess_ReturnsName()
        {
            Expression<Func<Container, string>> expr = x => x.Name;
            var name = expr.GetPropertyName();
            Assert.AreEqual("Name", name);
        }

        /// <summary>
        /// Constant-expression indexer (captured closure variable "key") —
        /// this is the original happy path that must continue to work.
        /// </summary>
        [TestMethod]
        public void GetPropertyName_ConstantIndexer_DoesNotThrow()
        {
            var key = "mykey";
            Expression<Func<Container, string?>> expr = x => x.Items[key];
            // Must not throw; returns something like "Items[mykey]."
            string? name = null;
            Assert.IsTrue(DoesNotThrowNRE(() => name = expr.GetPropertyName()),
                "Fix #12: constant-indexer path must not throw NRE.");
            Assert.IsNotNull(name);
        }

        // ── Regression tests for the null-forgiving cast NRE paths ───────────

        /// <summary>
        /// An indexer where the argument is a parameter (not a closure constant):
        /// e.g., the expression is compiled from <c>c => c.Items[k]</c> where
        /// <c>k</c> is a lambda parameter, not a captured field.
        ///
        /// The argument's Expression is a ParameterExpression, not a
        /// ConstantExpression, so the null-forgiving cast
        /// <c>((mex4.Expression as ConstantExpression)!)</c> would throw NRE.
        ///
        /// After fix #12 the code does a null check and breaks early.
        /// </summary>
        [TestMethod]
        public void GetPropertyName_ParameterBasedIndexer_DoesNotThrowNRE()
        {
            // Build: (Container c, string k) => c.Items[k]
            // The indexer argument "k" is a ParameterExpression — NOT a constant.
            var cParam = Expression.Parameter(typeof(Container), "c");
            var kParam = Expression.Parameter(typeof(string), "k");
            var itemsProp = Expression.Property(cParam, nameof(Container.Items));
            var getItem = typeof(Dictionary<string, string?>)
                .GetMethod("get_Item", new[] { typeof(string) })!;
            var indexCall = Expression.Call(itemsProp, getItem, kParam);

            // Wrap in a MethodCallExpression accessible via GetPropertyName.
            // GetPropertyName processes lambda bodies — build a LambdaExpression
            // whose Body contains the index access as the outermost node.
            // Actually GetPropertyName walks from a leaf MemberExpression upward;
            // to trigger the bug path we need the get_Item call to appear in the
            // *body* of the expression (as le.Body or as me.Expression in the walk).
            // The simpler repro: call GetPropertyName directly on the Call expression.
            Assert.IsTrue(DoesNotThrowNRE(() => indexCall.GetPropertyName()),
                "Fix #12: parameter-based indexer must not throw NRE.");
        }

        /// <summary>
        /// An indexer where <c>mexp.Object</c> is NOT a MemberExpression
        /// (e.g. it's a ConstantExpression holding the dictionary directly).
        ///
        /// The null-forgiving cast <c>(mexp.Object as MemberExpression)!</c>
        /// would throw NRE. After fix #12 the code null-checks and breaks.
        /// </summary>
        [TestMethod]
        public void GetPropertyName_NonMemberExpressionObject_DoesNotThrowNRE()
        {
            // Build: dict["key"] where dict is a ConstantExpression (not a Member).
            var dict = new Dictionary<string, string?> { ["x"] = "val" };
            var constDict = Expression.Constant(dict, typeof(Dictionary<string, string?>));
            var getItem = typeof(Dictionary<string, string?>)
                .GetMethod("get_Item", new[] { typeof(string) })!;
            var indexCall = Expression.Call(constDict, getItem, Expression.Constant("x"));

            Assert.IsTrue(DoesNotThrowNRE(() => indexCall.GetPropertyName()),
                "Fix #12: non-MemberExpression object on get_Item must not throw NRE.");
        }

        // ── Helper ────────────────────────────────────────────────────────────

        private static bool DoesNotThrowNRE(Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (NullReferenceException)
            {
                return false;
            }
            catch
            {
                // Any exception other than NRE is acceptable for this test
                return true;
            }
        }
    }
}
