#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WalkingTec.Mvvm.Core.Notifications;

/// <summary>
/// DI registration helper for the WTM SMS notification seam (issue #424).
/// </summary>
public static class SmsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="ISmsSender"/> seam with a no-op default implementation
    /// (<see cref="NullSmsSender"/>) that logs and returns <c>true</c> without sending real SMS.
    /// </summary>
    /// <remarks>
    /// This registration is opt-in and does not affect any other WTM services.
    /// <para>
    /// To plug in a real SMS provider, register your implementation <em>after</em> this call:
    /// </para>
    /// <code>
    ///   services.AddWtmSms();
    ///   // Override the default no-op with your implementation:
    ///   services.AddSingleton&lt;ISmsSender, MyAliyunSmsSender&gt;();
    /// </code>
    /// No Aliyun, Tencent, or other cloud SDK is bundled — the host application supplies
    /// the concrete provider.
    /// </remarks>
    /// <param name="services">Service collection.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddWtmSms(this IServiceCollection services)
    {
        services.AddLogging();

        // TryAddSingleton ensures we register the no-op only if the host has not already
        // registered a custom ISmsSender before calling AddWtmSms.
        services.TryAddSingleton<ISmsSender, NullSmsSender>();

        return services;
    }
}
