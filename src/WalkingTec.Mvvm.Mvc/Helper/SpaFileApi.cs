using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.Support.FileHandlers;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>What an image processor hands back to <see cref="SpaFileApi"/>.</summary>
    public sealed class SpaImageResult
    {
        /// <summary>The resized image (JPEG); null when the image was not processed.</summary>
        public Stream Resized { get; set; }

        /// <summary>A seekable copy of the input, for a source that has been read to the end and still has to be served as it is (when <see cref="Resized"/> is null).</summary>
        public Stream Buffered { get; set; }

        /// <summary>Why the image was rejected (a fixed text, never file content); logged.</summary>
        public string Error { get; set; }
    }

    /// <summary>
    /// Validates and resizes an image. Supplied by the application, which owns the decoding limits (the template uses <c>ImageDecodeGuard</c>).
    /// Never decode user supplied bytes outside such a guard.
    /// </summary>
    /// <param name="source">the image</param>
    /// <param name="width">requested width (either may be null, not both)</param>
    /// <param name="height">requested height</param>
    public delegate SpaImageResult SpaImageResizer(Stream source, int? width, int? height);

    /// <summary>
    /// The file API of a single page application (the template controller <c>/api/_file/*</c>) on the file endpoint machinery of the framework:
    /// references (<c>_DONOT_USE_FREF</c>), <c>FileEndpoints:Mode</c>, the connection of the request, response headers. A controller calls one of
    /// these methods per action and keeps nothing else, so a fix in the framework reaches every copy of the template.
    /// See docs/security/file-endpoint-authz.md, "single page application".
    /// </summary>
    /// <remarks>
    /// A caller reads a file with a reference: a capability the server wrote next to the data that names the file (<c>Field__fref</c> in list rows,
    /// <c>FileRefs</c> on a view model) or in the answer to an upload; <c>img</c> tags cannot send credentials, so a capability is not bound to a user.
    /// Deleting a file needs the user-bound delete reference that the upload answer carries.
    /// </remarks>
    public static class SpaFileApi
    {
        /// <summary>The route of the file API controller.</summary>
        public const string Route = "api/_file";

        private const string BasePath = "/" + Route;
        private const string ConnectionParameter = "csName";

        #region reads

        /// <summary>The name of a file (purpose <see cref="FilePurpose.Name"/>).</summary>
        public static IActionResult GetFileName(BaseApiController c, WtmFileProvider fp, string id, string csName, string reference)
        {
            var refused = AuthorizeRead(c, "GetFileName", id, FilePurpose.Name, csName, reference, false, null, out var dc);
            if (refused != null)
            {
                return refused;
            }
            Secure(c);
            return c.Ok(fp.GetFileName(id, dc));
        }

        /// <summary>
        /// Shows a file (purpose <see cref="FilePurpose.View"/>). A file that a browser would run as part of the site is sent as an attachment;
        /// <paramref name="resize"/> serves a smaller picture when <paramref name="width"/> or <paramref name="height"/> is given.
        /// </summary>
        public static IActionResult GetFile(BaseApiController c, WtmFileProvider fp, string id, string csName, int? width, int? height, string reference, SpaImageResizer resize = null)
        {
            return Serve(c, fp, "GetFile", id, FilePurpose.View, csName, width, height, reference, resize, attachment: false, embeddedForAnonymous: true, avatar: false);
        }

        /// <summary>Downloads a file (purpose <see cref="FilePurpose.Download"/>), always as an attachment.</summary>
        public static IActionResult DownloadFile(BaseApiController c, WtmFileProvider fp, string id, string csName, string reference)
        {
            return Serve(c, fp, "DownloadFile", id, FilePurpose.Download, csName, null, null, reference, null, attachment: true, embeddedForAnonymous: false, avatar: false);
        }

        /// <summary>
        /// The photo of a user. Anybody may see the photo of a user who is visible in the tenant of the request without a reference, as long as the file is
        /// a picture; any other id follows the rules of <see cref="GetFile"/>. With a main host the request is sent there first.
        /// </summary>
        public static IActionResult GetUserPhoto(BaseApiController c, WtmFileProvider fp, string id, string csName, int? width, int? height, string reference, SpaImageResizer resize = null)
        {
            if (c.ConfigInfo.HasMainHost && c.Wtm.LoginUserInfo?.CurrentTenant == null)
            {
                return c.Redirect(c.Wtm.ConfigInfo.MainHost + c.Request.Path + c.Request.QueryString);
            }
            return Serve(c, fp, "GetUserPhoto", id, FilePurpose.View, csName, width, height, reference, resize, attachment: false, embeddedForAnonymous: true, avatar: true);
        }

        /// <summary>
        /// What there is to know about a file, and nothing else: id, name, extension, length and upload time (purpose <see cref="FilePurpose.Name"/>).
        /// The stored data, the path and the handler information never leave the server.
        /// </summary>
        public static IActionResult GetFileInfo(BaseApiController c, string id, string csName, string reference)
        {
            var refused = AuthorizeRead(c, "GetFileInfo", id, FilePurpose.Name, csName, reference, false, null, out var dc);
            if (refused != null)
            {
                return refused;
            }
            Secure(c);
            object info = null;
            try
            {
                info = dc.Set<FileAttachment>().CheckID(id)
                    .Select(x => new { Id = x.ID, x.FileName, x.FileExt, x.Length, x.UploadTime })
                    .FirstOrDefault();
            }
            catch (Exception)
            {
                // an id that is no id: the same answer as for a file that is not there
            }
            return info == null ? c.BadRequest(Text(c, "Sys.FileNotFound", "File not found")) : c.Ok(info);
        }

        private static IActionResult Serve(BaseApiController c, WtmFileProvider fp, string endpoint, string id, FilePurpose purpose, string csName, int? width, int? height,
            string reference, SpaImageResizer resize, bool attachment, bool embeddedForAnonymous, bool avatar)
        {
            var refused = AuthorizeRead(c, endpoint, id, purpose, csName, reference, embeddedForAnonymous, avatar, out var dc);
            if (refused != null)
            {
                return refused;
            }
            var file = fp.GetFile(id, true, dc);
            if (file == null)
            {
                return c.BadRequest(Text(c, "Sys.FileNotFound", "File not found"));
            }
            var ext = (file.FileExt ?? string.Empty).ToLower();
            var contentType = FileServing.GetContentType(ext);
            Stream source = file.DataStream;
            if (source == null)
            {
                return c.BadRequest(Text(c, "Sys.FileNotFound", "File not found"));
            }
            if (attachment == false && resize != null && (width != null || height != null))
            {
                SpaImageResult processed = null;
                try
                {
                    processed = resize(source, width, height);
                }
                catch (Exception)
                {
                    // the picture is served as it is
                }
                if (processed?.Resized != null)
                {
                    source.Dispose();
                    processed.Buffered?.Dispose();
                    return Send(c, processed.Resized, "image/jpeg", file.FileName, "jpg", false);
                }
                if (processed?.Buffered != null)
                {
                    // the provider stream is consumed now; serve the fallback from the buffered copy
                    source.Dispose();
                    source = processed.Buffered;
                }
                else if (source.CanSeek)
                {
                    source.Position = 0;
                }
                if (string.IsNullOrEmpty(processed?.Error) == false)
                {
                    LogImageRejected(c, endpoint, processed.Error);
                }
            }
            return Send(c, source, contentType, file.FileName, ext, attachment);
        }

        private static IActionResult Send(BaseApiController c, Stream data, string contentType, string fileName, string ext, bool attachment)
        {
            Secure(c);
            if (attachment)
            {
                return new FileStreamResult(data, contentType) { FileDownloadName = fileName ?? (Guid.NewGuid().ToString() + ext) };
            }
            // only the start of the file is looked at; a stream that cannot seek is passed on after it
            var head = new byte[512];
            var read = 0;
            int got;
            while (read < head.Length && (got = data.Read(head, read, head.Length - read)) > 0)
            {
                read += got;
            }
            if (data.CanSeek)
            {
                data.Position = 0;
            }
            else
            {
                data = new PrefixedStream(head, read, data);
            }
            // a file a browser would run as part of the site (HTML, SVG, XML, script) is sent as an attachment, never shown inline
            var active = FileServing.IsActiveContent(ext, head.AsSpan(0, read));
            if (ext == "mp4" && active == false)
            {
                return new FileStreamResult(data, contentType) { EnableRangeProcessing = true };
            }
            c.Response.Headers["Content-Disposition"] = $"{(active ? "attachment" : "inline")}; filename=\"{HttpUtility.UrlEncode(fileName)}\"";
            if (active)
            {
                c.Response.Headers["Content-Security-Policy"] = "sandbox";
            }
            return new FileStreamResult(data, contentType);
        }

        /// <summary>The headers every read answer has: the browser uses the type the server states, and shared caches keep nothing unless the files are public.</summary>
        private static void Secure(BaseApiController c)
        {
            c.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (c.Wtm.ConfigInfo.IsFilePublic == false)
            {
                c.Response.Headers["Cache-Control"] = "private, no-store";
            }
        }

        /// <summary>The data context of a request lives until the response is done (the provider keeps using it for the stream).</summary>
        private static IDataContext Own(BaseApiController c, IDataContext dc)
        {
            if (dc is IDisposable disposable)
            {
                c.Response.RegisterForDispose(disposable);
            }
            return dc;
        }

        /// <summary>Reads the bytes that were peeked at first, then the rest of the stream.</summary>
        internal sealed class PrefixedStream : Stream
        {
            private readonly byte[] _prefix;
            private readonly int _length;
            private readonly Stream _rest;
            private int _position;

            public PrefixedStream(byte[] prefix, int length, Stream rest)
            {
                _prefix = prefix;
                _length = length;
                _rest = rest;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_position < _length)
                {
                    var n = Math.Min(count, _length - _position);
                    Buffer.BlockCopy(_prefix, _position, buffer, offset, n);
                    _position += n;
                    return n;
                }
                return _rest.Read(buffer, offset, count);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _rest.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// The checks every read has: the connection, the reference (or Audit/Legacy), the application's access policy and, for a caller who is not
        /// signed in while the files are not public, the extra rule of the single page application. Returns the refusal, or null.
        /// </summary>
        private static IActionResult AuthorizeRead(BaseApiController c, string endpoint, string id, FilePurpose purpose, string csName, string reference,
            bool embeddedForAnonymous, bool avatar, out IDataContext dc)
        {
            return AuthorizeRead(c, endpoint, id, purpose, csName, reference, embeddedForAnonymous, avatar ? (Func<IDataContext, bool>)(d => c.Wtm.IsUserPhoto(d, id)) : null, out dc);
        }

        private static IActionResult AuthorizeRead(BaseApiController c, string endpoint, string id, FilePurpose purpose, string csName, string reference,
            bool embeddedForAnonymous, Func<IDataContext, bool> isAvatar, out IDataContext dc)
        {
            var wtm = c.Wtm;
            dc = null;
            var connection = wtm.ResolveFileConnection(csName, id, purpose, reference);
            if (connection.Allowed == false)
            {
                return Refuse(c, endpoint, "the connection is not allowed");
            }
            var context = Own(c, wtm.CreateFileDC(connection));
            dc = context;
            var decision = wtm.AuthorizeFileRequest(endpoint, id, purpose, reference, () => ReadInfo(context, id), isAvatar == null ? null : (Func<bool>)(() => isAvatar(context)));
            if (decision.Allowed == false)
            {
                return Forbidden(c);
            }
            // A caller who is not signed in, with files that are not public: the reference decides. What the editors embedded and what was uploaded
            // before the cut-off stay visible in pictures (GetFile), nothing else is given for an id alone.
            if (wtm.LoginUserInfo == null && wtm.ConfigInfo.IsFilePublic == false && wtm.ConfigInfo.FileEndpoints?.Mode == FileEndpointMode.Enforce)
            {
                var allowed = decision.Admission == FileEndpointAdmission.Reference
                    || decision.Admission == FileEndpointAdmission.Avatar
                    || ((decision.Admission == FileEndpointAdmission.Embedded || decision.Admission == FileEndpointAdmission.Grandfathered) && embeddedForAnonymous);
                if (allowed == false)
                {
                    return Refuse(c, endpoint, "a caller who is not signed in needs a reference");
                }
            }
            return null;
        }

        private static FileEndpointFileInfo ReadInfo(IDataContext dc, string id)
        {
            try
            {
                return dc.Set<FileAttachment>().CheckID(id).Select(x => new FileEndpointFileInfo { ExtraInfo = x.ExtraInfo, UploadTime = x.UploadTime }).FirstOrDefault();
            }
            catch (Exception)
            {
                // an id that is no id is a file that is not there, and is answered like one
                return null;
            }
        }

        #endregion

        #region uploads

        /// <summary>
        /// Stores an uploaded file. Needs a signed-in caller. The answer keeps <c>Id</c>, <c>Name</c>, <c>errno</c> and <c>data.url</c> and adds
        /// <c>Url</c> (the address of the file with a reference), <c>Ref</c> (the reference, for pictures and downloads) and <c>DeleteRef</c> (the
        /// reference that lets this user delete the file again).
        /// </summary>
        public static IActionResult Upload(BaseApiController c, WtmFileProvider fp, string sm, string groupName, string subdir, string extra, string csName, string reference)
        {
            var refused = BeginUpload(c, "Upload", csName, reference, out var connection, out var form);
            if (refused != null)
            {
                return refused;
            }
            // the marker of files embedded by a rich text editor is the server's to set
            extra = FileEndpointGuard.StripEmbeddedMarker(extra);
            using (var data = form.OpenReadStream())
            {
                var file = fp.Upload(form.FileName, form.Length, data, groupName, subdir, extra, sm, Own(c, c.Wtm.CreateDC(cskey: connection.Key)));
                return file == null ? c.BadRequest(Text(c, "Sys.UploadFailed", "Upload failed")) : UploadResult(c, file, connection.Key);
            }
        }

        /// <summary>
        /// Same as <see cref="Upload"/>; with <paramref name="width"/> or <paramref name="height"/> the picture is validated and resized by
        /// <paramref name="resize"/> first, and a picture the guard rejects is not stored.
        /// </summary>
        public static IActionResult UploadImage(BaseApiController c, WtmFileProvider fp, int? width, int? height, string sm, string groupName, string subdir, string extra, string csName, string reference, SpaImageResizer resize)
        {
            if ((width == null && height == null) || resize == null)
            {
                return Upload(c, fp, sm, groupName, subdir, extra, csName, reference);
            }
            var refused = BeginUpload(c, "UploadImage", csName, reference, out var connection, out var form);
            if (refused != null)
            {
                return refused;
            }
            extra = FileEndpointGuard.StripEmbeddedMarker(extra);
            SpaImageResult processed;
            using (var upstream = form.OpenReadStream())
            {
                processed = resize(upstream, width, height);
            }
            if (processed?.Resized == null)
            {
                processed?.Buffered?.Dispose();
                LogImageRejected(c, "UploadImage", processed?.Error ?? "Invalid image");
                return c.BadRequest(Text(c, "Sys.UploadFailed", "Upload failed"));
            }
            using (processed.Resized)
            using (processed.Buffered)
            {
                processed.Resized.Position = 0;
                var file = fp.Upload(form.FileName, form.Length, processed.Resized, groupName, subdir, extra, sm, Own(c, c.Wtm.CreateDC(cskey: connection.Key)));
                return file == null ? c.BadRequest(Text(c, "Sys.UploadFailed", "Upload failed")) : UploadResult(c, file, connection.Key);
            }
        }

        /// <summary>
        /// Stores a file that a rich text editor embeds in its content. The server marks it (it stays reachable by its id, because the content outlives
        /// any reference) and answers in the shape of the editor: <c>errno</c> and <c>data.url</c>.
        /// </summary>
        public static IActionResult UploadForEditor(BaseApiController c, WtmFileProvider fp, string sm, string groupName, string subdir, string extra, string csName, string reference)
        {
            var refused = BeginUpload(c, "UploadForEditor", csName, reference, out var connection, out var form);
            if (refused != null)
            {
                return refused;
            }
            extra = FileEndpointGuard.MarkEmbedded(FileEndpointGuard.StripEmbeddedMarker(extra));
            using (var data = form.OpenReadStream())
            {
                var file = fp.Upload(form.FileName, form.Length, data, groupName, subdir, extra, sm, Own(c, c.Wtm.CreateDC(cskey: connection.Key)));
                if (file == null)
                {
                    return c.BadRequest(Text(c, "Sys.UploadFailed", "Upload failed"));
                }
                var url = $"{BasePath}/getfile/{file.GetID()}";
                if (string.IsNullOrEmpty(connection.Key) == false)
                {
                    url += "?" + ConnectionParameter + "=" + Uri.EscapeDataString(connection.Key);
                }
                return c.Content(JsonSerializer.Serialize(new { errno = 0, data = new { url, alt = file.FileName, href = string.Empty } }), "application/json");
            }
        }

        private static IActionResult BeginUpload(BaseApiController c, string endpoint, string csName, string reference, out FileConnectionDecision connection, out IFormFile form)
        {
            connection = null;
            form = null;
            if (c.Wtm.LoginUserInfo == null)
            {
                return c.Unauthorized();
            }
            connection = c.Wtm.ResolveFileConnection(csName, null, FilePurpose.Upload, reference);
            if (connection.Allowed == false)
            {
                return Refuse(c, endpoint, "the connection is not allowed");
            }
            if (c.Request.HasFormContentType == false || c.Request.Form.Files.Count == 0)
            {
                return c.BadRequest(Text(c, "Sys.UploadFailed", "Upload failed"));
            }
            form = c.Request.Form.Files[0];
            return null;
        }

        private static IActionResult UploadResult(BaseApiController c, IWtmFile file, string connectionKey)
        {
            var wtm = c.Wtm;
            var id = file.GetID();
            var key = string.IsNullOrEmpty(connectionKey) ? wtm.CurrentCS : connectionKey;
            // one capability for the file: the address carries the same one that is given on its own
            var capability = wtm.CreateFileCapability(id, FilePurpose.Read, key);
            var body = JsonSerializer.Serialize(new
            {
                Id = id,
                Name = file.FileName,
                errno = 0,
                data = new { url = $"{BasePath}/getfile/{id}" },
                Url = FileEndpointGuard.BuildUrl(BasePath + "/GetFile", id, ConnectionParameter, key, capability),
                Ref = capability,
                DeleteRef = wtm.CreateFileReference(id, FilePurpose.Delete, key)
            });
            return c.Ok(body);
        }

        #endregion

        #region delete

        /// <summary>
        /// Deletes a file. Needs a signed-in caller and the delete reference that was issued to this user for this file when it was uploaded
        /// (<c>DeleteRef</c>); without one the request is refused and logged in every mode except <c>Legacy</c>.
        /// </summary>
        public static IActionResult DeleteFile(BaseApiController c, WtmFileProvider fp, string id, string csName, string reference)
        {
            if (c.Wtm.LoginUserInfo == null)
            {
                return c.Unauthorized();
            }
            var connection = c.Wtm.ResolveFileConnection(csName, id, FilePurpose.Delete, reference);
            if (connection.Allowed == false)
            {
                return Refuse(c, "DeletedFile", "the connection is not allowed");
            }
            var decision = c.Wtm.AuthorizeFileDeletion("DeletedFile", id, reference);
            if (decision.Allowed == false)
            {
                return Forbidden(c);
            }
            fp.DeleteFile(id, Own(c, c.Wtm.CreateDC(cskey: connection.Key)));
            return c.Ok(true);
        }

        #endregion

        #region answers and logging

        private static string Text(BaseApiController c, string key, string fallback)
        {
            try
            {
                var value = c.Localizer?[key];
                return value == null || value.ResourceNotFound ? fallback : value.Value;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static IActionResult Forbidden(BaseApiController c)
        {
            return new ContentResult { StatusCode = StatusCodes.Status403Forbidden, ContentType = "text/plain; charset=utf-8", Content = Text(c, "Sys.NoPrivilege", "No privilege") };
        }

        private static IActionResult Refuse(BaseApiController c, string endpoint, string reason)
        {
            c.Wtm.LoggerFactory?.CreateLogger(typeof(SpaFileApi).FullName)
                .LogWarning("File endpoint {Endpoint} refused a request: {Reason} (user {User})", endpoint, reason, LogText.Sanitize(c.Wtm.LoginUserInfo?.ITCode, 100));
            return Forbidden(c);
        }

        private static void LogImageRejected(BaseApiController c, string action, string reason)
        {
            // reason is a fixed string from the guard, never file content
            c.Wtm.LoggerFactory?.CreateLogger(typeof(SpaFileApi).FullName)
                .LogWarning("{Action}: image rejected by upload guard: {Reason}", action, reason);
        }

        #endregion

        #region startup check

        /// <summary>
        /// Controllers routed at <c>api/_file</c> that expose <c>DeletedFile</c> or <c>GetFileInfo</c> without <see cref="FileEndpointAttribute"/>: copies of the
        /// template from before this helper, whose file actions still serve and delete by id alone.
        /// </summary>
        public static List<string> FindUnpatchedControllers(IEnumerable<Type> types)
        {
            var rv = new List<string>();
            foreach (var type in types ?? Enumerable.Empty<Type>())
            {
                try
                {
                    if (type.IsAbstract || typeof(ControllerBase).IsAssignableFrom(type) == false || IsFileApiRoute(type) == false)
                    {
                        continue;
                    }
                    var classMarked = type.GetCustomAttribute<FileEndpointAttribute>(false) != null;
                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        var name = method.Name;
                        if ((string.Equals(name, "DeletedFile", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "GetFileInfo", StringComparison.OrdinalIgnoreCase))
                            && classMarked == false && method.GetCustomAttribute<FileEndpointAttribute>(false) == null)
                        {
                            rv.Add($"{type.FullName}.{name}");
                        }
                    }
                }
                catch (Exception)
                {
                    // a type that cannot be inspected is not reported
                }
            }
            return rv;
        }

        private static bool IsFileApiRoute(Type type)
        {
            foreach (var route in type.GetCustomAttributes<RouteAttribute>(true))
            {
                if (string.Equals((route.Template ?? string.Empty).Trim('/'), Route, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Logs a warning (never throws) for each controller <see cref="FindUnpatchedControllers"/> finds.</summary>
        public static void LogUnpatchedControllerWarning(IEnumerable<Assembly> assemblies, ILogger logger)
        {
            if (logger == null)
            {
                return;
            }
            try
            {
                var types = new List<Type>();
                foreach (var assembly in assemblies ?? Enumerable.Empty<Assembly>())
                {
                    try
                    {
                        types.AddRange(assembly.GetExportedTypes());
                    }
                    catch (Exception)
                    {
                    }
                }
                foreach (var action in FindUnpatchedControllers(types))
                {
                    logger.LogWarning("{Action} is routed at /{Route} and does not use SpaFileApi / the FileEndpoint marker; update it as described in docs/security/vue3-file-api-downstream.md.", action, Route);
                }
            }
            catch (Exception)
            {
            }
        }

        #endregion
    }
}
