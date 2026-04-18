#nullable enable
namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Options for <see cref="ActionLogRetentionService"/>.
    /// Introduced by issue #832 to bound unbounded growth of the
    /// <c>ActionLogs</c> table without requiring DBA-level partitioning.
    /// </summary>
    public class ActionLogRetentionOptions
    {
        /// <summary>
        /// Master switch. Default <c>true</c> — the service runs. Set to
        /// <c>false</c> for apps that want infinite retention (regulated
        /// domains that manage retention via external archival pipeline).
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Local-time hour of day (0–23) when the daily retention sweep
        /// runs. Default 3 (03:00 local). Runs exactly once per
        /// calendar day.
        /// </summary>
        public int RunAtLocalHour { get; set; } = 3;

        /// <summary>
        /// Retention in days for <see cref="ActionLogTypesEnum.Normal"/>
        /// entries. Default 90. Set to <c>0</c> or negative to disable
        /// retention for this type (keeps forever).
        /// </summary>
        public int NormalDays { get; set; } = 90;

        /// <summary>
        /// Retention in days for <see cref="ActionLogTypesEnum.Exception"/>.
        /// Default 365 — exceptions are debug-valuable longer than
        /// normal traffic. Set to <c>0</c> or negative to disable.
        /// </summary>
        public int ExceptionDays { get; set; } = 365;

        /// <summary>
        /// Retention in days for <see cref="ActionLogTypesEnum.Debug"/>.
        /// Default 30 — debug noise should rotate fastest.
        /// </summary>
        public int DebugDays { get; set; } = 30;

        /// <summary>
        /// Retention in days for <see cref="ActionLogTypesEnum.Job"/>.
        /// Default 90.
        /// </summary>
        public int JobDays { get; set; } = 90;

        /// <summary>
        /// Max rows deleted per SQL statement. The service loops
        /// <c>ExecuteDeleteAsync</c> in chunks of this size until no
        /// more rows match. Prevents the lock contention / transaction
        /// log bloat that a single massive <c>DELETE</c> would cause.
        /// Default 5000.
        /// </summary>
        public int BatchSize { get; set; } = 5000;
    }
}
