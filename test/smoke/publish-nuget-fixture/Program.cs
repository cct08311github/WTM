// #925 post-pack, pre-publish smoke fixture for .github/workflows/publish-nuget.yml.
//
// This file is copied over the scaffolded `dotnet new console` Program.cs by the
// "Smoke test package install" step. Unlike the ORIGINAL version of this fixture, it is
// both built AND run (`dotnet build` then `dotnet run --no-build`): every check below is
// pure `System.Reflection` over the compiled types in the just-packed WalkingTec.Mvvm.Etl
// nupkg, which needs no database, no Quartz scheduler, and no HTTP pipeline to execute --
// so there is no reason to leave it compile-only.
//
// #925 cross-vendor review finding 6 (why this file was rewritten, not just re-commented):
// the PREVIOUS version of this fixture called all 13 members changed by #883 with every
// argument passed explicitly and claimed that proved "optionality, parameter order,
// requiredness and virtuality" for each. It did not, and the claim was wrong on every one
// of those four axes:
//   - passing every argument explicitly compiles whether a parameter is optional or
//     required -- an optional-to-required regression (or the reverse) does not change
//     whether that call still compiles;
//   - all the calls used NAMED arguments (`callerTenantCode: "tenant-a"`), which survive
//     a parameter-order swap -- a reordering regression does not change whether a
//     named-argument call still compiles;
//   - C# call syntax cannot observe `virtual`-ness at all -- removing `virtual` from a
//     Group A method does not change whether `scheduler.PauseAsync(...)` still compiles;
//   - the calls never construct real instances (`null!` throughout, by design -- see the
//     git history of this file), so nothing about default VALUES was ever checked, only
//     that some call shape type-checked.
// Reflection is not vulnerable to any of the four: `MethodInfo.GetParameters()` reports
// the ACTUAL declared order and each parameter's ACTUAL `IsOptional`/`DefaultValue`
// regardless of how a hypothetical caller might invoke the method, and `MethodInfo.
// IsVirtual` reports the ACTUAL metadata bit regardless of call-site syntax. This file
// asserts against exactly those members, and FAILS (non-zero exit, printed diagnostics)
// if any assertion does not hold -- it does not merely compile-check them.
//
// ── The thirteen members changed by #883 (CHANGELOG.md "### Changed" / "### Migration",
// 10.21.0), all asserted below, not a sample of them:
//
//   Group A -- WalkingTec.Mvvm.Etl.Scheduling.EtlSchedulerService, ten `public virtual`
//   methods, both new parameters OPTIONAL (defaulting to null/false):
//   TriggerNowAsync, PauseAsync, ResumeAsync, RescheduleAsync, AbortAsync, SkipNextAsync,
//   EnableAsync, DisableAsync, DryRunAsync, RerunFromSnapshotAsync.
//
//   Group B -- WalkingTec.Mvvm.Etl.Scheduling.EtlProgressTracker.Get / GetAll (NOT
//   virtual): `callerTenantCode` is REQUIRED (no default) -- omitting it is itself a
//   compile error, which is exactly the shape of the #883 review finding that made it
//   required in the first place (an omittable tenant parameter is how the dashboard leak
//   happened).
//
//   Group C -- WalkingTec.Mvvm.Etl.Dashboard.EtlDashboardService.BuildSummary (NOT
//   virtual): `callerTenantCode` is REQUIRED; there is NO `declaredSystemQuery` parameter
//   at all -- asserted here by checking the EXACT parameter count and name set, which
//   would catch an optional `declaredSystemQuery` silently added later (a change that
//   would NOT break any compile-time call site using positional or partial named args,
//   which is precisely why a compile-only fixture cannot catch it).
//
// If any of these 13 members is renamed, removed, reordered, has a parameter's
// optionality/type changed, or has its `virtual`-ness changed relative to what
// CHANGELOG.md documents, this file prints a FAIL line naming exactly which assertion
// broke and exits non-zero -- before anything is pushed to any registry.
//
// What this file does NOT prove: runtime BEHAVIOUR (that `declaredSystemQuery: true`
// actually bypasses the tenant filter, that the required-parameter methods actually
// enforce it end-to-end against a database) -- that is covered by
// test/WalkingTec.Mvvm.Etl.Test, not by this smoke fixture. This file's job is narrowly
// "does the shipped nupkg's compiled metadata match what the CHANGELOG documents",
// checked structurally, not "does the feature work".

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Etl.Dashboard;
using WalkingTec.Mvvm.Etl.Scheduling;

var failures = new List<string>();
var checks = 0;

void Check(bool condition, string description)
{
    checks++;
    if (condition)
    {
        Console.WriteLine($"  PASS: {description}");
    }
    else
    {
        failures.Add(description);
        Console.WriteLine($"  FAIL: {description}");
    }
}

