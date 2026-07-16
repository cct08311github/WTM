#nullable enable
using System;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Helper
{
    /// <summary>
    /// Static caches for hot-path reflection results in PropertyHelper and AnalysisFieldScanner.
    /// Production code should treat these as opaque — use only through the public helpers.
    /// </summary>
    internal static class ReflectionCache
    {
        // (Type, property-name) -> compiled accessor
        internal static readonly ConcurrentDictionary<(Type, string), Func<object, object?>> PropertyAccessors = new();

        // MemberInfo -> required flag
        internal static readonly ConcurrentDictionary<MemberInfo, bool> RequiredFlags = new();

        // MemberInfo -> raw display name (pre-localization)
        internal static readonly ConcurrentDictionary<MemberInfo, string> RawDisplayNames = new();

        // (Type, enum-value-string) -> raw enum display name (pre-localization)
        internal static readonly ConcurrentDictionary<(Type, string), string> RawEnumDisplayNames = new();

        // (Type, int) -> raw enum display name for int-overload (pre-localization)
        internal static readonly ConcurrentDictionary<(Type, int), string> RawEnumDisplayNamesByInt = new();

        // Type -> AnalysisFieldScanTemplate[] (structural scan, no live localizer-dependent fields)
        internal static readonly ConcurrentDictionary<Type, AnalysisFieldScanTemplate[]> AnalysisFieldTemplates = new();

        // Perf(#674): Type -> per-property (PropertyInfo, ValidationAttribute[]) pairs for
        // BaseImportVM's per-row data-annotation validation. GetProperties() and the per-property
        // GetCustomAttributes(true) scan (which re-instantiates attribute objects on every call)
        // are pure functions of the CLR type — cache once per model type instead of once per
        // imported row. DisplayName is intentionally NOT cached here: it is resolved fresh via
        // the already-cached PropertyHelper.GetPropertyDisplayName() at validation time so that
        // localizer/culture changes are still respected (same as before #674).
        internal static readonly ConcurrentDictionary<Type, ImportPropertyValidationInfo[]> ImportValidationInfos = new();

        /// <summary>Test-only: clear all caches. Production code should never call this.</summary>
        internal static void ClearAll()
        {
            PropertyAccessors.Clear();
            RequiredFlags.Clear();
            RawDisplayNames.Clear();
            RawEnumDisplayNames.Clear();
            RawEnumDisplayNamesByInt.Clear();
            AnalysisFieldTemplates.Clear();
            ImportValidationInfos.Clear();
        }
    }

    /// <summary>
    /// Cached structural template for a single analysis field — everything except
    /// live-localizer-dependent fields such as AllowedValues.
    /// </summary>
    internal sealed class AnalysisFieldScanTemplate
    {
        internal string FieldName { get; init; } = string.Empty;
        internal string DisplayName { get; init; } = string.Empty;
        internal AnalysisFieldKind Kind { get; init; }
        internal AggregateFunc AllowedFuncs { get; init; }
        internal Type ClrType { get; init; } = typeof(object);
        internal bool IsDate { get; init; }
        internal DateHierarchy Hierarchy { get; init; }
        internal string? AllowedRoles { get; init; }
        internal MeasureFormat Format { get; init; }
        /// <summary>The underlying (non-nullable) CLR type for enum AllowedValues generation.</summary>
        internal Type? UnderlyingEnumType { get; init; }
    }

    /// <summary>
    /// Cached (PropertyInfo, ValidationAttribute[]) pair for one property of an import model
    /// type, used by BaseImportVM's per-row data-annotation validation (#674).
    /// </summary>
    internal sealed class ImportPropertyValidationInfo(PropertyInfo property, ValidationAttribute[] rules)
    {
        internal PropertyInfo Property { get; } = property;
        internal ValidationAttribute[] Rules { get; } = rules;
    }
}
