using System;
using System.Collections.Generic;
using System.Linq;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>
    /// How the file endpoints of the framework (<c>/_Framework/GetFile</c>, <c>ViewFile</c>, <c>GetFileName</c>) decide whether a request for a
    /// file by its id may be served.
    /// </summary>
    public enum FileEndpointMode
    {
        /// <summary>
        /// The endpoints serve a file by its id as before this check existed. The way the database connection of a request is chosen
        /// (<see cref="FileEndpointOptions.RequestConnectionKeys"/>) still applies unless
        /// <see cref="GenericEndpointOptions.AllowRequestConnectionKey"/> is on. Meant as a rollback.
        /// </summary>
        Legacy = 0,

        /// <summary>
        /// Default of this release. References are issued with every file link the server renders and verified, but a request without a
        /// valid one is still served; a warning (caller, endpoint, referer; never the content of the file) is logged for each such request.
        /// Use it to find the callers that still use bare ids, then switch to <see cref="Enforce"/>.
        /// </summary>
        Audit = 1,

        /// <summary>
        /// A file is served only to a request that carries a valid reference (see <see cref="FileEndpointGuard"/>), or whose file was
        /// embedded by a rich text editor, or was uploaded before <see cref="FileEndpointOptions.LegacyUploadedBefore"/>.
        /// </summary>
        Enforce = 2
    }

    /// <summary>
    /// Settings of the file endpoints. Configuration section <c>FileEndpoints</c>.
    /// </summary>
    public class FileEndpointOptions
    {
        /// <summary>See <see cref="FileEndpointMode"/>. Default <see cref="FileEndpointMode.Audit"/>.</summary>
        public FileEndpointMode Mode { get; set; } = FileEndpointMode.Audit;

        /// <summary>
        /// How long a file reference stays valid, in minutes. Default 480. A page that stays open longer must be reloaded. Values below 1 are
        /// treated as the default.
        /// </summary>
        public int ReferenceLifetimeMinutes { get; set; } = DefaultReferenceLifetimeMinutes;

        /// <summary>
        /// Files whose upload time is earlier than this moment keep being served by their id in <see cref="FileEndpointMode.Enforce"/>
        /// (files that were uploaded before the check existed and are linked from places that cannot carry a reference). Local server time,
        /// as stored in <c>FileAttachment.UploadTime</c>. Default: not set.
        /// </summary>
        public DateTime? LegacyUploadedBefore { get; set; }

        /// <summary>
        /// Connection keys (other than the default connection) that the file endpoints accept in the <c>_DONOT_USE_CS</c> parameter of a
        /// request that has no reference. A reference carries the connection of the page that wrote it and does not need an entry.
        /// Default: empty, so the connection is always the one the server chooses.
        /// </summary>
        public List<string> RequestConnectionKeys { get; set; } = new List<string>();

        public const int DefaultReferenceLifetimeMinutes = 480;

        /// <summary>The lifetime in effect (the default when the configured value is below 1).</summary>
        public TimeSpan EffectiveLifetime => TimeSpan.FromMinutes(ReferenceLifetimeMinutes < 1 ? DefaultReferenceLifetimeMinutes : ReferenceLifetimeMinutes);

        /// <summary>True when <paramref name="key"/> (compared without the read replica suffix) is listed in <see cref="RequestConnectionKeys"/>.</summary>
        public bool IsRequestConnectionKeyListed(string key)
        {
            if (string.IsNullOrEmpty(key) || RequestConnectionKeys == null)
            {
                return false;
            }
            var normalized = FileEndpointGuard.NormalizeConnectionKey(key);
            return RequestConnectionKeys.Any(x => string.IsNullOrWhiteSpace(x) == false
                && string.Equals(FileEndpointGuard.NormalizeConnectionKey(x.Trim()), normalized, StringComparison.OrdinalIgnoreCase));
        }
    }
}
