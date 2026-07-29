using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using DUWENINK.Captcha;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NPOI.SS.Formula.Functions;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Models;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.Core.Support.FileHandlers;

namespace WalkingTec.Mvvm.Mvc
{
    [AllRights]
    [ActionDescription("Framework")]
    public class _FrameworkController(ISecurityCodeHelper securityCode) : BaseController
    {
        /// <summary>
        ///
        /// </summary>
        private readonly ISecurityCodeHelper _securityCode = securityCode;

        // Perf(#663): Menu() is called on every menu render; it does not depend on any
        // instance/request state (unlike CoreProgram.DefaultJsonOption elsewhere in this
        // file, there is no startup-ordering concern here), so a single shared, fully
        // self-contained instance is safe.
        private static readonly JsonSerializerOptions _menuJsonOptions = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault
        };

        /// <summary>
        /// #481: Unicode-escape HTML breakout characters (<c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>)
        /// in JSON that will be emitted raw inside an inline <c>&lt;script&gt;</c> element
        /// (Selector.cshtml line ~57).  The paired-tag regex that previously ran here only caught
        /// complete <c>&lt;script&gt;…&lt;/script&gt;</c> pairs; a bare <c>&lt;/script&gt;</c>
        /// in a field value would still terminate the enclosing page script block (stored XSS).
        /// Replacing with <c>\uXXXX</c> sequences is valid inside JSON string values and JS string
        /// literals — the JS engine decodes them transparently so the Selector view is unaffected.
        /// This matches the JSON.NET <c>StringEscapeHandling.EscapeHtml</c> strategy.
        /// </summary>
        public static string SanitizeSelectorJson(string json)
        {
            // Escape HTML breakout characters using JSON/JS unicode escape sequences so that
            // field values containing </script> cannot terminate the enclosing <script> block
            // in Selector.cshtml.  < / > / & are valid inside JSON string
            // values and JS string literals — the JS engine decodes \uXXXX transparently, so
            // displayed text in the Selector grid is unaffected.  & must be replaced first
            // to prevent double-escaping any pre-existing escape sequences in the JSON.
            // This matches the JSON.NET StringEscapeHandling.EscapeHtml strategy.
            return json
                .Replace("&",  "\\u0026")
                .Replace("<",  "\\u003c")
                .Replace(">",  "\\u003e")
                .Replace("\u2028", "\\u2028")
                .Replace("\u2029", "\\u2029");
        }

