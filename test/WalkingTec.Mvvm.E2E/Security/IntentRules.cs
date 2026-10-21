using System;
using System.Collections.Generic;
using System.Linq;
using WalkingTec.Mvvm.E2E.Coverage;

namespace WalkingTec.Mvvm.E2E.Security
{
    /// <summary>How an identity authenticates; decides which controllers know it.</summary>
    public enum IdentityKind
    {
        Anonymous,

        /// <summary>Signed in with the session cookie (the only scheme MVC controllers use).</summary>
        Cookie,

        /// <summary>Signed in with a bearer token (API controllers only).</summary>
        Bearer
    }

    /// <summary>
    /// The rules of the framework, written down from what the action declares, used to check that the table says what the framework is
    /// meant to do and not just what it happens to do (PrivilegeFilter): 
    /// <list type="number">
    /// <item><c>DebugOnly</c>: every identity gets the debug-only answer (the host does not run in debug mode).</item>
    /// <item><c>Public</c> or <c>AllowAnonymous</c>: nobody is refused, anonymous included.</item>
    /// <item>otherwise anonymous is refused;</item>
    /// <item>a bearer token on an MVC controller (one that does not ask for the token scheme) is not a session: refused (401);</item>
    /// <item><c>AllRights</c>: every signed-in identity passes;</item>
    /// <item>neither: the action needs a menu privilege, so an account without any role is refused and the administrator passes. The
    /// <c>limited</c> identity passes exactly the actions its role was given (<see cref="MatrixSetup.LimitedGrants"/>).</item>
    /// </list>
    /// An entry the rules do not describe (a disabled
    /// feature, an engine with its own scheme, a token as credential) carries an explanation in <see cref="MatrixEntry.Exempt"/>.
    /// </summary>
    public static class IntentRules
    {
        /// <summary>Identities that are an account without role / the administrator, by name prefix.</summary>
        public static bool IsNoRole(string identity) => identity == "norole";
        public static bool IsAdmin(string identity) => identity.StartsWith("admin", StringComparison.Ordinal);

        public static List<string> Violations(MatrixEntry entry, InventoryItem item, IReadOnlyDictionary<string, IdentityKind> kinds, IReadOnlyCollection<string> limitedGrants)
        {
            var problems = new List<string>();
            if (string.IsNullOrEmpty(entry.Exempt) == false)
            {
                return problems;
            }
            var attrs = new HashSet<string>(item.Attributes, StringComparer.OrdinalIgnoreCase);
            var isApi = attrs.Contains("ApiController");
            var anonymousAllowed = attrs.Contains("Public") || attrs.Contains("AllowAnonymous");
            // MVC controllers only know the session cookie, unless they ask for the token scheme as well
            var acceptsBearer = isApi || attrs.Contains("AuthorizeJwtWithCookie");
            foreach (var (identity, kind) in kinds)
            {
                var expected = entry.Expect[identity];
                string rule = null;
                if (attrs.Contains("DebugOnly"))
                {
                    rule = Outcome.IsDebugOnly(expected) ? null : "DebugOnly: every identity gets the debug-only answer";
                }
                else if (anonymousAllowed)
                {
                    rule = Outcome.IsAllowed(expected) ? null : "Public/AllowAnonymous: nobody is refused";
                }
                else if (kind == IdentityKind.Anonymous)
                {
                    rule = Outcome.IsDenied(expected) ? null : "anonymous is refused unless the action is public";
                }
                else if (kind == IdentityKind.Bearer && acceptsBearer == false)
                {
                    rule = expected == Outcome.Unauthorized ? null : "an MVC controller does not accept a bearer token (401)";
                }
                else if (attrs.Contains("AllRights"))
                {
                    rule = Outcome.IsAllowed(expected) ? null : "AllRights: every signed-in identity passes";
                }
                else if (IsNoRole(identity))
                {
                    rule = Outcome.IsDenied(expected) ? null : "a privileged action refuses an account without role";
                }
                else if (IsAdmin(identity))
                {
                    rule = Outcome.IsAllowed(expected) ? null : "a privileged action is open to the administrator";
                }
                else if (identity == "limited" && limitedGrants != null)
                {
                    var granted = limitedGrants.Contains(item.Key, StringComparer.OrdinalIgnoreCase);
                    rule = granted == Outcome.IsAllowed(expected) ? null : granted ? "the role of the limited account has the privilege for this action" : "the role of the limited account has no privilege for this action";
                }
                if (rule != null)
                {
                    problems.Add($"{entry.Key} / {identity}: expects '{expected}' but the declared rules say: {rule}");
                }
            }
            return problems;
        }
    }
}
