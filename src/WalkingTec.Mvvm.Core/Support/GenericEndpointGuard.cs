using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace WalkingTec.Mvvm.Core
{
    /// <summary>What a reference allows the generic data endpoints to do with a view model. Combinable.</summary>
    [Flags]
    public enum GenericEndpointPurpose
    {
        None = 0,

        /// <summary><c>GetPagingData</c> and <c>GetEmptyData</c>: rows of a list view model.</summary>
        List = 1,

        /// <summary><c>Selector</c>: the pop-up that lets the user choose rows of a list view model.</summary>
        Selector = 2,

        /// <summary><c>GetExportExcel</c>.</summary>
        Export = 4,

        /// <summary><c>GetExcelTemplate</c>: the import template of an import view model.</summary>
        Template = 8,

        /// <summary><c>UpdateModelProperty</c>: change of one property of a record through a CRUD view model.</summary>
        Update = 16
    }

    /// <summary>Why a request for a view model was not served.</summary>
    public enum GenericEndpointRefusal
    {
        None = 0,
        UnknownViewModel,
        MissingReference,
        InvalidReference,
        ExpiredReference,
        UserMismatch,
        ViewModelMismatch,
        PurposeMismatch,
        ConnectionMismatch,
        PrivilegeRevoked,
        FieldNotAllowed,
        UnknownConnection
    }

    /// <summary>How a request was admitted.</summary>
    public enum GenericEndpointAdmission
    {
        /// <summary>Not admitted.</summary>
        None = 0,

        /// <summary>The request carries a valid reference.</summary>
        Reference,

        /// <summary>The view model is listed in <see cref="GenericEndpointOptions.ExemptViewModels"/> and the caller is signed in.</summary>
        Exempt,

        /// <summary><see cref="GenericEndpointMode.Legacy"/>: no check.</summary>
        Legacy
    }

    /// <summary>The outcome of <see cref="GenericEndpointGuard.Authorize"/>.</summary>
    public sealed class GenericEndpointDecision
    {
        public bool Allowed => Admission != GenericEndpointAdmission.None;
        public GenericEndpointAdmission Admission { get; init; }
        public GenericEndpointRefusal Refusal { get; init; }

        /// <summary>For an admission by reference: the url of the page whose privilege the reference depends on (null: the page is open to every signed-in user).</summary>
        public string PrivilegeUrl { get; init; }

        /// <summary>
        /// For an admission by reference: the connection key the page was shown on, as the server decided it when the page was produced
        /// (empty: the connection the server picks by default). Null for the other admissions.
        /// </summary>
        public string Connection { get; init; }

        public static GenericEndpointDecision Admit(GenericEndpointAdmission how, string privilegeUrl = null, string connection = null) => new GenericEndpointDecision { Admission = how, PrivilegeUrl = privilegeUrl, Connection = connection };
        public static GenericEndpointDecision Refuse(GenericEndpointRefusal why) => new GenericEndpointDecision { Admission = GenericEndpointAdmission.None, Refusal = why };
    }

    /// <summary>
    /// What a page tells the generic data endpoints about a view model it shows. It travels as an opaque protected string
    /// (<see cref="GenericEndpointGuard.ReferenceParameter"/>), never in clear text.
    /// </summary>
    public sealed class GenericEndpointReference
    {
        public const int CurrentVersion = 1;

        [JsonPropertyName("v")] public int Version { get; set; } = CurrentVersion;

        /// <summary>The view model, spelled as the page spells it in <c>_DONOT_USE_VMNAME</c>.</summary>
        [JsonPropertyName("t")] public string ViewModel { get; set; }

        [JsonPropertyName("p")] public GenericEndpointPurpose Purposes { get; set; }

        /// <summary>ITCode of the signed-in user the page was shown to (empty: nobody).</summary>
        [JsonPropertyName("u")] public string User { get; set; }

        /// <summary>The tenant the user acts in (empty: the main tenant or tenants are not used).</summary>
        [JsonPropertyName("n")] public string Tenant { get; set; }

        /// <summary>The database connection key of the page (empty: the default).</summary>
        [JsonPropertyName("c")] public string Connection { get; set; }

        /// <summary>
        /// The url of the page, when opening that page requires a menu privilege (it is neither public nor open to every signed-in user).
        /// The reference is honoured only while the user still has that privilege. Null: no privilege to re-check.
        /// </summary>
        [JsonPropertyName("o")] public string PrivilegeUrl { get; set; }

        /// <summary>
        /// Only for <see cref="GenericEndpointPurpose.Update"/>: the properties the page lets the user edit. A request to change any other
        /// property is refused.
        /// </summary>
        [JsonPropertyName("f")] public List<string> Fields { get; set; }

        /// <summary>Expiry, seconds since 1970-01-01 UTC.</summary>
        [JsonPropertyName("e")] public long ExpiresAt { get; set; }
    }

    /// <summary>The facts about the current request that the decision needs, kept apart from <see cref="WTMContext"/> so the decision can be tested alone.</summary>
    public sealed class GenericEndpointRequestContext
    {
        public GenericEndpointOptions Options { get; init; }

        /// <summary>The view model named by the request (<c>_DONOT_USE_VMNAME</c>).</summary>
        public string ViewModelName { get; init; }

        public GenericEndpointPurpose Purpose { get; init; }

        /// <summary>The reference sent with the request (<see cref="GenericEndpointGuard.ReferenceParameter"/>), if any.</summary>
        public string Reference { get; init; }

        /// <summary>True when the endpoint takes a connection key (<c>_DONOT_USE_CS</c>) that has to agree with the page.</summary>
        public bool CheckConnection { get; init; }

        /// <summary>The connection key sent with the request.</summary>
        public string ConnectionKey { get; init; }

        /// <summary>ITCode of the signed-in user, null when not signed in.</summary>
        public string User { get; init; }

        public string Tenant { get; init; }

        public DateTimeOffset Now { get; init; }

        /// <summary>Opens a protected reference; null when no data protection is available (then no reference can be valid).</summary>
        public Func<string, GenericEndpointReference> OpenReference { get; init; }

        /// <summary>The property a request wants to change (<see cref="GenericEndpointPurpose.Update"/> only).</summary>
        public string Field { get; init; }

        /// <summary>Whether the signed-in user may open a page (the privilege check of the feature pages: <see cref="WTMContext.IsAccessable"/>).</summary>
        public Func<string, bool> CanOpenPage { get; init; }

        /// <summary>Resolves a view model name to a type; used only for view models listed in <see cref="GenericEndpointOptions.ExemptViewModels"/>.</summary>
        public Func<string, Type> ResolveType { get; init; }
    }

    /// <summary>
    /// Decides whether a generic data endpoint may serve the view model a request names.
    /// <para>
    /// The endpoints build a view model from a name in the request. The LayUI TagHelpers write a protected reference next to that name
    /// in every page that shows such a view model (grid, selector, export button, template button). The reference states: this view model,
    /// for this user (and tenant), over this connection, for these purposes, until this time, as the page at this url (whose privilege
    /// is checked again when the reference is used). Because the page itself was authorized by <c>PrivilegeFilter</c> when it was produced,
    /// a valid reference means the user may open a page that shows the view model; the privilege semantics are those of the feature pages.
    /// </para>
    /// </summary>
    public static class GenericEndpointGuard
    {
        /// <summary>Name of the request parameter that carries the reference.</summary>
        public const string ReferenceParameter = "_DONOT_USE_VMREF";

        /// <summary>Purpose string of the protector that signs and encrypts references.</summary>
        public const string ProtectorPurpose = "WalkingTec.Mvvm.Core.GenericEndpointReference.v1";

        private const int MaxReferenceLength = 4096;
        private const int MaxPrivilegeUrlLength = 512;

        /// <summary>
        /// The spelling of a view model's type that the LayUI TagHelpers put into <c>_DONOT_USE_VMNAME</c>: the assembly-qualified name
        /// without the version part.
        /// </summary>
        public static string GetViewModelName(Type viewModelType)
        {
            var name = viewModelType.AssemblyQualifiedName;
            var pos = name.LastIndexOf(", Version=", StringComparison.CurrentCulture);
            return pos > 0 ? name.Substring(0, pos) : name;
        }

        /// <summary>The full type name inside a spelling returned by <see cref="GetViewModelName"/> (the part before the assembly).</summary>
        public static string GetTypeFullName(string viewModelName)
        {
            if (string.IsNullOrEmpty(viewModelName))
            {
                return viewModelName;
            }
            var depth = 0;
            for (var i = 0; i < viewModelName.Length; i++)
            {
                var c = viewModelName[i];
                if (c == '[')
                {
                    depth++;
                }
                else if (c == ']')
                {
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    return viewModelName.Substring(0, i).Trim();
                }
            }
            return viewModelName.Trim();
        }

        /// <summary>Logs a warning (never throws) when the check is switched off or relaxed by configuration.</summary>
        public static void LogRelaxedWarning(Configs configs, Microsoft.Extensions.Logging.ILogger logger)
        {
            var options = configs?.GenericEndpoints;
            if (options == null || logger == null)
            {
                return;
            }
            if (options.Mode == GenericEndpointMode.Legacy)
            {
                logger.LogWarning("GenericEndpoints:Mode is Legacy: the generic data endpoints (/_Framework/GetPagingData, Selector, GetExportExcel, GetExcelTemplate, GetEmptyData, UpdateModelProperty) do not check the view model a request names. Use the default (Enforce) and list the few view models called from your own scripts in GenericEndpoints:ExemptViewModels.");
            }
            else if (options.ExemptViewModels != null && options.ExemptViewModels.Count > 0)
            {
                logger.LogWarning("GenericEndpoints:ExemptViewModels lists {Count} entr(y/ies): those view models are served by the generic data endpoints to any signed-in user without a page reference.", options.ExemptViewModels.Count);
            }
            if (options.AllowRequestConnectionKey)
            {
                logger.LogWarning("GenericEndpoints:AllowRequestConnectionKey is on: a signed-in user can choose the database connection of the pages they can open with the DONOTUSECSName query string. Use [FixConnection] or the connection function to decide the connection on the server.");
            }
        }

        #region protection

        public static IDataProtector CreateProtector(IDataProtectionProvider provider)
        {
            return provider?.CreateProtector(ProtectorPurpose);
        }

        public static string Protect(IDataProtector protector, GenericEndpointReference reference)
        {
            return protector.Protect(JsonSerializer.Serialize(reference));
        }

        /// <summary>Opens a reference; null when it is not one this application issued (wrong key, altered, malformed).</summary>
        public static GenericEndpointReference Unprotect(IDataProtector protector, string reference)
        {
            if (protector == null || string.IsNullOrEmpty(reference) || reference.Length > MaxReferenceLength)
            {
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<GenericEndpointReference>(protector.Unprotect(reference));
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion

        #region issue

        /// <summary>
        /// Builds a reference. Returns null when it cannot be built (no protector, or a page url that is too long to carry).
        /// </summary>
        /// <param name="protector">protector from <see cref="CreateProtector"/></param>
        /// <param name="options">settings (lifetime)</param>
        /// <param name="viewModelType">the view model the page shows</param>
        /// <param name="purposes">what the page needs the endpoints to do with it</param>
        /// <param name="connectionKey">the connection key of the page</param>
        /// <param name="user">ITCode of the signed-in user (null when nobody)</param>
        /// <param name="tenant">the tenant the user acts in</param>
        /// <param name="privilegeUrl">url of the page when it needs a menu privilege, otherwise null</param>
        /// <param name="now">current time</param>
        public static string Issue(IDataProtector protector, GenericEndpointOptions options, Type viewModelType, GenericEndpointPurpose purposes,
            string connectionKey, string user, string tenant, string privilegeUrl, DateTimeOffset now, IEnumerable<string> fields = null)
        {
            if (protector == null || viewModelType == null || purposes == GenericEndpointPurpose.None)
            {
                return null;
            }
            privilegeUrl = LimitPrivilegeUrl(privilegeUrl);
            if (privilegeUrl != null && privilegeUrl.Length > MaxPrivilegeUrlLength)
            {
                return null;
            }
            var reference = new GenericEndpointReference
            {
                ViewModel = GetViewModelName(viewModelType),
                Purposes = purposes,
                User = user ?? string.Empty,
                Tenant = tenant ?? string.Empty,
                Connection = connectionKey ?? string.Empty,
                PrivilegeUrl = string.IsNullOrEmpty(privilegeUrl) ? null : privilegeUrl,
                Fields = (purposes & GenericEndpointPurpose.Update) != 0 && fields != null ? new List<string>(fields) : null,
                ExpiresAt = now.Add((options ?? new GenericEndpointOptions()).EffectiveLifetime).ToUnixTimeSeconds()
            };
            return Protect(protector, reference);
        }

        /// <summary>Keeps the url of the page short: the query string is dropped when it would make the reference large.</summary>
        private static string LimitPrivilegeUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || url.Length <= 256)
            {
                return url;
            }
            var pos = url.IndexOf('?');
            return pos > 0 ? url.Substring(0, pos) : url;
        }

        #endregion

        #region connection

        /// <summary>
        /// The connection a key belongs to: the key without the read-replica suffix (<c>reports_1</c> is a replica of <c>reports</c>, the same
        /// rule as <see cref="Utils.GetCS"/>), empty for no key. A read picks a replica at random, so keys are compared in this form.
        /// </summary>
        public static string NormalizeConnectionKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }
            var index = key.LastIndexOf('_');
            return index > 0 ? key.Substring(0, index) : key;
        }

        /// <summary>
        /// Decides whether an endpoint that takes the connection key in the request (<c>GetPagingData</c>, <c>GetExportExcel</c>,
        /// <c>Selector</c>) runs on that key. For a request admitted by its page reference the key has been compared with the reference and
        /// is used; in <see cref="GenericEndpointMode.Legacy"/> it is used as before. For a view model listed in
        /// <see cref="GenericEndpointOptions.ExemptViewModels"/> there is no reference to compare it with, so it is used only when
        /// <see cref="GenericEndpointOptions.AllowRequestConnectionKey"/> is on; otherwise the connection the server chose is kept.
        /// </summary>
        /// <param name="connection">the key to run on, in the spelling of the configured connection (the request's own when it names none)</param>
        /// <returns>true when <paramref name="connection"/> is to be used, false when the connection the server chose is kept</returns>
        public static bool TryGetRequestConnection(GenericEndpointAdmission admission, string requestKey, Configs configs, out string connection)
        {
            connection = null;
            var use = admission == GenericEndpointAdmission.Reference
                || admission == GenericEndpointAdmission.Legacy
                || (admission == GenericEndpointAdmission.Exempt && configs?.GenericEndpoints?.AllowRequestConnectionKey == true);
            if (use == false)
            {
                return false;
            }
            connection = requestKey;
            if (string.IsNullOrEmpty(requestKey) == false)
            {
                var configured = configs?.Connections?.FirstOrDefault(x => string.Equals(x.Key, requestKey, StringComparison.OrdinalIgnoreCase));
                if (configured != null)
                {
                    connection = configured.Key;
                }
            }
            return true;
        }

        /// <summary>
        /// Decides which connection an endpoint that takes no connection key of its own runs on: the one the page's reference is bound to.
        /// Returns false when the key in the reference is not (or no longer) a configured connection, then the request must be refused.
        /// An empty key means the page ran on the connection the server picks by default; <paramref name="connection"/> is then null and the
        /// connection the server chose for the request is kept. Otherwise it is the configured key: for a write the base connection
        /// (never a replica), for a read the base connection or one of its replicas, as for an action with a fixed connection.
        /// </summary>
        public static bool TryResolveConnection(string pinnedKey, Configs configs, bool write, out string connection)
        {
            connection = null;
            if (string.IsNullOrEmpty(pinnedKey))
            {
                return true;
            }
            if (configs?.Connections == null || configs.Connections.Any(x => string.Equals(x.Key, pinnedKey, StringComparison.OrdinalIgnoreCase)) == false)
            {
                return false;
            }
            connection = Utils.GetCS(pinnedKey, write ? "Write" : "Read", configs);
            return true;
        }

        #endregion

        #region decision

        /// <summary>
        /// Decides whether the endpoint may serve the view model named by the request.
        /// </summary>
        public static GenericEndpointDecision Authorize(GenericEndpointRequestContext ctx)
        {
            var options = ctx.Options ?? new GenericEndpointOptions();
            if (options.Mode == GenericEndpointMode.Legacy)
            {
                return GenericEndpointDecision.Admit(GenericEndpointAdmission.Legacy);
            }
            if (string.IsNullOrWhiteSpace(ctx.ViewModelName))
            {
                return GenericEndpointDecision.Refuse(GenericEndpointRefusal.UnknownViewModel);
            }

            GenericEndpointDecision byReference = null;
            if (string.IsNullOrEmpty(ctx.Reference) == false)
            {
                byReference = CheckReference(ctx);
                if (byReference.Allowed)
                {
                    return byReference;
                }
            }

            // a view model the application has declared as served without a reference, for signed-in users (never for updates, which need the list of editable fields from the page)
            if (ctx.User != null && ctx.Purpose != GenericEndpointPurpose.Update && options.ExemptViewModels != null && options.ExemptViewModels.Count > 0
                && options.IsExempt(GetTypeFullName(ctx.ViewModelName)))
            {
                var type = ctx.ResolveType?.Invoke(ctx.ViewModelName);
                if (type == null || typeof(BaseVM).IsAssignableFrom(type) == false)
                {
                    return GenericEndpointDecision.Refuse(GenericEndpointRefusal.UnknownViewModel);
                }
                return GenericEndpointDecision.Admit(GenericEndpointAdmission.Exempt);
            }

            return byReference ?? GenericEndpointDecision.Refuse(GenericEndpointRefusal.MissingReference);
        }

        private static GenericEndpointDecision CheckReference(GenericEndpointRequestContext ctx)
        {
            var reference = ctx.OpenReference?.Invoke(ctx.Reference);
            if (reference == null || reference.Version != GenericEndpointReference.CurrentVersion || string.IsNullOrEmpty(reference.ViewModel))
            {
                return GenericEndpointDecision.Refuse(GenericEndpointRefusal.InvalidReference);
            }
            if (reference.ExpiresAt <= ctx.Now.ToUnixTimeSeconds())
            {
                return GenericEndpointDecision.Refuse(GenericEndpointRefusal.ExpiredReference);
            }
            if (string.Equals(reference.User ?? string.Empty, ctx.User ?? string.Empty, StringComparison.Ordinal) == false
                || string.Equals(reference.Tenant ?? string.Empty, ctx.Tenant ?? string.Empty, StringComparison.Ordinal) == false)
            {
                return GenericEndpointDecision.Refuse(GenericEndpointRefusal.UserMismatch);
            }
            if (string.Equals(reference.ViewModel, ctx.ViewModelName, StringComparison.Ordinal) == false)
            {
                return GenericEndpointDecision.Refuse(GenericEndpointRefusal.ViewModelMismatch);
            }
            if ((reference.Purposes & ctx.Purpose) != ctx.Purpose || ctx.Purpose == GenericEndpointPurpose.None)
            {
                return GenericEndpointDecision.Refuse(GenericEndpointRefusal.PurposeMismatch);
            }
            if (ctx.CheckConnection
                && string.Equals(NormalizeConnectionKey(reference.Connection), NormalizeConnectionKey(ctx.ConnectionKey), StringComparison.OrdinalIgnoreCase) == false)
            {
                return GenericEndpointDecision.Refuse(GenericEndpointRefusal.ConnectionMismatch);
            }
            if (string.IsNullOrEmpty(reference.PrivilegeUrl) == false
                && (ctx.CanOpenPage == null || ctx.CanOpenPage(reference.PrivilegeUrl) == false))
            {
                return GenericEndpointDecision.Refuse(GenericEndpointRefusal.PrivilegeRevoked);
            }
            if (ctx.Purpose == GenericEndpointPurpose.Update
                && (string.IsNullOrEmpty(ctx.Field) || reference.Fields == null || reference.Fields.Contains(ctx.Field, StringComparer.Ordinal) == false))
            {
                return GenericEndpointDecision.Refuse(GenericEndpointRefusal.FieldNotAllowed);
            }
            return GenericEndpointDecision.Admit(GenericEndpointAdmission.Reference, reference.PrivilegeUrl, reference.Connection ?? string.Empty);
        }

        #endregion
    }
}
