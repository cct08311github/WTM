#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Support.Email;

namespace WalkingTec.Mvvm.Core.Test.Support.Email;

/// <summary>
/// Tests for issue #421: verifies that <see cref="SmtpEmailService"/> builds the correct
/// <see cref="MailMessage"/> and that the disabled / no-op path is always safe.
/// No real SMTP connections are made — a fake <see cref="ISmtpTransport"/> is injected via
/// the protected <see cref="SmtpEmailService.CreateTransport"/> factory override.
/// </summary>
[TestClass]
public class SmtpEmailServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EmailOptions EnabledOptions() => new()
    {
        Host            = "smtp.example.com",
        Port            = 587,
        EnableSsl       = true,
        UserName        = "user@example.com",
        Password        = "secret",
        FromAddress     = "noreply@example.com",
        FromDisplayName = "WTM Alerts",
        Enabled         = true
    };

    private static EmailMessage SampleMessage(
        string[]? cc = null,
        bool isHtml  = false) => new()
    {
        To      = new[] { "alice@example.com", "bob@example.com" },
        Cc      = cc ?? Array.Empty<string>(),
        Subject = "Test Subject",
        Body    = "<b>Hello</b>",
        IsHtml  = isHtml
    };

    // ── Fake transport ────────────────────────────────────────────────────────

    /// <summary>
    /// Records every <see cref="MailMessage"/> passed to <see cref="SendMailAsync"/>
    /// without making any real SMTP connection.
    /// </summary>
    private sealed class FakeTransport : ISmtpTransport
    {
        public readonly List<MailMessage> SentMessages = new();
        public bool Disposed { get; private set; }

        public Task SendMailAsync(MailMessage message, CancellationToken cancellationToken = default)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Subclass of <see cref="SmtpEmailService"/> that returns a
    /// <see cref="FakeTransport"/> from <see cref="CreateTransport"/>.
    /// </summary>
    private sealed class TestableSmtpEmailService : SmtpEmailService
    {
        private readonly FakeTransport _transport;

        public TestableSmtpEmailService(EmailOptions options, FakeTransport transport)
            : base(options, NullLogger<SmtpEmailService>.Instance)
        {
            _transport = transport;
        }

        protected override ISmtpTransport CreateTransport(EmailOptions _) => _transport;
    }

    // ── To / Cc ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SendAsync_adds_all_To_recipients()
    {
        var fake = new FakeTransport();
        var svc  = new TestableSmtpEmailService(EnabledOptions(), fake);

        await svc.SendAsync(SampleMessage());

        Assert.AreEqual(1, fake.SentMessages.Count);
        var mail = fake.SentMessages[0];
        Assert.AreEqual(2, mail.To.Count);
        Assert.IsTrue(mail.To.ToString()!.Contains("alice@example.com"));
        Assert.IsTrue(mail.To.ToString()!.Contains("bob@example.com"));
    }

    [TestMethod]
    public async Task SendAsync_adds_Cc_recipients_when_provided()
    {
        var fake = new FakeTransport();
        var svc  = new TestableSmtpEmailService(EnabledOptions(), fake);
        var msg  = SampleMessage(cc: new[] { "carol@example.com" });

        await svc.SendAsync(msg);

        var mail = fake.SentMessages[0];
        Assert.AreEqual(1, mail.CC.Count);
        Assert.IsTrue(mail.CC.ToString()!.Contains("carol@example.com"));
    }

    [TestMethod]
    public async Task SendAsync_has_empty_Cc_when_none_provided()
    {
        var fake = new FakeTransport();
        var svc  = new TestableSmtpEmailService(EnabledOptions(), fake);

        await svc.SendAsync(SampleMessage());

        var mail = fake.SentMessages[0];
        Assert.AreEqual(0, mail.CC.Count);
    }

    // ── Subject ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SendAsync_sets_Subject_correctly()
    {
        var fake = new FakeTransport();
        var svc  = new TestableSmtpEmailService(EnabledOptions(), fake);

        await svc.SendAsync(SampleMessage());

        Assert.AreEqual("Test Subject", fake.SentMessages[0].Subject);
    }

    // ── Body / IsHtml ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SendAsync_sets_Body_correctly()
    {
        var fake = new FakeTransport();
        var svc  = new TestableSmtpEmailService(EnabledOptions(), fake);

        await svc.SendAsync(SampleMessage());

        Assert.AreEqual("<b>Hello</b>", fake.SentMessages[0].Body);
    }

    [TestMethod]
    public async Task SendAsync_sets_IsBodyHtml_true_when_IsHtml_is_true()
    {
        var fake = new FakeTransport();
        var svc  = new TestableSmtpEmailService(EnabledOptions(), fake);

        await svc.SendAsync(SampleMessage(isHtml: true));

        Assert.IsTrue(fake.SentMessages[0].IsBodyHtml);
    }

    [TestMethod]
    public async Task SendAsync_sets_IsBodyHtml_false_when_IsHtml_is_false()
    {
        var fake = new FakeTransport();
        var svc  = new TestableSmtpEmailService(EnabledOptions(), fake);

        await svc.SendAsync(SampleMessage(isHtml: false));

        Assert.IsFalse(fake.SentMessages[0].IsBodyHtml);
    }

    // ── From address ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SendAsync_sets_From_address_and_display_name()
    {
        var fake = new FakeTransport();
        var svc  = new TestableSmtpEmailService(EnabledOptions(), fake);

        await svc.SendAsync(SampleMessage());

        var from = fake.SentMessages[0].From;
        Assert.IsNotNull(from);
        Assert.AreEqual("noreply@example.com", from.Address);
        Assert.AreEqual("WTM Alerts",          from.DisplayName);
    }

    [TestMethod]
    public async Task SendAsync_sets_From_address_without_display_name_when_absent()
    {
        var opts = EnabledOptions();
        opts.FromDisplayName = null;

        var fake = new FakeTransport();
        var svc  = new TestableSmtpEmailService(opts, fake);

        await svc.SendAsync(SampleMessage());

        var from = fake.SentMessages[0].From;
        Assert.IsNotNull(from);
        Assert.AreEqual("noreply@example.com", from.Address);
        Assert.AreEqual(string.Empty,          from.DisplayName);
    }

    // ── Disabled path ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SendAsync_does_not_send_when_Enabled_is_false()
    {
        var opts = EnabledOptions();
        opts.Enabled = false;

        var fake = new FakeTransport();
        var svc  = new TestableSmtpEmailService(opts, fake);

        await svc.SendAsync(SampleMessage());

        Assert.AreEqual(0, fake.SentMessages.Count,
            "No message should be sent when Enabled=false.");
    }

    [TestMethod]
    public async Task SendAsync_disposes_transport_after_send()
    {
        var fake = new FakeTransport();
        var svc  = new TestableSmtpEmailService(EnabledOptions(), fake);

        await svc.SendAsync(SampleMessage());

        Assert.IsTrue(fake.Disposed, "Transport must be disposed after each send to release the SMTP socket.");
    }

    // ── NullEmailService ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task NullEmailService_completes_without_throwing()
    {
        IWtmEmailService svc = NullEmailService.Instance;

        // Must not throw regardless of message content.
        await svc.SendAsync(SampleMessage());
        await svc.SendAsync(new EmailMessage());
    }

    // ── DI / AddWtmEmail ──────────────────────────────────────────────────────

    [TestMethod]
    public void AddWtmEmail_registers_SmtpEmailService_as_IWtmEmailService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWtmEmail(o =>
        {
            o.Host        = "smtp.example.com";
            o.FromAddress = "noreply@example.com";
            o.Enabled     = true;
        });

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IWtmEmailService>();

        Assert.IsInstanceOfType(resolved, typeof(SmtpEmailService));
    }

    [TestMethod]
    public void AddWtmNullEmail_registers_NullEmailService_as_IWtmEmailService()
    {
        var services = new ServiceCollection();
        services.AddWtmNullEmail();

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IWtmEmailService>();

        Assert.IsInstanceOfType(resolved, typeof(NullEmailService));
    }

    [TestMethod]
    public void AddWtmEmail_replaces_previously_registered_NullEmailService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWtmNullEmail();
        services.AddWtmEmail(o =>
        {
            o.Host        = "smtp.example.com";
            o.FromAddress = "noreply@example.com";
        });

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IWtmEmailService>();

        Assert.IsInstanceOfType(resolved, typeof(SmtpEmailService),
            "AddWtmEmail must replace the previously registered NullEmailService.");
    }

    // ── Guard clauses ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SendAsync_throws_ArgumentNullException_when_msg_is_null()
    {
        var svc = new SmtpEmailService(EnabledOptions(), NullLogger<SmtpEmailService>.Instance);

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            async () => await svc.SendAsync(null!));
    }

    [TestMethod]
    public async Task SendAsync_throws_ArgumentException_when_To_is_empty()
    {
        var svc = new SmtpEmailService(EnabledOptions(), NullLogger<SmtpEmailService>.Instance);
        var msg = new EmailMessage { To = Array.Empty<string>(), Subject = "s", Body = "b" };

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            async () => await svc.SendAsync(msg));
    }

    [TestMethod]
    public void AddWtmEmail_throws_ArgumentNullException_when_configure_is_null()
    {
        var services = new ServiceCollection();

        Assert.ThrowsException<ArgumentNullException>(
            () => services.AddWtmEmail(null!));
    }
}
