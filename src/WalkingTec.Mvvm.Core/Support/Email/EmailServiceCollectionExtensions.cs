#nullable enable
using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core.Support.Email;

/// <summary>
/// DI registration helpers for the WTM opt-in e-mail notification service (issue #421).
/// </summary>
public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SmtpEmailService"/> as the <see cref="IWtmEmailService"/>
    /// implementation, configured via <paramref name="configure"/>.
    /// </summary>
    /// <remarks>
    /// This call is entirely opt-in.  Without it, no <see cref="IWtmEmailService"/> is
    /// registered by default; consuming code that conditionally resolves the service
    /// should check whether it is present, or call
    /// <see cref="AddWtmNullEmail"/> to ensure a safe no-op fallback is always available.
    /// <para>
    /// When <see cref="EmailOptions.Enabled"/> is <c>false</c> the registered
    /// <see cref="SmtpEmailService"/> silently drops all messages, so it is safe to register
    /// in non-production environments and enable selectively via configuration.
    /// </para>
    /// </remarks>
    /// <param name="services">Service collection.</param>
    /// <param name="configure">Delegate that populates <see cref="EmailOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddWtmEmail(
        this IServiceCollection services,
        Action<EmailOptions> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));

        services.AddLogging();

        var options = new EmailOptions();
        configure(options);

        // Remove any previously registered no-op before installing the real service.
        services.RemoveAll<IWtmEmailService>();

        services.AddSingleton<IWtmEmailService>(sp =>
            new SmtpEmailService(options, sp.GetRequiredService<ILogger<SmtpEmailService>>()));

        return services;
    }

    /// <summary>
    /// Registers the <see cref="NullEmailService"/> (silent no-op) as
    /// <see cref="IWtmEmailService"/> so that the interface can always be resolved
    /// without throwing, even when e-mail has not been configured.
    /// </summary>
    /// <remarks>
    /// Call this method if your application resolves <see cref="IWtmEmailService"/> from
    /// DI unconditionally but e-mail is not always set up.  <see cref="AddWtmEmail"/>
    /// will replace this registration when called afterwards.
    /// </remarks>
    public static IServiceCollection AddWtmNullEmail(this IServiceCollection services)
    {
        services.TryAddSingleton<IWtmEmailService>(NullEmailService.Instance);
        return services;
    }
}
