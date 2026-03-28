#nullable enable
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Encapsulates structured logging logic previously embedded in WTMContext.DoLog().
    /// Consumers can inject this service directly for explicit, testable logging.
    /// </summary>
    public interface IWtmLogService
    {
        /// <summary>
        /// Write a structured action log entry.
        /// </summary>
        /// <param name="msg">Log message / remark.</param>
        /// <param name="logtype">Log severity category.</param>
        /// <param name="moduleName">Module name (e.g. controller description).</param>
        /// <param name="actionName">Action name (e.g. method description).</param>
        /// <param name="ip">Client IP address.</param>
        /// <param name="url">Request URL. If null/empty, no URL is recorded.</param>
        /// <param name="duration">Request duration in milliseconds.</param>
        /// <param name="userCode">Current user's ITCode (optional).</param>
        /// <param name="existingLog">An existing SimpleLog to use as a base (optional). When provided, its pre-populated fields are merged into the ActionLog before being overwritten by explicit parameters.</param>
        void DoLog(string? msg,
            ActionLogTypesEnum logtype = ActionLogTypesEnum.Normal,
            string? moduleName = "",
            string? actionName = "",
            string? ip = "",
            string? url = "",
            double duration = 0,
            string? userCode = null,
            SimpleLog? existingLog = null);
    }
}
