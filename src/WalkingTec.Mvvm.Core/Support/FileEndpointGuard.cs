using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>What a file reference allows the file endpoints to do with a file. Combinable.</summary>
    [Flags]
    public enum FilePurpose
    {
        None = 0,

        /// <summary><c>GetFile</c> with <c>stream=true</c> and <c>ViewFile</c>: show the file in the page.</summary>
        View = 1,

        /// <summary><c>GetFile</c> without <c>stream</c>: download the file.</summary>
        Download = 2,

        /// <summary><c>GetFileName</c>: the name of the file.</summary>
        Name = 4,

        /// <summary>An upload: the reference carries no file, only the connection of the page.</summary>
        Upload = 8,

        /// <summary>View, download and name: what a file shown by a page needs.</summary>
        Read = View | Download | Name
    }

    /// <summary>Why a request for a file was not admitted.</summary>
    public enum FileEndpointRefusal
    {
        None = 0,
        MissingReference,
        InvalidReference,
        ExpiredReference,
        UserMismatch,
        FileMismatch,
        PurposeMismatch,
        UnknownConnection,
        PolicyDenied
    }

    /// <summary>How a request for a file was admitted.</summary>
    public enum FileEndpointAdmission
    {
        None = 0,

        /// <summary>The request carries a valid reference.</summary>
        Reference,

        /// <summary>The file was embedded by a rich text editor (marked by the server when it was uploaded).</summary>
        Embedded,

        /// <summary>The file was uploaded before <see cref="FileEndpointOptions.LegacyUploadedBefore"/>.</summary>
        Grandfathered,

        /// <summary><see cref="FileEndpointMode.Legacy"/>: no check.</summary>
        Legacy,

        /// <summary><see cref="FileEndpointMode.Audit"/>: no valid reference, served and logged.</summary>
        Audited,

        /// <summary>There is no such file; the endpoint answers as it always did.</summary>
        FileMissing
    }

    /// <summary>What a page tells the file endpoints about a file it shows. It travels as an opaque protected string, never in clear text.</summary>
    public sealed class FileEndpointReference
    {
        public const int CurrentVersion = 1;

        [JsonPropertyName("v")] public int Version { get; set; } = CurrentVersion;

        /// <summary>The file (empty for an upload reference).</summary>
        [JsonPropertyName("i")] public string FileId { get; set; }

        [JsonPropertyName("p")] public FilePurpose Purposes { get; set; }

        /// <summary>ITCode of the signed-in user the link was written for (empty: it was written for nobody, the link is open to the page's audience).</summary>
        [JsonPropertyName("u")] public string User { get; set; }

        /// <summary>The tenant the user acts in.</summary>
        [JsonPropertyName("n")] public string Tenant { get; set; }

        /// <summary>The database connection key of the page (empty: the default).</summary>
        [JsonPropertyName("c")] public string Connection { get; set; }

        /// <summary>Expiry, seconds since 1970-01-01 UTC.</summary>
        [JsonPropertyName("e")] public long ExpiresAt { get; set; }
    }

    /// <summary>The facts about a stored file that the decision needs; read only when no valid reference admits the request.</summary>
    public sealed class FileEndpointFileInfo
    {
        public string ExtraInfo { get; set; }
        public DateTime UploadTime { get; set; }
    }

    /// <summary>The facts about the current request that the decision needs, kept apart from <see cref="WTMContext"/> so the decision can be tested alone.</summary>
    public sealed class FileEndpointRequestContext
    {
        public FileEndpointOptions Options { get; set; }
        public string FileId { get; set; }
        public FilePurpose Purpose { get; set; }
        public string Reference { get; set; }
        public string User { get; set; }
        public string Tenant { get; set; }
        public DateTimeOffset Now { get; set; }
        public Func<string, FileEndpointReference> OpenReference { get; set; }

        /// <summary>Reads the file's data (null: there is no such file). Called only when the request carries no valid reference.</summary>
        public Func<FileEndpointFileInfo> GetFileInfo { get; set; }
    }

    /// <summary>The outcome of <see cref="FileEndpointGuard.Authorize"/>.</summary>
    public sealed class FileEndpointDecision
    {
        public bool Allowed { get; private set; }
        public FileEndpointAdmission Admission { get; private set; }

        /// <summary>For a refusal, and for <see cref="FileEndpointAdmission.Audited"/>: why no reference admitted the request.</summary>
        public FileEndpointRefusal Refusal { get; private set; }

        public static FileEndpointDecision Admit(FileEndpointAdmission admission, FileEndpointRefusal refusal = FileEndpointRefusal.None)
            => new FileEndpointDecision { Allowed = true, Admission = admission, Refusal = refusal };

        public static FileEndpointDecision Refuse(FileEndpointRefusal refusal)
            => new FileEndpointDecision { Allowed = false, Admission = FileEndpointAdmission.None, Refusal = refusal };
    }

    /// <summary>What the file endpoints know about a request they are about to serve; see <see cref="IFileAccessPolicy"/>.</summary>
    public sealed class FileAccessRequest
    {
        public string FileId { get; set; }
        public FilePurpose Purpose { get; set; }
        public FileEndpointAdmission Admission { get; set; }

        /// <summary>ITCode of the signed-in caller (null for an anonymous one).</summary>
        public string User { get; set; }
        public string Tenant { get; set; }
        public WTMContext Wtm { get; set; }
    }

    /// <summary>
    /// A seam for applications that want to add their own rule (for example, who owns the record that links the file) after the file
    /// endpoints have admitted a request. Called for every request the endpoints are about to serve in <see cref="FileEndpointMode.Audit"/>
    /// and <see cref="FileEndpointMode.Enforce"/>; returning false refuses it. The default allows everything. Replace it by registering
    /// your own implementation before the framework services are added (the framework registers its default with TryAdd).
    /// </summary>
    public interface IFileAccessPolicy
    {
        bool IsAllowed(FileAccessRequest request);
    }

    /// <summary>The default policy: allows everything.</summary>
    public sealed class AllowAllFileAccessPolicy : IFileAccessPolicy
    {
        public bool IsAllowed(FileAccessRequest request) => true;
    }

    /// <summary>Which connection a file request runs on.</summary>
    public sealed class FileConnectionDecision
    {
        /// <summary>False when the request names a connection that does not exist and must be refused.</summary>
        public bool Allowed { get; private set; }

        /// <summary>The configured key to run on; null: the connection the server chooses.</summary>
        public string Key { get; private set; }

        public static FileConnectionDecision Server() => new FileConnectionDecision { Allowed = true };
        public static FileConnectionDecision Use(string key) => new FileConnectionDecision { Allowed = true, Key = key };
        public static FileConnectionDecision Refuse() => new FileConnectionDecision { Allowed = false };
    }

    /// <summary>
    /// Decides whether the file endpoints serve a file to a request, and which database connection they use.
    /// A file link that the server renders (LayUI TagHelpers, <c>MakeDownloadButton</c>, <c>Wtm.GetFileUrl</c>) carries a reference
    /// (<see cref="ReferenceParameter"/>): a protected string bound to the file, the purpose, the connection, the signed-in user, the tenant and an
    /// expiry. See docs/security/file-endpoint-authz.md.
    /// </summary>
    public static class FileEndpointGuard
    {
        /// <summary>Name of the request parameter that carries the reference.</summary>
        public const string ReferenceParameter = "_DONOT_USE_FREF";

        /// <summary>Purpose string of the protector that signs and encrypts references.</summary>
        public const string ProtectorPurpose = "WalkingTec.Mvvm.Core.FileEndpointReference.v1";

        /// <summary>
        /// Start of the <c>ExtraInfo</c> of a file that a rich text editor embeds. The server writes it; a value sent by a client is removed
        /// (<see cref="StripEmbeddedMarker"/>).
        /// </summary>
        public const string EmbeddedMarker = "wtm:embedded;";

        private const int MaxReferenceLength = 2048;

        #region embedded marker

        /// <summary>Marks <paramref name="extra"/> as the extra information of an embedded file.</summary>
        public static string MarkEmbedded(string extra) => EmbeddedMarker + extra;

        public static bool IsEmbedded(string extra) => extra != null && extra.StartsWith(EmbeddedMarker, StringComparison.OrdinalIgnoreCase);

        /// <summary>Removes the reserved prefix (as often as it is repeated) from a value a client sent; other values are unchanged.</summary>
        public static string StripEmbeddedMarker(string extra)
        {
            while (IsEmbedded(extra))
            {
                extra = extra.Substring(EmbeddedMarker.Length);
            }
            return extra;
        }

        #endregion

        #region protection

        public static IDataProtector CreateProtector(IDataProtectionProvider provider) => provider?.CreateProtector(ProtectorPurpose);

        /// <summary>Opens a reference; null when it is not one this application issued (wrong key, altered, malformed).</summary>
        public static FileEndpointReference Unprotect(IDataProtector protector, string reference)
        {
            if (protector == null || string.IsNullOrEmpty(reference) || reference.Length > MaxReferenceLength)
            {
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<FileEndpointReference>(protector.Unprotect(reference));
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion

        #region issue

        /// <summary>Builds a reference. Returns null when none can be built (no protector, or nothing to refer to).</summary>
        /// <param name="fileId">the file; null or empty for an upload reference</param>
        public static string Issue(IDataProtector protector, FileEndpointOptions options, string fileId, FilePurpose purposes,
            string connectionKey, string user, string tenant, DateTimeOffset now)
        {
            if (protector == null || purposes == FilePurpose.None)
            {
                return null;
            }
            if (string.IsNullOrEmpty(fileId) && (purposes & FilePurpose.Upload) == 0)
            {
                return null;
            }
            var reference = new FileEndpointReference
            {
                FileId = NormalizeFileId(fileId),
                Purposes = purposes,
                User = user ?? string.Empty,
                Tenant = tenant ?? string.Empty,
                Connection = connectionKey ?? string.Empty,
                ExpiresAt = now.Add((options ?? new FileEndpointOptions()).EffectiveLifetime).ToUnixTimeSeconds()
            };
            return protector.Protect(JsonSerializer.Serialize(reference));
        }

        /// <summary>The spelling of a file id inside a reference: a guid in lower case without braces, other text unchanged.</summary>
        public static string NormalizeFileId(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return string.Empty;
            }
            return Guid.TryParse(id, out var g) ? g.ToString("D") : id;
        }

        #endregion

        #region connection

        /// <summary>The key without the read replica suffix (<c>reports_1</c> belongs to <c>reports</c>); empty for no key.</summary>
        public static string NormalizeConnectionKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }
            var index = key.LastIndexOf('_');
            return index > 0 ? key.Substring(0, index) : key;
        }

        private static bool IsDefaultConnection(string key, string serverKey)
        {
            var normalized = NormalizeConnectionKey(key);
            return normalized.Length == 0
                || string.Equals(normalized, "default", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, NormalizeConnectionKey(serverKey), StringComparison.OrdinalIgnoreCase);
        }

        private static string Configured(string key, Configs configs)
        {
            var connections = configs?.Connections;
            if (connections == null || string.IsNullOrEmpty(key))
            {
                return null;
            }
            // a read replica (other_1) stands for its connection (other); a key that has an underscore but is a connection of its own is kept
            var normalized = NormalizeConnectionKey(key);
            var baseKey = connections.FirstOrDefault(x => string.Equals(x.Key, normalized, StringComparison.OrdinalIgnoreCase))?.Key;
            return baseKey ?? connections.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))?.Key;
        }

        /// <summary>
        /// Chooses the database connection of a file request. The connection key in the request (<c>_DONOT_USE_CS</c>) does not choose it:
        /// <list type="bullet">
        /// <item>a valid reference decides (the connection of the page that wrote the link; it must be a configured connection);</item>
        /// <item>without one, the key is used only for a connection listed in <see cref="FileEndpointOptions.RequestConnectionKeys"/>, or when
        /// <see cref="GenericEndpointOptions.AllowRequestConnectionKey"/> is on;</item>
        /// <item>the default connection, or the one the server chose (<paramref name="serverKey"/>), is always the server's choice;</item>
        /// <item>a key that is not a configured connection is refused.</item>
        /// </list>
        /// </summary>
        /// <param name="reference">the opened reference of the request, null when it has none or it is invalid (then only the request key counts)</param>
        /// <param name="serverKey">the connection the server chose for the request (<c>Wtm.CurrentCS</c>)</param>
        public static FileConnectionDecision ResolveConnection(string requestKey, FileEndpointReference reference, Configs configs, string serverKey)
        {
            // a key that names no connection of this application is refused, whatever else the request carries
            if (IsDefaultConnection(requestKey, serverKey) == false && Configured(requestKey, configs) == null)
            {
                return FileConnectionDecision.Refuse();
            }
            if (reference != null && string.IsNullOrEmpty(reference.Connection) == false && IsDefaultConnection(reference.Connection, serverKey) == false)
            {
                var key = Configured(reference.Connection, configs);
                return key == null ? FileConnectionDecision.Refuse() : FileConnectionDecision.Use(key);
            }
            if (reference != null)
            {
                // the reference is for the default connection
                return FileConnectionDecision.Server();
            }
            if (IsDefaultConnection(requestKey, serverKey))
            {
                return FileConnectionDecision.Server();
            }
            var configured = Configured(requestKey, configs);
            if (configured == null)
            {
                return FileConnectionDecision.Refuse();
            }
            if (configs?.FileEndpoints?.IsRequestConnectionKeyListed(requestKey) == true || configs?.GenericEndpoints?.AllowRequestConnectionKey == true)
            {
                return FileConnectionDecision.Use(configured);
            }
            return FileConnectionDecision.Server();
        }

        #endregion

        #region decision

        /// <summary>Decides whether the endpoint may serve the file the request names.</summary>
        public static FileEndpointDecision Authorize(FileEndpointRequestContext ctx)
        {
            var options = ctx.Options ?? new FileEndpointOptions();
            if (options.Mode == FileEndpointMode.Legacy)
            {
                return FileEndpointDecision.Admit(FileEndpointAdmission.Legacy);
            }

            var refusal = FileEndpointRefusal.MissingReference;
            if (string.IsNullOrEmpty(ctx.Reference) == false)
            {
                refusal = CheckReference(ctx, out _);
                if (refusal == FileEndpointRefusal.None)
                {
                    return FileEndpointDecision.Admit(FileEndpointAdmission.Reference);
                }
            }

            var info = ctx.GetFileInfo?.Invoke();
            if (info == null)
            {
                return FileEndpointDecision.Admit(FileEndpointAdmission.FileMissing);
            }
            if (IsEmbedded(info.ExtraInfo))
            {
                return FileEndpointDecision.Admit(FileEndpointAdmission.Embedded);
            }
            if (options.LegacyUploadedBefore != null && info.UploadTime < options.LegacyUploadedBefore.Value)
            {
                return FileEndpointDecision.Admit(FileEndpointAdmission.Grandfathered);
            }
            if (options.Mode == FileEndpointMode.Audit)
            {
                return FileEndpointDecision.Admit(FileEndpointAdmission.Audited, refusal);
            }
            return FileEndpointDecision.Refuse(refusal);
        }

        /// <summary>
        /// Opens and checks the reference of the request. Returns <see cref="FileEndpointRefusal.None"/> and the reference when it is valid for the
        /// file, purpose, user and tenant of the request (for <see cref="FilePurpose.Upload"/> the file is not compared).
        /// </summary>
        public static FileEndpointRefusal CheckReference(FileEndpointRequestContext ctx, out FileEndpointReference reference)
        {
            reference = null;
            if (string.IsNullOrEmpty(ctx.Reference))
            {
                return FileEndpointRefusal.MissingReference;
            }
            var opened = ctx.OpenReference?.Invoke(ctx.Reference);
            if (opened == null || opened.Version != FileEndpointReference.CurrentVersion)
            {
                return FileEndpointRefusal.InvalidReference;
            }
            if (opened.ExpiresAt <= ctx.Now.ToUnixTimeSeconds())
            {
                return FileEndpointRefusal.ExpiredReference;
            }
            // a link that was written for nobody (a page shown to an anonymous visitor) is open to the audience of that page
            if (string.IsNullOrEmpty(opened.User) == false
                && (string.Equals(opened.User, ctx.User ?? string.Empty, StringComparison.Ordinal) == false
                    || string.Equals(opened.Tenant ?? string.Empty, ctx.Tenant ?? string.Empty, StringComparison.Ordinal) == false))
            {
                return FileEndpointRefusal.UserMismatch;
            }
            if (ctx.Purpose == FilePurpose.None || (opened.Purposes & ctx.Purpose) != ctx.Purpose)
            {
                return FileEndpointRefusal.PurposeMismatch;
            }
            if (ctx.Purpose != FilePurpose.Upload
                && (string.IsNullOrEmpty(opened.FileId) || string.Equals(opened.FileId, NormalizeFileId(ctx.FileId), StringComparison.OrdinalIgnoreCase) == false))
            {
                return FileEndpointRefusal.FileMismatch;
            }
            reference = opened;
            return FileEndpointRefusal.None;
        }

        #endregion

        /// <summary>Logs a warning (never throws) when the check is off or relaxed by configuration.</summary>
        public static void LogRelaxedWarning(Configs configs, ILogger logger)
        {
            var options = configs?.FileEndpoints;
            if (options == null || logger == null)
            {
                return;
            }
            if (options.Mode == FileEndpointMode.Legacy)
            {
                logger.LogWarning("FileEndpoints:Mode is Legacy: the file endpoints (/_Framework/GetFile, ViewFile, GetFileName) serve a file to anyone who is allowed to call them and knows its id. Use Audit to find the callers that use bare ids, then Enforce.");
            }
            else if (options.Mode == FileEndpointMode.Audit)
            {
                logger.LogInformation("FileEndpoints:Mode is Audit (the default): requests for a file without a valid reference are served and a warning is logged for each. Read the warnings, then set FileEndpoints:Mode to Enforce.");
            }
            if (options.RequestConnectionKeys != null && options.RequestConnectionKeys.Count > 0)
            {
                logger.LogWarning("FileEndpoints:RequestConnectionKeys lists {Count} entr(y/ies): file requests without a reference may choose these database connections.", options.RequestConnectionKeys.Count);
            }
        }
    }
}
