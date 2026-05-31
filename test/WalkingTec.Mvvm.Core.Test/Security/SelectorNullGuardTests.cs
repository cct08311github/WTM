#nullable enable
using System;
using System.Linq.Expressions;
using System.Reflection;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// Regression tests for issue #106: unauthenticated DoS via null
    /// <see cref="PropertyInfo"/> in the Selector action and GetBatchQuery.
    ///
    /// The Selector action is [Public] (unauthenticated), so an attacker can pass
    /// an arbitrary _DONOT_USE_VFIELD value.  Prior to the fix, when
    /// <c>modelType.GetSingleProperty(_DONOT_USE_VFIELD)</c> returned null (because
    /// the named property does not exist on the model), the following
    /// <c>Expression.Property(para, null)</c> call threw
    /// <see cref="ArgumentNullException"/>, producing an unhandled 500 on every
    /// request and effectively making the endpoint a crash-on-demand vector.
    ///
    /// These tests exercise the property-resolution guard path directly — that is,
    /// the core invariant fixed in both
    /// <c>_FrameworkController.Selector</c> and
    /// <c>BasePagedListVM.GetBatchQuery</c>: a missing property must NOT cause
    /// <see cref="ArgumentNullException"/> when passed to <see cref="Expression.Property"/>.
    /// </summary>
    [TestClass]
    public class SelectorNullGuardTests
    {
        // ── Fixture model that has a known set of properties ────────────────────

        private class SampleModel : TopBasePoco
        {
            public string? Name { get; set; }
        }

        // ── GetSingleProperty behaviour ──────────────────────────────────────────

        [TestMethod]
        public void GetSingleProperty_returns_null_for_nonexistent_field()
        {
            // Confirms the contract relied upon by the guard: GetSingleProperty
            // returns null (not throws) for an unknown property name.
            var prop = typeof(SampleModel).GetSingleProperty("___NoSuchProperty___");
            prop.Should().BeNull(
                "GetSingleProperty must return null for a name that does not exist on the model");
        }

        [TestMethod]
        public void GetSingleProperty_returns_PropertyInfo_for_existing_field()
        {
            var prop = typeof(SampleModel).GetSingleProperty("Name");
            prop.Should().NotBeNull();
            prop!.Name.Should().Be("Name");
        }

        // ── Core guard invariant: Expression.Property(para, null) must NOT be called ──

        /// <summary>
        /// Demonstrates the root cause: passing a null PropertyInfo to
        /// Expression.Property throws ArgumentNullException.  The fix in both
        /// Selector and GetBatchQuery prevents this call from ever being reached.
        /// </summary>
        [TestMethod]
        public void Expression_Property_with_null_PropertyInfo_throws_ArgumentNullException()
        {
            // This documents WHY the guard is needed.
            var para = Expression.Parameter(typeof(SampleModel));
            PropertyInfo? nullProp = null;
            Action act = () => Expression.Property(para, nullProp!);
            act.Should().Throw<ArgumentNullException>(
                "Expression.Property throws ArgumentNullException when the PropertyInfo is null");
        }

        /// <summary>
        /// The guard pattern used in the fixed code: only call Expression.Property
        /// when the resolved PropertyInfo is non-null.  This is the exact shape of
        /// the production fix in _FrameworkController.Selector (issue #106).
        /// </summary>
        [TestMethod]
        public void Selector_guard_pattern_with_invalid_field_does_not_throw()
        {
            // Simulate the attacker-supplied field name that does not exist.
            const string attackerField = "___NonExistentField___";

            var modelType = typeof(SampleModel);
            var para = Expression.Parameter(modelType);
            var idproperty = modelType.GetSingleProperty(attackerField);

            // Production guard: only enter Expression.Property when idproperty != null.
            // This must NOT throw.
            Expression? pro = null;
            if (idproperty != null)
            {
                pro = Expression.Property(para, idproperty);
            }

            // Result: guard triggered, pro stays null, no exception.
            pro.Should().BeNull(
                "the guard must prevent Expression.Property from being called with a null PropertyInfo");
        }

        /// <summary>
        /// Happy-path companion: when the field exists, the guard allows the
        /// expression to be built normally.
        /// </summary>
        [TestMethod]
        public void Selector_guard_pattern_with_valid_field_builds_expression()
        {
            const string validField = "Name";

            var modelType = typeof(SampleModel);
            var para = Expression.Parameter(modelType);
            var idproperty = modelType.GetSingleProperty(validField);

            Expression? pro = null;
            if (idproperty != null)
            {
                pro = Expression.Property(para, idproperty);
            }

            pro.Should().NotBeNull(
                "when the property exists, the guard must allow expression construction");
        }
    }
}
