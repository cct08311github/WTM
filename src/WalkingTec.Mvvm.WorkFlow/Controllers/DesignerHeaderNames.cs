#nullable enable
// WF-21 FIX-B1: Single source of truth for designer HTTP header names.
// MUST be kept byte-identical with the JS mirror in framework_workflow_designer_core.js
// (WfHeaders const object). Any rename here MUST be reflected in the JS file and
// in the cross-layer contract tests (DesignerDraftTests.cs § B1 contract tests).

namespace WalkingTec.Mvvm.WorkFlow.Controllers;

/// <summary>
/// Canonical HTTP header names for the workflow designer protocol.
///
/// <para><strong>Cross-layer contract:</strong> these string literals MUST be
/// byte-identical to the <c>WfHeaders</c> constant object in
/// <c>framework_workflow_designer_core.js</c>.  The cross-layer contract tests
/// in <c>DesignerDraftTests.cs</c> assert that both sides agree at the C# test
/// level (reading the JS file from disk and matching the same literals).</para>
///
/// <para>Header name conventions (all use the <c>X-WTM-WF-*</c> namespace):</para>
/// <list type="bullet">
///   <item><see cref="Xsrf"/> — antiforgery token for mutating actions.</item>
///   <item><see cref="ExpectedHash"/> — optional CAS guard on publish: value must
///         equal the current version ContentHash; mismatch → 409.</item>
///   <item><see cref="BaseHash"/> — base-version hash attached to draft saves:
///         the hash of the published version the client started editing from.</item>
/// </list>
/// </summary>
public static class DesignerHeaderNames
{
    /// <summary>Antiforgery header — mutating designer requests.</summary>
    public const string Xsrf         = "X-WTM-WF-XSRF";

    /// <summary>
    /// CAS guard header — publish endpoint.
    /// When supplied, the server rejects the publish with 409 if the current
    /// version ContentHash differs from this value.
    /// </summary>
    public const string ExpectedHash = "X-WTM-WF-Expected-Hash";

    /// <summary>
    /// Base-version hash header — draft save endpoint.
    /// The published-version ContentHash the client was editing from.
    /// Stored on the draft row for audit and future stale-draft detection.
    /// </summary>
    public const string BaseHash     = "X-WTM-WF-Base-Hash";
}