        /// <summary>
        /// #530: Computes the Content-Type used when <see cref="GetFile"/> streams a file
        /// inline (<c>stream=true</c>). Only the whitelisted image extensions are safe to
        /// serve with their native, renderable content type; everything else \u2014 including
        /// unknown extensions and actively renderable types like <c>text/html</c> or
        /// <c>image/svg+xml</c> \u2014 is forced to <c>application/octet-stream</c> so the browser
        /// cannot MIME-sniff an uploaded file as active content in the app's origin (stored XSS).
        /// Callers must also set <c>X-Content-Type-Options: nosniff</c> to pin the browser to
        /// this value.
        /// </summary>
        public static string GetSafeStreamContentType(string? ext, string contenttype)
        {
            var imageContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "png", "bmp", "gif", "tif", "jpg", "jpeg" };
            return !string.IsNullOrEmpty(ext) && imageContentTypes.Contains(ext) ? contenttype : "application/octet-stream";
        }

        /// <summary>
        /// MVC-010: Validates that the supplied connection-string key is an explicitly
        /// configured key in Configs.Connections.  Returns true if the key is null/empty
        /// (caller will use the default) or is a known key.  Returns false for any
        /// unrecognised value — the caller must reject the request (400/throw).
        /// Delegates to <see cref="WTMContext.IsKnownConnectionKey"/> — single source of truth (#517).
        /// </summary>
        private bool IsKnownConnectionKey(string? csKey) => Wtm.IsKnownConnectionKey(csKey);

        /// <summary>
        /// Returns true if the authenticated caller has the "Admin" role in their
        /// current tenant. Used to gate framework-controller endpoints that are
        /// marked [AllRights] but in fact require admin authority — see #30.
        /// </summary>
        internal bool CallerIsAdmin()
        {
            var roles = Wtm?.LoginUserInfo?.Roles;
            if (roles == null) return false;
            return roles.Any(r => string.Equals(
                r.RoleCode, "Admin", StringComparison.OrdinalIgnoreCase));
        }

        // #827: _FrameworkController is the CONCRETE class MVC routes every /_Framework/*
        // request to (see the class declaration above) -- it is not abstract, so a controller
        // that inherits from it and overrides one of the five hooks below creates a SECOND
        // controller the front end's hard-coded URLs never call. Resolving an
        // IWtmFrameworkEndpointAuthorizer from DI, on this same concrete instance, is what
        // makes a per-caller policy reachable on a real production route. Both null-conditional
        // operators are required: HttpContext can itself be null in some test/console-invocation
        // shapes (see the hook call sites' existing tests, several of which build a bare
        // DefaultHttpContext with no RequestServices at all), and Microsoft.Extensions
        // .DependencyInjection's own IServiceProvider extension methods are not used here on
        // purpose -- GetService<T>() calls GetService(Type) internally, but a handful of this
        // controller's own tests wire a Mock<IServiceProvider> that only stubs the SINGULAR
        // Type-taking GetService(Type) overload (mirroring FrameworkControllerRbacHooksTest's
        // CreateController helper); GetRequiredService/GetServices would throw against that mock.
        private IWtmFrameworkEndpointAuthorizer? ResolveEndpointAuthorizer() =>
            HttpContext?.RequestServices?.GetService(typeof(IWtmFrameworkEndpointAuthorizer)) as IWtmFrameworkEndpointAuthorizer;

        // #796: a bare Forbid() on these two hooks' denied path does NOT put a literal HTTP
        // 403 on the wire when the default cookie auth scheme is in effect — cookie auth
        // (FrameworkServiceExtension.cs, AddCookie(...).AccessDeniedPath) turns Forbid() into a
        // 302 redirect to AccessDeniedPath, same as every other Forbid() call already in this
        // controller (e.g. BatchAssignRoles' CallerIsAdmin() checks). These AJAX-called endpoints
        // (framework_layui.js) therefore see a redirect-to-HTML response, not a clean denial, on
        // this path — kept consistent with the controller's existing convention rather than
        // special-cased, so a denial here does not read differently from every other denial in
        // this file. See MvcAuthHolesTests.GetExportExcel_EnforceFlagEnabled_Forbid_Is302RedirectToAccessDenied,
        // which pins the actual wire status (including the Location header) so a future
        // auth-scheme change cannot silently flip it.

        /// <summary>
        /// #796: Extension hook for per-caller authorization of the VM type targeted by
        /// <see cref="GetExportExcel"/> / <see cref="GetExportExcelStream"/>. Both endpoints
        /// accept a caller-supplied VM type name and, without this hook, run that VM's own
        /// <c>GetSearchQuery()</c> and hand the resulting file to ANY authenticated caller —
        /// there is no built-in mapping from an arbitrary VM type back to the menu /
        /// FunctionPrivilege that normally gates the page the VM belongs to (the same VM type
        /// can be reused by more than one controller, or by none), so this cannot be derived
        /// automatically without risking false negatives on custom deployments. Override this
        /// in a controller that inherits from <c>_FrameworkController</c> to enforce your own
        /// per-VM policy (e.g. checking <see cref="WTMContext.IsAccessable(string?)"/> for the
        /// VM's known page URL, or restricting specific VM types to specific roles).
        /// <para>
        /// When not overridden, the default answer is driven by
        /// <see cref="WalkingTec.Mvvm.Core.ConfigOptions.Configs.EnforceVmExportAuthorization"/>
        /// (default <c>false</c> → <c>true</c>/allow, unchanged pre-#796 behaviour). Setting that
        /// config flag to <c>true</c> flips the un-overridden default to deny (fail-closed) — a
        /// no-code kill switch for deployments that want every export blocked until they write a
        /// real per-VM policy. See Issue #796 for the full analysis and the residual exposure
        /// with the flag left at its default.
        /// </para>
        /// </summary>
        /// <param name="vmType">The resolved type of the VM the caller asked to export.</param>
        /// <returns><c>true</c> if the export is allowed; <c>false</c> to return 403.</returns>
        /// <remarks>
        /// #827: before the config flag is consulted, this now asks a DI-resolved
        /// <see cref="IWtmFrameworkEndpointAuthorizer"/> (if one is registered) via
        /// <see cref="ResolveEndpointAuthorizer"/>. <see cref="WtmAuthorizationDecision.Allow"/>/
        /// <see cref="WtmAuthorizationDecision.Deny"/> short-circuit straight to the matching
        /// bool; <see cref="WtmAuthorizationDecision.Inherit"/> (including no policy registered
        /// at all) falls through to exactly the flag-driven answer below -- unregistered, this
        /// changes nothing.
        /// </remarks>
        protected virtual bool CanExportVm(Type vmType) => ResolveEndpointAuthorizer()?.CanExportVm(Wtm, vmType) switch
        {
            WtmAuthorizationDecision.Allow => true,
            WtmAuthorizationDecision.Deny => false,
            _ => !(Wtm?.ConfigInfo?.EnforceVmExportAuthorization ?? false),
        };

        /// <summary>
        /// #814: Extension hook for per-caller authorization of the <see cref="FileAttachment"/>
        /// targeted by <see cref="GetFile"/>, <see cref="GetFileName"/>, <see cref="ViewFile"/>,
        /// and <see cref="DoImport"/>'s uploaded-template (<c>UploadFileId</c>) read/delete.
        /// <c>FileAttachment</c> carries no owner/uploader column, so the framework cannot
        /// enforce row-level ownership by default without a schema migration; override this in a
        /// derived controller to plug in your own scheme (e.g. a join table, or an uploader id
        /// you stash in <c>FileAttachment.ExtraInfo</c>) if your deployment needs it. Combine with
        /// <see cref="WalkingTec.Mvvm.Core.ConfigOptions.FileUploadOptions.EnforceTenantFileScope"/>
        /// for the tenant-boundary half of this gap.
        /// <para>
        /// <b>Issue #815 status (resolved at the source for <c>BaseCRUDVM</c>, resolved for
        /// <c>BaseImportVM.BatchSaveData</c>, still a known gap for generated Import
        /// controllers):</b> the underlying primitive — a caller writing a
        /// <see cref="FileAttachment"/>-typed navigation property's FK scalar (e.g.
        /// <c>PhotoId</c>) to a file they cannot access, which then resolves both for reads
        /// (<c>GetById</c>) and, via <c>BaseVM.DeletedFileIds</c>, for deletes — is now rejected
        /// at write time in <c>BaseCRUDVM.DoAdd(Async)/DoEdit(Async)/DoDelete(Async)</c>
        /// (<c>RejectUnresolvableFileAttachmentReferences</c>: a posted FK that does not resolve
        /// for the caller's own tenant, query filter kept ON unconditionally regardless of
        /// <c>EnforceTenantFileScope</c>, is reverted before <c>SaveChanges</c>). This is the
        /// PRIMARY control. <c>WtmFileProvider.DeleteFileTenantScoped</c> (same unconditional
        /// tenant scoping) at every <c>DeletedFileIds</c>/<c>DoRealDelete(Async)</c>/
        /// <c>BaseBatchVM.DoBatchDelete(Async)</c> sink, plus the pre-save entity-reference
        /// check, remain as defence in depth. <c>BaseImportVM.BatchSaveData</c> never had a
        /// legitimate reference to validate against, so it still ignores
        /// <c>DeletedFileIds</c> entirely rather than deleting unchecked. This hook
        /// (<c>CanAccessFile</c>) is unrelated to that fix — it is only consulted by
        /// <c>DoImport</c> (this base controller's generic import action) for
        /// <c>UploadFileId</c>. <b>The remaining known gap is the code generator's own
        /// per-entity Import($modelname$ImportVM, ...) action</b> (<c>GeneratorFiles/Mvc/
        /// Controller.txt</c>), which does not inherit from this hook at all and model-binds
        /// <c>UploadFileId</c> straight into <c>BatchSaveData</c> unauthorized — tracked as
        /// Issue #816. Whether the caller may invoke the import VM in the first place (VM-level
        /// authorization, as opposed to which file id it touches) is likewise separate and
        /// tracked as Issue #818.
        /// </para>
        /// <para>
        /// When not overridden, the default answer is driven by
        /// <see cref="WalkingTec.Mvvm.Core.Configs.EnforceFileAccessAuthorization"/>
        /// (default <c>false</c> → <c>true</c>/allow, unchanged pre-#796 behaviour); setting that
        /// flag to <c>true</c> flips the un-overridden default to deny (fail-closed) with no code
        /// change. See Issue #796 for the original analysis and Issue #814 for the complete
        /// gated-endpoint list.
        /// </para>
        /// <para>
        /// <paramref name="fileId"/> is always the caller-supplied id normalized to a canonical
        /// <see cref="Guid"/> "D"-format string (see <see cref="TryNormalizeFileId"/>) before this
        /// hook is invoked, so an override that string-compares ids sees the same representation
        /// regardless of which endpoint called it.
        /// </para>
        /// </summary>
        /// <param name="fileId">The caller-supplied <see cref="FileAttachment"/> id, normalized to
        /// a canonical <see cref="Guid"/> "D"-format string.</param>
        /// <returns><c>true</c> if access is allowed; <c>false</c> to deny.</returns>
        /// <remarks>
        /// #827: see <see cref="CanExportVm"/>'s remarks -- same DI-first, flag-fallback shape.
        /// A registered policy is consulted here for anonymous requests too (e.g.
        /// <c>IsFilePublic=true</c> serving <c>GetFile</c>/<c>ViewFile</c> without a session):
        /// <see cref="WTMContext.LoginUserInfo"/> may be <c>null</c> on <see cref="Wtm"/> when
        /// this runs, and a policy must tolerate that itself -- this method does not special-case
        /// anonymous callers.
        /// </remarks>
        protected virtual bool CanAccessFile(string fileId) => ResolveEndpointAuthorizer()?.CanAccessFile(Wtm, fileId) switch
        {
            WtmAuthorizationDecision.Allow => true,
            WtmAuthorizationDecision.Deny => false,
            _ => !(Wtm?.ConfigInfo?.EnforceFileAccessAuthorization ?? false),
        };

        /// <summary>
        /// #814: normalizes a caller-supplied file id to a canonical <see cref="Guid"/>
        /// "D"-format string before it reaches <see cref="CanAccessFile"/>. Without this,
        /// <see cref="GetFileName"/> passes a <see cref="Guid"/>'s already-canonical
        /// <c>ToString()</c> while <see cref="GetFile"/>/<see cref="ViewFile"/> pass the raw,
        /// unparsed request string (any case, with or without braces/dashes) — a
        /// string-comparing <see cref="CanAccessFile"/> override keyed on the canonical form
        /// would silently never match the raw form, making the guard bypassable simply by
        /// varying the id's textual representation. Returns <c>false</c> (reject) for an id that
        /// does not parse as a <see cref="Guid"/> at all.
        /// </summary>
        private static bool TryNormalizeFileId(string? rawId, out string normalizedId)
        {
            if (Guid.TryParse(rawId, out var guid))
            {
                normalizedId = guid.ToString();
                return true;
            }
            normalizedId = string.Empty;
            return false;
        }

        /// <summary>
        /// #796: Extension hook for per-caller authorization of the VM type targeted by
        /// <see cref="GetDeletePreview"/>. Without this hook, any authenticated caller can supply
        /// an arbitrary VM type name plus up to 10 GUIDs and get back a confirmed-existence
        /// oracle and a human-readable label for every row that exists, regardless of whether
        /// they hold any privilege over that VM.
        /// <para>
        /// When not overridden, the default answer is driven by
        /// <see cref="WalkingTec.Mvvm.Core.ConfigOptions.Configs.EnforceDeletePreviewAuthorization"/>
        /// (default <c>false</c> → <c>true</c>/allow, unchanged behaviour), for the same
        /// compatibility reason and the same no-code fail-closed opt-in as
        /// <see cref="CanExportVm"/>.
        /// </para>
        /// </summary>
        /// <param name="vmType">The resolved type of the VM the caller is previewing a delete for.</param>
        /// <returns><c>true</c> if the preview is allowed; <c>false</c> to return 403.</returns>
        /// <remarks>
        /// #827: see <see cref="CanExportVm"/>'s remarks -- same DI-first, flag-fallback shape.
        /// </remarks>
        protected virtual bool CanPreviewDelete(Type vmType) => ResolveEndpointAuthorizer()?.CanPreviewDelete(Wtm, vmType) switch
        {
            WtmAuthorizationDecision.Allow => true,
            WtmAuthorizationDecision.Deny => false,
            _ => !(Wtm?.ConfigInfo?.EnforceDeletePreviewAuthorization ?? false),
        };

        // Note: the file-access hook (CanAccessFile, gating GetFile/GetFileName/ViewFile) was
        // carved out of the #796 work to issue #814 and lives there.

        /// <summary>
        /// #818: Extension hook for per-caller authorization of the VM type targeted by
        /// <see cref="DoImport"/>. Without this hook, any authenticated caller can supply an
        /// arbitrary VM type name that implements <see cref="IWtmImportable"/> and have
        /// <c>BatchSaveData()</c> bulk-insert into whatever entity that VM imports — a WRITE,
        /// strictly more severe than the read-only export/preview disclosures gated by
        /// <see cref="CanExportVm"/> and <see cref="CanPreviewDelete"/>. As with those hooks,
        /// there is no built-in mapping from an arbitrary VM type back to the menu /
        /// FunctionPrivilege that normally gates the page it belongs to (the same VM type can be
        /// reused by more than one controller, or by none), so this cannot be derived
        /// automatically without risking false negatives on custom deployments. Override this in
        /// a controller that inherits from <c>_FrameworkController</c> to enforce your own
        /// per-VM policy.
        /// <para>
        /// When not overridden, the default answer is driven by
        /// <see cref="WalkingTec.Mvvm.Core.Configs.EnforceVmImportAuthorization"/>
        /// (default <c>false</c> → <c>true</c>/allow, unchanged pre-#818 behaviour). Setting that
        /// config flag to <c>true</c> flips the un-overridden default to deny (fail-closed) — a
        /// no-code kill switch, same shape as <see cref="CanExportVm"/>. See Issue #818 for the
        /// full analysis.
        /// </para>
        /// <para>
        /// <b>Scope:</b> this hook covers VM-level authorization only — whether the caller may
        /// invoke this import VM at all. It does not cover the uploaded template file
        /// (<c>UploadFileId</c>, gated separately by <see cref="CanAccessFile"/>), nor
        /// <c>BaseVM.DeletedFileIds</c> — read at the top of <c>BatchSaveData</c>. Neither of
        /// those needs a hook here: Issue #815 already fixed <c>DeletedFileIds</c> at the source
        /// (<c>BaseCRUDVM.RejectUnresolvableFileAttachmentReferences</c> rejects an unresolvable
        /// posted FK at write time; <c>BatchSaveData</c> itself ignores <c>DeletedFileIds</c>
        /// entirely, since bulk import has no pre-existing entity to validate it against) — see
        /// the fuller status note on <see cref="CanAccessFile"/> above. The real remaining
        /// <c>UploadFileId</c> gap is the generated per-entity Import controllers, tracked as
        /// Issue #816. The declarative per-VM authorization tier meant to eventually replace all
        /// of these hooks is tracked as Issue #811.
        /// </para>
        /// </summary>
        /// <param name="vmType">The resolved type of the VM the caller asked to import into.</param>
        /// <returns><c>true</c> if the import is allowed; <c>false</c> to return 403.</returns>
        /// <remarks>
        /// #827: see <see cref="CanExportVm"/>'s remarks -- same DI-first, flag-fallback shape.
        /// </remarks>
        protected virtual bool CanImportVm(Type vmType) => ResolveEndpointAuthorizer()?.CanImportVm(Wtm, vmType) switch
        {
            WtmAuthorizationDecision.Allow => true,
            WtmAuthorizationDecision.Deny => false,
            _ => !(Wtm?.ConfigInfo?.EnforceVmImportAuthorization ?? false),
        };

        // MVC-006 (BREAKING): Selector was previously [Public] (unauthenticated).
        // Changed to [AllRights] so an authenticated session is required.
        // To restore the old open behaviour (e.g. for public kiosk deployments),
        // set "AllowUnauthenticatedSelector": true in your appsettings.json / Configs.
        // See CHANGELOG for migration notes.
        [HttpPost]
        [AllRights]
        public IActionResult Selector(string _DONOT_USE_VMNAME
            , string _DONOT_USE_KFIELD
            , string _DONOT_USE_VFIELD
            , string _DONOT_USE_FIELD
            , bool _DONOT_USE_MULTI_SEL
            , string _DONOT_USE_SEL_ID
            , string _DONOT_USE_SUBMIT
            , string _DONOT_USE_LINK_FIELD
            , string _DONOT_USE_TRIGGER_URL
            , string _DONOT_USE_CURRENTCS
        )
        {
            // MVC-006 opt-out: when AllowUnauthenticatedSelector=true, allow
            // unauthenticated callers (legacy / public-kiosk mode). Default = false (secure).
            if (ConfigInfo.AllowUnauthenticatedSelector != true && Wtm.LoginUserInfo == null)
            {
                return Unauthorized();
            }

            // MVC-010: reject unknown connection-string keys to prevent cross-DB reads (#503)
            if (!IsKnownConnectionKey(_DONOT_USE_CURRENTCS))
                return BadRequest("Unknown connection string key");

            string cs =_DONOT_USE_CURRENTCS;
            Wtm.CurrentCS = cs;
            var listVM = Wtm.CreateVM(_DONOT_USE_VMNAME, null, null, true) as IBasePagedListVM<TopBasePoco, ISearcher>;

            if (listVM is IBasePagedListVM<TopBasePoco, ISearcher>)
            {
                RedoUpdateModel(listVM);
            }

            listVM.SearcherMode = ListVMSearchModeEnum.Selector;
            listVM.RemoveActionColumn();
            listVM.RemoveAction();
            ViewBag.TextName = _DONOT_USE_KFIELD;
            ViewBag.ValName = _DONOT_USE_VFIELD;
            ViewBag.FieldName = _DONOT_USE_FIELD;
            ViewBag.MultiSel = _DONOT_USE_MULTI_SEL;
            ViewBag.SelId = _DONOT_USE_SEL_ID;
            ViewBag.SubmitFunc = _DONOT_USE_SUBMIT;
            ViewBag.LinkField = _DONOT_USE_LINK_FIELD;
            ViewBag.TriggerUrl = _DONOT_USE_TRIGGER_URL;
            ViewBag.CurrentCS = cs;
            #region 获取选中的数据
            ViewBag.SelectData = "[]";
            ViewBag.SelectorValueField = _DONOT_USE_VFIELD;
            if (listVM.Ids?.Count > 0)
            {
                listVM.DC = Wtm.CreateDC();
                var originNeedPage = listVM.NeedPage;
                listVM.NeedPage = false;
                listVM.SearcherMode = ListVMSearchModeEnum.Batch;
                Type modelType = listVM.ModelType;
                var para = Expression.Parameter(modelType);
                var idproperty = modelType.GetSingleProperty(_DONOT_USE_VFIELD);
                // Guard: an attacker-supplied _DONOT_USE_VFIELD that names a
                // non-existent property would cause GetSingleProperty to return
                // null, and the subsequent Expression.Property call would throw
                // ArgumentNullException (unauthenticated DoS — issue #106).
                // When the property is not found, skip SelectData population and
                // fall through to return the PartialView with empty SelectData ("[]").
                if (idproperty != null)
                {
                    var pro = Expression.Property(para, idproperty);
                    listVM.ReplaceWhere = listVM.Ids.GetContainIdExpression(modelType, Expression.Parameter(modelType), pro);
                    string selectData = SanitizeSelectorJson((listVM as IBasePagedListVM<TopBasePoco, BaseSearcher>).GetDataJson());
                    ViewBag.SelectData = selectData;
                    listVM.IsSearched = false;
                    listVM.SearcherMode = ListVMSearchModeEnum.Selector;
                    listVM.NeedPage = originNeedPage;
                }
            }
            #endregion

            return PartialView(listVM);
        }

        [ActionDescription("GetEmptyData")]
        public IActionResult GetEmptyData(string _DONOT_USE_VMNAME)
        {
            var listVM = Wtm.CreateVM(_DONOT_USE_VMNAME, null, null, true) as IBasePagedListVM<TopBasePoco, BaseSearcher>;
            string data = listVM.GetSingleDataJson(null, false);
            var rv = new ContentResult
            {
                ContentType = "application/json",
                Content = data
            };
            return rv;
        }


        /// <summary>
        /// 获取分页数据
        /// </summary>
        /// <param name="_DONOT_USE_VMNAME"></param>
        /// <param name="_DONOT_USE_CS"></param>
        /// <returns></returns>
        [HttpPost]
        [ActionDescription("GetPagingData")]
        public async Task<IActionResult> GetPagingData(string _DONOT_USE_VMNAME, string _DONOT_USE_CS)
        {
            // MVC-010: reject unknown connection-string keys to prevent lateral DB reads
            if (!IsKnownConnectionKey(_DONOT_USE_CS))
                return BadRequest("Unknown connection string key");

            var qs = new Dictionary<string, object>();
            foreach (var item in Request.Form.Keys)
            {
                qs.Add(item, Request.Form[item]);
            }
            Wtm.CurrentCS = _DONOT_USE_CS;
            var listVM = Wtm.CreateVM(_DONOT_USE_VMNAME, null, null, true) as IBasePagedListVM<TopBasePoco, BaseSearcher>;
            listVM.FC = qs;
            if (listVM is IBasePagedListVM<TopBasePoco, ISearcher>)
            {
                RedoUpdateModel(listVM);
                // #867: GetPagingData was the only one of the five RedoUpdateModel call sites
                // that did not re-pin SearcherMode after binding (Selector and both Export
                // endpoints all do). A caller-supplied "SearcherMode=Batch" here would route
                // GetSearchQuery through GetBatchQuery, which strips every Where the ListVM's own
                // GetSearchQuery() applied — including row-level authorization filtering — before
                // adding an Ids.Contains(...) clause. Pin it back to the mode this endpoint is
                // actually for.
                listVM.SearcherMode = ListVMSearchModeEnum.Search;
                string url = "";
                if (ConfigInfo.HasMainHost && Wtm.LoginUserInfo?.CurrentTenant == null)
                {
                    Type[] checktypes = new Type[3] { typeof(FrameworkUserBase), typeof(FrameworkGroup), typeof(FrameworkRole) };
                    if (typeof(FrameworkUserBase).IsAssignableFrom(listVM.ModelType))
                    {
                        url = "/api/_frameworkuser/search";
                    }
                    else if (typeof(FrameworkGroup).IsAssignableFrom(listVM.ModelType))
                    {
                        url = "/api/_frameworkgroup/search";
                    }
                    else if (typeof(FrameworkRole).IsAssignableFrom(listVM.ModelType))
                    {
                        url = "/api/_frameworkrole/search";
                    }                    
                }
                if(string.IsNullOrEmpty(url) == false)
                {
                    var result = await Wtm.CallAPI<string>("mainhost", url, HttpMethodEnum.POST, listVM.Searcher, 10);
                    var rv = new ContentResult
                    {
                        ContentType = "application/json",
                        Content = result.Data
                };
                    return rv;
                }
                else
                {
                    // #431 Server-side aggregate footers: compute aggregates (if any column
                    // has AggregateType set) and include them in the response under "Aggregates".
                    // The payload is a back-compatible addition — existing consumers that do not
                    // know about "Aggregates" will simply ignore the extra key.
                    var aggregates = listVM.ComputeAggregates();
                    string aggregateFragment = aggregates.Count > 0
                        ? $@",""Aggregates"":{System.Text.Json.JsonSerializer.Serialize(aggregates)}"
                        : string.Empty;

                    var rv = new ContentResult
                    {
                        ContentType = "application/json",
                        Content = $@"{{""Data"":{listVM.GetDataJson()},""Count"":{listVM.Searcher.Count},""Msg"":""success"",""Code"":{StatusCodes.Status200OK}{aggregateFragment}}}"
                    };
                    return rv;
                }
            }
            else
            {
                throw new Exception("Invalid Vm Name");
            }
        }


        /// <summary>
        /// 单元格编辑
        /// </summary>
        /// <param name="_DONOT_USE_VMNAME"></param>
        /// <param name="id">实体主键</param>
        /// <param name="field">属性名</param>
        /// <param name="value">属性值</param>
        /// <returns></returns>
        /// <summary>
        /// MVC-004: Override this method to restrict which fields may be edited via
        /// the inline-grid single-cell endpoint.  The default implementation returns
        /// <c>true</c> for all fields that pass the built-in blocklist.
        /// </summary>
        /// <param name="entity">The entity instance that would be mutated.</param>
        /// <param name="propertyName">The property name requested by the client.</param>
        /// <returns><c>true</c> if the edit is allowed; <c>false</c> to return 403.</returns>
        /// <remarks>
        /// #827: see <see cref="CanExportVm"/>'s remarks for the DI-first shape. This hook has
        /// no <c>Enforce*Authorization</c> config flag at all (it is the only WRITE endpoint
        /// among the five, and the only one that shipped with no no-code kill switch) -- so its
        /// Inherit/no-policy fallback is the unconditional <c>true</c> this method always
        /// returned before #827, unchanged.
        /// </remarks>
        protected virtual bool CanEditProperty(object entity, string propertyName) => ResolveEndpointAuthorizer()?.CanEditProperty(Wtm, entity, propertyName) switch
        {
            WtmAuthorizationDecision.Allow => true,
            WtmAuthorizationDecision.Deny => false,
            _ => true,
        };

        // #532: IDataContext.UpdateProperty<T>(T, string) is generic on the *concrete*
        // entity type because it calls DbContext.Set<T>() internally, which throws for an
        // unmapped base type. UpdateModelProperty only has the entity typed through the
        // covariant IBaseCRUDVM<TopBasePoco>.Entity (static type TopBasePoco), so the open
        // generic method must be closed over the entity's runtime type via reflection.
        // Cache the open MethodInfo once, and cache each closed-generic MethodInfo per
        // entity type, instead of reflecting on every request (perf convention).
        private static readonly System.Reflection.MethodInfo s_updatePropertyMethod =
            typeof(IDataContext).GetMethods()
                .First(m => m.Name == nameof(IDataContext.UpdateProperty)
                    && m.GetParameters().Length == 2
                    && m.GetParameters()[1].ParameterType == typeof(string));

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.MethodInfo> s_updatePropertyMethodCache = new();

        [HttpPost]
        public IActionResult UpdateModelProperty(string _DONOT_USE_VMNAME, Guid id, string field, string value)
        {
            if (string.IsNullOrWhiteSpace(field))
            {
                return BadRequest("Field name is required");
            }

            // Block navigation paths (dot-notation) to prevent traversal to related entities (#766)
            if (field.Contains('.'))
            {
                return BadRequest("Navigation property paths are not allowed for inline editing");
            }

            // Block known sensitive/infrastructure fields from inline editing
            var blockedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ID", "Password", "PasswordHash", "Salt",
                "TenantCode", "CreateTime", "CreateBy",
                "UpdateTime", "UpdateBy", "ITCode",
            };
            if (blockedFields.Contains(field))
            {
                return BadRequest("This field cannot be edited inline");
            }

            if (value == null && Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(Request.Form[nameof(value)]))
            {
                value = string.Empty;
            }

            // MVC-004: load the entity via the CRUD VM so field validation, duplicate
            // checking, and row-level auth are all exercised through the normal VM path.
            var vm = Wtm.CreateVM(_DONOT_USE_VMNAME, id, null, true) as IBaseCRUDVM<TopBasePoco>;
            if (vm?.Entity == null)
            {
                return BadRequest("Entity not found");
            }

            // #809: resolve the DataContext once and use it for every DB operation below
            // (the null guard, both UpdateProperty invocations, and SaveChanges) instead of
            // unconditionally reaching for Wtm.DC. Entity was already loaded through vm.DC
            // inside SetEntityById() above (BaseVM.DC returns the VM's own _dc when the VM
            // assigns one — the framework's supported multi-connection-string pattern —
            // falling back to Wtm.DC otherwise). Saving through Wtm.DC instead for such a VM
            // would silently write to the wrong database, or throw an EF tracking exception
            // (the entity is attached to a different DbContext instance) surfacing as a 500.
            var dc = (vm as BaseVM)?.DC ?? Wtm.DC;

            // Verify the property exists and is writable on the entity type
            var entityType = vm.Entity.GetType();
            var prop = entityType.GetProperty(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (prop == null || !prop.CanWrite)
            {
                return BadRequest("Field not found or not writable");
            }

            // #824 Part 1 / B1.1: unconditionally refuse to edit any FK whose principal is
            // FileAttachment, regardless of tenant. This closes ONE of Issue #824's write-path
            // sinks — the UpdateModelProperty exploit chain: without this gate, any [AllRights]
            // caller could set e.g. FrameworkUser.PhotoId to another tenant's FileAttachment GUID
            // through this endpoint — #815's FK gate
            // (BaseCRUDVM.RejectUnresolvableFileAttachmentReferences) never runs here because
            // #797 deliberately routes this endpoint around DoEdit()/DoEditPrepare(), and the
            // blockedFields HashSet above has no attachment-FK entry (and should not gain one — a
            // hardcoded field-name list would miss any downstream-defined attachment FK; see
            // DCExtension.IsFileAttachmentForeignKeyProperty's doc comment. Driving this off EF
            // relationship metadata instead of a name list follows the same principle Issue #824
            // states for its own recommended fix).
            //
            // IMPORTANT — this is NOT the fix Issue #824 asks for. The issue's own conclusion
            // (after five review rounds on #815) is that per-sink placement is "structurally
            // doomed" and it explicitly recommends a single guard at the
            // EmptyContext.SaveChanges/SaveChangesAsync boundary instead, so every write path is
            // covered by construction rather than one sink at a time (see the issue body's
            // "suggested implementation" section and docs/production-readiness.md). Gating only
            // this one endpoint was a scope decision made by the orchestrating session for this
            // PR — to close the live, already-reachable UpdateModelProperty chain quickly — not
            // something the issue itself asked for. The SaveChanges-boundary work Issue #824
            // describes REMAINS OUTSTANDING, and so do the other write-path sinks it lists
            // (BasePagedListVM.UpdateEntityList, BaseBatchVM.DoBatchEdit/Async,
            // BaseImportVM.BatchSaveData, grandchild IEnumerable<ISubFile>, direct DbSet writers).
            // This gate is defence in depth at one sink, not the architectural fix.
            //
            // Deny is unconditional, not tenant-conditional — a decision made for this PR, not
            // dictated by the issue: inline grid cell edit can only POST a bare GUID string,
            // never upload a file, so legitimate same-tenant use of
            // this path is ~nil — and a tenant-conditional check would drag tenant-resolution
            // logic into an endpoint that should not touch files at all. This means a same-tenant
            // attachment FK edit is ALSO rejected, on purpose.
            if (dc.IsFileAttachmentForeignKeyProperty(entityType, prop.Name))
            {
                return BadRequest("This field is a FileAttachment foreign key and cannot be edited inline");
            }

            // MVC-004: honour the opt-in per-property authz hook
            if (!CanEditProperty(vm.Entity, field))
            {
                return Forbid();
            }

            vm.Entity.SetPropertyValue(field, value);

            if (dc == null)
            {
                // Before #797, a null DC here was not actually tolerated: the code
                // unconditionally fell through to vm.DoEdit(false), which dereferences DC via
                // a null-forgiving `DC!.SaveChanges()` and would throw an unhandled
                // NullReferenceException (an opaque 500) instead of a clean 400. Returning a
                // 400 here is a deliberate hardening of that path, not a new restriction — no
                // caller could previously depend on a null-DC request succeeding.
                return BadRequest(Wtm.Localizer?["Sys.NoDbContext"] ?? "No database context available");
            }

            // #809: run only the duplicate-key check (ValidateDuplicateData(), the
            // "DuplicateCheck" MVC-004 refers to), not the full user-overridable Validate().
            // Several in-tree generated CRUD VMs override Validate() to require state that
            // only InitVM() populates (e.g. FrameworkMenuVM.Validate() requires
            // SelectedModule, which only InitVM() sets), and this endpoint builds its VM with
            // passInit: true — InitVM() never runs, since this action only needs Entity
            // loaded, not the VM's full init pipeline. Calling the full Validate() here would
            // 400 requests that a bound-VM Edit action would have accepted. BaseCRUDVM's own
            // Validate() override is only base.Validate() (a no-op in BaseVM) plus
            // ValidateDuplicateData(), so this is the whole of what MVC-004 ever promised. Run
            // this before any DC mutation below so a failed validation never touches the
            // database — mirroring the standard Edit action's
            // "if (!ModelState.IsValid) return ... else vm.DoEdit()" ordering.
            vm.ValidateDuplicateDataOnly();
            if (!vm.MSD.IsValid)
            {
                var validationError = vm.MSD.GetFirstError();
                return BadRequest(string.IsNullOrEmpty(validationError) ? "Validation failed" : validationError);
            }

            // #532/#797: the entity is loaded AsNoTracking (detached) via GetById(), and
            // routing the save through vm.DoEdit(false) — as this endpoint originally did —
            // runs DoEditPrepare's full edit-mutation pass unconditionally. That pass nulls
            // every TopBasePoco-typed navigation on the entity (BaseCRUDVM.DoEditPrepare's
            // 更新子表 region), and for the standard generated CRUD VM shape — whose
            // constructor calls SetInclude() for its navigations, and whose generated
            // DoEdit() override resyncs child collections from a "Selected<X>IDs" property
            // that is only ever populated by InitVM() — resyncs or deletes DB-loaded child
            // rows, none of which this single-field inline edit ever intended to touch.
            // #532 originally fixed "the value never persists" by Attach()-ing the entity
            // ahead of that pass, but Attach() tracks the whole reachable graph, so the
            // untouched pass above then got applied to tracked entities and its collateral
            // mutations were actually persisted on SaveChanges.
            //
            // Fix: never run DoEdit()/DoEditPrepare for this endpoint. Persist only the one
            // requested property (plus the standard UpdateTime/UpdateBy audit fields, which
            // DoEditPrepare would otherwise have set) by marking exactly those properties
            // Modified on the entity, so nothing else in the tracked graph can be written.
            var closedUpdatePropertyMethod = s_updatePropertyMethodCache.GetOrAdd(
                entityType,
                t => s_updatePropertyMethod.MakeGenericMethod(t));

            if (vm.Entity is IBasePoco auditEntity)
            {
                auditEntity.UpdateTime = Wtm.TimeProvider.GetLocalNow().DateTime;
                auditEntity.UpdateBy = Wtm.LoginUserInfo?.ITCode;
            }

            // #809: emit the "Edit" audit ChangeLog row (only written when TModel carries
            // [AuditChanges]) before SaveChanges, so the DB snapshot it loads for OldValues
            // still reflects the pre-edit row, and so it commits atomically with the property
            // write below. Bypassing DoEdit() above to avoid its collateral writes (#797) also
            // dropped DoEdit()'s own AppendChangeLog() call — this restores the audit trail for
            // inline edits of RBAC entities (FrameworkUser, FrameworkRole, ...) going through
            // this [AllRights] endpoint.
            vm.AppendEditChangeLog();

            // Use prop.Name (the exact CLR-cased property name resolved above), not the
            // raw client-supplied 'field', because EF Core's EntityEntry.Property(string)
            // lookup is case-sensitive. The first call attaches the entity (see
            // IDataContext.UpdateProperty); subsequent calls reuse the same tracked entry.
            closedUpdatePropertyMethod.Invoke(dc, [vm.Entity, prop.Name]);
            if (vm.Entity is IBasePoco)
            {
                closedUpdatePropertyMethod.Invoke(dc, [vm.Entity, nameof(IBasePoco.UpdateTime)]);
                closedUpdatePropertyMethod.Invoke(dc, [vm.Entity, nameof(IBasePoco.UpdateBy)]);
            }

            try
            {
                dc.SaveChanges();
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
            {
                vm.MSD?.AddModelError(" ", Wtm.Localizer?["Sys.ConcurrencyConflict"] ?? "The record was modified by another user. Please reload and try again.");
            }
            catch
            {
                vm.MSD?.AddModelError(" ", Wtm.Localizer?["Sys.EditFailed"] ?? "Edit failed");
            }

            if (!vm.MSD.IsValid)
            {
                var firstError = vm.MSD.GetFirstError();
                return BadRequest(string.IsNullOrEmpty(firstError) ? "Validation failed" : firstError);
            }
            return JsonMore("Success");
        }

        #region Import/Export Excel

        /// <summary>
        /// Download Excel
        /// </summary>
        /// <param name="_DONOT_USE_VMNAME"></param>
        /// <param name="_DONOT_USE_CS"></param>
        /// <returns></returns>
        [HttpPost]
        [ActionDescription("Export")]
        public IActionResult GetExportExcel(string _DONOT_USE_VMNAME, string _DONOT_USE_CS)
        {
            // MVC-010: reject unknown connection-string keys to prevent lateral DB reads
            if (!IsKnownConnectionKey(_DONOT_USE_CS))
                return BadRequest("Unknown connection string key");

            var qs = new Dictionary<string, object>();
            foreach (var item in Request.Query.Keys)
            {
                qs.Add(item, Request.Query[item]);
            }
            foreach (var item in Request.Form)
            {
                if (qs.ContainsKey(item.Key) == false)
                {
                    qs.Add(item.Key, item.Value);
                }
            }
            Wtm.CurrentCS =  _DONOT_USE_CS;
            // #829: resolve the Type WITHOUT constructing anything (WTMContext.TryResolveVmType)
            // so CanExportVm can deny before a single constructor, SetSubVm, or
            // lvm.DoInitListVM() runs on the caller-named VM. The round-3 passInit:true probe
            // this replaced (Wtm.CreateVM(name, null, null, true)) still allocated a full
            // instance via reflection and still ran DoInitListVM() unconditionally -- passInit
            // only gates DoInit()/InitVM()/searcher.DoInit(), never construction or
            // DoInitListVM() (see WTMContext.CreateVM.cs). This is the same resolution
            // DoImport already used for the #818 fix; TryResolveVmType itself returns null for
            // an unresolvable/unregistered name instead of throwing, so the previous
            // ArgumentException catch is no longer needed.
            Type? instanceType = Wtm.TryResolveVmType(_DONOT_USE_VMNAME);
            if (instanceType == null || !typeof(IBasePagedListVM<TopBasePoco, ISearcher>).IsAssignableFrom(instanceType))
                return BadRequest(MvcProgram._localizer?["Sys.InvalidVM"] ?? "Invalid Vm Name");

            // #796: authorize the target VM type — without this, any authenticated user could
            // Excel-export any ListVM registered in the application by supplying its type name.
            if (!CanExportVm(instanceType))
            {
                return Forbid();
            }

            // Only now do we pay for a fully initialized instance (DoInit()/InitVM()).
            var listVM = Wtm.CreateVM(_DONOT_USE_VMNAME) as IBasePagedListVM<TopBasePoco, ISearcher>;
            if (listVM == null)
                return BadRequest(MvcProgram._localizer?["Sys.InvalidVM"] ?? "Invalid Vm Name");

            listVM.FC = qs;
            if (listVM is IBasePagedListVM<TopBasePoco, ISearcher>)
            {
                RedoUpdateModel(listVM);

                listVM.SearcherMode = listVM.Ids != null && listVM.Ids.Count > 0 ? ListVMSearchModeEnum.CheckExport : ListVMSearchModeEnum.Export;

                var data = listVM.GenerateExcel();

                if (listVM.ExportRowCount == 0)
                {
                    return StatusCode(422, new { message = MvcProgram._localizer?["Sys.NoData"] ?? "No data" });
                }

                var now = Wtm.TimeProvider.GetLocalNow().DateTime;
                HttpContext.Response.Cookies.Append("DONOTUSEDOWNLOADING", "0", new Microsoft.AspNetCore.Http.CookieOptions() { Path = "/", Expires = now.AddDays(2), SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax, Secure = Request.IsHttps });

                var typeName = instanceType?.Name ?? _DONOT_USE_VMNAME ?? "Export";
                return File(data, "application/vnd.ms-excel", $"Export_{typeName}_{now.ToString("yyyy-MM-dd")}.xls");
            }
            else
            {
                throw new Exception("Invalid Vm Name");
            }
        }

        /// <summary>
        /// Opt-in streaming Excel export using NPOI SXSSFWorkbook.
        /// Only available when the VM has <c>UseStreamingExport = true</c>.
        /// All guards (MVC-010, MVC-013, ExportRowCount==0, DONOTUSEDOWNLOADING cookie)
        /// from <see cref="GetExportExcel"/> are preserved.
        /// <para>
        /// The SXSSF workbook is written to a <see cref="MemoryStream"/> so the
        /// response headers (including the 422 empty-data guard) can be set before
        /// the bytes are sent to the client.  Peak memory is proportional to the
        /// SXSSF sliding window (<c>rowWindowSize</c> rows), not the full result set.
        /// </para>
        /// </summary>
        [HttpPost]
        [ActionDescription("ExportStream")]
        public IActionResult GetExportExcelStream(string _DONOT_USE_VMNAME, string _DONOT_USE_CS)
        {
            // MVC-010: reject unknown connection-string keys
            if (!IsKnownConnectionKey(_DONOT_USE_CS))
                return BadRequest("Unknown connection string key");

            var qs = new Dictionary<string, object>();
            foreach (var item in Request.Query.Keys)
            {
                qs.Add(item, Request.Query[item]);
            }
            foreach (var item in Request.Form)
            {
                if (!qs.ContainsKey(item.Key))
                {
                    qs.Add(item.Key, item.Value);
                }
            }
            Wtm.CurrentCS = _DONOT_USE_CS;

            // #829: same fix as GetExportExcel — resolve the Type via TryResolveVmType (no
            // construction, no DoInitListVM()) instead of a passInit:true probe. See that
            // method's comment above for why the probe this replaced still had a construction-
            // before-authorization gap.
            Type? instanceType = Wtm.TryResolveVmType(_DONOT_USE_VMNAME);
            if (instanceType == null || !typeof(IBasePagedListVM<TopBasePoco, ISearcher>).IsAssignableFrom(instanceType))
                return BadRequest(MvcProgram._localizer?["Sys.InvalidVM"] ?? "Invalid Vm Name");

            // #796: authorize the target VM type — same gap as GetExportExcel, since this is
            // the same shared, VM-name-driven endpoint with a different serialization format.
            if (!CanExportVm(instanceType))
            {
                return Forbid();
            }

            // Only now do we pay for a fully initialized instance (DoInit()/InitVM()).
            var listVM = Wtm.CreateVM(_DONOT_USE_VMNAME) as IBasePagedListVM<TopBasePoco, ISearcher>;
            if (listVM == null)
                return BadRequest(MvcProgram._localizer?["Sys.InvalidVM"] ?? "Invalid Vm Name");

            // Opt-in gate: the VM must explicitly enable streaming
            if (!listVM.UseStreamingExport)
                return BadRequest("Streaming export is not enabled for this VM. Set UseStreamingExport = true.");

            listVM.FC = qs;
            RedoUpdateModel(listVM);
            listVM.SearcherMode = listVM.Ids != null && listVM.Ids.Count > 0
                ? ListVMSearchModeEnum.CheckExport
                : ListVMSearchModeEnum.Export;

            // Use a MemoryStream so we can inspect ExportRowCount before committing
            // to the HTTP response.  The SXSSF window (default 100 rows) keeps
            // in-process heap proportional to the window size — only the final serialised
            // XLSX bytes are temporarily buffered here before being handed to File().
            using var ms = new MemoryStream();
            listVM.GenerateExcelToStream(ms);

            if (listVM.ExportRowCount == 0)
            {
                return StatusCode(422, new { message = MvcProgram._localizer?["Sys.NoData"] ?? "No data" });
            }

            var now = Wtm.TimeProvider.GetLocalNow().DateTime;
            HttpContext.Response.Cookies.Append("DONOTUSEDOWNLOADING", "0",
                new Microsoft.AspNetCore.Http.CookieOptions()
                {
                    Path = "/",
                    Expires = now.AddDays(2),
                    SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                    Secure = Request.IsHttps
                });

            ms.Position = 0;
            var typeName = instanceType?.Name ?? _DONOT_USE_VMNAME ?? "Export";
            const string contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            return File(ms.ToArray(), contentType, $"Export_{typeName}_{now:yyyy-MM-dd}.xlsx");
        }

        /// <summary>
        /// Download Excel Template
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [ActionDescription("DownloadTemplate")]
        public IActionResult GetExcelTemplate(string _DONOT_USE_VMNAME, string _DONOT_USE_CS)
        {
            // MVC-010: reject unknown connection-string keys to prevent lateral DB reads
            if (!IsKnownConnectionKey(_DONOT_USE_CS))
                return BadRequest("Unknown connection string key");
            //Wtm.CurrentCS = _DONOT_USE_CS ?? "default";
            //
            // #796 (residual-risk follow-up): GetExcelTemplate is a caller-VM-named
            // file-generation endpoint with the same shape of exposure as GetExportExcel /
            // GetExportExcelStream — it discloses an arbitrary registered VM's column layout
            // to any authenticated caller — so it is gated with the same CanExportVm hook.
            // Default (flag off, no override) is unaffected.
            //
            // #829: this used to probe via Wtm.CreateVM(name, null, null, true), which — for
            // an IBaseImport<BaseTemplateVM> — called tvm.Template.DoInit() UNCONDITIONALLY
            // (WTMContext.CreateVM.cs does not gate that call behind passInit the way it gates
            // the plain-VM DoInit()/ListVM searcher.DoInit()), so the template VM's own DoInit()
            // — and whatever DB queries it performs — ran before CanExportVm's deny decision.
            // Resolving the Type via TryResolveVmType instead avoids constructing anything at
            // all until after authorization; this is the same fix applied to GetExportExcel/
            // GetExportExcelStream/GetDeletePreview and closes what #829 called the one probe
            // shape that could not be fixed by passInit alone.
            var vmType = Wtm.TryResolveVmType(_DONOT_USE_VMNAME);
            if (vmType == null || !typeof(IBaseImport<BaseTemplateVM>).IsAssignableFrom(vmType))
                return BadRequest(MvcProgram._localizer?["Sys.InvalidVM"] ?? "Invalid Vm Name");
            if (!CanExportVm(vmType))
                return Forbid();

            // Only now do we pay for the fully initialized instance (DoInit()).
            var importVM = Wtm.CreateVM(_DONOT_USE_VMNAME) as IBaseImport<BaseTemplateVM>;
            if (importVM == null)
                return BadRequest(MvcProgram._localizer?["Sys.InvalidVM"] ?? "Invalid Vm Name");

            var qs = new Dictionary<string, string>();
            foreach (var item in Request.Query.Keys)
            {
                qs.Add(item, Request.Query[item]);
            }
            importVM.SetParms(qs);
            var data = importVM.GenerateTemplate(out string fileName);
            HttpContext.Response.Cookies.Append("DONOTUSEDOWNLOADING", "0", new Microsoft.AspNetCore.Http.CookieOptions() { Path = "/", Expires = Wtm.TimeProvider.GetLocalNow().DateTime.AddDays(2), SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax, Secure = Request.IsHttps });
            return File(data, "application/vnd.ms-excel", fileName);
        }

        /// <summary>
        /// Generic framework import endpoint (#433).
        /// Accepts a posted form whose fields are bound to the concrete
        /// <see cref="BaseImportVM{T,P}"/> identified by <paramref name="_DONOT_USE_VMNAME"/>.
        /// <para>
        /// When <paramref name="validateOnly"/> is <c>true</c> the VM's
        /// <see cref="BaseImportVM{T,P}.ValidateOnly"/> flag is set so that validation
        /// runs but no rows are persisted (dry-run / preflight mode).
        /// </para>
        /// <para>
        /// Success response (HTTP 200):
        /// <code>{ "ImportedCount": N, "InlineErrors": [], "ValidateOnly": false }</code>
        /// Error response (HTTP 400):
        /// <code>{ "ImportedCount": 0, "InlineErrors": [{...}], "ValidateOnly": false }</code>
        /// The <c>InlineErrors</c> array carries up to <c>InlineErrorLimit</c> per-row
        /// errors so the UI can display them without requiring the user to download an
        /// error file.  Callers that do not use the new fields (legacy callers that only
        /// inspect the status code) are fully backward-compatible.
        /// </para>
        /// </summary>
        [HttpPost]
        [ActionDescription("Import")]
        public IActionResult DoImport(string _DONOT_USE_VMNAME, string _DONOT_USE_CS, bool validateOnly = false)
        {
            // MVC-010: reject unknown connection-string keys to prevent lateral DB reads
            if (!IsKnownConnectionKey(_DONOT_USE_CS))
                return BadRequest("Unknown connection string key");

            Wtm.CurrentCS = _DONOT_USE_CS;

            // #818: resolve the Type WITHOUT constructing anything, so CanImportVm can deny
            // before a single constructor/DoInit()/DB query runs on the caller-named VM. Uses
            // WTMContext.TryResolveVmType directly — the same resolution CreateVM(string, ...)
            // below goes on to construct — so there is exactly ONE implementation of the
            // name-to-Type lookup and the authorized type can never diverge from the constructed
            // one (see the #818 review discussion; a passInit:true CreateVM probe, the shape used
            // by GetExportExcel/GetDeletePreview, is not enough for this endpoint specifically —
            // WTMContext.CreateVM's IBaseImport<BaseTemplateVM> branch calls
            // tvm.Template.DoInit() unconditionally, so even a passInit:true probe would still run
            // that DoInit() and whatever DB queries it performs before an authorization decision).
            var vmType = Wtm.TryResolveVmType(_DONOT_USE_VMNAME);
            if (vmType == null || !typeof(IWtmImportable).IsAssignableFrom(vmType))
                return BadRequest(MvcProgram._localizer?["Sys.InvalidVM"] ?? "Not an import VM");

            // #818: authorize the target VM type before constructing anything — without this,
            // any authenticated caller could bulk-insert into any importable entity by supplying
            // its type name. See CanImportVm.
            if (!CanImportVm(vmType))
            {
                HttpContext.RequestServices.GetService<ILogger<_FrameworkController>>()?.LogWarning(
                    "DoImport denied: user {UserId} was refused authorization to import VM {VmType}",
                    Wtm.LoginUserInfo?.ITCode, vmType.FullName);
                return Forbid();
            }

            // Resolve the import VM; return 400 on an unknown or non-import VM type.
            BaseVM rawVm;
            try
            {
                rawVm = Wtm.CreateVM(_DONOT_USE_VMNAME, null, null, true);
            }
            catch (ArgumentException)
            {
                return BadRequest(MvcProgram._localizer?["Sys.InvalidVM"] ?? "Invalid Vm Name");
            }

            // #818 defense-in-depth: the type CanImportVm just authorized (vmType) and the type
            // CreateVM just constructed (rawVm.GetType()) both come from the same
            // TryResolveVmType call now, so they cannot structurally diverge — but this guard
            // costs one line and directly encodes that invariant, so it stays even after the
            // resolvers were unified: a future refactor that reintroduces a second resolution
            // path (or a caller that swaps in a different CreateVM overload) trips this instead
            // of silently authorizing type A while constructing type B.
            if (rawVm.GetType() != vmType)
                return Forbid();

            if (rawVm is not IWtmImportable importVm)
                return BadRequest(MvcProgram._localizer?["Sys.InvalidVM"] ?? "Not an import VM");

            // Bind posted form fields onto the VM (mirrors how GetPagingData/GetExportExcel bind).
            var fc = new Dictionary<string, object>();
            foreach (var key in Request.Form.Keys)
                fc[key] = Request.Form[key];
            foreach (var key in Request.Query.Keys)
            {
                if (!fc.ContainsKey(key))
                    fc[key] = Request.Query[key];
            }
            rawVm.FC = fc;
            RedoUpdateModel(rawVm);

            // #814: UploadFileId arrives through model binding (RedoUpdateModel above), not as
            // an action parameter, so it cannot be checked before this point. Without this guard
            // BatchSaveData() below both READS the FileAttachment at this id (SetTemplateData)
            // and, on a successful non-ValidateOnly run, DELETES it — entirely unauthorized, even
            // with EnforceFileAccessAuthorization=true, since nothing on this path called
            // CanAccessFile. An id that is present but doesn't resolve to a real Guid is denied
            // outright rather than silently ignored; a genuinely absent UploadFileId is left to
            // BatchSaveData's own "please upload template" validation error.
            //
            // Read via reflection off the concrete VM rather than adding a member to
            // IWtmImportable: BaseImportVM<T,P> already exposes a public settable UploadFileId,
            // and putting it on the interface instead would be a source-breaking change for any
            // downstream type that implements IWtmImportable directly (this repo's Compatibility
            // red line) — see the PR body's Compatibility section.
            //
            // Scope: this guard covers UploadFileId (the uploaded import template) only.
            // BaseVM.DeletedFileIds — read at the top of BatchSaveData — was the #815 hole; that
            // is now fixed at the source in BaseCRUDVM (a posted FileAttachment FK that does not
            // resolve for the caller's own tenant is rejected at write time — see
            // BaseCRUDVM.RejectUnresolvableFileAttachmentReferences — with
            // WtmFileProvider.DeleteFileTenantScoped and the pre-save entity-reference check as
            // defence in depth at every DeletedFileIds/DoRealDelete/DoBatchDelete sink) and at
            // the BaseImportVM level (BatchSaveData still ignores DeletedFileIds entirely rather
            // than deleting unchecked, since bulk import has no pre-existing entity to validate
            // against), so it needs no guard here. The real remaining UploadFileId
            // gap is NOT this action — DoImport DOES call CanAccessFile below — it is the code
            // generator's own per-entity Import($modelname$ImportVM, ...) action
            // (GeneratorFiles/Mvc/Controller.txt), which doesn't inherit from _FrameworkController
            // at all and so never reaches this guard; tracked as #816. DoImport's own VM-level
            // authorization (whether the caller may run this import VM at all, as opposed to
            // which file it may touch) is likewise out of scope for this file-access guard and is
            // tracked as #818.
            string? uploadFileId = rawVm.GetType().GetProperty("UploadFileId")?.GetValue(rawVm) as string;
            if (!string.IsNullOrEmpty(uploadFileId))
            {
                if (!TryNormalizeFileId(uploadFileId, out var normalizedUploadFileId) || !CanAccessFile(normalizedUploadFileId))
                {
                    HttpContext.RequestServices.GetService<ILogger<_FrameworkController>>()?.LogWarning(
                        "DoImport denied: user {UserId} was refused access to uploaded file {FileId}",
                        Wtm.LoginUserInfo?.ITCode, uploadFileId);
                    return Forbid();
                }
            }

            // Apply the dry-run flag before BatchSaveData runs.
            importVm.ValidateOnly = validateOnly;

            bool success = importVm.BatchSaveData();

            // Build the back-compatible response envelope.
            // Existing callers that only check the status code are unaffected; new callers
            // can read InlineErrors and ValidateOnly from the JSON body.
            var inlineErrors = importVm.InlineErrors
                .Select(e => new { row = e.Index, message = e.Message })
                .ToList();

            if (!success)
            {
                return BadRequest(new
                {
                    ImportedCount = 0,
                    InlineErrors = inlineErrors,
                    ValidateOnly = importVm.ValidateOnly,
                });
            }

            return Ok(new
            {
                ImportedCount = importVm.ImportedEntityCount,
                InlineErrors = inlineErrors,
                ValidateOnly = importVm.ValidateOnly,
            });
        }

        #endregion

        [AllowAnonymous]
        [ActionDescription("Sys.ErrorHandle")]
        public IActionResult Error()
        {
            var ex = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
            // MVC-012: guard against null feature (e.g. calling /error directly with no exception context)
            if (ex == null)
                return BadRequest(MvcProgram._localizer?["Sys.Error"] ?? "An error occurred while processing your request.");
            ActionLog log = new ActionLog();
            log.LogType = ActionLogTypesEnum.Exception;
            log.ActionTime = Wtm.TimeProvider.GetLocalNow().DateTime;
            log.ITCode = Wtm.LoginUserInfo?.ITCode ?? string.Empty;

            // MVC-002: EF dynamic-query exceptions have null TargetSite — guard to
            // prevent a double-fault that would mask the original error in the log.
            var targetSite = ex.Error?.TargetSite;
            var declaringType = targetSite?.DeclaringType;

            var controllerDes = declaringType?.GetCustomAttributes(typeof(ActionDescriptionAttribute), false).Cast<ActionDescriptionAttribute>().FirstOrDefault();
            var actionDes = targetSite?.GetCustomAttributes(typeof(ActionDescriptionAttribute), false).Cast<ActionDescriptionAttribute>().FirstOrDefault();
            var postDes = targetSite?.GetCustomAttributes(typeof(HttpPostAttribute), false).Cast<HttpPostAttribute>().FirstOrDefault();
            //给日志的多语言属性赋值
            log.ModuleName = controllerDes?.GetDescription(declaringType) ?? declaringType?.Name.Replace("Controller", string.Empty) ?? "Unknown";
            log.ActionName = actionDes?.GetDescription(declaringType) ?? targetSite?.Name ?? "Unknown";
            if (postDes != null)
            {
                log.ActionName += "[P]";
            }
            log.ActionUrl = ex.Path;
            log.IP = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
            log.Remark = ex.Error?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(log.Remark) == false && log.Remark.Length > 2000)
            {
                log.Remark = log.Remark.Substring(0, 2000);
            }
            DateTime? starttime = HttpContext.Items["actionstarttime"] as DateTime?;
            if (starttime != null)
            {
                log.Duration = Wtm.TimeProvider.GetLocalNow().DateTime.Subtract(starttime.Value).TotalSeconds;
            }
            var logger = HttpContext.RequestServices.GetRequiredService<ILogger<ActionLog>>();
            if (logger != null)
            {
                logger.Log<ActionLog>(LogLevel.Error, new EventId(), log, null, (a, b) =>
                {
                    return a.GetLogString();
                });
            }

            var rv = string.Empty;
            // Only expose full stack traces in debug mode when request is from localhost
            var remoteIp = HttpContext.Connection.RemoteIpAddress;
            var isLocalhost = remoteIp != null &&
                (System.Net.IPAddress.IsLoopback(remoteIp) ||
                 remoteIp.Equals(System.Net.IPAddress.IPv6Loopback));
            if (ConfigInfo.IsQuickDebug == true && isLocalhost)
            {
                rv = (ex.Error?.ToString() ?? string.Empty).Replace(Environment.NewLine, "<br />");
            }
            else
            {
                // Never expose raw exception messages — they may contain SQL, paths, or secrets (#769)
                rv = MvcProgram._localizer?["Sys.Error"] ?? "An error occurred while processing your request.";
            }
            return BadRequest(rv);
        }

        [HttpPost]
        [ActionDescription("UploadFileRoute")]
        public async Task<IActionResult> Upload([FromServices] WtmFileProvider fp, string sm = null, string groupName = null, string subdir = null, string extra = null, bool IsTemprory = true, string _DONOT_USE_CS=null)
        {
            // MVC-010: reject unknown connection-string keys to prevent lateral DB reads
            if (!IsKnownConnectionKey(_DONOT_USE_CS))
                return BadRequest("Unknown connection string key");
            var FileData = Request.Form.Files[0];

            // Issue #407: opt-in upload validation (extension / content-type / size).
            var validationResult = await ValidateUploadAsync(FileData).ConfigureAwait(false);
            if (!validationResult.IsValid)
                return BadRequest(validationResult.Error);

            var file = fp.Upload(FileData.FileName, FileData.Length, FileData.OpenReadStream(), groupName, subdir, extra, sm, Wtm.CreateDC(cskey: _DONOT_USE_CS));
            return JsonMore(new { Id = file.GetID(), Name = file.FileName });
        }

        [HttpPost]
        [ActionDescription("UploadFileRoute")]
        public async Task<IActionResult> UploadImage([FromServices] WtmFileProvider fp, string sm = null, string groupName = null, string subdir = null, string extra = null, bool IsTemprory = true, string _DONOT_USE_CS = null, int? width = null, int? height = null)
        {
            // MVC-010: reject unknown connection-string keys to prevent lateral DB reads
            if (!IsKnownConnectionKey(_DONOT_USE_CS))
                return JsonMore(new { Errors = "Unknown connection string key" }, StatusCodes.Status400BadRequest);
            if (width == null && height == null)
            {
                return await Upload(fp, sm, groupName, subdir, extra, IsTemprory, _DONOT_USE_CS).ConfigureAwait(false);
            }
            var FileData = Request.Form.Files[0];

            // Issue #407: opt-in upload validation before any image processing.
            var validationResult = await ValidateUploadAsync(FileData).ConfigureAwait(false);
            if (!validationResult.IsValid)
                return BadRequest(validationResult.Error);

            Image oimage;
            try
            {
                oimage = Image.Load(FileData.OpenReadStream());
            }
            catch (Exception)
            {
                return JsonMore(new { Id = string.Empty, Name = string.Empty }, StatusCodes.Status400BadRequest);
            }
            // oimage is assigned here on every non-throw path; wrap in using so
            // the pooled pixel buffer is released even if Mutate/SaveAsJpeg/Upload throws.
            using (oimage)
            using (var ms = new MemoryStream())
            {
                if (width == null)
                {
                    width = height * oimage.Width / oimage.Height;
                }
                if (height == null)
                {
                    height = width * oimage.Height / oimage.Width;
                }
                oimage.Mutate(x => x.Resize(width.Value, height.Value));
                oimage.SaveAsJpeg(ms);
                ms.Position = 0;

                var file = fp.Upload(FileData.FileName, ms.Length, ms, groupName, subdir, extra, sm, Wtm.CreateDC(cskey: _DONOT_USE_CS));
                return JsonMore(new { Id = file.GetID(), Name = file.FileName });
            }
        }

        [HttpPost]
        [ActionDescription("UploadForLayUIRichTextBox")]
        public async Task<IActionResult> UploadForLayUIRichTextBox([FromServices] WtmFileProvider fp, string _DONOT_USE_CS = null, string groupName = null, string subdir = null)
        {
            // MVC-010: reject unknown connection-string keys to prevent lateral DB reads
            if (!IsKnownConnectionKey(_DONOT_USE_CS))
                return Content("{\"code\": 1 , \"msg\": \"Unknown connection string key\", \"data\": {\"src\": \"\"}}");
            var FileData = Request.Form.Files[0];

            // Issue #407: opt-in upload validation.
            var validationResult = await ValidateUploadAsync(FileData).ConfigureAwait(false);
            if (!validationResult.IsValid)
                return Content($"{{\"code\": 1 , \"msg\": \"{HttpUtility.JavaScriptStringEncode(validationResult.Error ?? "Upload rejected")}\", \"data\": {{\"src\": \"\"}}}}");

            var file = fp.Upload(FileData.FileName, FileData.Length, FileData.OpenReadStream(), groupName, subdir, dc: Wtm.CreateDC(cskey: _DONOT_USE_CS));
            if (file != null)
            {
                string url = $"/_Framework/GetFile?id={file.GetID()}&stream=true&_DONOT_USE_CS={CurrentCS}";
                return Content($"{{\"code\": 0 , \"msg\": \"\", \"data\": {{\"src\": \"{url}\"}}}}");

            }
            else
            {
                return Content($"{{\"code\": 1 , \"msg\": \"{MvcProgram._localizer["Sys.UploadFailed"]}\", \"data\": {{\"src\": \"\"}}}}");

            }

        }

        /// <summary>
        /// Builds an <see cref="UploadValidationContext"/> from <paramref name="file"/> and
        /// delegates to the registered <see cref="IUploadValidator"/>.
        /// </summary>
        private async Task<UploadValidationResult> ValidateUploadAsync(IFormFile file)
        {
            var validator = HttpContext.RequestServices.GetService<IUploadValidator>();
            if (validator == null)
                return UploadValidationResult.Valid;

            var fileName = file.FileName ?? string.Empty;
            var dotPos = fileName.LastIndexOf('.');
            var extension = dotPos >= 0 ? fileName[dotPos..].ToLowerInvariant() : string.Empty;

            var ctx = new UploadValidationContext
            {
                FileName = fileName,
                Extension = extension,
                ContentType = file.ContentType ?? string.Empty,
                Length = file.Length,
                OpenReadStream = file.OpenReadStream,
            };

            return await validator.ValidateAsync(ctx, HttpContext.RequestAborted).ConfigureAwait(false);
        }

        [ActionDescription("GetFileName")]
        public IActionResult GetFileName([FromServices] WtmFileProvider fp, Guid id, string _DONOT_USE_CS)
        {
            // MVC-010: reject unknown connection-string keys to prevent lateral DB reads
            if (!IsKnownConnectionKey(_DONOT_USE_CS))
                return BadRequest("Unknown connection string key");
            // #814: opt-in ownership/authorization hook — see CanAccessFile for why this
            // cannot enforce true ownership by default (FileAttachment has no owner column).
            // Denies with Forbid() (not EmptyResult()), even though an existence oracle DOES
            // technically exist here too: WtmFileProvider.GetFileName returns the literal string
            // "unknown" for a missing id, so a caller can already tell "no such file" apart from
            // a real name. That oracle is accepted for this endpoint specifically because it only
            // ever exposes a sanitized file NAME, never file contents — unlike GetFile/ViewFile,
            // which expose contents and therefore get the indistinguishable-EmptyResult treatment
            // (see GetFile's comment) so a denial can't be told apart from a genuine miss.
            if (!CanAccessFile(id.ToString()))
            {
                HttpContext.RequestServices.GetService<ILogger<_FrameworkController>>()?.LogWarning(
                    "GetFileName denied: user {UserId} was refused access to file {FileId}",
                    Wtm.LoginUserInfo?.ITCode, id);
                return Forbid();
            }
            return Ok(fp.GetFileName(id.ToString(), Wtm.CreateDC(cskey: _DONOT_USE_CS)));
        }

        [ActionDescription("GetFile")]
        public async Task<IActionResult> GetFile([FromServices] WtmFileProvider fp, string id, bool stream = false, string _DONOT_USE_CS = null, int? width = null, int? height = null)
        {
            // MVC-010: reject unknown connection-string keys to prevent lateral DB reads
            if (!IsKnownConnectionKey(_DONOT_USE_CS))
                return new EmptyResult();
            // #814: opt-in ownership/authorization hook — see CanAccessFile for why this
            // cannot enforce true ownership by default (FileAttachment has no owner column).
            // An id that doesn't even parse as a Guid is rejected outright (TryNormalizeFileId
            // returns false) rather than reaching CanAccessFile with an empty string, which
            // would let a malformed-but-"allowed" id sail through unnoticed.
            // Deliberately returns EmptyResult() (HTTP 200, empty body) rather than Forbid() on
            // denial: a distinguishable "denied" response would let a caller probing file ids
            // tell "exists but denied" apart from "does not exist" — an existence oracle.
            // GetFileName returns Forbid() instead because it does not expose file contents, so
            // there is no equivalent oracle to protect against there.
            if (!TryNormalizeFileId(id, out var normalizedId) || !CanAccessFile(normalizedId))
            {
                HttpContext.RequestServices.GetService<ILogger<_FrameworkController>>()?.LogWarning(
                    "GetFile denied: user {UserId} was refused access to file {FileId}",
                    Wtm.LoginUserInfo?.ITCode, id);
                return new EmptyResult();
            }
            var file = fp.GetFile(id, true, Wtm.CreateDC(cskey: _DONOT_USE_CS));
            if (file == null)
            {
                return new EmptyResult();
            }
            Stream rv = file.DataStream;
            var ext = file.FileExt.ToLower();

            // Only attempt image resize for known image types; skip for non-images to avoid parse errors.
            var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "png", "gif", "bmp", "webp" };
            if (imageExtensions.Contains(ext) && (width != null || height != null))
            {
                try
                {
                    Image oimage = Image.Load(rv);
                    if (width == null)
                    {
                        width = oimage.Width * height / oimage.Height;
                    }
                    if (height == null)
                    {
                        height = oimage.Height * width / oimage.Width;
                    }
                    var ms = new MemoryStream();
                    oimage.Mutate(x => x.Resize(width.Value, height.Value));
                    oimage.SaveAsJpeg(ms);
                    oimage.Dispose();
                    rv.Dispose();
                    rv = ms;
                }
                catch
                {
                    // Image processing failed — reset stream position for raw file serving.
                    rv.Position = 0;
                }
            }

            var contenttype = "application/octet-stream";
            if (ext == "pdf")
            {
                contenttype = "application/pdf";
            }
            if (ext == "png" || ext == "bmp" || ext == "gif" || ext == "tif" || ext == "jpg" || ext == "jpeg")
            {
                contenttype = $"image/{ext}";
            }
            if (ext == "mp4")
            {
                contenttype = $"video/mpeg4";
            }
            rv.Position = 0;
            if (stream == false)
            {
                return File(rv, contenttype, file.FileName ?? (Guid.NewGuid().ToString() + ext));
            }
            else
            {
                if (ext == "mp4")
                {
                    return File(rv, contenttype, enableRangeProcessing: true);
                }
                else
                {
                    // Security (#530): never let the browser MIME-sniff an inline-streamed file.
                    // GetSafeStreamContentType forces anything outside the image whitelist
                    // (including unknown/renderable types like text/html or image/svg+xml) to
                    // application/octet-stream, and nosniff pins the browser to that value.
                    Response.ContentType = GetSafeStreamContentType(ext, contenttype);
                    Response.Headers["X-Content-Type-Options"] = "nosniff";
                    Response.Headers.TryAdd("Content-Disposition", $"inline; filename=\"{HttpUtility.UrlEncode(file.FileName)}\"");
                    await rv.CopyToAsync(Response.Body);
                    rv.Dispose();
                    return new EmptyResult();
                }
            }
        }

        [ActionDescription("ViewFile")]
        public IActionResult ViewFile([FromServices] WtmFileProvider fp, string id, string width, string _DONOT_USE_CS = null)
        {
            // MVC-010: reject unknown connection-string keys to prevent lateral DB reads
            if (!IsKnownConnectionKey(_DONOT_USE_CS))
                return new EmptyResult();
            // #814: ViewFile is a third file-id-driven endpoint on this controller (alongside
            // GetFile/GetFileName) and must share the same CanAccessFile guard — otherwise the
            // fail-closed flag only closes two of the three, leaving this one exploitable at
            // default settings and, worse, unconditionally with the flag enabled too. Same
            // EmptyResult()-not-Forbid() choice as GetFile: a distinguishable denial would turn
            // file-id probing here into an existence oracle.
            if (!TryNormalizeFileId(id, out var normalizedId) || !CanAccessFile(normalizedId))
            {
                HttpContext.RequestServices.GetService<ILogger<_FrameworkController>>()?.LogWarning(
                    "ViewFile denied: user {UserId} was refused access to file {FileId}",
                    Wtm.LoginUserInfo?.ITCode, id);
                return new EmptyResult();
            }
            var file = fp.GetFile(id, false, Wtm.CreateDC(cskey: _DONOT_USE_CS));
            if (file == null)
            {
                // #814: miss path — no such file (or IgnoreQueryFilters() found nothing).
                // Previously fell through to file.FileExt.ToLower() and NREd into an
                // unhandled 500, which is itself a hit-vs-miss existence oracle (a 500 for a
                // missing id vs. rendered HTML for a real one). EmptyResult() matches the
                // denied-path response above so a miss and a denial are indistinguishable.
                return new EmptyResult();
            }
            string html = string.Empty;
            var ext = file.FileExt.ToLower();
            // HTML-encode all user input to prevent XSS
            var safeId = HttpUtility.HtmlEncode(id);
            var safeWidth = HttpUtility.HtmlEncode(width ?? "");
            var safeCS = HttpUtility.HtmlEncode(_DONOT_USE_CS ?? "default");
            if (ext == "pdf")
            {
                html = $@"
<embed src=""/_Framework/GetFile?id={safeId}&stream=true"" width=""100%"" height=""100%"" type=""application/pdf"" ></embed>
            ";
            }
            else if (ext == "mp4")
            {
                html = $@"<video id='FileObject' controls='controls' style='{(string.IsNullOrEmpty(safeWidth) ? "" : $"width:{safeWidth}px")}'  border=0 src='/_Framework/GetFile?id={safeId}&stream=true&_DONOT_USE_CS={safeCS}'></video>";
            }
            else
            {
                html = $@"<img id='FileObject' style='flex:auto;{(string.IsNullOrEmpty(safeWidth) ? "" : $"width:{safeWidth}px")}'  border=0 src='/_Framework/GetFile?id={safeId}&stream=true&_DONOT_USE_CS={safeCS}'/>";
            }
            return Content(html);

        }

        [Public]
        public IActionResult OutSide(string url)
        {
            url = HttpUtility.UrlDecode(url);
            string pagetitle = string.Empty;
            var menu = Utils.FindMenu(url, Wtm.GlobaInfo.AllMenus);
            if (menu == null)
            {
            }
            else
            {
                if (menu.ParentId != null)
                {
                    var pmenu = GlobaInfo.AllMenus.Where(x => x.ID == menu.ParentId).FirstOrDefault();
                    if (pmenu != null)
                    {
                        pmenu.PageName = Core.CoreProgram._localizer?[pmenu.PageName];

                        pagetitle = pmenu.PageName + " - ";
                    }
                }
                menu.PageName = Core.CoreProgram._localizer?[menu.PageName];

                pagetitle += menu.PageName;
            }
            if (Wtm.IsUrlPublic(url) || Wtm.IsAccessable(url))
            {
                // Block dangerous URI schemes and protocol-relative URLs (#778, #783)
                if (url.TrimStart().StartsWith("//")
                    || (Uri.TryCreate(url, UriKind.Absolute, out var absUri)
                        && absUri.Scheme != "http" && absUri.Scheme != "https"))
                {
                    throw new Exception(MvcProgram._localizer["Sys.NoPrivilege"]);
                }

                var safeTitle = HttpUtility.HtmlEncode(pagetitle);
                var safeUrl = HttpUtility.HtmlAttributeEncode(url);
                return Content($@"<title>{safeTitle}</title>
<iframe src=""{safeUrl}"" frameborder=""0"" class=""layadmin-iframe""></iframe>");
            }
            else
            {
                throw new Exception(MvcProgram._localizer["Sys.NoPrivilege"]);
            }
        }


        [HttpGet]
        public IActionResult Menu()
        {
            var resultMenus = GlobaInfo.AllMenus.ToLayuiMenu(Wtm);
            return Content(JsonSerializer.Serialize(new { Code = 200, Msg = string.Empty, Data = resultMenus }, _menuJsonOptions), "application/json");
        }

        [AllowAnonymous]
        public IActionResult IsAccessable(string url)
        {
            url = HttpUtility.UrlDecode(url);
            if (Wtm.LoginUserInfo == null)
            {
                if (Wtm.IsUrlPublic(url))
                {
                    return Ok(true);
                }
                else
                {
                    return Unauthorized();
                }
            }
            else
            {
                bool canAccess = Wtm.IsAccessable(url);
                return Ok(canAccess);
            }
        }

        [AllowAnonymous]
        [ResponseCache(Duration = 3600)]
        public async Task<string> GetGithubStarts()
        {
            // #538: async factory avoids blocking a ThreadPool thread on the outbound GitHub call.
            return await Wtm.ReadFromCacheAsync<string>("githubstar", async () =>
            {
                var s = (await Wtm.CallAPI<Github>("github", "/repos/dotnetcore/wtm")).Data;
                return s == null ? "" : s.stargazers_count.ToString();
            }, 1800);
        }

        [AllowAnonymous]
        [ResponseCache(Duration = 3600)]
        public async Task<ActionResult> GetGithubInfo()
        {
            // #538: async factory avoids blocking a ThreadPool thread on the outbound GitHub call.
            var rv = await Wtm.ReadFromCacheAsync<string>("githubinfo", async () =>
            {
                var s = await Wtm.CallAPI<Github>("github", "/repos/dotnetcore/wtm");
                return JsonSerializer.Serialize(s);
            }, 1800);
            return Content(rv, "application/json");
        }

        [AllowAnonymous]
        [ResponseCache(Duration = 3600)]
        public string Redirect()
        {
            return "";
        }

        private class Github
        {
            public int stargazers_count { get; set; }
            public int forks_count { get; set; }
            public int subscribers_count { get; set; }
            public int open_issues_count { get; set; }
        }

        [AllowAnonymous]
        public async Task<ActionResult> GetVerifyCode()
        {
            var chkCode = _securityCode.GetRandomEnDigitalText(4);
            //写入Session用于验证码校验，可以对校验码进行加密，提高安全性
            // Issue #535: this is a hot, unauthenticated (pre-login) endpoint — use the
            // async SetAsync to avoid blocking a ThreadPool thread on CommitAsync().
            await HttpContext.Session.SetAsync<string>("verify_code", chkCode);
            var imgbyte = _securityCode.GetEnDigitalCodeByte(chkCode);
            return File(imgbyte, "image/png");

        }

        [Public]
        public Dictionary<string, string> GetScriptLanguage()
        {
            Dictionary<string, string> rv = new Dictionary<string, string>();
            rv.Add("DONOTUSE_Text_LoadFailed", MvcProgram._localizer["Sys.LoadFailed"]);
            rv.Add("DONOTUSE_Text_SubmitFailed", MvcProgram._localizer["Sys.SubmitFailed"]);
            rv.Add("DONOTUSE_Text_PleaseSelect", MvcProgram._localizer["Sys.PleaseSelect"]);
            rv.Add("DONOTUSE_Text_FailedLoadData", MvcProgram._localizer["Sys.FailedLoadData"]);
            rv.Add("DONOTUSE_Text_ExportNoData", MvcProgram._localizer?["Sys.NoData"] ?? "No data");
            return rv;
        }

        [AllRights]
        [HttpPost]
        [ActionDescription("UploadForLayUIUEditor")]
        public async Task<IActionResult> UploadForLayUIUEditor([FromServices] WtmFileProvider fp, string _DONOT_USE_CS = "default", string groupName = null, string subdir = null)
        {
            // MVC-010: reject unknown connection-string keys to prevent lateral DB reads
            if (!IsKnownConnectionKey(_DONOT_USE_CS))
                return Content("{\"Code\": 400 , \"Msg\": \"Unknown connection string key\", \"Data\": {\"src\": \"\"}}");
            IWtmFile file = null;
            if (Request.Form.Files != null && Request.Form.Files.Count() > 0)
            {
                //通过文件流方式上传附件
                var FileData = Request.Form.Files[0];

                // Issue #407: opt-in upload validation before persisting.
                var validationResult = await ValidateUploadAsync(FileData).ConfigureAwait(false);
                if (!validationResult.IsValid)
                {
                    var safeErr = HttpUtility.JavaScriptStringEncode(validationResult.Error ?? "Upload rejected");
                    return Content($"{{\"Code\": 400 , \"Msg\": \"{safeErr}\", \"Data\": {{\"src\": \"\"}}}}");
                }

                file = fp.Upload(FileData.FileName, FileData.Length, FileData.OpenReadStream(), groupName, subdir, dc: Wtm.CreateDC(cskey: _DONOT_USE_CS));
            }
            else if (Request.Form.Keys != null && Request.Form.ContainsKey("FileID"))
            {
                //通过Base64方式上传附件
                var FileData = Convert.FromBase64String(Request.Form["FileID"]);
                MemoryStream MS = new MemoryStream(FileData);
                file = fp.Upload("SCRAWL_" + Wtm.TimeProvider.GetLocalNow().DateTime.ToString("yyyyMMddHHmmssttt") + ".jpg", FileData.Length, MS, groupName, subdir, dc: Wtm.CreateDC(cskey: _DONOT_USE_CS));
                MS.Dispose();
            }



            if (file != null)
            {
                string url = $"/_Framework/GetFile?id={file.GetID()}&stream=true&_DONOT_USE_CS={CurrentCS}";
                var safeFileName = HttpUtility.JavaScriptStringEncode(file.FileName ?? "");
                return Content($"{{\"Code\": 200 , \"Msg\": \"success\", \"Data\": {{\"src\": \"{url}\",\"FileName\":\"{safeFileName}\"}}}}");

            }
            else
            {
                var errorMsg = HttpUtility.JavaScriptStringEncode(MvcProgram._localizer["Sys.UploadFailed"] ?? "Upload failed");
                return Content($"{{\"code\": 1 , \"msg\": \"{errorMsg}\", \"data\": {{\"src\": \"\"}}}}");

            }


        }

        [Public]
        [ActionDescription("加载UEditor配置文件")]
        [ResponseCache(Duration = 3600)]
        [HttpGet]
        public IActionResult UEditorOptions()
        {
            if (ConfigInfo.UEditorOptions == null)
                throw new Exception($"Unregistered service: {nameof(ConfigInfo.UEditorOptions)}");
            return JsonMore(ConfigInfo.UEditorOptions);
        }

        [Public]
        public IActionResult SetLanguage(string culture)
        {
            Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    SameSite = SameSiteMode.Lax,
                    Secure = Request.IsHttps,
                }
            );

            return FFResultJson().Reload();
        }

        [Public]
        public IActionResult SetTenant(string tenant)
        {
            var switched = Wtm.SetCurrentTenant(tenant == "" ? null : tenant);
            // #538: guard against NRE for anonymous callers — LoginUserInfo is null when unauthenticated.
            if (Wtm.LoginUserInfo == null)
            {
                return Unauthorized();
            }
            // #796: SetCurrentTenant returns false when the caller has no claim to the requested
            // tenant (it is neither their own TenantCode nor a child tenant of it). That bool was
            // previously discarded, so a rejected switch still re-signed the (unchanged)
            // principal and told the caller Reload — the page reloaded still showing the OLD
            // tenant with no error, and the refusal was never logged. Surface it as 403 and log
            // it (security events must be logged).
            if (!switched)
            {
                HttpContext.RequestServices.GetService<ILogger<_FrameworkController>>()?.LogWarning(
                    "SetTenant refused: user {UserId} in tenant {CurrentTenant} attempted to switch to unauthorized tenant {RequestedTenant}",
                    Wtm.LoginUserInfo.ITCode, Wtm.LoginUserInfo.TenantCode, tenant);
                return Forbid();
            }
            var principal = Wtm.LoginUserInfo.CreatePrincipal();
            HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, null);
            return FFResultJson().Reload();
        }


        [Public]
        public IActionResult SetLanguageForBlazor(string culture, string redirect)
        {
            Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), SameSite = SameSiteMode.Lax, Secure = Request.IsHttps }
            );

            // Use 302 redirect instead of <script> to prevent XSS (#778).
            // Url.IsLocalUrl is recognised by CodeQL as a sanitizer for cs/web/unvalidated-url-redirection.
            if (!Url.IsLocalUrl(redirect))
                return Redirect("/");
            return Redirect(redirect);
        }


        [Public]
        [HttpGet]
        public IActionResult Redirect401()
        {
            return this.Unauthorized();
        }

        [Public]
        public async Task<ActionResult> RemoteEntry(string redirect)
        {
            if (Wtm?.LoginUserInfo != null)
            {
                var principal = Wtm.LoginUserInfo.CreatePrincipal();
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, null);
            }
            // Use 302 redirect instead of <script> to prevent XSS (#778).
            // Url.IsLocalUrl is recognised by CodeQL as a sanitizer for cs/web/unvalidated-url-redirection.
            if (!Url.IsLocalUrl(redirect))
                return Redirect("/");
            return Redirect(redirect);
        }

        /// <summary>
        /// Returns a preview list (max 10) of entity labels for bulk-delete confirmation (#619).
        /// </summary>
        [AllRights]
        [HttpPost]
        public IActionResult GetDeletePreview(string _DONOT_USE_VMNAME, string[] ids)
        {
            if (ids == null || ids.Length == 0)
                return JsonMore(Array.Empty<object>());

            // #796: authorize the target VM type before returning any entity label to the
            // caller — without this, any authenticated user could supply an arbitrary VM type
            // name and up to 10 GUIDs and get back a confirmed-existence oracle plus a
            // human-readable label for every row that exists, regardless of privilege.
            //
            // #829: resolve via TryResolveVmType (Type only, no construction) rather than the
            // previous Wtm.CreateVM(name, null, null, true) probe. passInit:true only gated
            // DoInit()/InitVM()/searcher.DoInit() — the probe still ran the constructor and
            // SetSubVm (which recursively constructs and, unless passInit, DoInit()s every
            // BaseVM-typed sub-property) on the caller-named VM on EVERY request to this
            // endpoint, including when the flag is off and before the deny decision. This
            // matches the fix already applied to GetExportExcel/GetExportExcelStream/
            // GetExcelTemplate.
            //
            // The unresolvable-name and CanPreviewDelete-denies cases are kept as two separate
            // returns (not merged into one Forbid()) specifically to preserve the pre-existing
            // wire contract: base behaviour was BadRequest for an unresolvable/unregistered VM
            // name (Wtm.CreateVM's string overload throws ArgumentException, caught below) and
            // Forbid() only for an actual policy/flag denial. An earlier version of this fix
            // merged both into Forbid() and claimed the contract was unchanged in this very
            // comment — it was not: under cookie auth Forbid() wire up as a 302 redirect, not
            // the 400 an unresolvable name produced before. See
            // FrameworkControllerRbacHooksTest.GetDeletePreview_UnknownVmName_ReturnsBadRequestNotForbid.
            var previewVmType = Wtm.TryResolveVmType(_DONOT_USE_VMNAME);
            if (previewVmType == null)
            {
                return BadRequest(MvcProgram._localizer?["Sys.InvalidVM"] ?? "Invalid Vm Name");
            }
            if (!CanPreviewDelete(previewVmType))
            {
                return Forbid();
            }

            List<object> results = [];
            foreach (var idStr in ids.Take(10))
            {
                if (!Guid.TryParse(idStr, out var guid)) continue;
                var vm = Wtm.CreateVM(_DONOT_USE_VMNAME, guid, null, true) as IBaseCRUDVM<TopBasePoco>;
                if (vm == null) continue;
                results.Add(new { id = idStr, label = vm.GetDeletePreviewString() });
            }
            return JsonMore(results);
        }

        /// <summary>
        /// Assigns a role to multiple users (#619).
        /// </summary>
        [AllRights]
        [HttpPost]
        public async Task<IActionResult> BatchAssignRoles(string roleCode, string[] userCodes)
        {
            if (!CallerIsAdmin()) return Forbid();
            if (string.IsNullOrWhiteSpace(roleCode) || userCodes == null || userCodes.Length == 0)
                return BadRequest();

            var existing = DC.Set<FrameworkUserRole>()
                .Where(x => x.RoleCode == roleCode && userCodes.Contains(x.UserCode))
                .Select(x => x.UserCode)
                .ToHashSet();

            foreach (var code in userCodes)
            {
                if (!existing.Contains(code))
                {
                    DC.Set<FrameworkUserRole>().Add(new FrameworkUserRole
                    {
                        UserCode = code,
                        RoleCode = roleCode,
                        TenantCode = Wtm.LoginUserInfo?.CurrentTenant
                    });
                }
            }
            DC.SaveChanges();
            await Wtm.RemoveUserCache(userCodes);
            return Ok();
        }

        [AllRights]
        [HttpPost]
        public async Task<ActionResult> RemoveUserCacheByAccount(string[] itcode)
        {
            if (!CallerIsAdmin()) return Forbid();
            await Wtm.RemoveUserCache(itcode);
            return Ok();
        }

        [AllRights]
        [HttpPost]
        public async Task<ActionResult> RemoveUserCacheByRole(string[] rolecode)
        {
            if (!CallerIsAdmin()) return Forbid();
            await Wtm.RemoveUserCacheByRole(rolecode);
            return Ok();
        }

        [AllRights]
        [HttpPost]
        public async Task<ActionResult> RemoveUserCacheByGroup(string[] groupcode)
        {
            if (!CallerIsAdmin()) return Forbid();
            await Wtm.RemoveUserCacheByGroup(groupcode);
            return Ok();
        }

        // SECURITY (#721): this is the single canonical refresh-token endpoint for the
        // whole framework — it delegates to WTMContext.RefreshTokenAsync(string), which
        // validates the PRESENTED token (rejecting bogus/expired/already-rotated tokens)
        // and transparently forwards to the mainhost for federation frontends. Apps must
        // NOT define their own competing action on this same route ("api/_account/refreshtoken"
        // matches case-insensitively) — two attribute-routed actions on the same URL+verb
        // throw AmbiguousMatchException at request time. Demo AccountControllers were
        // updated to stop shadowing this route rather than duplicate it.
        [AllowAnonymous]
        [HttpPost("api/_account/refreshtoken")]
        public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest req)
        {
            if (string.IsNullOrEmpty(req?.RefreshToken))
                return BadRequest(new { message = "RefreshToken is required" });
            var token = await Wtm.RefreshTokenAsync(req.RefreshToken);
            if (token == null)
                return Unauthorized(new { message = "Invalid or expired refresh token" });
            return Ok(token);
        }

        [AllowAnonymous]
        [HttpPost("api/_account/revoketoken")]
        public async Task<IActionResult> RevokeToken([FromBody] RefreshTokenRequest req)
        {
            if (string.IsNullOrEmpty(req?.RefreshToken))
                return BadRequest(new { message = "RefreshToken is required" });
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            var tokenService = HttpContext.RequestServices
                .GetRequiredService<ITokenService>();
            await tokenService.RevokeTokenAsync(req.RefreshToken, ip);
            return Ok(new { message = "Token revoked" });
        }
    }

    public class RefreshTokenRequest
    {
        public string RefreshToken { get; set; }
    }
}
