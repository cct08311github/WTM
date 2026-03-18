#nullable enable
using System.Diagnostics;

namespace WalkingTec.Mvvm.Core;

/// <summary>
/// Named <see cref="ActivitySource"/> instances for WTM framework telemetry.
/// Register these source names via <c>AddWtmOpenTelemetry()</c> so the OTel SDK
/// can collect spans emitted by the Core, Analysis, and ETL subsystems.
/// </summary>
public static class WtmActivitySources
{
    public const string CoreName = "WalkingTec.Mvvm.Core";
    public const string AnalysisName = "WalkingTec.Mvvm.Analysis";
    public const string EtlName = "WalkingTec.Mvvm.Etl";

    /// <summary>Core framework spans (VM lifecycle, controller operations).</summary>
    public static readonly ActivitySource Core = new(CoreName);

    /// <summary>Analysis engine spans (ad-hoc query, export).</summary>
    public static readonly ActivitySource Analysis = new(AnalysisName);

    /// <summary>ETL pipeline spans (extract, load, merge).</summary>
    public static readonly ActivitySource Etl = new(EtlName);
}
