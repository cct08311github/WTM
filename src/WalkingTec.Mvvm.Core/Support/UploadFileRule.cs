using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// What an upload endpoint knows about a file it is about to store; see <see cref="IUploadFileRule"/>.
    /// </summary>
    public sealed class UploadFileRequest
    {
        private readonly Func<Stream> _open;
        private readonly long _limit;
        private Stream _content;

        internal UploadFileRequest(WTMContext wtm, string endpoint, string fileName, long length, Func<Stream> open, long limit)
        {
            Wtm = wtm;
            Endpoint = endpoint;
            FileName = fileName;
            Length = length;
            Extension = UploadFileNames.GetExtension(fileName);
            _open = open;
            _limit = limit;
        }

        /// <summary>The endpoint that received the file (for example <c>Upload</c>).</summary>
        public string Endpoint { get; }

        /// <summary>The name the client gave the file. Untrusted; never write it to a log without sanitizing it.</summary>
        public string FileName { get; }

        /// <summary>The last extension of <see cref="FileName"/>, lower case, without the dot; empty when there is none.</summary>
        public string Extension { get; }

        /// <summary>The length of the file in bytes, as reported by the request.</summary>
        public long Length { get; }

        public WTMContext Wtm { get; }

        /// <summary>
        /// Set by a rule that refuses a file: a short fixed text that is written to the log (never file content). Without one, a generic text is logged.
        /// </summary>
        public string RefusalReason { get; set; }

        /// <summary>
        /// The content of the file as a seekable stream positioned at its start (every read of this property rewinds it). The stream is opened on first
        /// use, so a rule that does not look at the content costs nothing; the endpoint owns it and disposes it. A source that cannot seek is copied
        /// (at most <c>FileUploadOptions.UploadLimit</c> bytes) first. Do not dispose it.
        /// </summary>
        public Stream Content
        {
            get
            {
                if (_content == null)
                {
                    _content = Materialize();
                }
                _content.Position = 0;
                return _content;
            }
        }

        private Stream Materialize()
        {
            var source = _open();
            if (source == null)
            {
                throw new InvalidDataException("The upload has no content");
            }
            if (source.CanSeek)
            {
                return source;
            }
            using (source)
            {
                var copy = new MemoryStream();
                try
                {
                    var chunk = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        total += read;
                        if (total > _limit)
                        {
                            throw new InvalidDataException("The upload is larger than the upload limit");
                        }
                        copy.Write(chunk, 0, read);
                    }
                }
                catch
                {
                    copy.Dispose();
                    throw;
                }
                return copy;
            }
        }

        /// <summary>The stream if a rule has read the content, rewound; null otherwise. The caller becomes its owner.</summary>
        internal Stream TakeContent()
        {
            var rv = _content;
            _content = null;
            if (rv != null)
            {
                rv.Position = 0;
            }
            return rv;
        }

        internal void DisposeContent()
        {
            _content?.Dispose();
            _content = null;
        }
    }

    /// <summary>
    /// A rule that decides whether an uploaded file may be stored, applied by the upload endpoints of the framework
    /// (<c>/_Framework/Upload</c>, <c>UploadImage</c>, <c>UploadForLayUIRichTextBox</c>, <c>UploadForLayUIUEditor</c> and the single page application
    /// file API) before anything is stored. It is not applied to <c>WtmFileProvider</c>, which application code calls directly. The framework registers
    /// <see cref="ConfiguredUploadFileRule"/> (it only does something when <c>FileEndpoints:UploadAllowList</c> is configured); replace it by
    /// registering your own implementation before the framework services are added (the framework registers its default with TryAdd). A rule that
    /// throws refuses the upload.
    /// </summary>
    public interface IUploadFileRule
    {
        /// <summary>
        /// Whether the rule is in force. When it is not, the endpoints behave as if there were no rule: the content is not read and nothing changes.
        /// The default is true, so that a rule that only implements <see cref="IsAllowed"/> is always applied.
        /// </summary>
        bool IsActive(WTMContext wtm) => true;

        /// <summary>True to store the file, false to refuse it (set <see cref="UploadFileRequest.RefusalReason"/> to say why in the log).</summary>
        bool IsAllowed(UploadFileRequest request);
    }

    /// <summary>
    /// The default rule: applies the <c>FileEndpoints:UploadAllowList</c> settings (<see cref="UploadAllowListOptions"/>). It is not in force while no
    /// extension is configured.
    /// </summary>
    public sealed class ConfiguredUploadFileRule : IUploadFileRule
    {
        public const string ExtensionRefused = "the file extension is not allowed";
        public const string ContentRefused = "the file content does not match its extension";
        public const string ContentUnreadable = "the file content could not be read";

        public bool IsActive(WTMContext wtm) => wtm?.ConfigInfo?.FileEndpoints?.UploadAllowList?.IsEnabled == true;

        public bool IsAllowed(UploadFileRequest request)
        {
            var options = request.Wtm?.ConfigInfo?.FileEndpoints?.UploadAllowList;
            if (options == null || options.IsEnabled == false)
            {
                return true;
            }
            if (options.IsExtensionAllowed(request.Extension) == false)
            {
                request.RefusalReason = ExtensionRefused;
                return false;
            }
            if (options.VerifyContent == false || UploadContentSniffer.HasRule(request.Extension) == false)
            {
                // no content rule for this type: the extension alone decides, and the content is not read
                return true;
            }
            bool? matches;
            try
            {
                matches = UploadContentSniffer.Matches(request.Extension, request.Content);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is NotSupportedException || ex is ObjectDisposedException)
            {
                request.RefusalReason = ContentUnreadable;
                return false;
            }
            if (matches == false)
            {
                request.RefusalReason = ContentRefused;
                return false;
            }
            return true;
        }
    }

    /// <summary>The outcome of <see cref="UploadFileGuard.Check"/>. Dispose it when the upload is done.</summary>
    public sealed class UploadFileCheck : IDisposable
    {
        /// <summary>False: no rule is in force; the endpoint behaves exactly as it does without this feature.</summary>
        public bool Applied { get; internal set; }

        /// <summary>False: the file must not be stored.</summary>
        public bool Allowed { get; internal set; } = true;

        /// <summary>A fixed text that says why a file was refused (null when it was not).</summary>
        public string Reason { get; internal set; }

        /// <summary>
        /// A seekable copy of the content at its start, when the rule read it (a source that cannot be read twice must be stored from this one);
        /// null otherwise, in which case the endpoint opens the upload itself, as it always did. Owned (and disposed) by this object.
        /// </summary>
        public Stream Content { get; internal set; }

        public void Dispose()
        {
            Content?.Dispose();
            Content = null;
        }
    }

    /// <summary>Applies <see cref="IUploadFileRule"/> for the upload endpoints.</summary>
    public static class UploadFileGuard
    {
        /// <summary>
        /// Asks the registered rule (if any) whether the file may be stored. <paramref name="open"/> opens the content of the upload; it is called at
        /// most once, and only if the rule reads the content. A refusal is logged with a fixed reason (never with the content or an unsanitized name).
        /// </summary>
        public static UploadFileCheck Check(WTMContext wtm, string endpoint, string fileName, long length, Func<Stream> open)
        {
            var check = new UploadFileCheck();
            IUploadFileRule rule;
            try
            {
                rule = wtm?.ServiceProvider?.GetService<IUploadFileRule>();
            }
            catch (Exception)
            {
                rule = null;
            }
            if (rule == null)
            {
                return check;
            }
            var limit = wtm.ConfigInfo?.FileUploadOptions?.UploadLimit ?? 0;
            var request = new UploadFileRequest(wtm, endpoint, fileName, length, open, limit > 0 ? limit : WalkingTec.Mvvm.Core.ConfigOptions.DefaultConfigConsts.DEFAULT_UPLOAD_LIMIT);
            var logger = wtm.LoggerFactory?.CreateLogger("WalkingTec.Mvvm.Core.UploadFileGuard");
            try
            {
                if (rule.IsActive(wtm) == false)
                {
                    return check;
                }
                check.Applied = true;
                check.Allowed = rule.IsAllowed(request);
                check.Reason = check.Allowed ? null : (string.IsNullOrWhiteSpace(request.RefusalReason) ? "the upload rule refused the file" : LogText.Sanitize(request.RefusalReason, 100));
            }
            catch (Exception ex)
            {
                // a rule that fails does not let the upload through
                logger?.LogError("The upload rule failed for {Endpoint}: {ExceptionType}; the upload is refused", endpoint, ex.GetType().Name);
                check.Applied = true;
                check.Allowed = false;
                check.Reason = "the upload rule failed";
            }
            if (check.Allowed)
            {
                check.Content = request.TakeContent();
                return check;
            }
            request.DisposeContent();
            logger?.LogWarning("Upload endpoint {Endpoint} refused a file: {Reason} (user {User})", endpoint, check.Reason, LogText.Sanitize(wtm.LoginUserInfo?.ITCode, 100));
            return check;
        }
    }

    /// <summary>File name helpers of the upload rules.</summary>
    public static class UploadFileNames
    {
        /// <summary>The last extension of a file name, lower case, without the dot; empty when there is none (also for a trailing dot).</summary>
        public static string GetExtension(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return string.Empty;
            }
            var name = fileName.Substring(Math.Max(fileName.LastIndexOf('/'), fileName.LastIndexOf('\\')) + 1);
            var dot = name.LastIndexOf('.');
            if (dot < 0 || dot == name.Length - 1)
            {
                return string.Empty;
            }
            return name.Substring(dot + 1).ToLowerInvariant();
        }

        /// <summary>
        /// The name under which a thumbnail is stored. Thumbnails are always JPEG: a name that already ends in jpg or jpeg is kept, any other final
        /// extension is replaced by <c>.jpg</c> (and one is added when there is none), so that the extension tells the format.
        /// </summary>
        public static string ThumbnailFileName(string fileName)
        {
            fileName ??= string.Empty;
            var ext = GetExtension(fileName);
            if (ext == "jpg" || ext == "jpeg")
            {
                return fileName;
            }
            var stem = ext.Length > 0
                ? fileName.Substring(0, fileName.LastIndexOf('.'))
                : fileName.TrimEnd('.');
            return stem + ".jpg";
        }
    }

    /// <summary>
    /// Checks that the content of a file looks like the type its extension says. Types: xls (OLE2 compound file), xlsx (zip with the workbook part of
    /// an Office Open XML package), jpg/jpeg, png, gif. For any other extension there is no rule (<see cref="Matches"/> returns null).
    /// Only the signature (and for xlsx the part names in the zip directory) is checked; the content is not parsed or decompressed.
    /// </summary>
    public static class UploadContentSniffer
    {
        private static readonly byte[] Ole2 = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>The type of an extension: lower case, and jpeg is jpg. The extension is taken as it is (a space in it is not trimmed away).</summary>
        public static string Canonical(string extension)
        {
            var e = (extension ?? string.Empty).ToLowerInvariant();
            return e == "jpeg" ? "jpg" : e;
        }

        /// <summary>The type an entry of the configured list stands for: surrounding spaces and a leading dot are ignored.</summary>
        public static string FromListEntry(string entry)
        {
            return Canonical((entry ?? string.Empty).Trim().TrimStart('.'));
        }

        /// <summary>True when there is a content rule for the extension (xls, xlsx, jpg/jpeg, png, gif).</summary>
        public static bool HasRule(string extension)
        {
            var type = Canonical(extension);
            return type == "xls" || type == "xlsx" || type == "jpg" || type == "png" || type == "gif";
        }

        /// <summary>True when the content has the signature of the type, false when it has not, null when there is no rule for the type.</summary>
        public static bool? Matches(string extension, Stream seekable)
        {
            switch (Canonical(extension))
            {
                case "xls":
                    return StartsWith(seekable, Ole2);
                case "xlsx":
                    return IsWorkbookPackage(seekable);
                case "jpg":
                    return StartsWith(seekable, new byte[] { 0xFF, 0xD8, 0xFF });
                case "png":
                    return StartsWith(seekable, Png);
                case "gif":
                    return StartsWith(seekable, "GIF87a"u8.ToArray()) || StartsWith(seekable, "GIF89a"u8.ToArray());
                default:
                    return null;
            }
        }

        private static bool StartsWith(Stream s, byte[] signature)
        {
            s.Position = 0;
            var head = new byte[signature.Length];
            var read = 0;
            while (read < head.Length)
            {
                var n = s.Read(head, read, head.Length - read);
                if (n <= 0)
                {
                    break;
                }
                read += n;
            }
            s.Position = 0;
            return read == head.Length && head.SequenceEqual(signature);
        }

        private static bool IsWorkbookPackage(Stream s)
        {
            if (StartsWith(s, new byte[] { 0x50, 0x4B, 0x03, 0x04 }) == false)
            {
                return false;
            }
            try
            {
                s.Position = 0;
                using (var zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true))
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in zip.Entries)
                    {
                        names.Add(entry.FullName);
                    }
                    return names.Contains("[Content_Types].xml") && names.Contains("xl/workbook.xml");
                }
            }
            catch (InvalidDataException)
            {
                return false;
            }
            finally
            {
                s.Position = 0;
            }
        }
    }
}
