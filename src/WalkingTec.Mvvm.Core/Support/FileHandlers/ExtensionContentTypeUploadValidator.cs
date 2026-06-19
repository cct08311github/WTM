#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core.ConfigOptions;

namespace WalkingTec.Mvvm.Core.Support.FileHandlers
{
    /// <summary>
    /// Built-in upload validator that enforces the allowlists and size cap
    /// declared in <see cref="FileUploadOptions"/>.
    /// <para>
    /// Activated automatically when <see cref="FileUploadOptions.AllowedExtensions"/>,
    /// <see cref="FileUploadOptions.AllowedContentTypes"/>, or
    /// <see cref="FileUploadOptions.MaxUploadBytes"/> is configured with a non-empty/non-zero value.
    /// Hosts may also register it explicitly via DI to override the default no-op.
    /// </para>
    /// </summary>
    public sealed class ExtensionContentTypeUploadValidator : IUploadValidator
    {
        private readonly FileUploadOptions _options;

        public ExtensionContentTypeUploadValidator(FileUploadOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <inheritdoc />
        public Task<UploadValidationResult> ValidateAsync(UploadValidationContext context, CancellationToken ct = default)
        {
            // 1. Size check (MaxUploadBytes == 0 means unlimited)
            if (_options.MaxUploadBytes > 0 && context.Length > _options.MaxUploadBytes)
            {
                var limitMb = _options.MaxUploadBytes / (1024.0 * 1024.0);
                return Task.FromResult(UploadValidationResult.Reject(
                    $"Upload rejected: file size {context.Length:N0} bytes exceeds the maximum of {limitMb:F1} MB."));
            }

            // 2. Extension check (empty list = allow all)
            var allowed = _options.AllowedExtensions;
            if (allowed != null && allowed.Count > 0)
            {
                // Normalize the incoming extension: ensure leading dot, lowercase
                var ext = NormalizeExtension(context.Extension);

                var match = allowed.Any(a =>
                    string.Equals(NormalizeExtension(a), ext, StringComparison.OrdinalIgnoreCase));

                if (!match)
                {
                    return Task.FromResult(UploadValidationResult.Reject(
                        $"Upload rejected: file extension '{ext}' is not permitted."));
                }
            }

            // 3. Content-Type check (empty list = allow all)
            var allowedCt = _options.AllowedContentTypes;
            if (allowedCt != null && allowedCt.Count > 0)
            {
                // Compare only the media-type part (strip any params like charset=utf-8)
                var mediaType = context.ContentType.Split(';')[0].Trim();

                var ctMatch = allowedCt.Any(a =>
                    string.Equals(a.Split(';')[0].Trim(), mediaType, StringComparison.OrdinalIgnoreCase));

                if (!ctMatch)
                {
                    return Task.FromResult(UploadValidationResult.Reject(
                        $"Upload rejected: content type '{mediaType}' is not permitted."));
                }
            }

            return Task.FromResult(UploadValidationResult.Valid);
        }

        /// <summary>Ensures the extension has a leading dot and is lower-cased.</summary>
        private static string NormalizeExtension(string? ext)
        {
            if (string.IsNullOrEmpty(ext))
                return string.Empty;

            ext = ext.Trim().ToLowerInvariant();
            return ext.StartsWith('.') ? ext : '.' + ext;
        }
    }
}
