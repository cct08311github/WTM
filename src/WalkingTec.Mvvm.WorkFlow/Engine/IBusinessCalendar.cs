#nullable enable
// WF-20: IBusinessCalendar seam + PassThroughBusinessCalendar default (Wave-5 §0 S2 verdict).
//
// Design §0 Hybrid verdict S2:
//   • IBusinessCalendar ships as a seam only — real calendar data is consumer-specific.
//   • PassThroughBusinessCalendar is the default: AddBusinessTime returns wall-clock math.
//   • Arm-time severity rules (enforced by the arm sites in WF-20.2):
//       - Remind  with businessCalendar:true + pass-through → arm wall-clock + LogWarning (harmless, early is OK)
//       - AutoApprove / AutoReject / Escalate with businessCalendar:true + pass-through → SKIP arm + LogWarning
//         (a compliance lie to fire on weekends is fail-closed here)
//   • At publish time: graphs with businessCalendar:true + dangerous auto-actions are
//     rejected by the validator (WF-20.2) — this seam is purely the runtime implementation.
//
// Third-party consumers register their own IBusinessCalendar via:
//   services.AddSingleton<IBusinessCalendar, MyCalendarImpl>();
// AFTER calling AddWtmWorkFlowTimers(), which registers TryAddSingleton<IBusinessCalendar, PassThroughBusinessCalendar>().
// TryAdd means the first registration wins — consumer registration before calling
// AddWtmWorkFlowTimers() is NOT supported (AddWtmWorkFlowTimers must be called first).

using System;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

/// <summary>
/// Seam for business-calendar-aware time computation.  Used by the timeout wave (WF-20)
/// to convert ISO-8601 duration into a fire UTC that skips weekends/holidays when required
/// by the graph's <c>TimeoutDef.BusinessCalendar</c> flag.
///
/// <para>The default implementation (<see cref="PassThroughBusinessCalendar"/>) performs
/// plain wall-clock addition — no business-calendar adjustment.  This means:</para>
/// <list type="bullet">
///   <item>Remind timers: arm normally (early reminder on weekends is harmless).</item>
///   <item>AutoApprove / AutoReject / Escalate timers: arm is SKIPPED by the arm sites
///     when <c>BusinessCalendar:true</c> is set in the graph + only the pass-through is
///     registered (the reaper would fire on a weekend, which is a compliance lie).
///     A LogWarning is emitted instead.</item>
/// </list>
///
/// <para>Register a real implementation via
/// <c>services.AddSingleton&lt;IBusinessCalendar, YourImpl&gt;()</c>
/// AFTER calling <c>AddWtmWorkFlowTimers()</c>.</para>
/// </summary>
public interface IBusinessCalendar
{
    /// <summary>
    /// Returns whether this implementation is a real business calendar (not the pass-through default).
    /// The arm sites check this to decide whether to warn about and skip auto-action arming when
    /// <c>businessCalendar:true</c> is set in the graph but no real calendar data is available.
    /// </summary>
    bool IsPassThrough { get; }

    /// <summary>
    /// Add <paramref name="duration"/> of business time to <paramref name="fromUtc"/>,
    /// using the optional <paramref name="calendarId"/> to identify the applicable
    /// holiday/working-day schedule.
    ///
    /// <para>Implementations MUST ensure the returned value is strictly greater than
    /// <paramref name="fromUtc"/> (Duration &gt; TimeSpan.Zero is validated at publish time).
    /// The result is used as <c>WorkflowTimer.FireAtUtc</c>.</para>
    ///
    /// <para>The result is a UTC <see cref="DateTime"/> (UTC Kind); never returns local time.</para>
    /// </summary>
    /// <param name="fromUtc">Start UTC instant (app-bound @now; never SQL CURRENT_TIMESTAMP).</param>
    /// <param name="duration">ISO-8601 duration string (e.g. "PT2H", "P1D").</param>
    /// <param name="calendarId">Optional calendar identifier from <c>WorkFlowOptions.BusinessCalendarId</c>.
    /// Null means no calendar filtering — use wall-clock addition.</param>
    /// <returns>UTC fire instant.</returns>
    DateTime AddBusinessTime(DateTime fromUtc, string duration, string? calendarId);
}

/// <summary>
/// Default (pass-through) business calendar: plain wall-clock addition via
/// <see cref="System.Xml.XmlConvert.ToTimeSpan"/>.
///
/// <para>Registered as the default <see cref="IBusinessCalendar"/> by <c>AddWtmWorkFlowTimers()</c>
/// via <c>TryAddSingleton</c>.  Consumers override by registering their own implementation
/// after <c>AddWtmWorkFlowTimers()</c>.</para>
/// </summary>
public sealed class PassThroughBusinessCalendar : IBusinessCalendar
{
    /// <inheritdoc/>
    /// <value>Always <c>true</c> — this is the pass-through default.</value>
    public bool IsPassThrough => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Parses <paramref name="duration"/> as an ISO-8601 duration via
    /// <see cref="System.Xml.XmlConvert.ToTimeSpan"/> and adds it to <paramref name="fromUtc"/>.
    /// <paramref name="calendarId"/> is ignored.
    /// </remarks>
    public DateTime AddBusinessTime(DateTime fromUtc, string duration, string? calendarId)
    {
        // XmlConvert.ToTimeSpan handles ISO-8601 duration strings: PT2H, P1D, P1DT4H, etc.
        var span = System.Xml.XmlConvert.ToTimeSpan(duration);
        return DateTime.SpecifyKind(fromUtc + span, DateTimeKind.Utc);
    }
}