// expectedParams: (name, type, isOptional, defaultValueIfOptional)
void CheckMember(
    Type declaringType,
    string memberName,
    bool expectVirtual,
    (string Name, Type Type, bool Optional, object? Default)[] expectedParams)
{
    Console.WriteLine($"-- {declaringType.FullName}.{memberName} --");
    var method = declaringType.GetMethod(memberName, BindingFlags.Public | BindingFlags.Instance);
    if (method is null)
    {
        checks++;
        failures.Add($"{declaringType.Name}.{memberName}: member not found at all");
        Console.WriteLine($"  FAIL: {declaringType.Name}.{memberName} not found");
        return;
    }

    Check(
        method.IsVirtual == expectVirtual && !method.IsFinal,
        $"{memberName}: IsVirtual={method.IsVirtual}, IsFinal={method.IsFinal} (expected IsVirtual={expectVirtual}, IsFinal=false)");

    var actualParams = method.GetParameters();
    Check(
        actualParams.Length == expectedParams.Length,
        $"{memberName}: parameter count is {actualParams.Length} (expected {expectedParams.Length}: [{string.Join(", ", expectedParams.Select(p => p.Name))}])");

    var n = Math.Min(actualParams.Length, expectedParams.Length);
    for (var i = 0; i < n; i++)
    {
        var (expectedName, expectedType, expectedOptional, expectedDefault) = expectedParams[i];
        var actual = actualParams[i];

        Check(actual.Name == expectedName, $"{memberName}: parameter[{i}].Name = '{actual.Name}' (expected '{expectedName}')");
        Check(actual.ParameterType == expectedType, $"{memberName}: parameter[{i}] '{expectedName}'.ParameterType = {actual.ParameterType} (expected {expectedType})");
        Check(actual.IsOptional == expectedOptional, $"{memberName}: parameter[{i}] '{expectedName}'.IsOptional = {actual.IsOptional} (expected {expectedOptional})");
        if (expectedOptional)
        {
            Check(
                Equals(actual.DefaultValue, expectedDefault),
                $"{memberName}: parameter[{i}] '{expectedName}'.DefaultValue = {actual.DefaultValue ?? "null"} (expected {expectedDefault ?? "null"})");
        }
    }
}

var schedulerType = typeof(EtlSchedulerService);
var trackerType = typeof(EtlProgressTracker);
var dashboardType = typeof(EtlDashboardService);

Console.WriteLine("═══ Group A — EtlSchedulerService (public virtual, both new params OPTIONAL) ═══");

CheckMember(schedulerType, nameof(EtlSchedulerService.TriggerNowAsync), expectVirtual: true,
    [
        ("jobId", typeof(Guid), false, null),
        ("watermarkOverride", typeof(string), true, null),
        ("callerTenantCode", typeof(string), true, null),
        ("declaredSystemQuery", typeof(bool), true, false),
    ]);

CheckMember(schedulerType, nameof(EtlSchedulerService.PauseAsync), expectVirtual: true,
    [
        ("jobId", typeof(Guid), false, null),
        ("callerTenantCode", typeof(string), true, null),
        ("declaredSystemQuery", typeof(bool), true, false),
    ]);

CheckMember(schedulerType, nameof(EtlSchedulerService.ResumeAsync), expectVirtual: true,
    [
        ("jobId", typeof(Guid), false, null),
        ("callerTenantCode", typeof(string), true, null),
        ("declaredSystemQuery", typeof(bool), true, false),
    ]);

CheckMember(schedulerType, nameof(EtlSchedulerService.RescheduleAsync), expectVirtual: true,
    [
        ("jobId", typeof(Guid), false, null),
        ("newCron", typeof(string), false, null),
        ("callerTenantCode", typeof(string), true, null),
        ("declaredSystemQuery", typeof(bool), true, false),
    ]);

CheckMember(schedulerType, nameof(EtlSchedulerService.AbortAsync), expectVirtual: true,
    [
        ("jobId", typeof(Guid), false, null),
        ("callerTenantCode", typeof(string), true, null),
        ("declaredSystemQuery", typeof(bool), true, false),
    ]);

CheckMember(schedulerType, nameof(EtlSchedulerService.SkipNextAsync), expectVirtual: true,
    [
        ("jobId", typeof(Guid), false, null),
        ("callerTenantCode", typeof(string), true, null),
        ("declaredSystemQuery", typeof(bool), true, false),
    ]);

CheckMember(schedulerType, nameof(EtlSchedulerService.EnableAsync), expectVirtual: true,
    [
        ("jobId", typeof(Guid), false, null),
        ("callerTenantCode", typeof(string), true, null),
        ("declaredSystemQuery", typeof(bool), true, false),
    ]);

