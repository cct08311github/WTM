using System;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>Startup diagnostics of the <see cref="GenericEditOptions"/> settings.</summary>
    public static class GenericEditGuard
    {
        /// <summary>
        /// Sets the members the framework owns on a row that a list edit or a child collection adds: with <paramref name="protect"/> the create time and
        /// creator are the framework's whatever the request carried and a validity flag is true. Without it (the legacy setting) the create time and
        /// creator are set only when empty, as before, and the validity flag is left as posted. The tenant code is set by the caller.
        /// </summary>
        internal static void StampAddedRow(object row, string userCode, bool protect)
        {
            if (row is IBasePoco poco)
            {
                if (protect || poco.CreateTime == null)
                {
                    poco.CreateTime = DateTime.Now;
                }
                if (protect || string.IsNullOrEmpty(poco.CreateBy))
                {
                    poco.CreateBy = userCode;
                }
            }
            if (protect && row is IPersistPoco persist)
            {
                persist.IsValid = true;
            }
        }

        /// <summary>Logs a warning (never throws) when the protection of the framework-owned members is switched off by configuration.</summary>
        public static void LogRelaxedWarning(Configs configs, ILogger logger)
        {
            var options = configs?.GenericEdit;
            if (options == null || logger == null)
            {
                return;
            }
            if (options.LegacyProtectedMemberWrites)
            {
                logger.LogWarning("GenericEdit:LegacyProtectedMemberWrites is on: the generic edit paths (form edit, list edit, batch edit and the overwrite of an import) write the tenant code, the validity flag and the create time and creator of a record when a request carries them. Leave it off (the default) unless a client still depends on that.");
            }
        }
    }
}
