#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Notifications;

namespace WalkingTec.Mvvm.Core.Test.Notifications;

/// <summary>
/// Tests for issue #424: verifies that the ISmsSender seam resolves, is safely callable,
/// and that a custom ISmsSender override is invoked correctly.
/// </summary>
[TestClass]
public class SmsSeamTests
{
    // ── NullSmsSender direct tests ────────────────────────────────────────

    [TestMethod]
    public async Task NullSmsSender_SendAsync_returns_true_without_throwing()
    {
        // Arrange: construct via DI so NullLogger is injected.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWtmSms();
        var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<ISmsSender>();

        // Act
        var result = await sender.SendAsync("+8613812345678", "Test message");

        // Assert
        Assert.IsTrue(result, "NullSmsSender.SendAsync must return true.");
    }

    [TestMethod]
    public async Task NullSmsSender_SendAsync_with_cancellation_token_does_not_throw()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWtmSms();
        var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<ISmsSender>();

        using var cts = new CancellationTokenSource();
        var result = await sender.SendAsync("+8613812345678", "Test message", cts.Token);

        Assert.IsTrue(result);
    }

    [TestMethod]
    public async Task NullSmsSender_SendTemplateAsync_returns_true_without_throwing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWtmSms();
        var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<ISmsSender>();

        var templateParams = new[]
        {
            new KeyValuePair<string, string>("code", "123456"),
            new KeyValuePair<string, string>("product", "WTM")
        };

        var result = await sender.SendTemplateAsync(
            "+8613812345678",
            "SMS_12345678",
            templateParams);

        Assert.IsTrue(result, "NullSmsSender.SendTemplateAsync must return true.");
    }

    [TestMethod]
    public async Task NullSmsSender_SendTemplateAsync_accepts_null_template_params()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWtmSms();
        var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<ISmsSender>();

        var result = await sender.SendTemplateAsync(
            "+8613812345678",
            "SMS_12345678",
            templateParams: null);

        Assert.IsTrue(result, "NullSmsSender.SendTemplateAsync must accept null params.");
    }

    // ── DI seam — AddWtmSms registers ISmsSender ─────────────────────────

    [TestMethod]
    public void AddWtmSms_registers_ISmsSender_as_singleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWtmSms();

        var provider = services.BuildServiceProvider();
        var sender1 = provider.GetRequiredService<ISmsSender>();
        var sender2 = provider.GetRequiredService<ISmsSender>();

        Assert.IsNotNull(sender1, "ISmsSender must be resolvable after AddWtmSms().");
        Assert.AreSame(sender1, sender2, "Singleton — same instance on repeated resolution.");
    }

    [TestMethod]
    public void AddWtmSms_resolves_to_NullSmsSender_by_default()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWtmSms();

        var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<ISmsSender>();

        // The default must be NullSmsSender.
        StringAssert.Contains(
            sender.GetType().Name,
            "NullSmsSender",
            "Default ISmsSender must be NullSmsSender.");
    }

    [TestMethod]
    public async Task AddWtmSms_default_sender_SendAsync_is_safely_callable()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWtmSms();
        var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<ISmsSender>();

        // Must not throw and must return true.
        var result = await sender.SendAsync("+8613812345678", "Hello WTM");

        Assert.IsTrue(result);
    }

    [TestMethod]
    public async Task AddWtmSms_default_sender_SendTemplateAsync_is_safely_callable()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWtmSms();
        var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<ISmsSender>();

        var result = await sender.SendTemplateAsync(
            "+8613812345678",
            "SMS_TEST",
            new[] { new KeyValuePair<string, string>("code", "999") });

        Assert.IsTrue(result);
    }

    // ── DI seam — custom provider override ───────────────────────────────

    [TestMethod]
    public async Task Custom_ISmsSender_SendAsync_is_invoked_when_registered_after_AddWtmSms()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWtmSms();
        // Override: last registration of a singleton wins over TryAdd.
        services.AddSingleton<ISmsSender, FakeSmsSender>();

        var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<ISmsSender>();

        var result = await sender.SendAsync("+8613812345678", "Override test");

        Assert.IsTrue(result, "Custom sender must return true.");
        Assert.IsInstanceOfType<FakeSmsSender>(sender, "Custom FakeSmsSender must be resolved.");
        Assert.AreEqual(1, ((FakeSmsSender)sender).SendAsyncCallCount,
            "FakeSmsSender.SendAsync must have been called once.");
    }

    [TestMethod]
    public async Task Custom_ISmsSender_SendTemplateAsync_is_invoked()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddWtmSms();
        services.AddSingleton<ISmsSender, FakeSmsSender>();

        var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<ISmsSender>();

        var result = await sender.SendTemplateAsync(
            "+8613812345678",
            "SMS_TPL_001",
            new[] { new KeyValuePair<string, string>("key", "value") });

        Assert.IsTrue(result);
        Assert.IsInstanceOfType<FakeSmsSender>(sender);
        Assert.AreEqual(1, ((FakeSmsSender)sender).SendTemplateAsyncCallCount,
            "FakeSmsSender.SendTemplateAsync must have been called once.");
    }

    [TestMethod]
    public void AddWtmSms_is_idempotent_when_called_multiple_times()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Call twice — TryAdd semantics mean the second call is a no-op.
        services.AddWtmSms();
        services.AddWtmSms();

        var provider = services.BuildServiceProvider();
        // Must still resolve exactly one instance without throwing.
        var sender = provider.GetRequiredService<ISmsSender>();
        Assert.IsNotNull(sender);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────

/// <summary>
/// A simple counting spy that verifies the custom-provider override path.
/// </summary>
internal sealed class FakeSmsSender : ISmsSender
{
    public int SendAsyncCallCount { get; private set; }
    public int SendTemplateAsyncCallCount { get; private set; }

    public Task<bool> SendAsync(string phoneNumber, string message, CancellationToken ct = default)
    {
        SendAsyncCallCount++;
        return Task.FromResult(true);
    }

    public Task<bool> SendTemplateAsync(
        string phoneNumber,
        string templateCode,
        IReadOnlyList<KeyValuePair<string, string>>? templateParams,
        CancellationToken ct = default)
    {
        SendTemplateAsyncCallCount++;
        return Task.FromResult(true);
    }
}
