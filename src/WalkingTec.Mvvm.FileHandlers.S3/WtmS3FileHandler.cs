#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.Support.FileHandlers;

namespace WalkingTec.Mvvm.FileHandlers.S3;

/// <summary>
/// WTM opt-in file handler that stores objects in an S3-compatible store
/// (AWS S3 or MinIO).  Register via
/// <see cref="S3FileHandlerServiceCollectionExtensions.AddWtmS3FileHandler"/>.
/// </summary>
[Display(Name = "s3")]
public sealed class WtmS3FileHandler : IWtmFileHandler
{
    private readonly IAmazonS3 _s3;
    private readonly S3FileHandlerOptions _options;
    private readonly ILogger<WtmS3FileHandler>? _logger;

    /// <summary>
    /// Constructs the handler.  All dependencies are resolved from DI.
    /// </summary>
    public WtmS3FileHandler(
        IAmazonS3 s3,
        IOptions<S3FileHandlerOptions> options,
        ILogger<WtmS3FileHandler>? logger = null)
    {
        _s3 = s3 ?? throw new ArgumentNullException(nameof(s3));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <summary>
    /// Uploads <paramref name="data"/> to S3 and returns the object key as
    /// <c>path</c> and <c>"s3"</c> as <c>handlerInfo</c>.
    /// </summary>
    /// <param name="fileName">Original file name (used to determine extension).</param>
    /// <param name="fileLength">Length hint (informational; not used by the S3 PUT).</param>
    /// <param name="data">File content stream.</param>
    /// <param name="group">Ignored for S3 — pass <see langword="null"/>.</param>
    /// <param name="subdir">Optional subdirectory prefix inserted after <see cref="S3FileHandlerOptions.KeyPrefix"/>.</param>
    /// <param name="extra">Extra info forwarded to the caller unchanged.</param>
    /// <returns>
    /// <c>(objectKey, "s3")</c> on success; <c>(null, null)</c> on failure.
    /// </returns>
    public (string? path, string? handlerInfo) Upload(
        string fileName,
        long fileLength,
        Stream data,
        string? group = null,
        string? subdir = null,
        string? extra = null)
    {
        var ext = string.Empty;
        if (!string.IsNullOrEmpty(fileName))
        {
            var dotPos = fileName.LastIndexOf('.');
            if (dotPos >= 0)
                ext = fileName[(dotPos + 1)..];
        }

        var key = BuildKey(subdir, ext);

        var request = new PutObjectRequest
        {
            BucketName  = _options.BucketName,
            Key         = key,
            InputStream = data,
        };

        try
        {
            var response = _s3.PutObjectAsync(request).GetAwaiter().GetResult();
            return (key, "s3");
        }
        catch (AmazonS3Exception ex)
        {
            _logger?.LogError(ex,
                "S3 PutObject failed for bucket '{Bucket}', key '{Key}'",
                _options.BucketName, key);
            return (null, null);
        }
    }

    /// <summary>
    /// Downloads the object identified by <see cref="IWtmFile.Path"/> and returns
    /// its content as a <see cref="MemoryStream"/>.
    /// Returns <see langword="null"/> if <see cref="IWtmFile.Path"/> is empty or
    /// the object does not exist.
    /// </summary>
    public Stream? GetFileData(IWtmFile file)
    {
        if (string.IsNullOrEmpty(file?.Path))
            return null;

        var request = new GetObjectRequest
        {
            BucketName = _options.BucketName,
            Key        = file.Path,
        };

        try
        {
            var response = _s3.GetObjectAsync(request).GetAwaiter().GetResult();
            var ms = new MemoryStream();
            response.ResponseStream.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }
        catch (AmazonS3Exception ex)
        {
            _logger?.LogWarning(ex,
                "S3 GetObject failed for bucket '{Bucket}', key '{Key}'",
                _options.BucketName, file.Path);
            return null;
        }
    }

    /// <summary>
    /// Deletes the object identified by <see cref="IWtmFile.Path"/> from S3.
    /// Silently no-ops if <see cref="IWtmFile.Path"/> is empty.
    /// </summary>
    public void DeleteFile(IWtmFile file)
    {
        if (string.IsNullOrEmpty(file?.Path))
            return;

        var request = new DeleteObjectRequest
        {
            BucketName = _options.BucketName,
            Key        = file.Path,
        };

        try
        {
            _s3.DeleteObjectAsync(request).GetAwaiter().GetResult();
        }
        catch (AmazonS3Exception ex)
        {
            _logger?.LogWarning(ex,
                "S3 DeleteObject failed for bucket '{Bucket}', key '{Key}'",
                _options.BucketName, file?.Path);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the full S3 object key:
    /// <c>[KeyPrefix/][subdir/]&lt;guid&gt;.ext</c>
    /// Forward slashes are used throughout (S3 convention).
    /// </summary>
    private string BuildKey(string? subdir, string ext)
    {
        var prefix = string.Empty;

        if (!string.IsNullOrEmpty(_options.KeyPrefix))
        {
            prefix = _options.KeyPrefix.TrimEnd('/') + "/";
        }

        if (!string.IsNullOrEmpty(subdir))
        {
            // Sanitise: allow only alphanumeric, underscore, hyphen, period, forward-slash
            var safe = System.Text.RegularExpressions.Regex.Replace(
                subdir, @"[^a-zA-Z0-9_\-\./]", "");
            if (!string.IsNullOrEmpty(safe))
                prefix += safe.TrimEnd('/') + "/";
        }

        var fileName = string.IsNullOrEmpty(ext)
            ? Guid.NewGuid().ToString("N")
            : $"{Guid.NewGuid():N}.{ext}";

        return prefix + fileName;
    }
}
