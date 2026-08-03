#nullable enable

namespace WalkingTec.Mvvm.Core.Services
{
    /// <summary>
    /// Issue #1007 — a DI-resolvable seam that lets a host override
    /// <see cref="WTMContext.SetCurrentTenant(string?)"/>'s default entitlement decision for a
    /// tenant-switch request whose requested code has already been uniquely resolved against
    /// <see cref="GlobalData.AllTenant"/>.
    /// <para>
    /// <b>What this interface CAN change.</b> Only the entitlement question — should THIS caller,
    /// who already has a real <see cref="LoginUserInfo"/>, be allowed into a tenant that has
    /// already been uniquely identified. Concretely, it can override the two structural defaults
    /// <c>SetCurrentTenant</c> would otherwise apply once a single matching
    /// <see cref="FrameworkTenant"/> descriptor exists: a host caller's (
    /// <c><see cref="LoginUserInfo.TenantCode"/> == null</c>) otherwise-unrestricted depth,
    /// and a tenant-scoped caller's otherwise-restricted-to-direct-children default.
    /// </para>
    /// <para>
    /// <b>What this interface CANNOT change — resolution is not delegable.</b>
    /// <see cref="CanSwitchTenant"/> is consulted ONLY after the requested code has already been
    /// looked up in a single read of <see cref="GlobalData.AllTenant"/> and found to match
    /// EXACTLY one row. If the code matches zero rows or more than one row,
    /// <c>SetCurrentTenant</c> returns <c>false</c> BEFORE this method is ever called — a
    /// registered policy's call count is structurally zero for those two inputs, and no
    /// implementation can rescue them. Nor is a <c>null</c> request (return to home) or a
    /// request equal to the caller's own current tenant code ever routed through this interface
    /// — see <see cref="WtmAuthorizationDecision.Deny"/> below for why a policy cannot use this
    /// to trap a caller inside a tenant they did not ask to be in.
    /// </para>
    /// </summary>
    public interface IWtmTenantSwitchPolicy
    {
        /// <summary>
        /// Decides whether <paramref name="user"/> may switch into the tenant described by
        /// <paramref name="resolvedDescriptor"/>.
        /// </summary>
        /// <param name="wtm">
        /// The current request's <see cref="WTMContext"/> — the same instance
        /// <c>SetCurrentTenant</c> is running on, so <see cref="WTMContext.HttpContext"/>,
        /// <see cref="WTMContext.ServiceProvider"/>, etc. are all available to an implementation
        /// that needs more context than the three arguments below provide (e.g. to query a
        /// database for an explicit grant record).
        /// </param>
        /// <param name="user">
        /// The authenticated caller's current <see cref="LoginUserInfo"/> — never
        /// <c>null</c> (an unauthenticated caller is rejected by <c>SetCurrentTenant</c> before
        /// this method is ever reached).
        /// </param>
        /// <param name="resolvedDescriptor">
        /// The single <see cref="FrameworkTenant"/> row that
        /// <paramref name="requestedCode"/> uniquely resolved to in this call's one read of
        /// <see cref="GlobalData.AllTenant"/>. This is never <c>null</c> and is never one of
        /// several rows that shared <paramref name="requestedCode"/> — see the interface-level
        /// remarks: an ambiguous or unresolved code never reaches this method at all.
        /// </param>
        /// <param name="requestedCode">
        /// The caller-proposed tenant code, verbatim — the same string
        /// <paramref name="resolvedDescriptor"/>.<c>TCode</c> matched. Compared with C#
        /// <c>==</c> (ordinal) throughout <c>SetCurrentTenant</c>; this interface performs no
        /// normalization of its own and an implementation should not either, to stay consistent
        /// with the ordinal comparison the rest of the admission path uses.
        /// </param>
        /// <returns>
        /// <see cref="WtmAuthorizationDecision.Allow"/> to admit the switch regardless of
        /// <c>SetCurrentTenant</c>'s structural default (e.g. a legitimate sibling- or
        /// grandchild-tenant workflow the default would otherwise refuse).
        /// <see cref="WtmAuthorizationDecision.Deny"/> to refuse the switch even where the
        /// structural default would otherwise admit it (e.g. narrowing a host caller's normally
        /// unrestricted reach). <see cref="WtmAuthorizationDecision.Inherit"/> (the default
        /// value, and what an unregistered policy is treated as) leaves
        /// <c>SetCurrentTenant</c>'s structural default answer unchanged. Note that
        /// <see cref="WtmAuthorizationDecision.Deny"/> can never be used to trap a caller inside
        /// someone else's tenant: a <c>null</c> request (return home) and a request equal to the
        /// caller's own current tenant code are both admitted BEFORE this method is ever
        /// consulted, so neither can be overridden to <c>Deny</c> here.
        /// </returns>
        /// <exception cref="System.Exception">
        /// Any exception this method throws PROPAGATES to <c>SetCurrentTenant</c>'s caller — it
        /// is deliberately NOT caught and converted into a refusal. A thrown exception is an
        /// error, not a policy decision, and must not silently collapse into the same observable
        /// outcome (<c>false</c>) as a legitimate <see cref="WtmAuthorizationDecision.Deny"/>;
        /// doing so would hide the failure from whatever would otherwise report it (logging,
        /// error middleware, alerting).
        /// </exception>
        WtmAuthorizationDecision CanSwitchTenant(
            WTMContext wtm, LoginUserInfo user, FrameworkTenant resolvedDescriptor, string requestedCode);
    }
}
