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
        /// <para>
        /// <strong>Default is <c>true</c> as of Issue #859 (was <c>false</c> through 10.18.x).</strong>
        /// With the old default, <c>WtmFileProvider.GetFile</c> bypassed the tenant filter
        /// (<c>IgnoreQueryFilters()</c>) for every caller, so any request that could reach the
        /// framework's own <c>/_Framework/GetFile</c>/<c>ViewFile</c> routes — including an
        /// unauthenticated one, on any template that also ships <c>IsFilePublic: true</c> — could
        /// read any tenant's file content by GUID. See the CHANGELOG's #859 entry for the full
        /// migration note, including the one deployment shape this flip does NOT protect
        /// (<c>IsFilePublic=true</c> combined with a genuinely cross-tenant "public" file that was
        /// uploaded with a real, non-null <c>TenantCode</c> — such a file stops resolving for an
        /// anonymous caller after the flip; re-upload it through a null-tenant/main-host context,
        /// or keep it on a dedicated public file store, to preserve that specific use).
        /// </para>
        ///
        /// <para><strong>Trade-off:</strong> With this <c>true</c>, a file uploaded by tenant A
        /// cannot be resolved by tenant B even if the caller has the correct GUID — including a
        /// file whose <c>TenantCode</c> is <c>NULL</c> (a pre-multi-tenancy legacy row, or one
        /// uploaded via a main-host/no-identity context): the EF Core null-safe equality translation
        /// of the global <c>ITenant</c> filter only resolves a <c>NULL</c>-tenant row for a caller
        /// whose own resolved tenant is ALSO <c>NULL</c>, matching the precedent
        /// <c>WtmFileProvider.DeleteFileTenantScoped</c> already established for Issue #815 (see
        /// <c>docs/production-readiness.md</c>'s #815 entry) — deliberately not widened here, since
        /// doing so would reopen the exact cross-tenant primitive #815 closed. Set this back to
        /// <c>false</c> only when every file upload is genuinely meant to be tenant-agnostic by ID
        /// and access control is fully enforced at a higher layer.</para>
        /// </summary>
        public bool EnforceTenantFileScope { get; set; } = true;

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
