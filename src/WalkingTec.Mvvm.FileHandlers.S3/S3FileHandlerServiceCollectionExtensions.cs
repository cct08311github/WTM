#nullable enable
using System;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using WalkingTec.Mvvm.Core.Support.FileHandlers;

namespace WalkingTec.Mvvm.FileHandlers.S3;

/// <summary>
/// <see cref="IServiceCollection"/> extensions for the WTM S3/MinIO file handler.
/// </summary>
public static class S3FileHandlerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the opt-in S3/MinIO file handler.
    /// <para>
    /// Registers:
    /// <list type="bullet">
    ///   <item><see cref="IAmazonS3"/> as <b>singleton</b> (one client per process).</item>
    ///   <item><see cref="WtmS3FileHandler"/> as <b>scoped</b> <see cref="IWtmFileHandler"/>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Minimal MinIO example:
    /// <code>
    /// services.AddWtmS3FileHandler(o =>
    /// {
    ///     o.ServiceUrl    = "http://localhost:9000";
    ///     o.BucketName    = "my-bucket";
    ///     o.AccessKey     = "minioadmin";
    ///     o.SecretKey     = "minioadmin";
    ///     o.ForcePathStyle = true;
    /// });
    /// </code>
    /// </para>
    /// <para>
    /// AWS S3 example:
    /// <code>
    /// services.AddWtmS3FileHandler(o =>
    /// {
    ///     o.BucketName = "my-aws-bucket";
    ///     o.AccessKey  = "AKIAIOSFODNN7EXAMPLE";
    ///     o.SecretKey  = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    ///     o.Region     = "ap-northeast-1";
    /// });
    /// </code>
    /// </para>
    /// </summary>
    /// <param name="services">The DI service collection.</param>
    /// <param name="configure">Delegate that configures <see cref="S3FileHandlerOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddWtmS3FileHandler(
        this IServiceCollection services,
        Action<S3FileHandlerOptions> configure)
    {
        if (configure is null)
            throw new ArgumentNullException(nameof(configure));

        // Capture and validate options eagerly so misconfiguration is detected at startup.
        var options = new S3FileHandlerOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.BucketName))
            throw new ArgumentException(
                $"{nameof(S3FileHandlerOptions.BucketName)} must not be empty.",
                nameof(configure));

        if (string.IsNullOrWhiteSpace(options.AccessKey))
            throw new ArgumentException(
                $"{nameof(S3FileHandlerOptions.AccessKey)} must not be empty.",
                nameof(configure));

        if (string.IsNullOrWhiteSpace(options.SecretKey))
            throw new ArgumentException(
                $"{nameof(S3FileHandlerOptions.SecretKey)} must not be empty.",
                nameof(configure));

        // Register options for IOptions<S3FileHandlerOptions> injection.
        services.Configure<S3FileHandlerOptions>(configure);

        // Register IAmazonS3 as singleton — AmazonS3Client is thread-safe and expensive to create.
        services.AddSingleton<IAmazonS3>(_ =>
        {
            var credentials = new BasicAWSCredentials(options.AccessKey, options.SecretKey);
            var config = new AmazonS3Config
            {
                ForcePathStyle = options.ForcePathStyle,
            };

            if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
            {
                // MinIO / custom S3-compatible endpoint.
                config.ServiceURL = options.ServiceUrl;
            }
            else
            {
                // AWS: resolve region by system name.
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
            }

            return new AmazonS3Client(credentials, config);
        });

        // Register the handler as scoped IWtmFileHandler (one per HTTP request, matching
        // the existing WtmLocalFileHandler / WtmOssFileHandler lifetime pattern).
        services.AddScoped<IWtmFileHandler, WtmS3FileHandler>();

        return services;
    }
}
