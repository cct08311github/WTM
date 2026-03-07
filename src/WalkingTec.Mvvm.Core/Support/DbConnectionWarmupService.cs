#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// Background service that pre-warms database connection pools on app startup.
    /// Moves the cold-start latency (especially Oracle's ~60s first-query delay)
    /// from the first user request to the app boot phase — runs in background,
    /// does not block startup.
    ///
    /// For Oracle specifically, eliminates delay caused by:
    /// - TNS name resolution and caching
    /// - Connection pool creation (ODP.NET default Min Pool Size=0)
    /// - ODP.NET internal metadata loading and self-tuning calibration
    ///
    /// The service is fault-tolerant: a connection failure is logged as a warning,
    /// never crashes the app.
    /// </summary>
    public class DbConnectionWarmupService : BackgroundService
    {
        private readonly Configs _configs;
        private readonly ILogger<DbConnectionWarmupService> _logger;

        public DbConnectionWarmupService(
            Microsoft.Extensions.Options.IOptionsMonitor<Configs> configs,
            ILogger<DbConnectionWarmupService> logger)
        {
            _configs = configs.CurrentValue;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Small delay to let the rest of the app finish starting
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

            var enabledConnections = _configs.Connections?.Where(x => x.Enabled).ToList();
            if (enabledConnections == null || enabledConnections.Count == 0)
            {
                return;
            }

            _logger.LogInformation("[WTM] Starting database connection warm-up for {Count} connection(s)...", enabledConnections.Count);

            foreach (var cs in enabledConnections)
            {
                if (stoppingToken.IsCancellationRequested) break;
                try
                {
                    using var dc = cs.CreateDC();
                    var dbContext = dc as Microsoft.EntityFrameworkCore.DbContext;
                    if (dbContext != null)
                    {
                        await dbContext.Database.CanConnectAsync(stoppingToken);
                        _logger.LogInformation("[WTM] Warm-up OK: connection '{Key}' ({DbType})", cs.Key, cs.DbType);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[WTM] Warm-up failed for connection '{Key}' ({DbType}): {Message}. First user query may experience delay.",
                        cs.Key, cs.DbType, ex.Message);
                }
            }

            _logger.LogInformation("[WTM] Database connection warm-up completed.");
        }
    }
}
