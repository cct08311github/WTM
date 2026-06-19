#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WalkingTec.Mvvm.Core.Support.FileHandlers
{
    /// <summary>
    /// Context passed to <see cref="IUploadValidator"/> containing all metadata about the incoming file.
    /// </summary>
    public sealed class UploadValidationContext
    {
        /// <summary>Original file name as provided by the client (not sanitized).</summary>
        public string FileName { get; init; } = string.Empty;

        /// <summary>
        /// File extension, normalized to lowercase with a leading dot (e.g. ".png").
        /// Empty string when the file has no extension.
        /// </summary>
        public string Extension { get; init; } = string.Empty;

        /// <summary>Content-Type header value as provided by the client.</summary>
        public string ContentType { get; init; } = string.Empty;

        /// <summary>Reported byte length of the uploaded file.</summary>
        public long Length { get; init; }

        /// <summary>
        /// Factory that opens a readable, non-seekable stream of the file body.
        /// Validators that need to inspect bytes should open the stream here.
        /// The caller will dispose the stream after the validator returns.
        /// </summary>
        public Func<Stream>? OpenReadStream { get; init; }
    }

    /// <summary>
    /// Result returned by <see cref="IUploadValidator.ValidateAsync"/>.
    /// </summary>
    public sealed class UploadValidationResult
    {
        /// <summary>When <c>true</c> the upload may proceed; when <c>false</c> it must be rejected.</summary>
        public bool IsValid { get; init; }

        /// <summary>
        /// Human-readable rejection reason, safe to return to the caller.
        /// <c>null</c> when <see cref="IsValid"/> is <c>true</c>.
        /// </summary>
        public string? Error { get; init; }

        /// <summary>Singleton result indicating the upload is accepted.</summary>
        public static readonly UploadValidationResult Valid = new() { IsValid = true };

        /// <summary>Creates a rejection result with the supplied message.</summary>
        public static UploadValidationResult Reject(string error) =>
            new() { IsValid = false, Error = error };
    }

    /// <summary>
    /// Opt-in seam for custom upload validation (e.g. AV scanning, magic-byte checks).
    /// <para>
    /// Register a custom implementation via <c>services.AddScoped&lt;IUploadValidator, MyValidator&gt;()</c>
    /// <strong>after</strong> calling <c>AddWtmContext</c>.  The default no-op implementation
    /// (<see cref="NoOpUploadValidator"/>) is registered by WTM and preserves existing behaviour.
    /// </para>
    /// </summary>
    public interface IUploadValidator
    {
        /// <summary>
        /// Validate the incoming file.  Return <see cref="UploadValidationResult.Valid"/> to allow,
        /// or <see cref="UploadValidationResult.Reject(string)"/> to block.
        /// </summary>
        Task<UploadValidationResult> ValidateAsync(UploadValidationContext context, CancellationToken ct = default);
    }

    /// <summary>
    /// Default no-op validator — always accepts the upload.
    /// Registered by WTM so existing apps are unaffected until they opt in
    /// by configuring allowlists or registering a custom <see cref="IUploadValidator"/>.
    /// </summary>
    public sealed class NoOpUploadValidator : IUploadValidator
    {
        /// <inheritdoc />
        public Task<UploadValidationResult> ValidateAsync(UploadValidationContext context, CancellationToken ct = default)
            => Task.FromResult(UploadValidationResult.Valid);
    }
}
