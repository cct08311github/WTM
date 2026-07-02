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
        protected virtual bool CanEditProperty(object entity, string propertyName) => true;

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

            // Verify the property exists and is writable on the entity type
            var entityType = vm.Entity.GetType();
            var prop = entityType.GetProperty(field, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (prop == null || !prop.CanWrite)
            {
                return BadRequest("Field not found or not writable");
            }

            // MVC-004: honour the opt-in per-property authz hook
            if (!CanEditProperty(vm.Entity, field))
            {
                return Forbid();
            }

            vm.Entity.SetPropertyValue(field, value);

            // #532: the entity is loaded AsNoTracking (detached), and DoEdit(false) only
            // marks a property modified when the form collection carries an "entity.<field>"
            // prefixed key. This endpoint's own field/value/id/_DONOT_USE_VMNAME keys never
            // match that prefix, so without this the reflected value above was silently
            // discarded — SaveChanges persisted only UpdateTime/UpdateBy while the endpoint
            // still returned Success. Explicitly mark the field modified (it has already
            // passed the navigation-path guard, the sensitive-field blocklist, the
            // writable-property check, and the CanEditProperty authz hook above) so DoEdit's
            // SaveChanges call actually writes it.
            if (Wtm.DC != null)
            {
                var closedUpdatePropertyMethod = s_updatePropertyMethodCache.GetOrAdd(
                    entityType,
                    t => s_updatePropertyMethod.MakeGenericMethod(t));
                // Use prop.Name (the exact CLR-cased property name resolved above), not the
                // raw client-supplied 'field', because EF Core's EntityEntry.Property(string)
                // lookup is case-sensitive.
                closedUpdatePropertyMethod.Invoke(Wtm.DC, [vm.Entity, prop.Name]);
            }

            // MVC-004: route the save through DoEdit() so VM-level Validate() and
            // DuplicateCheck() apply, exactly as the normal edit endpoint does.
            vm.DoEdit(false);
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
            // MVC-013: CreateVM throws ArgumentException for unresolvable/unregistered VM names
            // (and the `as` cast returns null for valid VMs that aren't IBasePagedListVM).
            // Wrap both failure modes so they produce a clean 400 instead of an unhandled 500.
            IBasePagedListVM<TopBasePoco, ISearcher>? listVM;
            Type? instanceType;
            try
            {
                var rawVm = Wtm.CreateVM(_DONOT_USE_VMNAME);
                listVM = rawVm as IBasePagedListVM<TopBasePoco, ISearcher>;
                // MVC-011: derive filename from the created VM instance — Type.GetType fails
                // for unqualified names so always prefer the instance type when available.
                instanceType = rawVm?.GetType() ?? Type.GetType(_DONOT_USE_VMNAME);
            }
            catch (ArgumentException)
            {
                return BadRequest(MvcProgram._localizer?["Sys.InvalidVM"] ?? "Invalid Vm Name");
            }

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

            // MVC-013: wrap CreateVM to return 400 on bad VM name
            IBasePagedListVM<TopBasePoco, ISearcher>? listVM;
            Type? instanceType;
            try
            {
                var rawVm = Wtm.CreateVM(_DONOT_USE_VMNAME);
                listVM = rawVm as IBasePagedListVM<TopBasePoco, ISearcher>;
                instanceType = rawVm?.GetType() ?? Type.GetType(_DONOT_USE_VMNAME);
            }
            catch (ArgumentException)
            {
                return BadRequest(MvcProgram._localizer?["Sys.InvalidVM"] ?? "Invalid Vm Name");
            }

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
            var importVM = Wtm.CreateVM(_DONOT_USE_VMNAME) as IBaseImport<BaseTemplateVM>;
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
            return Ok(fp.GetFileName(id.ToString(), Wtm.CreateDC(cskey: _DONOT_USE_CS)));
        }

        [ActionDescription("GetFile")]
        public async Task<IActionResult> GetFile([FromServices] WtmFileProvider fp, string id, bool stream = false, string _DONOT_USE_CS = null, int? width = null, int? height = null)
        {
            // MVC-010: reject unknown connection-string keys to prevent lateral DB reads
            if (!IsKnownConnectionKey(_DONOT_USE_CS))
                return new EmptyResult();
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
            var file = fp.GetFile(id, false, Wtm.CreateDC(cskey: _DONOT_USE_CS));
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
            return Content(JsonSerializer.Serialize(new { Code = 200, Msg = string.Empty, Data = resultMenus }, new JsonSerializerOptions()
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault
            }), "application/json");
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
            Wtm.SetCurrentTenant(tenant == "" ? null : tenant);
            // #538: guard against NRE for anonymous callers — LoginUserInfo is null when unauthenticated.
            if (Wtm.LoginUserInfo == null)
            {
                return Unauthorized();
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

        [AllowAnonymous]
        [HttpPost("api/_account/refreshtoken")]
        public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest req)
        {
            if (string.IsNullOrEmpty(req?.RefreshToken))
                return BadRequest(new { message = "RefreshToken is required" });
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            var tokenService = HttpContext.RequestServices
                .GetRequiredService<ITokenService>();
            var token = await tokenService.RefreshTokenAsync(req.RefreshToken, ip);
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
