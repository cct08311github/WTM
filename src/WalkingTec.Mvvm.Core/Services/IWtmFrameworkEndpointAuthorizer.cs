#nullable enable
using System;

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Three-valued authorization decision consulted by <see cref="IWtmFrameworkEndpointAuthorizer"/>.
    /// <see cref="Inherit"/> (the default, numeric value <c>0</c>) means "this policy has no
    /// opinion about this call" — the caller falls back to whatever it would have done had no
    /// policy been registered at all (today: the matching <c>Enforce*Authorization</c> config
    /// flag, or an unconditional allow for the one hook, <c>CanEditProperty</c>, that has no
    /// flag). A policy that wants to participate returns <see cref="Allow"/> or
    /// <see cref="Deny"/> explicitly. There is deliberately no separate "DefaultAllow" property
    /// that would let a policy flip the meaning of Inherit — see Issue #827.
    /// </summary>
    public enum WtmAuthorizationDecision
    {
        Inherit = 0,
        Allow,
        Deny,
    }

    /// <summary>
    /// Issue #827: a DI-resolvable authorization seam for <c>_FrameworkController</c>'s five
    /// per-resource hooks (<c>CanExportVm</c>, <c>CanAccessFile</c>, <c>CanPreviewDelete</c>,
    /// <c>CanImportVm</c>, <c>CanEditProperty</c>).
    /// <para>
    /// <b>Why this exists.</b> <c>_FrameworkController</c> is the concrete class MVC routes
    /// every <c>/_Framework/*</c> request to (it is not abstract). The five hooks were
    /// <c>protected virtual</c>, and the documented remedy — override them in a controller that
    /// inherits from <c>_FrameworkController</c> — produces a SECOND controller the front end's
    /// hard-coded <c>/_Framework/*</c> URLs never call; production requests always reach the
    /// base class's own, un-overridden (permissive) answer. This interface lets a host register
    /// a real per-caller policy through DI instead, which <c>_FrameworkController</c> itself
    /// (not a hypothetical subclass) resolves and consults on every request.
    /// </para>
    /// <para>
    /// <b>Registration:</b> <c>services.AddScoped&lt;IWtmFrameworkEndpointAuthorizer,
    /// YourPolicy&gt;()</c> (or the <c>AddWtmFrameworkEndpointAuthorizer&lt;T&gt;()</c> helper
    /// on <c>FrameworkServiceExtension</c>) — scoped, not singleton: a real policy will
    /// typically query <see cref="WTMContext.LoginUserInfo"/> and/or the database, and copying
    /// <c>IWtmAuthorizationService</c>'s singleton shape here would create a captive dependency
    /// on request-scoped state. Nothing is registered by default; an unregistered seam resolves
    /// to <c>null</c>, and every hook treats a <c>null</c> authorizer exactly like one whose
    /// every method returns <see cref="WtmAuthorizationDecision.Inherit"/> — this changes
    /// nothing for a default deployment.
    /// </para>
    /// <para>
    /// <b>Coexistence with <see cref="IWtmAuthorizationService"/>:</b> that service answers a
    /// different question — is this URL accessible to this user at all (page/menu-level RBAC).
    /// This interface answers "may this caller touch THIS resource" for the caller-selected VM
    /// type / file id / entity+property a <c>/_Framework/*</c> endpoint accepts as a parameter,
    /// after <c>IWtmAuthorizationService</c> (and <c>PrivilegeFilter</c>) have already let the
    /// request reach the controller. Neither replaces the other; there is no migration path
    /// between them and none is planned.
    /// </para>
    /// <para>
    /// <b>What this does NOT cover:</b> whether a caller-supplied VM type NAME may be resolved
    /// to a <see cref="Type"/> at all — that is a pre-construction allowlist question (see
    /// <c>WTMContext.TryResolveVmType</c>), answered before any of these methods run, and
    /// deliberately out of scope for this seam: merging the two would downgrade a
    /// pre-construction check into a post-construction one. See Issue #836's exhaustive
    /// entrypoint/sink table for the full breakdown of which primitive answers which question.
    /// </para>
    /// </summary>
    public interface IWtmFrameworkEndpointAuthorizer
    {
        /// <summary>
        /// Per-caller authorization for exporting <paramref name="vmType"/>
        /// (<c>GetExportExcel</c> / <c>GetExportExcelStream</c> / <c>GetExcelTemplate</c>).
        /// </summary>
        WtmAuthorizationDecision CanExportVm(WTMContext wtm, Type vmType);

        /// <summary>
        /// Per-caller authorization for reading/deleting the <c>FileAttachment</c> identified
        /// by <paramref name="fileId"/> (<c>GetFile</c> / <c>GetFileName</c> / <c>ViewFile</c> /
        /// <c>DoImport</c>'s <c>UploadFileId</c>). <paramref name="wtm"/>.<c>LoginUserInfo</c>
        /// may be <c>null</c> here — <c>IsFilePublic=true</c> serves <c>GetFile</c>/<c>ViewFile</c>
        /// anonymously, and a registered policy must not assume an authenticated caller.
        /// </summary>
        WtmAuthorizationDecision CanAccessFile(WTMContext wtm, string fileId);

        /// <summary>
        /// Per-caller authorization for previewing a bulk delete of <paramref name="vmType"/>
        /// rows (<c>GetDeletePreview</c>).
        /// </summary>
        WtmAuthorizationDecision CanPreviewDelete(WTMContext wtm, Type vmType);

        /// <summary>
        /// Per-caller authorization for bulk-importing into <paramref name="vmType"/>
        /// (<c>DoImport</c>).
        /// </summary>
        WtmAuthorizationDecision CanImportVm(WTMContext wtm, Type vmType);

        /// <summary>
        /// Per-caller authorization for inline-editing <paramref name="propertyName"/> on
        /// <paramref name="entity"/> (<c>UpdateModelProperty</c>) — the only WRITE endpoint
        /// among the five, and the only one with no config-flag kill switch at all (its
        /// un-overridden, un-registered default is an unconditional allow).
        /// </summary>
        WtmAuthorizationDecision CanEditProperty(WTMContext wtm, object entity, string propertyName);
    }
}
