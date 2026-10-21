using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WalkingTec.Mvvm.E2E.Security
{
    /// <summary>
    /// The distinguishable results a request can have in the framework. A request is classified from the status code, the redirect
    /// target and, only where the framework answers 200 with a fixed message, the start of the body. Anything that is not one of the
    /// named types below is reported by its status code ("400", "415", "500", ...).
    /// </summary>
    public static class Outcome
    {
        /// <summary>The request was accepted and answered with a 2xx status (the action ran).</summary>
        public const string Ok = "ok";

        /// <summary>
        /// The caller is sent to the sign-in page: either a redirect response to the login path (the cookie scheme also uses it as its
        /// access-denied path, so a signed-in caller that is refused by Forbid ends up there as well), or (MVC controllers, caller not
        /// signed in) status 200 with the small script the framework writes that sets window.location to the login path.
        /// </summary>
        public const string RedirectLogin = "redirect-login";

        /// <summary>Status 401: not signed in (APIs), or a bearer token on a controller that only knows the cookie.</summary>
        public const string Unauthorized = "401";

        /// <summary>Status 403: signed in without the privilege (APIs).</summary>
        public const string Forbidden = "403";

        /// <summary>Status 200 whose whole body is the framework's "not allowed to access this page" message (MVC controllers).</summary>
        public const string NoPrivilegePage = "200-no-privilege-page";

        /// <summary>Status 200 whose whole body is the framework's message for actions only the main tenant may use (MVC controllers).</summary>
        public const string TenantNotAllowedPage = "200-tenant-not-allowed-page";

        /// <summary>Status 200 whose whole body is the "only in debug mode" message (MVC controllers, action marked DebugOnly).</summary>
        public const string DebugOnlyPage = "200-debug-only-page";

        /// <summary>Status 400 whose whole body is the "only in debug mode" message (API controllers, action marked DebugOnly).</summary>
        public const string DebugOnlyBadRequest = "400-debug-only";

        /// <summary>Status 404: the route does not exist for anybody (disabled feature) or the record does not exist.</summary>
        public const string NotFound = "404";

        /// <summary>LayUI grid request (header layuisearch): status 200 with JSON whose Code is 401 (not signed in).</summary>
        public const string JsonNeedLogin = "200-json-401";

        /// <summary>LayUI grid request (header layuisearch): status 200 with JSON whose Code is 403 (no privilege).</summary>
        public const string JsonNoPrivilege = "200-json-403";

        /// <summary>
        /// Only in the table, never observed: the request must not be refused by authentication or authorization and must not fail with a
        /// server error. Used where the action's own answer to the harmless request (400, 404, 415, ...) is of no interest.
        /// </summary>
        public const string Allowed = "allowed";

        /// <summary>Every named type with its meaning, for the report and FORK.md.</summary>
        public static readonly IReadOnlyList<(string Type, string Meaning)> Catalog = new[]
        {
            (Ok, "status 2xx: the request passed authentication and authorization and the action answered"),
            (RedirectLogin, "redirect to the sign-in page (HTTP redirect to the login or access-denied path, or status 200 with the framework's redirect script)"),
            (Unauthorized, "status 401"),
            (Forbidden, "status 403"),
            (NoPrivilegePage, "status 200 whose body is the no-privilege message"),
            (TenantNotAllowedPage, "status 200 whose body is the main-tenant-only message"),
            (DebugOnlyPage, "status 200 whose body is the debug-only message"),
            (DebugOnlyBadRequest, "status 400 whose body is the debug-only message"),
            (NotFound, "status 404"),
            (JsonNeedLogin, "grid search answered 200 with JSON code 401"),
            (JsonNoPrivilege, "grid search answered 200 with JSON code 403"),
            (Allowed, "only in the table: any answer that is neither a refusal nor a server error"),
        };

        /// <summary>True when an observed outcome satisfies an expectation of the table (<see cref="Allowed"/> stands for any answer that is not a refusal or a server error).</summary>
        public static bool Matches(string expected, string observed)
        {
            return expected == observed || (expected == Allowed && IsAllowed(observed));
        }

        /// <summary>The caller was refused by authentication or authorization.</summary>
        public static bool IsDenied(string outcome)
        {
            return outcome == RedirectLogin || outcome == Unauthorized || outcome == Forbidden || outcome == NoPrivilegePage
                || outcome == TenantNotAllowedPage || outcome == JsonNeedLogin || outcome == JsonNoPrivilege;
        }

        public static bool IsDebugOnly(string outcome) => outcome == DebugOnlyPage || outcome == DebugOnlyBadRequest;

        /// <summary>
        /// The request was not refused by the framework's checks and did not fail with a server error: the action ran or the route answered
        /// by itself (whatever the status for the harmless request, including 400 and 404 for a record that does not exist).
        /// </summary>
        public static bool IsAllowed(string outcome)
        {
            return IsDenied(outcome) == false && IsDebugOnly(outcome) == false && IsServerError(outcome) == false;
        }

        /// <summary>A status of 500 or above.</summary>
        public static bool IsServerError(string outcome) => int.TryParse(outcome, out var status) && status >= 500;
    }

    /// <summary>The fixed texts the framework writes for refusals, in the language the probes ask for (English).</summary>
    public sealed class RefusalTexts
    {
        public string NoPrivilege { get; init; }
        public string DebugOnly { get; init; }
        public string TenantNotAllowed { get; init; }

        /// <summary>
        /// Reads the texts from the resource file the test sites share (the localizer returns the key itself when a key is missing, which
        /// is also accepted). The files are the demo's UI strings, see FORK.md.
        /// </summary>
        public static RefusalTexts Load(string repoRoot)
        {
            var file = Path.Combine(repoRoot, "demo", "WalkingTec.Mvvm.Vue3Demo", "Resources", "Program.en.resx");
            var values = XDocument.Load(file).Root.Elements("data")
                .ToDictionary(x => (string)x.Attribute("name"), x => (string)x.Element("value"));
            string Get(string key) => values.TryGetValue(key, out var v) ? v.Trim() : key;
            return new RefusalTexts { NoPrivilege = Get("Sys.NoPrivilege"), DebugOnly = Get("Sys.DebugOnly"), TenantNotAllowed = Get("_Admin.TenantNotAllowed") };
        }
    }

    /// <summary>The part of a response the classification needs. The body is only read far enough to recognize the framework's own messages.</summary>
    public sealed class ProbeResponse
    {
        public int Status { get; init; }
        public string Location { get; init; }
        public string ContentType { get; init; }

        /// <summary>First characters of the body (never kept beyond classification), null for an empty body.</summary>
        public string BodyStart { get; init; }

        /// <summary>True when the body was longer than <see cref="BodyStart"/>.</summary>
        public bool BodyTruncated { get; init; }
    }

    public static class OutcomeClassifier
    {
        /// <summary>The script the framework writes for a not signed in MVC request: it sets window.location to the login path.</summary>
        private static readonly Regex LoginScript = new Regex(@"^<script>var redirect=.*window\.location\.href='(?<path>[^'?]*)", RegexOptions.Compiled | RegexOptions.Singleline);

        public static string Classify(ProbeResponse r, RefusalTexts texts, IReadOnlyCollection<string> loginPaths)
        {
            var body = r.BodyTruncated || r.BodyStart == null ? null : r.BodyStart.Trim();
            if (r.Status >= 300 && r.Status < 400)
            {
                return IsLoginPath(r.Location, loginPaths) ? Outcome.RedirectLogin : r.Status.ToString();
            }
            if (r.Status == 200)
            {
                if (body != null)
                {
                    if (body == texts.NoPrivilege) return Outcome.NoPrivilegePage;
                    if (body == texts.DebugOnly) return Outcome.DebugOnlyPage;
                    if (body == texts.TenantNotAllowed) return Outcome.TenantNotAllowedPage;
                }
                var start = r.BodyStart ?? string.Empty;
                var script = LoginScript.Match(start);
                if (script.Success && IsLoginPath(script.Groups["path"].Value, loginPaths))
                {
                    return Outcome.RedirectLogin;
                }
                var json = Regex.Match(start, "\"Code\":(?<code>401|403)\\}\\s*$");
                if (r.BodyTruncated == false && r.ContentType != null && r.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) && json.Success)
                {
                    return json.Groups["code"].Value == "401" ? Outcome.JsonNeedLogin : Outcome.JsonNoPrivilege;
                }
                return Outcome.Ok;
            }
            if (r.Status == 400 && body != null && body == texts.DebugOnly)
            {
                return Outcome.DebugOnlyBadRequest;
            }
            if (r.Status >= 200 && r.Status < 300)
            {
                return Outcome.Ok;
            }
            return r.Status.ToString();
        }

        private static bool IsLoginPath(string location, IReadOnlyCollection<string> loginPaths)
        {
            if (string.IsNullOrEmpty(location))
            {
                return false;
            }
            var path = location;
            if (Uri.TryCreate(location, UriKind.Absolute, out var abs))
            {
                path = abs.AbsolutePath;
            }
            var q = path.IndexOfAny(new[] { '?', '#' });
            if (q >= 0)
            {
                path = path.Substring(0, q);
            }
            return loginPaths.Any(p => string.Equals(path.TrimEnd('/'), p.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        }
    }
}
