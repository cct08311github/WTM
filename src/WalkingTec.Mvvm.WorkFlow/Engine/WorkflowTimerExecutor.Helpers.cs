#nullable enable
// WorkflowTimerExecutor — Helpers region (shared cross-region utility used by Remind/Escalate/Fire).
//
// #668: partial-class split of WorkflowTimerExecutor.cs — pure code motion (see
// WorkflowTimerExecutor.cs for the shared design notes and invariants). Members below were
// cut verbatim (including their original doc comments) from WorkflowTimerExecutor.cs; no
// signature, accessibility, or logic changes were made during the move.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.WorkFlow.Definition;
using WalkingTec.Mvvm.WorkFlow.Models;
using WalkingTec.Mvvm.WorkFlow.Notifications;

namespace WalkingTec.Mvvm.WorkFlow.Engine;

internal sealed partial class WorkflowTimerExecutor
{
    /// <summary>
    /// Build the idempotency key for the next chain link.
    /// Replaces the trailing ":{n}" counter suffix with ":{n+1}".
    /// Falls back to appending ":{n+1}" if the convention is not recognized.
    /// </summary>
    private static string BuildNextLinkKey(string currentKey, int currentRemindCount)
    {
        string suffix = $":{currentRemindCount}";
        string nextSuffix = $":{currentRemindCount + 1}";

        if (currentKey.EndsWith(suffix, StringComparison.Ordinal))
            return string.Concat(currentKey.AsSpan(0, currentKey.Length - suffix.Length), nextSuffix);

        // Fallback: append next suffix.
        return currentKey + nextSuffix;
    }
}
