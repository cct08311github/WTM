#nullable enable
using System;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Default implementation of <see cref="IWtmLogService"/>.
    /// Logic is an exact copy of WTMContext.DoLog (WTMContext.cs lines 1077-1123)
    /// to guarantee identical behaviour.
    /// </summary>
    public class WtmLogService : IWtmLogService
    {
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<ActionLog>? _logger;

        public WtmLogService(TimeProvider timeProvider, ILoggerFactory? loggerFactory = null)
        {
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
            _logger = loggerFactory?.CreateLogger<ActionLog>();
        }

        public void DoLog(string? msg,
            ActionLogTypesEnum logtype = ActionLogTypesEnum.Normal,
            string? moduleName = "",
            string? actionName = "",
            string? ip = "",
            string? url = "",
            double duration = 0,
            string? userCode = null,
            SimpleLog? existingLog = null)
        {
            var log = existingLog?.GetActionLog() ?? new ActionLog();
            log.LogType = logtype;
            log.ActionTime = _timeProvider.GetLocalNow().DateTime;
            log.Remark = msg;
            log.ActionUrl = url;
            log.Duration = duration;
            log.ModuleName = moduleName;
            log.ActionName = actionName;
            log.ITCode = userCode;
            log.IP = ip;

            LogLevel ll = logtype switch
            {
                ActionLogTypesEnum.Normal => LogLevel.Information,
                ActionLogTypesEnum.Exception => LogLevel.Error,
                ActionLogTypesEnum.Debug => LogLevel.Debug,
                _ => LogLevel.Information,
            };

            _logger?.Log(ll, new EventId(), log, null, (a, b) =>
            {
                return $@"
===WTM Log===
内容:{a.Remark}
地址:{a.ActionUrl}
时间:{a.ActionTime}
===WTM Log===
";
            });
        }
    }
}
