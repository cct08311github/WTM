#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core.Notifications.Providers;

namespace WalkingTec.Mvvm.Core.Notifications;

/// <summary>
/// DI registration helpers for the WTM webhook/notification sink subsystem (issue #219).
/// </summary>
public static class WebhookServiceCollectionExtensions
{
    /// <summary>
    /// Registers a single <see cref="IWtmWebhookSink"/> configured via <paramref name="configure"/>.
    /// The sink is opt-in and does not affect any existing WTM services.
    /// </summary>
    /// <remarks>
    /// The named <c>HttpClient</c> (<c>WtmWebhookSink</c>) is registered with:
    /// <list type="bullet">
    ///   <item><c>AllowAutoRedirect = false</c> — redirects cannot bypass the SSRF guard.</item>
    ///   <item>A <c>ConnectCallback</c> that enforces SSRF blocking at actual TCP connect time
    ///         (TOCTOU-safe DNS pinning).</item>
    /// </list>
    /// </remarks>
    /// <param name="services">Service collection.</param>
    /// <param name="configure">Webhook options configuration delegate.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddWtmWebhookSink(
        this IServiceCollection services,
        Action<WtmWebhookOptions> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        services.AddLogging();
        EnsureHttpClient(services);

        var options = new WtmWebhookOptions();
        configure(options);

        // Validate eagerly at registration time so mistakes surface at startup.
        WebhookProviderBase.ValidateOptions(options);

        // Register as a factory so IHttpClientFactory is resolved from the real container.
        services.AddSingleton<IWtmWebhookSink>(sp => BuildSink(sp, options));

        return services;
    }

    /// <summary>
    /// Registers multiple <see cref="IWtmWebhookSink"/> instances (one per <paramref name="configs"/>)
    /// and wraps them in a <see cref="CompositeWtmWebhookSink"/> that fans out to all sinks in parallel.
    /// </summary>
    public static IServiceCollection AddWtmWebhookSinks(
        this IServiceCollection services,
        params Action<WtmWebhookOptions>[] configs)
    {
        if (configs is null || configs.Length == 0)
            throw new ArgumentException("At least one configuration delegate is required.", nameof(configs));

        services.AddLogging();
        EnsureHttpClient(services);

        // Capture options upfront (validation at registration time).
        var optionsList = new List<WtmWebhookOptions>(configs.Length);
        foreach (var configure in configs)
        {
            var opts = new WtmWebhookOptions();
            configure(opts);
            WebhookProviderBase.ValidateOptions(opts);
            optionsList.Add(opts);
        }

        services.AddSingleton<IWtmWebhookSink>(sp =>
        {
            if (optionsList.Count == 1)
                return BuildSink(sp, optionsList[0]);

            var sinks = new List<IWtmWebhookSink>(optionsList.Count);
            foreach (var opts in optionsList)
                sinks.Add(BuildSink(sp, opts));

            var logger = sp.GetRequiredService<ILogger<CompositeWtmWebhookSink>>();
            return new CompositeWtmWebhookSink(sinks, logger);
        });

        return services;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static IWtmWebhookSink BuildSink(IServiceProvider sp, WtmWebhookOptions options)
    {
        var factory = sp.GetRequiredService<IHttpClientFactory>();

        return options.Provider switch
        {
            WebhookProviderKind.DingTalk => new DingTalkWebhookSink(
                factory, options,
                sp.GetRequiredService<ILogger<DingTalkWebhookSink>>()),

            WebhookProviderKind.WeCom => new WeComWebhookSink(
                factory, options,
                sp.GetRequiredService<ILogger<WeComWebhookSink>>()),

            WebhookProviderKind.Feishu => new FeishuWebhookSink(
                factory, options,
                sp.GetRequiredService<ILogger<FeishuWebhookSink>>()),

            WebhookProviderKind.Slack => new SlackWebhookSink(
                factory, options,
                sp.GetRequiredService<ILogger<SlackWebhookSink>>()),

            WebhookProviderKind.MicrosoftTeams => new MicrosoftTeamsWebhookSink(
                factory, options,
                sp.GetRequiredService<ILogger<MicrosoftTeamsWebhookSink>>()),

            _ => throw new NotSupportedException($"Webhook provider '{options.Provider}' is not supported.")
        };
    }

    private static void EnsureHttpClient(IServiceCollection services)
    {
        // AddHttpClient() is idempotent — safe to call multiple times.
        services.AddHttpClient();

        // Register the named HttpClient with SSRF defences:
        //   - AllowAutoRedirect=false: 302 redirects cannot bypass the SSRF guard.
        //   - ConnectCallback: enforces SSRF blocking at TCP connect time (TOCTOU-safe).
        services.AddHttpClient(WebhookProviderBase.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback   = WebhookProviderBase.PinnedConnectAsync
            });
    }
}
