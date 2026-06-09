#nullable enable
namespace WalkingTec.Mvvm.WorkFlow;

/// <summary>
/// Configuration options for the WTM WorkFlow engine.
/// Pass to <see cref="ServiceCollectionExtensions.AddWtmWorkFlow"/> to customise defaults.
/// </summary>
public sealed class WorkFlowOptions
{
    // WF-2: engine options (routing, concurrency, timer) will be added here.

    /// <summary>
    /// When <c>true</c>, the process initiator's own approval step is automatically
    /// approved on submission (common Chinese-corporate convenience).
    /// Default is <c>false</c> — opt-in only, per spec §9.
    /// </summary>
    public bool InitiatorAutoApprove { get; set; } = false;
}
