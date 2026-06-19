#nullable enable
using System.Collections.Generic;

namespace WalkingTec.Mvvm.Core.ConfigOptions
{
    /// <summary>
    /// FileOptions
    /// </summary>
    public class FileUploadOptions
    {
        /// <summary>
        /// 文件保存位置
        /// </summary>
        public string SaveFileMode { get; set; } = "database";

        /// <summary>
        /// 上传文件限制 单位字节 默认 20 * 1024 * 1024 = 20971520 bytes
        /// </summary>
        public long UploadLimit { get; set; } = 20971520;


        public Dictionary<string, List<FileHandlerOptions>> Settings { get; set; } = new Dictionary<string, List<FileHandlerOptions>>();

        /// <summary>
        /// When <c>true</c>, <see cref="WalkingTec.Mvvm.Core.Support.FileHandlers.WtmFileProvider"/>
        /// will NOT call <c>IgnoreQueryFilters()</c> on <c>FileAttachment</c> queries, so the
        /// EF Core global ITenant query filter is honoured and cross-tenant file access is blocked.
        ///
        /// Default is <c>false</c> (backward-compatible: file lookup is tenant-agnostic by ID).
        ///
        /// <para><strong>Trade-off:</strong> Setting this to <c>true</c> means a file uploaded by
        /// tenant A cannot be resolved by tenant B even if the caller has the correct GUID.  Enable
        /// this flag only when all file uploads are strictly tenant-scoped and you want the database
        /// layer to enforce that boundary.  Leave it <c>false</c> (default) when files are shared
        /// across tenants or when the caller already enforces access control at a higher layer.</para>
        /// </summary>
        public bool EnforceTenantFileScope { get; set; } = false;

        // ─── Opt-in upload validation (Issue #407) ───────────────────────────────
        // All fields default to "allow everything" so existing apps are unaffected.

        /// <summary>
        /// Allowlist of permitted file extensions (e.g. <c>".png"</c>, <c>".pdf"</c>).
        /// <para>
        /// Extension matching is case-insensitive and a leading dot is normalised automatically
        /// ("<c>png</c>" and "<c>.PNG</c>" are treated identically).
        /// </para>
        /// <para><strong>Default: empty (all extensions allowed).</strong></para>
        /// <para>
        /// Setting any values activates the built-in <c>ExtensionContentTypeUploadValidator</c>
        /// unless the host has already registered a custom <c>IUploadValidator</c> via DI.
        /// </para>
        /// </summary>
        public List<string> AllowedExtensions { get; set; } = new List<string>();

        /// <summary>
        /// Allowlist of permitted MIME / Content-Type values (e.g. <c>"image/png"</c>, <c>"application/pdf"</c>).
        /// The comparison strips parameters (<c>; charset=utf-8</c>) before matching.
        /// <para><strong>Default: empty (all content types allowed).</strong></para>
        /// </summary>
        public List<string> AllowedContentTypes { get; set; } = new List<string>();

        /// <summary>
        /// Maximum permitted upload size in bytes.  <c>0</c> (default) means unlimited.
        /// <para>
        /// Note: the ASP.NET Core form body limit is still governed by <see cref="UploadLimit"/>
        /// (the Kestrel / IIS gate).  <c>MaxUploadBytes</c> is an application-layer check that
        /// runs after the body has been received, allowing a tighter per-endpoint cap.
        /// </para>
        /// </summary>
        public long MaxUploadBytes { get; set; } = 0;

    }

    public class FileHandlerOptions
    {
        public string? GroupName { get; set; }
        public string? GroupLocation { get; set; }
        public string? ServerUrl { get; set; }

        public string? Key { get; set; }
        public string? Secret { get; set; }
    }
}
