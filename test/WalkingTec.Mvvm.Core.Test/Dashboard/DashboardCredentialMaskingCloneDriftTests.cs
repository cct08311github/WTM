#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Dashboard;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    /// <summary>
    /// Issue #957 follow-up: <see cref="DashboardCredentialMasking.MaskForRead"/> and its private helper
    /// <c>MaskWidgetHeaders</c> hand-copy every property of FOUR types — <see cref="DashboardDefinition"/>,
    /// <see cref="WidgetDefinition"/>, <see cref="WidgetSourceDefinition"/>,
    /// <see cref="RestWidgetDataSourceOptions"/> — when building the masked copy. That copy is complete
    /// TODAY (independently verified: 13/6/7/11 public get-properties respectively, all assigned in
    /// <c>DashboardCredentialMasking.cs</c>'s object initializers). <b>Nothing keeps it complete.</b> Add a
    /// property to any of those four types and forget to add the matching line to the hand-written clone,
    /// and <c>MaskForRead</c> silently returns that property at its CLR default on the masked copy —
    /// masking becomes silent data-erasure for that field, and any caller doing read-modify-write (e.g. a
    /// future editor UI that round-trips a fetched dashboard) writes that erasure back into the store. No
    /// other test in this repo goes red for that: <c>DashboardCredentialMaskingTests</c>/
    /// <c>DashboardCredentialMaskingEndToEndTests</c> only ever assert on the small, fixed set of
    /// properties they were written against.
    ///
    /// <para>
    /// <b>This class exists ONLY to catch that drift — it is not behaviour verification, and it is NOT
    /// redundant with the masking tests above.</b> It answers a different question ("is the hand-written
    /// clone still complete for every property these four types currently declare, including ones added
    /// after this test was written"), which a fixed set of hand-picked assertions structurally cannot
    /// answer. Do not delete this class as a duplicate of the masking behaviour tests.
    /// </para>
    ///
    /// <para>
    /// <b>Mechanism</b>: <see cref="PropertyGraphPopulator"/> reflects over the full object graph reachable
    /// from a fresh <see cref="DashboardDefinition"/> — which includes all four target types nested inside
    /// each other, plus every other POCO they reference (<see cref="SharingDefinition"/>,
    /// <see cref="LayoutItem"/>, etc.) — and sets EVERY public writable property, at every level, to a
    /// distinctive non-default value, recursing into nested POCOs/collections/dictionaries so nothing is
    /// left at a CLR default (a property left at its default could never reveal a clone that silently
    /// drops it — the populator throws rather than silently leaving an unrecognized property type
    /// unpopulated, for the same reason). <see cref="PropertyGraphComparer"/> then reflects over the
    /// original and the <c>MaskForRead</c>'d copy in parallel and asserts every property is equal, with
    /// exactly one documented exception: <see cref="RestWidgetDataSourceOptions.Headers"/> VALUES, which
    /// must equal <see cref="DashboardCredentialMasking.MaskedHeaderValue"/> (keys unchanged). Because both
    /// walk the graph by reflection rather than a hand-written property list, a property added to any of
    /// the four types tomorrow is automatically populated and compared — the clone code's author has never
    /// heard of it, so if it is not also added to the clone, this test fails without anyone updating it.
    /// </para>
    /// </summary>
    [TestClass]
    public class DashboardCredentialMaskingCloneDriftTests
    {
        [TestMethod]
        public void MaskForRead_preserves_every_property_of_the_four_hand_cloned_types_except_masked_header_values()
        {
            var original = new DashboardDefinition();
            var counter = 0;
            PropertyGraphPopulator.Populate(original, ref counter, depth: 0);

            // Premise check: the masking path must actually clone (not take the no-op fast path that
            // returns the SAME instance when nothing has headers) for this test to prove anything.
            original.Widgets.Should().NotBeEmpty("the populator must have produced at least one widget");
            var firstWidget = original.Widgets.Values.First();
            firstWidget.Source.Should().NotBeNull();
            firstWidget.Source!.RestOptions.Should().NotBeNull();
            firstWidget.Source.RestOptions!.Headers.Should().NotBeNullOrEmpty(
                "the populator must have produced a non-empty Headers dictionary, or MaskForRead takes " +
                "its no-op fast path and returns the SAME instance — which would make this test vacuous");

            var masked = DashboardCredentialMasking.MaskForRead(original);

            masked.Should().NotBeSameAs(original,
                "masking must have actually cloned given the non-empty Headers premise checked above");

            var mismatches = new List<string>();
            PropertyGraphComparer.Compare(original, masked, "DashboardDefinition", mismatches, currentProperty: null);

            mismatches.Should().BeEmpty(
                "MaskForRead's hand-written clone (DashboardCredentialMasking.cs) must copy every property " +
                "of DashboardDefinition/WidgetDefinition/WidgetSourceDefinition/RestWidgetDataSourceOptions " +
                "unchanged, except RestWidgetDataSourceOptions.Headers VALUES (masked to " +
                "DashboardCredentialMasking.MaskedHeaderValue, keys unchanged) — a mismatch here means a " +
                "property was added to one of those four types without a matching line being added to the " +
                "clone, so MaskForRead now silently erases it on read. Mismatches:\n" +
                string.Join("\n", mismatches));
        }

        /// <summary>
        /// Reflects over a POCO graph and sets every public writable property, recursively, to a
        /// distinctive non-default value. See the class doc above for why. Deliberately THROWS (does not
        /// silently skip) on a property type it has no strategy for — an unpopulated property would stay
        /// at its CLR default, which would make the drift guard unable to detect that same property being
        /// dropped by a future clone, silently defeating this test's entire purpose.
        /// </summary>
        /// <remarks>
        /// Adversarial review finding F8 (#960): a naive <c>bool</c> generator that always returns
        /// <c>true</c> is populate-to-default-blind for any FUTURE <c>bool</c> property whose own
        /// declared default is already <c>true</c> (e.g. <c>public bool Foo {'{'} get; set; {'}'} = true;</c>)
        /// — "populating" it with <c>true</c> would be indistinguishable from leaving it untouched, so a
        /// future clone that drops that property would show the SAME value on both sides and this guard
        /// would not detect it. Fixed generally, not by special-casing <c>bool</c>: for every property,
        /// <see cref="Populate"/> constructs a FRESH default instance of the declaring type, reads that
        /// property's own actual default from it, and retries <see cref="CreateValue"/> (which is
        /// counter/index-driven for every type that has more than one possible value — <c>bool</c>
        /// alternates, <c>enum</c> cycles through every member) until the generated candidate provably
        /// differs from that property's specific default — not just from "the CLR zero value" as an
        /// earlier version of this populator assumed. A property whose type genuinely cannot produce a
        /// second distinct value (e.g. a hypothetical single-member enum) fails loudly after a bounded
        /// number of attempts, per this class's own established principle: an unpopulated-to-default
        /// property can never prove a future clone drops it, so silently accepting one defeats this guard
        /// exactly as silently skipping an unknown type would.
        /// </remarks>
        private static class PropertyGraphPopulator
        {
            private const int MaxDepth = 12;
            private const int MaxDistinctFromDefaultAttempts = 8;

            public static void Populate(object instance, ref int counter, int depth)
            {
                if (depth > MaxDepth)
                {
                    throw new InvalidOperationException(
                        $"PropertyGraphPopulator recursion exceeded {MaxDepth} levels — either a genuine " +
                        "cycle exists in this object graph (unexpected for the Dashboard model types) or " +
                        "a new type needs a fast-path case added above the generic POCO recursion branch " +
                        "in CreateValue.");
                }

                var type = instance.GetType();
                // F8: a FRESH, untouched instance of this exact type — its properties' values ARE each
                // property's own real default (not merely "the CLR zero value"), including e.g. a
                // hypothetical `public bool Foo { get; set; } = true;`.
                var freshDefault = Activator.CreateInstance(type)
                    ?? throw new InvalidOperationException($"Could not construct a fresh default instance of '{type.FullName}'.");

                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.GetIndexParameters().Length > 0) continue; // indexers
                    if (prop.SetMethod is not { IsPublic: true }) continue;

                    var defaultValue = prop.GetValue(freshDefault);

                    object? candidate = null;
                    var attempt = 0;
                    do
                    {
                        candidate = CreateValue(prop.PropertyType, ref counter, depth + 1);
                        attempt++;
                    } while (attempt < MaxDistinctFromDefaultAttempts && DeepEquals(candidate, defaultValue, 0));

                    if (DeepEquals(candidate, defaultValue, 0))
                    {
                        throw new NotSupportedException(
                            $"PropertyGraphPopulator could not generate a value for '{type.FullName}.{prop.Name}' " +
                            $"that differs from its own default after {MaxDistinctFromDefaultAttempts} attempts " +
                            "(F8, issue #960): a value indistinguishable from the default can never reveal a " +
                            "future clone silently dropping this property. Extend CreateValue so this " +
                            "property's type can vary, or special-case it explicitly.");
                    }

                    prop.SetValue(instance, candidate);
                }
            }

            /// <summary>
            /// Purpose-built structural equality for the F8 default-avoidance check ONLY — deliberately
            /// NOT <see cref="PropertyGraphComparer.Compare"/>, which has masked-Headers-specific
            /// semantics that make no sense when comparing "freshly generated candidate" against "this
            /// property's own bare default" (a different question from "original vs masked copy").
            /// </summary>
            private static bool DeepEquals(object? a, object? b, int depth)
            {
                if (depth > MaxDepth) return false; // defensive; same rationale as Populate's own cap
                if (a == null && b == null) return true;
                if (a == null || b == null) return false;

                var type = a.GetType();
                if (type != b.GetType()) return false;

                if (type == typeof(string) || type.IsValueType)
                    return Equals(a, b);

                if (a is IDictionary aDict)
                {
                    var bDict = (IDictionary)b;
                    if (aDict.Count != bDict.Count) return false;
                    foreach (DictionaryEntry entry in aDict)
                    {
                        if (!bDict.Contains(entry.Key)) return false;
                        if (!DeepEquals(entry.Value, bDict[entry.Key], depth + 1)) return false;
                    }
                    return true;
                }

                if (a is IEnumerable aEnumerable)
                {
                    var aList = aEnumerable.Cast<object?>().ToList();
                    var bList = ((IEnumerable)b).Cast<object?>().ToList();
                    if (aList.Count != bList.Count) return false;
                    for (var i = 0; i < aList.Count; i++)
                    {
                        if (!DeepEquals(aList[i], bList[i], depth + 1)) return false;
                    }
                    return true;
                }

                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    if (prop.GetMethod is not { IsPublic: true }) continue;
                    if (!DeepEquals(prop.GetValue(a), prop.GetValue(b), depth + 1)) return false;
                }
                return true;
            }

            private static object? CreateValue(Type type, ref int counter, int depth)
            {
                var underlying = Nullable.GetUnderlyingType(type);
                if (underlying != null) type = underlying;

                if (type == typeof(string))
                    return $"drift-{counter++}";
                if (type == typeof(int))
                    return 1000 + counter++;
                if (type == typeof(bool))
                    // F8: must be able to produce EITHER value on retry (a property whose own default
                    // is `true` needs this to eventually hand back `false`, and vice versa) — a
                    // generator that always returns the same value cannot satisfy Populate's
                    // default-avoidance retry loop above.
                    return (counter++ % 2) == 0;
                if (type == typeof(double))
                    return 1000.5 + counter++;
                if (type == typeof(DateTime))
                    return new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(counter++);

                if (type.IsEnum)
                {
                    // F8: cycle through every declared member by counter (not "always the first
                    // non-CLR-zero member") so a retry can find one that differs from THIS property's
                    // own default, which may not be the CLR zero value.
                    var values = Enum.GetValues(type);
                    if (values.Length == 0)
                        throw new NotSupportedException($"Enum '{type.FullName}' has no members to populate with.");
                    return values.GetValue(counter++ % values.Length);
                }

                if (type.IsArray)
                {
                    var elementType = type.GetElementType()!;
                    var array = Array.CreateInstance(elementType, 2);
                    array.SetValue(CreateValue(elementType, ref counter, depth), 0);
                    array.SetValue(CreateValue(elementType, ref counter, depth), 1);
                    return array;
                }

                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                {
                    var args = type.GetGenericArguments();
                    var keyType = args[0];
                    var valueType = args[1];
                    var dict = (IDictionary)Activator.CreateInstance(type)!;
                    for (var i = 0; i < 2; i++)
                    {
                        object key = keyType == typeof(string)
                            ? $"drift-key-{counter++}"
                            : CreateValue(keyType, ref counter, depth)!;
                        // Dictionary<string, object?> (WidgetDefinition.Config): "populate a value of
                        // static type object" makes no sense generically — inject a distinctive string.
                        object? value = valueType == typeof(object)
                            ? $"drift-objval-{counter++}"
                            : CreateValue(valueType, ref counter, depth);
                        dict.Add(key, value);
                    }
                    return dict;
                }

                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var elementType = type.GetGenericArguments()[0];
                    var list = (IList)Activator.CreateInstance(type)!;
                    list.Add(CreateValue(elementType, ref counter, depth));
                    list.Add(CreateValue(elementType, ref counter, depth));
                    return list;
                }

                if (type.IsClass && type.GetConstructor(Type.EmptyTypes) != null)
                {
                    var nested = Activator.CreateInstance(type)!;
                    Populate(nested, ref counter, depth);
                    return nested;
                }

                throw new NotSupportedException(
                    $"PropertyGraphPopulator has no strategy for type '{type.FullName}'. If a new property " +
                    "of this type was added to DashboardDefinition/WidgetDefinition/WidgetSourceDefinition/" +
                    "RestWidgetDataSourceOptions (or a type they reference), extend CreateValue in " +
                    "DashboardCredentialMaskingCloneDriftTests.cs rather than letting this property go " +
                    "unpopulated — an unpopulated property stays at its CLR default, and a default value " +
                    "can never reveal that a future clone silently drops it.");
            }
        }

        /// <summary>
        /// Reflects over two parallel POCO graphs (an original and its <c>MaskForRead</c>'d copy) and
        /// asserts every property is equal, with exactly one documented exception — see the class doc
        /// above.
        /// </summary>
        private static class PropertyGraphComparer
        {
            public static void Compare(
                object? expected, object? actual, string path, List<string> mismatches, PropertyInfo? currentProperty)
            {
                if (IsMaskedHeadersProperty(currentProperty))
                {
                    CompareMaskedHeaders((IDictionary?)expected, (IDictionary?)actual, path, mismatches);
                    return;
                }

                if (expected == null && actual == null) return;
                if (expected == null || actual == null)
                {
                    mismatches.Add($"{path}: expected {Describe(expected)}, actual {Describe(actual)}");
                    return;
                }

                var type = expected.GetType();

                // string / int / bool / double / DateTime / enum — anything that carries its own value
                // equality and has no properties worth walking into.
                if (type == typeof(string) || type.IsValueType)
                {
                    if (!Equals(expected, actual))
                        mismatches.Add($"{path}: expected {Describe(expected)}, actual {Describe(actual)}");
                    return;
                }

                if (expected is IDictionary expectedDict)
                {
                    var actualDict = (IDictionary)actual;
                    if (expectedDict.Count != actualDict.Count)
                    {
                        mismatches.Add($"{path}: expected {expectedDict.Count} entries, actual {actualDict.Count}");
                        return;
                    }
                    foreach (DictionaryEntry entry in expectedDict)
                    {
                        if (!actualDict.Contains(entry.Key))
                        {
                            mismatches.Add($"{path}[{entry.Key}]: key missing on the masked side");
                            continue;
                        }
                        Compare(entry.Value, actualDict[entry.Key], $"{path}[{entry.Key}]", mismatches, currentProperty: null);
                    }
                    return;
                }

                if (expected is IEnumerable expectedEnumerable)
                {
                    var expectedList = expectedEnumerable.Cast<object?>().ToList();
                    var actualList = ((IEnumerable)actual).Cast<object?>().ToList();
                    if (expectedList.Count != actualList.Count)
                    {
                        mismatches.Add($"{path}: expected {expectedList.Count} elements, actual {actualList.Count}");
                        return;
                    }
                    for (var i = 0; i < expectedList.Count; i++)
                        Compare(expectedList[i], actualList[i], $"{path}[{i}]", mismatches, currentProperty: null);
                    return;
                }

                // POCO: recurse into every public readable property.
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    if (prop.GetMethod is not { IsPublic: true }) continue;

                    var expectedValue = prop.GetValue(expected);
                    var actualValue = prop.GetValue(actual);
                    Compare(expectedValue, actualValue, $"{path}.{prop.Name}", mismatches, currentProperty: prop);
                }
            }

            private static bool IsMaskedHeadersProperty(PropertyInfo? property)
                => property is { Name: nameof(RestWidgetDataSourceOptions.Headers) } &&
                   property.DeclaringType == typeof(RestWidgetDataSourceOptions);

            private static void CompareMaskedHeaders(
                IDictionary? expected, IDictionary? actual, string path, List<string> mismatches)
            {
                if (expected == null && actual == null) return;
                if (expected == null || actual == null)
                {
                    mismatches.Add($"{path}: expected {Describe(expected)}, actual {Describe(actual)}");
                    return;
                }
                if (expected.Count != actual.Count)
                {
                    mismatches.Add($"{path}: expected {expected.Count} header keys, actual {actual.Count}");
                    return;
                }
                foreach (DictionaryEntry entry in expected)
                {
                    if (!actual.Contains(entry.Key))
                    {
                        mismatches.Add($"{path}[{entry.Key}]: header key missing on the masked side");
                        continue;
                    }
                    var actualValue = actual[entry.Key];
                    if (!Equals(actualValue, DashboardCredentialMasking.MaskedHeaderValue))
                    {
                        mismatches.Add(
                            $"{path}[{entry.Key}]: expected masked value " +
                            $"'{DashboardCredentialMasking.MaskedHeaderValue}', actual '{actualValue}'");
                    }
                }
            }

            private static string Describe(object? value)
                => value == null ? "<null>" : $"'{value}' ({value.GetType().Name})";
        }
    }
}
