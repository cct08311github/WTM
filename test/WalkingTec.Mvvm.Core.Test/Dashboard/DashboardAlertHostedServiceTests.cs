#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core.Dashboard;
using WalkingTec.Mvvm.Core.Dashboard.Alerting;
using WalkingTec.Mvvm.Core.Notifications;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    [TestClass]
    public class DashboardAlertHostedServiceTests
    {
        // ── Helpers ───────────────────────────────────────────────────────────

        private static DashboardAlertHostedService BuildService(
            IDashboardService dashSvc,
            IWtmWebhookSink? sink,
            int intervalSeconds = 60,
            int cooldownSeconds = 0)
        {
            var opts = Options.Create(new DashboardAlertOptions
            {
                EvaluationIntervalSeconds = intervalSeconds,
                AlertCooldownSeconds = cooldownSeconds,
                EvaluationWidgetTimeoutSeconds = 5
            });
            return new DashboardAlertHostedService(
                dashSvc, sink, opts,
                NullLogger<DashboardAlertHostedService>.Instance);
        }

        private static DashboardDefinition MakeDashboard(
            string id,
            string widgetId,
            List<WidgetThreshold> thresholds,
            string? tenantId = null)
        {
            return new DashboardDefinition
            {
                Id = id,
                Title = "Test Dashboard",
                Owner = "alice",
                TenantId = tenantId,
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    [widgetId] = new WidgetDefinition
                    {
                        Type = "kpi",
                        Title = "KPI Widget",
                        Source = new WidgetSourceDefinition { Kind = "custom" },
                        Thresholds = thresholds
                    }
                }
            };
        }

        // ── Service is no-op when disabled ────────────────────────────────────

        [TestMethod]
        public async Task Service_is_noop_when_interval_is_zero()
        {
            var dashSvc = new Mock<IDashboardService>();
            var sink = new Mock<IWtmWebhookSink>();

            var svc = BuildService(dashSvc.Object, sink.Object, intervalSeconds: 0);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await svc.StartAsync(cts.Token);

            dashSvc.Verify(d => d.ListAsync(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string?>()),
                Times.Never, "dashboard service must not be called when interval is 0");
            sink.Verify(s => s.SendAsync(It.IsAny<WebhookMessage>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [TestMethod]
        public async Task Service_is_noop_when_sink_is_null()
        {
            var dashSvc = new Mock<IDashboardService>();

            var svc = BuildService(dashSvc.Object, null, intervalSeconds: 60);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await svc.StartAsync(cts.Token);

            dashSvc.Verify(d => d.ListAsync(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string?>()),
                Times.Never);
        }

        // ── Breach → alert fires ──────────────────────────────────────────────

        [TestMethod]
        public async Task Breach_sends_WebhookMessage_with_correct_level_Warning()
        {
            var threshold = new WidgetThreshold
            {
                RuleId = "rule1",
                Measure = "value",
                Op = ThresholdComparisonOp.Gt,
                Value = 100,
                Level = ThresholdAlertLevel.Warning
            };

            var dashboard = MakeDashboard("d1", "w1", new List<WidgetThreshold> { threshold });
            var summary = new DashboardSummary
            {
                Id = "d1", Title = "Test Dashboard", Owner = "alice", TenantId = null,
                Sharing = new SharingDefinition { Mode = "public" }
            };

            var dashSvc = new Mock<IDashboardService>();
            dashSvc
                .Setup(d => d.ListAsync(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string?>()))
                .ReturnsAsync(new List<DashboardSummary> { summary });
            dashSvc
                .Setup(d => d.GetAsync("d1", null))
                .ReturnsAsync(dashboard);
            dashSvc
                .Setup(d => d.GetWidgetDataAsync("d1", "w1", null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WidgetDataResult { Value = 150.0 }); // 150 > 100 → breach

            WebhookMessage? captured = null;
            var sink = new Mock<IWtmWebhookSink>();
            sink
                .Setup(s => s.SendAsync(It.IsAny<WebhookMessage>(), It.IsAny<CancellationToken>()))
                .Callback<WebhookMessage, CancellationToken>((m, _) => captured = m)
                .Returns(Task.CompletedTask);

            var svc = BuildService(dashSvc.Object, sink.Object, intervalSeconds: 60, cooldownSeconds: 0);
            await svc.EvaluateAllAsync(CancellationToken.None);

            sink.Verify(s => s.SendAsync(It.IsAny<WebhookMessage>(), It.IsAny<CancellationToken>()),
                Times.Once);
            captured.Should().NotBeNull();
            captured!.Level.Should().Be(WebhookLevel.Warning);
            captured.Title.Should().Contain("KPI Widget");
        }

        [TestMethod]
        public async Task Breach_sends_WebhookMessage_with_correct_level_Error()
        {
            var threshold = new WidgetThreshold
            {
                RuleId = "rule1",
                Measure = "value",
                Op = ThresholdComparisonOp.Gt,
                Value = 100,
                Level = ThresholdAlertLevel.Error
            };

            var dashboard = MakeDashboard("d1", "w1", new List<WidgetThreshold> { threshold });
            var summary = new DashboardSummary
            {
                Id = "d1", Title = "Test", Owner = "alice",
                Sharing = new SharingDefinition { Mode = "public" }
            };

            var dashSvc = new Mock<IDashboardService>();
            dashSvc.Setup(d => d.ListAsync(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string?>()))
                .ReturnsAsync(new List<DashboardSummary> { summary });
            dashSvc.Setup(d => d.GetAsync("d1", null)).ReturnsAsync(dashboard);
            dashSvc.Setup(d => d.GetWidgetDataAsync("d1", "w1", null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WidgetDataResult { Value = 200.0 });

            WebhookMessage? captured = null;
            var sink = new Mock<IWtmWebhookSink>();
            sink.Setup(s => s.SendAsync(It.IsAny<WebhookMessage>(), It.IsAny<CancellationToken>()))
                .Callback<WebhookMessage, CancellationToken>((m, _) => captured = m)
                .Returns(Task.CompletedTask);

            var svc = BuildService(dashSvc.Object, sink.Object);
            await svc.EvaluateAllAsync(CancellationToken.None);

            captured!.Level.Should().Be(WebhookLevel.Error);
        }

        [TestMethod]
        public async Task Breach_message_contains_required_fields()
        {
            var threshold = new WidgetThreshold
            {
                RuleId = "r-test",
                Measure = "errorRate",
                Op = ThresholdComparisonOp.Gt,
                Value = 0.1,
                Level = ThresholdAlertLevel.Warning,
                Message = "Error rate too high!"
            };

            var dashboard = MakeDashboard("dash-a", "widget-b", new List<WidgetThreshold> { threshold });
            var summary = new DashboardSummary
            {
                Id = "dash-a", Title = "T", Owner = "o",
                Sharing = new SharingDefinition { Mode = "public" }
            };

            var dashSvc = new Mock<IDashboardService>();
            dashSvc.Setup(d => d.ListAsync(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string?>()))
                .ReturnsAsync(new List<DashboardSummary> { summary });
            dashSvc.Setup(d => d.GetAsync("dash-a", null)).ReturnsAsync(dashboard);
            dashSvc.Setup(d => d.GetWidgetDataAsync("dash-a", "widget-b", null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WidgetDataResult
                {
                    Metadata = new Dictionary<string, object?> { ["errorRate"] = 0.5 }
                });

            WebhookMessage? captured = null;
            var sink = new Mock<IWtmWebhookSink>();
            sink.Setup(s => s.SendAsync(It.IsAny<WebhookMessage>(), It.IsAny<CancellationToken>()))
                .Callback<WebhookMessage, CancellationToken>((m, _) => captured = m)
                .Returns(Task.CompletedTask);

            var svc = BuildService(dashSvc.Object, sink.Object);
            await svc.EvaluateAllAsync(CancellationToken.None);

            captured.Should().NotBeNull();
            captured!.Body.Should().Be("Error rate too high!");

            // Fields must carry key metrics.
            var fieldKeys = captured.Fields.Select(f => f.Key).ToList();
            fieldKeys.Should().Contain("Dashboard");
            fieldKeys.Should().Contain("Widget");
            fieldKeys.Should().Contain("Rule");
            fieldKeys.Should().Contain("Measure");
        }

        // ── No breach → no alert ─────────────────────────────────────────────

        [TestMethod]
        public async Task No_breach_does_not_fire_alert()
        {
            var threshold = new WidgetThreshold
            {
                RuleId = "rule1",
                Measure = "value",
                Op = ThresholdComparisonOp.Gt,
                Value = 100,
                Level = ThresholdAlertLevel.Warning
            };

            var dashboard = MakeDashboard("d1", "w1", new List<WidgetThreshold> { threshold });
            var summary = new DashboardSummary
            {
                Id = "d1", Title = "T", Owner = "o",
                Sharing = new SharingDefinition { Mode = "public" }
            };

            var dashSvc = new Mock<IDashboardService>();
            dashSvc.Setup(d => d.ListAsync(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string?>()))
                .ReturnsAsync(new List<DashboardSummary> { summary });
            dashSvc.Setup(d => d.GetAsync("d1", null)).ReturnsAsync(dashboard);
            dashSvc.Setup(d => d.GetWidgetDataAsync("d1", "w1", null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WidgetDataResult { Value = 50.0 }); // 50 <= 100 → no breach

            var sink = new Mock<IWtmWebhookSink>();

            var svc = BuildService(dashSvc.Object, sink.Object);
            await svc.EvaluateAllAsync(CancellationToken.None);

            sink.Verify(s => s.SendAsync(It.IsAny<WebhookMessage>(), It.IsAny<CancellationToken>()),
                Times.Never, "no alert should fire when below threshold");
        }

        // ── De-duplication (alert-on-transition) ─────────────────────────────

        [TestMethod]
        public async Task DeduplicateTransition_alert_fires_only_once_per_breach_episode()
        {
            var threshold = new WidgetThreshold
            {
                RuleId = "rule1",
                Measure = "value",
                Op = ThresholdComparisonOp.Gt,
                Value = 100,
                Level = ThresholdAlertLevel.Warning
            };

            var dashboard = MakeDashboard("d1", "w1", new List<WidgetThreshold> { threshold });
            var summary = new DashboardSummary
            {
                Id = "d1", Title = "T", Owner = "o",
                Sharing = new SharingDefinition { Mode = "public" }
            };

            var dashSvc = new Mock<IDashboardService>();
            dashSvc.Setup(d => d.ListAsync(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string?>()))
                .ReturnsAsync(new List<DashboardSummary> { summary });
            dashSvc.Setup(d => d.GetAsync("d1", null)).ReturnsAsync(dashboard);
            // Always returns a breach value.
            dashSvc.Setup(d => d.GetWidgetDataAsync("d1", "w1", null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WidgetDataResult { Value = 200.0 });

            var sink = new Mock<IWtmWebhookSink>();
            sink.Setup(s => s.SendAsync(It.IsAny<WebhookMessage>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Large cooldown: guarantees de-dup is by transition, not expiry.
            var svc = BuildService(dashSvc.Object, sink.Object, cooldownSeconds: 9999);

            // First tick → breach → alert fires.
            await svc.EvaluateAllAsync(CancellationToken.None);
            // Second tick → still breached → should NOT re-fire (cooldown in effect).
            await svc.EvaluateAllAsync(CancellationToken.None);
            // Third tick → still breached → should NOT re-fire.
            await svc.EvaluateAllAsync(CancellationToken.None);

            sink.Verify(s => s.SendAsync(It.IsAny<WebhookMessage>(), It.IsAny<CancellationToken>()),
                Times.Once, "alert should fire only once per breach episode while in cooldown");
        }

        [TestMethod]
        public async Task DeduplicateTransition_alert_refires_after_clear_and_new_breach()
        {
            var threshold = new WidgetThreshold
            {
                RuleId = "rule1",
                Measure = "value",
                Op = ThresholdComparisonOp.Gt,
                Value = 100,
                Level = ThresholdAlertLevel.Warning
            };

            var dashboard = MakeDashboard("d1", "w1", new List<WidgetThreshold> { threshold });
            var summary = new DashboardSummary
            {
                Id = "d1", Title = "T", Owner = "o",
                Sharing = new SharingDefinition { Mode = "public" }
            };

            var callCount = 0;
            var dashSvc = new Mock<IDashboardService>();
            dashSvc.Setup(d => d.ListAsync(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string?>()))
                .ReturnsAsync(new List<DashboardSummary> { summary });
            dashSvc.Setup(d => d.GetAsync("d1", null)).ReturnsAsync(dashboard);
            dashSvc.Setup(d => d.GetWidgetDataAsync("d1", "w1", null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    callCount++;
                    // Tick 1: breach; Tick 2: clear; Tick 3: breach again.
                    return callCount switch
                    {
                        1 => new WidgetDataResult { Value = 200.0 }, // breach
                        2 => new WidgetDataResult { Value = 50.0 },  // clear
                        _ => new WidgetDataResult { Value = 200.0 }  // breach again
                    };
                });

            var sink = new Mock<IWtmWebhookSink>();
            sink.Setup(s => s.SendAsync(It.IsAny<WebhookMessage>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Cooldown = 0 so cooldown doesn't interfere with the transition test.
            var svc = BuildService(dashSvc.Object, sink.Object, cooldownSeconds: 0);

            await svc.EvaluateAllAsync(CancellationToken.None); // tick 1: breach → fire
            await svc.EvaluateAllAsync(CancellationToken.None); // tick 2: clear → no fire
            await svc.EvaluateAllAsync(CancellationToken.None); // tick 3: new breach → fire again

            sink.Verify(s => s.SendAsync(It.IsAny<WebhookMessage>(), It.IsAny<CancellationToken>()),
                Times.Exactly(2), "alert should fire on first breach and again after a clear+re-breach");
        }

        // ── Widget error result → no alert ────────────────────────────────────

        [TestMethod]
        public async Task Widget_data_error_does_not_fire_alert()
        {
            var threshold = new WidgetThreshold
            {
                RuleId = "rule1",
                Measure = "value",
                Op = ThresholdComparisonOp.Gt,
                Value = 0,
                Level = ThresholdAlertLevel.Error
            };

            var dashboard = MakeDashboard("d1", "w1", new List<WidgetThreshold> { threshold });
            var summary = new DashboardSummary
            {
                Id = "d1", Title = "T", Owner = "o",
                Sharing = new SharingDefinition { Mode = "public" }
            };

            var dashSvc = new Mock<IDashboardService>();
            dashSvc.Setup(d => d.ListAsync(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<string?>()))
                .ReturnsAsync(new List<DashboardSummary> { summary });
            dashSvc.Setup(d => d.GetAsync("d1", null)).ReturnsAsync(dashboard);
            // Return an error result.
            dashSvc.Setup(d => d.GetWidgetDataAsync("d1", "w1", null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WidgetDataResult { Error = "source unavailable" });

            var sink = new Mock<IWtmWebhookSink>();

            var svc = BuildService(dashSvc.Object, sink.Object);
            await svc.EvaluateAllAsync(CancellationToken.None);

            sink.Verify(s => s.SendAsync(It.IsAny<WebhookMessage>(), It.IsAny<CancellationToken>()),
                Times.Never, "a widget data error result must not trigger an alert");
        }

    }
}
