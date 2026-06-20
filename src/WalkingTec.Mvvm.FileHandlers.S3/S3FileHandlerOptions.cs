#nullable enable
using System;

namespace WalkingTec.Mvvm.FileHandlers.S3;

/// <summary>
/// Configuration for <see cref="WtmS3FileHandler"/>.
/// Supports AWS S3 and MinIO (or any S3-compatible endpoint).
/// </summary>
public sealed class S3FileHandlerOptions
{
    /// <summary>
    /// S3-compatible service URL / endpoint.
    /// For AWS leave <c>null</c> and set <see cref="Region"/>.
    /// For MinIO supply the base URL, e.g. <c>http://localhost:9000</c>.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// Target bucket name (must already exist or be created before first upload).
    /// </summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>
    /// AWS / MinIO access key (Access Key ID).
    /// </summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>
    /// AWS / MinIO secret key (Secret Access Key).
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// AWS region system name (e.g. <c>us-east-1</c>).
    /// Ignored when <see cref="ServiceUrl"/> is provided (MinIO / custom endpoint).
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// When <c>true</c> the SDK uses path-style addressing (<c>http://host/bucket/key</c>)
    /// instead of virtual-hosted-style (<c>http://bucket.host/key</c>).
    /// Required for MinIO and most self-hosted S3-compatible stores.
    /// Default: <c>false</c> (AWS convention); set to <c>true</c> for MinIO.
    /// </summary>
    public bool ForcePathStyle { get; set; } = false;

    /// <summary>
    /// Optional prefix prepended to every object key before storing in S3.
    /// Example: <c>"uploads/"</c> — resulting keys will be <c>uploads/&lt;guid&gt;.ext</c>.
    /// Trailing slash is added automatically when absent and value is non-empty.
    /// </summary>
    public string? KeyPrefix { get; set; }
}
