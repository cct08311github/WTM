#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Dashboard.Snapshot;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    [TestClass]
    public class DashboardRendererTests
    {
        // ── NotConfiguredDashboardRenderer ────────────────────────────────────

        [TestMethod]
        public async Task NotConfiguredRenderer_PDF_throws_NotSupportedException_with_clear_message()
        {
            var renderer = new NotConfiguredDashboardRenderer();

            Func<Task> act = () => renderer.RenderAsync("dash1", DashboardExportFormat.Pdf);

            await act.Should().ThrowAsync<NotSupportedException>()
                .WithMessage("*IDashboardRenderer*");
        }

        [TestMethod]
        public async Task NotConfiguredRenderer_PNG_throws_NotSupportedException_with_clear_message()
        {
            var renderer = new NotConfiguredDashboardRenderer();

            Func<Task> act = () => renderer.RenderAsync("dash1", DashboardExportFormat.Png, "tenantA");

            await act.Should().ThrowAsync<NotSupportedException>()
                .WithMessage("*IDashboardRenderer*");
        }

        [TestMethod]
        public async Task NotConfiguredRenderer_message_mentions_registration_instructions()
        {
            var renderer = new NotConfiguredDashboardRenderer();
            NotSupportedException? ex = null;

            try
            {
                await renderer.RenderAsync("x", DashboardExportFormat.Pdf, null, CancellationToken.None);
            }
            catch (NotSupportedException e)
            {
                ex = e;
            }

            ex.Should().NotBeNull();
            ex!.Message.Should().Contain("services.AddSingleton");
        }

        // ── DashboardExportFormat helpers ─────────────────────────────────────

        [TestMethod]
        public void SnapshotResult_FileExtension_returns_correct_values()
        {
            new DashboardSnapshotResult { Format = DashboardExportFormat.Excel }.FileExtension.Should().Be("xlsx");
            new DashboardSnapshotResult { Format = DashboardExportFormat.Pdf }.FileExtension.Should().Be("pdf");
            new DashboardSnapshotResult { Format = DashboardExportFormat.Png }.FileExtension.Should().Be("png");
        }
    }
}
