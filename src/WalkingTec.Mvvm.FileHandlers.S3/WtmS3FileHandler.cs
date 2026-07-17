#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using Amazon.Runtime;
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
            // #680: set Content-Type from the file extension so objects browsed
            // directly in S3 (or served by a CDN/proxy in front of the bucket)
            // get a correct type instead of the SDK's implicit
            // application/octet-stream default. WTM's own GetFile action
            // (Mvc/_FrameworkController) re-derives its response Content-Type
            // from the extension independently, so this is additive metadata
            // rather than something WTM's own download path depends on.
            ContentType = GetContentType(ext),
        };

        try
        {
            var response = _s3.PutObjectAsync(request).GetAwaiter().GetResult();
            return (key, "s3");
        }
        catch (AmazonServiceException ex)
        {
            // #680: broadened from AmazonS3Exception to its base AmazonServiceException
            // so throttling/HTTP-timeout/other-service-level failures are handled the
            // same way (logged, null-signalled) instead of escaping unhandled.
            // AmazonS3Exception derives from AmazonServiceException, so S3-specific
            // failures are still caught identically to before.
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
            using var response = _s3.GetObjectAsync(request).GetAwaiter().GetResult();
            // #680: pre-size the buffer from the response's known Content-Length
            // instead of letting MemoryStream grow organically. An un-sized
            // MemoryStream.CopyTo doubles its backing array on every overflow —
            // each doubling reallocates a new array and copies the entire
            // existing contents into it, so for a multi-GB object the naive
            // path performs several large-object-heap copies and briefly holds
            // both the old and new buffers in memory simultaneously (worse
            // peak memory than a single correctly-sized allocation). This does
            // NOT eliminate full in-memory buffering — IWtmFileHandler.GetFileData
            // must return a seekable stream (Mvc's GetFile action unconditionally
            // does `rv.Position = 0`, and BaseImportVM checks CanSeek before
            // trusting Length), so the S3 response stream itself (forward-only,
            // non-seekable) cannot be returned directly without breaking that
            // contract. ContentLength is capped defensively at
            // MaxPreSizableContentLength to stay within MemoryStream's ~2GB
            // backing-array limit; falls back to the default growable
            // constructor when the length is unknown/negative/oversized.
            var contentLength = response.Headers?.ContentLength ?? 0;
            var ms = contentLength > 0 && contentLength <= MaxPreSizableContentLength
                ? new MemoryStream((int)contentLength)
                : new MemoryStream();
            response.ResponseStream.CopyTo(ms);
            ms.Position = 0;
            return ms;
        }
        catch (AmazonServiceException ex)
        {
            // #680: broadened from AmazonS3Exception — see Upload() for rationale.
            _logger?.LogWarning(ex,
                "S3 GetObject failed for bucket '{Bucket}', key '{Key}'",
                _options.BucketName, file.Path);
            return null;
        }
    }

    /// <summary>
    /// Upper bound for pre-sizing the <see cref="GetFileData"/> buffer from the
    /// S3 response's Content-Length. <see cref="MemoryStream"/>'s backing array
    /// is limited to just under 2 GiB (<c>Array.MaxLength</c>-ish); staying well
    /// under that avoids an <see cref="OutOfMemoryException"/>/overflow on the
    /// pre-size attempt itself for pathologically large objects — those still
    /// download via the default growable constructor, unchanged from before
    /// this fix (no smaller and no larger than the pre-#680 buffering).
    /// </summary>
    private const long MaxPreSizableContentLength = 1_900_000_000L;

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
        catch (AmazonServiceException ex)
        {
            // #680: broadened from AmazonS3Exception — see Upload() for rationale.
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

            // #680: the character-class filter above still allows '.' and '/'
            // (needed for legitimate segments like "archive.2024/reports"), so a
            // caller-supplied subdir of "../../secrets" survived unchanged and
            // could walk the resulting key above KeyPrefix. Reject '.'/'..'
            // path segments outright rather than trying to "resolve" them —
            // only whole segments that are exactly "." or ".." are dropped;
            // a segment merely containing a dot (e.g. "archive.2024") is
            // untouched.
            var segments = safe.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var normalizedSegments = new List<string>(segments.Length);
            foreach (var segment in segments)
            {
                if (segment != "." && segment != "..")
                    normalizedSegments.Add(segment);
            }
            var normalized = string.Join('/', normalizedSegments);
            if (!string.IsNullOrEmpty(normalized))
                prefix += normalized + "/";
        }

        var fileName = string.IsNullOrEmpty(ext)
            ? Guid.NewGuid().ToString("N")
            : $"{Guid.NewGuid():N}.{ext}";

        return prefix + fileName;
    }

    /// <summary>
    /// Small extension→MIME-type map used to set <see cref="PutObjectRequest.ContentType"/>
    /// on upload (#680). Deliberately conservative — unknown extensions fall back to
    /// <c>application/octet-stream</c> rather than guessing, matching the same
    /// fail-safe default the AWS SDK itself would otherwise apply.
    /// </summary>
    private static string GetContentType(string ext)
    {
        if (string.IsNullOrEmpty(ext)) { return "application/octet-stream"; }
        return ext.ToLowerInvariant() switch
        {
            "txt" or "csv" or "log" => "text/plain",
            "html" or "htm" => "text/html",
            "css" => "text/css",
            "json" => "application/json",
            "xml" => "application/xml",
            "pdf" => "application/pdf",
            "zip" => "application/zip",
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            "webp" => "image/webp",
            "svg" => "image/svg+xml",
            "mp4" => "video/mp4",
            "mp3" => "audio/mpeg",
            "doc" => "application/msword",
            "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "xls" => "application/vnd.ms-excel",
            "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "ppt" => "application/vnd.ms-powerpoint",
            "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => "application/octet-stream",
        };
    }
}