CheckMember(schedulerType, nameof(EtlSchedulerService.DisableAsync), expectVirtual: true,
    [
        ("jobId", typeof(Guid), false, null),
        ("callerTenantCode", typeof(string), true, null),
        ("declaredSystemQuery", typeof(bool), true, false),
    ]);

CheckMember(schedulerType, nameof(EtlSchedulerService.DryRunAsync), expectVirtual: true,
    [
        ("jobId", typeof(Guid), false, null),
        ("sampleSize", typeof(int), true, 10),
        // A `CancellationToken cancellationToken = default` optional parameter cannot
        // carry a metadata constant (the CLR constant table only holds primitives,
        // enums, strings, and null) -- verified empirically: ParameterInfo.DefaultValue
        // for this exact parameter returns null, not a boxed default(CancellationToken),
        // even though IsOptional/HasDefaultValue are both true. Assert null here, not
        // `default(CancellationToken)` -- the latter looked "obviously right" and failed
        // against the real compiled metadata the first time this fixture was run.
        ("cancellationToken", typeof(System.Threading.CancellationToken), true, null),
        ("callerTenantCode", typeof(string), true, null),
        ("declaredSystemQuery", typeof(bool), true, false),
    ]);

CheckMember(schedulerType, nameof(EtlSchedulerService.RerunFromSnapshotAsync), expectVirtual: true,
    [
        ("runLogId", typeof(Guid), false, null),
        ("callerTenantCode", typeof(string), true, null),
        ("declaredSystemQuery", typeof(bool), true, false),
    ]);

Console.WriteLine();
Console.WriteLine("═══ Group B — EtlProgressTracker (NOT virtual, callerTenantCode REQUIRED) ═══");

CheckMember(trackerType, nameof(EtlProgressTracker.Get), expectVirtual: false,
    [
        ("jobId", typeof(Guid), false, null),
        ("callerTenantCode", typeof(string), false, null),
        ("declaredSystemQuery", typeof(bool), true, false),
    ]);

CheckMember(trackerType, nameof(EtlProgressTracker.GetAll), expectVirtual: false,
    [
        ("callerTenantCode", typeof(string), false, null),
        ("declaredSystemQuery", typeof(bool), true, false),
    ]);

Console.WriteLine();
Console.WriteLine("═══ Group C — EtlDashboardService.BuildSummary (NOT virtual, callerTenantCode REQUIRED, NO declaredSystemQuery) ═══");

CheckMember(dashboardType, nameof(EtlDashboardService.BuildSummary), expectVirtual: false,
    [
        ("dc", typeof(IDataContext), false, null),
        ("callerTenantCode", typeof(string), false, null),
        ("windowDays", typeof(int), true, 7),
        ("topN", typeof(int), true, 10),
    ]);

Console.WriteLine();
Console.WriteLine("═══ The other five packages — type-resolved (compile-time touch only; no known active binary-compat risk today) ═══");
// #925 scoped the "must call/assert members" requirement to Etl specifically -- it is
// the one package with a known, active binary-compatibility risk (the #883 change
// above). The other five have no such known risk today, so a type-resolution touch is
// proportionate: stronger evidence than `dotnet add package` alone (which never
// references a single type in the package), without pretending to be the same kind of
// API-metadata check the Etl assertions above are.
_ = typeof(Configs);                                              // WalkingTec.Mvvm.Core
_ = typeof(WalkingTec.Mvvm.Mvc.BaseController);                   // WalkingTec.Mvvm.Mvc
_ = typeof(WalkingTec.Mvvm.TagHelpers.LayUI.DataTableTagHelper);  // WalkingTec.Mvvm.TagHelpers.LayUI
_ = typeof(WalkingTec.Mvvm.WorkFlow.WorkFlowOptions);             // WalkingTec.Mvvm.WorkFlow
_ = typeof(WalkingTec.Mvvm.FileHandlers.S3.S3FileHandlerOptions); // WalkingTec.Mvvm.FileHandlers.S3
Console.WriteLine("  all five type-resolved without a TypeLoadException.");

Console.WriteLine();
Console.WriteLine($"Reflection checks: {checks - failures.Count}/{checks} passed, {failures.Count} failed.");
if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("FAILURES:");
    foreach (var f in failures)
    {
        Console.WriteLine($"  - {f}");
    }
    return 1;
}

Console.WriteLine("Etl signature smoke OK: all 13 #883 members' optionality, requiredness, parameter order, and virtual-ness verified by reflection against the just-packed nupkg's real compiled metadata.");
return 0;
