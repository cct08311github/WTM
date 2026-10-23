using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
using WalkingTec.Mvvm.Mvc.Imaging;

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

        #region check of the view model a request names

        private string GenericEndpointReferenceValue()
        {
            string rv = Request.Query[GenericEndpointGuard.ReferenceParameter];
            if (string.IsNullOrEmpty(rv) && Request.HasFormContentType)
            {
                rv = Request.Form[GenericEndpointGuard.ReferenceParameter];
            }
            return rv;
        }

        /// <summary>
        /// The generic data endpoints build a view model from a name in the request. Before they do, the request must be one the page that
        /// shows the view model has prepared (see <see cref="GenericEndpointGuard"/>), and the connection key must be a configured one.
        /// Returns null when the request may be served, otherwise the answer to send.
        /// </summary>
        /// <param name="applyRequestConnection">for the endpoints that take the connection key in the request: run on it, in the spelling of the configured connection, where <see cref="GenericEndpointGuard.TryGetRequestConnection"/> allows it</param>
        /// <param name="usePinnedConnection">for the endpoints that take no connection key: run on the connection the page's reference is bound to</param>
        /// <param name="writeConnection">with <paramref name="usePinnedConnection"/>: the request writes, so it runs on the base connection and never on a replica</param>
        private IActionResult CheckGenericEndpoint(string vmName, GenericEndpointPurpose purpose, bool checkConnection = false, string connectionKey = null, string field = null, bool propagatePrivilege = false, bool usePinnedConnection = false, bool writeConnection = false, bool applyRequestConnection = false)
        {
            GenericEndpointRefusal refusal;
            if (string.IsNullOrEmpty(connectionKey) == false
                && ConfigInfo.Connections.Any(x => string.Equals(x.Key, connectionKey, StringComparison.OrdinalIgnoreCase)) == false)
            {
                refusal = GenericEndpointRefusal.UnknownConnection;
            }
            else
            {
                var decision = Wtm.AuthorizeGenericEndpoint(vmName, GenericEndpointReferenceValue(), purpose, checkConnection, connectionKey, field);
                if (decision.Allowed && usePinnedConnection && decision.Admission == GenericEndpointAdmission.Reference)
                {
                    // the key in the reference was decided by the server when the page was produced; a key that is no longer configured is refused
                    if (GenericEndpointGuard.TryResolveConnection(decision.Connection, ConfigInfo, writeConnection, out var pinned))
                    {
                        if (pinned != null)
                        {
                            Wtm.CurrentCS = pinned;
                        }
                    }
                    else
                    {
                        decision = GenericEndpointDecision.Refuse(GenericEndpointRefusal.UnknownConnection);
                    }
                }
                if (decision.Allowed && applyRequestConnection
                    && GenericEndpointGuard.TryGetRequestConnection(decision.Admission, connectionKey, ConfigInfo, out var requested))
                {
                    Wtm.CurrentCS = requested;
                }
                if (decision.Allowed)
                {
                    if (propagatePrivilege)
                    {
                        // views produced for the request (the grid inside the selector) write references with the privilege of the original page
                        Wtm.PagePrivilegeUrl = decision.PrivilegeUrl;
                    }
                    return null;
                }
                refusal = decision.Refusal;
            }
            HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<_FrameworkController>()
                .LogWarning("Generic data endpoint {Action} refused a request: {Reason} (user {User})", ControllerContext.ActionDescriptor.ActionName, refusal, Wtm.LoginUserInfo?.ITCode);
            if (Request.Headers.ContainsKey("layuisearch"))
            {
                return new ContentResult()
                {
                    Content = "{\"Data\":[],\"Count\":0,\"Page\":1,\"PageCount\":0,\"Msg\":" + JsonSerializer.Serialize((string)MvcProgram._localizer["Sys.NoPrivilege"]) + ",\"Code\":403}",
                    ContentType = "application/json",
                    StatusCode = 200
                };
            }
            return new ContentResult() { Content = MvcProgram._localizer["Sys.NoPrivilege"], ContentType = "text/html", StatusCode = 200 };
        }

        #endregion





        [HttpPost]
        [Public]
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
            string cs =_DONOT_USE_CURRENTCS;
            var refused = CheckGenericEndpoint(_DONOT_USE_VMNAME, GenericEndpointPurpose.Selector, true, cs, propagatePrivilege: true, applyRequestConnection: true);
            if (refused != null)
            {
                return refused;
            }
            cs = Wtm.CurrentCS;
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
            SelectorScript.ReportDroppedSubmitFunc(HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<_FrameworkController>(), _DONOT_USE_SUBMIT);
            ViewBag.LinkField = _DONOT_USE_LINK_FIELD;
            ViewBag.TriggerUrl = _DONOT_USE_TRIGGER_URL;
            ViewBag.CurrentCS = cs;
            #region 获取选中的数�?
            ViewBag.SelectData = "[]";
            ViewBag.SelectorValueField = _DONOT_USE_VFIELD;
            if (listVM.Ids?.Count > 0)
            {
                var tst = DC.Set<FrameworkRole>().Where(x => listVM.Ids.Contains(x.RoleName)).ToList();
                listVM.DC = Wtm.CreateDC();
                var originNeedPage = listVM.NeedPage;
                listVM.NeedPage = false;
                listVM.SearcherMode = ListVMSearchModeEnum.Batch;
                Type modelType = listVM.ModelType;
                var para = Expression.Parameter(modelType);
                var idproperty = modelType.GetSingleProperty(_DONOT_USE_VFIELD);
                var pro = Expression.Property(para, idproperty);
                listVM.ReplaceWhere = listVM.Ids.GetContainIdExpression(modelType, Expression.Parameter(modelType), pro);
                Regex r = new Regex("<script>.*?</script>");
                string selectData = r.Replace((listVM as IBasePagedListVM<TopBasePoco, BaseSearcher>).GetDataJson(), "");
                ViewBag.SelectData = selectData;
                listVM.IsSearched = false;
                listVM.SearcherMode = ListVMSearchModeEnum.Selector;
                listVM.NeedPage = originNeedPage;
            }
            #endregion

            return PartialView(listVM);
        }

        [ActionDescription("GetEmptyData")]
        public IActionResult GetEmptyData(string _DONOT_USE_VMNAME)
        {
            var refused = CheckGenericEndpoint(_DONOT_USE_VMNAME, GenericEndpointPurpose.List, usePinnedConnection: true);
            if (refused != null)
            {
                return refused;
            }
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
        public IActionResult GetPagingData(string _DONOT_USE_VMNAME, string _DONOT_USE_CS)
        {
            var refused = CheckGenericEndpoint(_DONOT_USE_VMNAME, GenericEndpointPurpose.List, true, _DONOT_USE_CS, applyRequestConnection: true);
            if (refused != null)
            {
                return refused;
            }
            var qs = new Dictionary<string, object>();
            foreach (var item in Request.Form.Keys)
            {
                qs.Add(item, Request.Form[item]);
            }
            //LogDebug.Info($"QueryString:{JsonConvert.SerializeObject(qs)}");
            //var vmType = Type.GetType(_DONOT_USE_VMNAME);
            //var vmCreater = vmType.GetConstructor(Type.EmptyTypes);
            //var listVM = vmCreater.Invoke(null) as BaseVM;
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
                    var result = Wtm.CallAPI<string>("mainhost", url, HttpMethodEnum.POST, listVM.Searcher, 10).Result;
                    var rv = new ContentResult
                    {
                        ContentType = "application/json",
                        Content = result.Data
                };
                    return rv;
                }
                else
                {
                    var rv = new ContentResult
                    {
                        ContentType = "application/json",
                        Content = $@"{{""Data"":{listVM.GetDataJson()},""Count"":{listVM.Searcher.Count},""Msg"":""success"",""Code"":{StatusCodes.Status200OK}}}"
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
        /// 单元格编�?
        /// </summary>
        /// <param name="_DONOT_USE_VMNAME"></param>
        /// <param name="id">实体主键</param>
        /// <param name="field">属性名</param>
        /// <param name="value">属性�?/param>
        /// <returns></returns>
        [HttpPost]
        public IActionResult UpdateModelProperty(string _DONOT_USE_VMNAME, Guid id, string field, string value)
        {
            var refused = CheckGenericEndpoint(_DONOT_USE_VMNAME, GenericEndpointPurpose.Update, field: field, usePinnedConnection: true, writeConnection: true);
            if (refused != null)
            {
                return refused;
            }
            if (value == null && Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(Request.Form[nameof(value)]))
            {
                value = string.Empty;
            }
            // the view model loads the record without change tracking: set the value on the loaded record and mark only that column (and the
            // update stamp) as modified, the way an edit of single fields is saved (BaseCRUDVM.DoEdit), so that the save below writes it.
            // Whatever goes wrong answers an error, never Success; the log names the kind of exception only
            try
            {
                if (Wtm.CreateVM(_DONOT_USE_VMNAME, id, null, true) is not IBaseCRUDVM<TopBasePoco> vm)
                {
                    return UpdateModelPropertyRefused(null);
                }
                var entity = vm.Entity;
                // what this endpoint never writes (key, tenant, validity flag, create/update stamps, non-scalar or non-editable members) is
                // refused whatever the reference lists and in every GenericEndpoints:Mode
                var property = ModelPropertyUpdateRules.FindWritableProperty(entity.GetType(), field);
                if (property == null)
                {
                    return UpdateModelPropertyRefused(null);
                }
                if ((DC as Microsoft.EntityFrameworkCore.DbContext)?.Model.FindEntityType(entity.GetType()) is { } mapped
                    && (mapped.FindProperty(property.Name) is not { } column || column.IsPrimaryKey() || column.IsShadowProperty()))
                {
                    return UpdateModelPropertyRefused(null);
                }
                if (ModelPropertyUpdateRules.TryConvert(property.PropertyType, value, out var converted) == false)
                {
                    return UpdateModelPropertyRefused(null);
                }
                property.SetValue(entity, converted);
                var invalid = new List<ValidationResult>();
                if (Validator.TryValidateProperty(converted, new ValidationContext(entity) { MemberName = property.Name }, invalid) == false)
                {
                    return BadRequest(invalid[0].ErrorMessage);
                }
                vm.Validate();
                if (vm.MSD.IsValid == false)
                {
                    return BadRequest(vm.MSD.GetFirstError());
                }
                DC.UpdateProperty((dynamic)entity, property.Name);
                if (entity is IBasePoco stamped)
                {
                    stamped.UpdateTime = DateTime.Now;
                    stamped.UpdateBy = Wtm.LoginUserInfo?.ITCode;
                    DC.UpdateProperty((dynamic)entity, nameof(IBasePoco.UpdateTime));
                    DC.UpdateProperty((dynamic)entity, nameof(IBasePoco.UpdateBy));
                }
                DC.SaveChanges();
            }
            catch (Exception ex)
            {
                return UpdateModelPropertyRefused(ex);
            }
            return JsonMore("Success");
        }

        private IActionResult UpdateModelPropertyRefused(Exception ex)
        {
            if (ex != null)
            {
                HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<_FrameworkController>()
                    .LogWarning("UpdateModelProperty did not save: {Reason} (user {User})", ex.GetType().Name, Wtm.LoginUserInfo?.ITCode);
            }
            return BadRequest((string)MvcProgram._localizer["Sys.EditFailed"]);
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
            var refused = CheckGenericEndpoint(_DONOT_USE_VMNAME, GenericEndpointPurpose.Export, true, _DONOT_USE_CS, applyRequestConnection: true);
            if (refused != null)
            {
                return refused;
            }
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
            var instanceType = Type.GetType(_DONOT_USE_VMNAME);

            var listVM = Wtm.CreateVM(_DONOT_USE_VMNAME) as IBasePagedListVM<TopBasePoco, ISearcher>;

            listVM.FC = qs;
            if (listVM is IBasePagedListVM<TopBasePoco, ISearcher>)
            {
                RedoUpdateModel(listVM);

                listVM.SearcherMode = listVM.Ids != null && listVM.Ids.Count > 0 ? ListVMSearchModeEnum.CheckExport : ListVMSearchModeEnum.Export;

                var data = listVM.GenerateExcel();
                HttpContext.Response.Cookies.Append("DONOTUSEDOWNLOADING", "0", new Microsoft.AspNetCore.Http.CookieOptions() { Path = "/", Expires = DateTime.Now.AddDays(2), Secure = HttpContext.Request.IsHttps });

                return File(data, "application/vnd.ms-excel", $"Export_{instanceType.Name}_{DateTime.Now.ToString("yyyy-MM-dd")}.xls");
            }
            else
            {
                throw new Exception("Invalid Vm Name");
            }
        }

        /// <summary>
        /// Download Excel Template
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [ActionDescription("DownloadTemplate")]
        public IActionResult GetExcelTemplate(string _DONOT_USE_VMNAME, string _DONOT_USE_CS)
        {
            var refused = CheckGenericEndpoint(_DONOT_USE_VMNAME, GenericEndpointPurpose.Template, usePinnedConnection: true);
            if (refused != null)
            {
                return refused;
            }
            //Wtm.CurrentCS = _DONOT_USE_CS ?? "default";
            var importVM = Wtm.CreateVM(_DONOT_USE_VMNAME) as IBaseImport<BaseTemplateVM>;
            var qs = new Dictionary<string, string>();
            foreach (var item in Request.Query.Keys)
            {
                qs.Add(item, Request.Query[item]);
            }
            importVM.SetParms(qs);
            var data = importVM.GenerateTemplate(out string fileName);
            HttpContext.Response.Cookies.Append("DONOTUSEDOWNLOADING", "0", new Microsoft.AspNetCore.Http.CookieOptions() { Domain = "/", Expires = DateTime.Now.AddDays(2), Secure = HttpContext.Request.IsHttps });
            return File(data, "application/vnd.ms-excel", fileName);
        }

        #endregion

        [AllowAnonymous]
        [ActionDescription("Sys.ErrorHandle")]
        public IActionResult Error()
        {
            var ex = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
            ActionLog log = new ActionLog();
            log.LogType = ActionLogTypesEnum.Exception;
            log.ActionTime = DateTime.Now;
            log.ITCode = Wtm.LoginUserInfo?.ITCode ?? string.Empty;

            var controllerDes = ex.Error.TargetSite.DeclaringType.GetCustomAttributes(typeof(ActionDescriptionAttribute), false).Cast<ActionDescriptionAttribute>().FirstOrDefault();
            var actionDes = ex.Error.TargetSite.GetCustomAttributes(typeof(ActionDescriptionAttribute), false).Cast<ActionDescriptionAttribute>().FirstOrDefault();
            var postDes = ex.Error.TargetSite.GetCustomAttributes(typeof(HttpPostAttribute), false).Cast<HttpPostAttribute>().FirstOrDefault();
            //给日志的多语言属性赋�?
            log.ModuleName = controllerDes?.GetDescription(ex.Error.TargetSite.DeclaringType) ?? ex.Error.TargetSite.DeclaringType.Name.Replace("Controller", string.Empty);
            log.ActionName = actionDes?.GetDescription(ex.Error.TargetSite.DeclaringType) ?? ex.Error.TargetSite.Name;
            if (postDes != null)
            {
                log.ActionName += "[P]";
            }
            log.ActionUrl = ex.Path;
            log.IP = HttpContext.Connection.RemoteIpAddress.ToString();
            log.Remark = ex.Error.ToString();
            if (string.IsNullOrEmpty(log.Remark) == false && log.Remark.Length > 2000)
            {
                log.Remark = log.Remark.Substring(0, 2000);
            }
            DateTime? starttime = HttpContext.Items["actionstarttime"] as DateTime?;
            if (starttime != null)
            {
                log.Duration = DateTime.Now.Subtract(starttime.Value).TotalSeconds;
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
            if (ConfigInfo.IsQuickDebug == true)
            {
                rv = ex.Error.ToString().Replace(Environment.NewLine, "<br />");
            }
            else
            {
                rv = ex.Error.Message.Replace(Environment.NewLine, "<br />"); ;
            }
            return BadRequest(rv);
        }

        [HttpPost]
        [ActionDescription("UploadFileRoute")]
        public IActionResult Upload([FromServices] WtmFileProvider fp, string sm = null, string groupName = null, string subdir = null, string extra = null, bool IsTemprory = true, string _DONOT_USE_CS=null, string _DONOT_USE_FREF = null)
        {
            var connection = Wtm.ResolveFileConnection(_DONOT_USE_CS, null, FilePurpose.Upload, _DONOT_USE_FREF);
            if (connection.Allowed == false)
            {
                return RefuseFileRequest("Upload", "the connection is not allowed");
            }
            var FileData = Request.Form.Files[0];
            // the marker of files embedded by a rich text editor is the server's to set
            extra = FileEndpointGuard.StripEmbeddedMarker(extra);
            var file = fp.Upload(FileData.FileName, FileData.Length, FileData.OpenReadStream(), groupName, subdir, extra, sm, Wtm.CreateDC(cskey: connection.Key));
            return JsonMore(UploadResult(file, connection.Key));
        }

        /// <summary>The answer to an upload: the id and name, and (additionally) the url and the reference for the file that was just stored.</summary>
        private object UploadResult(IWtmFile file, string connectionKey)
        {
            var id = file.GetID();
            return new
            {
                Id = id,
                Name = file.FileName,
                Url = Wtm.GetFileUrl(id, FilePurpose.Download, connectionKey),
                Ref = Wtm.CreateFileReference(id, FilePurpose.Read, connectionKey)
            };
        }

        /// <summary>The framework's refusal for the file endpoints: the same answer as the generic data endpoints give, 200 with the no-privilege message.</summary>
        private IActionResult RefuseFileRequest(string endpoint, string reason)
        {
            HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger<_FrameworkController>()
                .LogWarning("File endpoint {Endpoint} refused a request: {Reason} (user {User})", endpoint, reason, Wtm.LoginUserInfo?.ITCode);
            return new ContentResult() { Content = MvcProgram._localizer["Sys.NoPrivilege"], ContentType = "text/html", StatusCode = 200 };
        }

        /// <summary>
        /// Chooses the connection of a file request, reads nothing yet. Returns null and the data context to use, or the refusal to send.
        /// </summary>
        private IActionResult CreateFileDc(string endpoint, string fileId, FilePurpose purpose, string requestKey, string reference, out IDataContext dc)
        {
            dc = null;
            var connection = Wtm.ResolveFileConnection(requestKey, fileId, purpose, reference);
            if (connection.Allowed == false)
            {
                return RefuseFileRequest(endpoint, "the connection is not allowed");
            }
            dc = Wtm.CreateDC(cskey: connection.Key);
            return null;
        }

        /// <summary>Decides whether the file may be served (reference, mode, access policy). Returns null when it may, otherwise the refusal to send.</summary>
        private IActionResult AuthorizeFile(string endpoint, string fileId, FilePurpose purpose, string reference, IDataContext dc)
        {
            var decision = Wtm.AuthorizeFileRequest(endpoint, fileId, purpose, reference, () =>
                dc.Set<FileAttachment>().CheckID(fileId).Select(x => new FileEndpointFileInfo { ExtraInfo = x.ExtraInfo, UploadTime = x.UploadTime }).FirstOrDefault());
            return decision.Allowed ? null : new ContentResult() { Content = MvcProgram._localizer["Sys.NoPrivilege"], ContentType = "text/html", StatusCode = 200 };
        }

        [HttpPost]
        [ActionDescription("UploadFileRoute")]
        public IActionResult UploadImage([FromServices] WtmFileProvider fp, string sm = null, string groupName = null, string subdir = null, string extra = null, bool IsTemprory = true, string _DONOT_USE_CS = null, int? width = null, int? height = null, string _DONOT_USE_FREF = null)
        {
            if (width == null && height == null)
            {
                return Upload(fp, sm, groupName, subdir, extra, IsTemprory, _DONOT_USE_CS, _DONOT_USE_FREF);
            }
            var connection = Wtm.ResolveFileConnection(_DONOT_USE_CS, null, FilePurpose.Upload, _DONOT_USE_FREF);
            if (connection.Allowed == false)
            {
                return RefuseFileRequest("UploadImage", "the connection is not allowed");
            }
            extra = FileEndpointGuard.StripEmbeddedMarker(extra);
            var FileData = Request.Form.Files[0];

            // the picture is identified first and decoded only within the limits of ImageUploadLimits (see ImageDecodeGuard)
            SpaImageResult processed;
            using (var upstream = FileData.OpenReadStream())
            {
                processed = ImageDecodeGuard.Resize(upstream, width, height, ImageLimits());
            }
            if (processed.Resized == null)
            {
                processed.Buffered?.Dispose();
                LogImageRejected("UploadImage", processed.Error);
                return JsonMore(new { Id = string.Empty, Name = string.Empty }, StatusCodes.Status404NotFound);
            }
            using (processed.Resized)
            using (processed.Buffered)
            {
                var file = fp.Upload(FileData.FileName, processed.Resized.Length, processed.Resized, groupName, subdir, extra, sm, Wtm.CreateDC(cskey: connection.Key));
                return JsonMore(UploadResult(file, connection.Key));
            }
        }

        /// <summary>The limits for decoding pictures (configuration section <c>ImageUploadLimits</c>); the defaults when it is not there.</summary>
        private ImageDecodeLimits ImageLimits()
        {
            return ImageDecodeLimits.FromConfiguration(HttpContext.RequestServices?.GetService<IConfiguration>());
        }

        /// <summary>A picture that was not processed: logged with a fixed reason, never with file content.</summary>
        private void LogImageRejected(string endpoint, string reason, LogLevel level = LogLevel.Warning)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return;
            }
            HttpContext.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger<_FrameworkController>()
                .Log(level, "File endpoint {Endpoint} did not process a picture: {Reason} (user {User})", endpoint, reason, Wtm.LoginUserInfo?.ITCode);
        }

        [HttpPost]
        [ActionDescription("UploadForLayUIRichTextBox")]
        public IActionResult UploadForLayUIRichTextBox([FromServices] WtmFileProvider fp, string _DONOT_USE_CS = null, string groupName = null, string subdir = null, string _DONOT_USE_FREF = null)
        {
            var connection = Wtm.ResolveFileConnection(_DONOT_USE_CS, null, FilePurpose.Upload, _DONOT_USE_FREF);
            if (connection.Allowed == false)
            {
                return RefuseFileRequest("UploadForLayUIRichTextBox", "the connection is not allowed");
            }
            var FileData = Request.Form.Files[0];
            // the file is embedded in the content of an editor: the server marks it, so that it stays reachable by its id
            var file = fp.Upload(FileData.FileName, FileData.Length, FileData.OpenReadStream(), groupName, subdir, FileEndpointGuard.MarkEmbedded(null), dc: Wtm.CreateDC(cskey: connection.Key));
            if (file != null)
            {
                string url = EmbeddedFileUrl(file, connection.Key);
                return Content($"{{\"code\": 0 , \"msg\": \"\", \"data\": {{\"src\": \"{url}\"}}}}");

            }
            else
            {
                return Content($"{{\"code\": 1 , \"msg\": \"{MvcProgram._localizer["Sys.UploadFailed"]}\", \"data\": {{\"src\": \"\"}}}}");

            }

        }

        /// <summary>The address of a file that an editor embeds in its content: by id (the content outlives any reference), with the connection the file was stored on.</summary>
        private static string EmbeddedFileUrl(IWtmFile file, string connectionKey)
        {
            var url = $"/_Framework/GetFile?id={file.GetID()}&stream=true";
            return string.IsNullOrEmpty(connectionKey) ? url : url + "&_DONOT_USE_CS=" + Uri.EscapeDataString(connectionKey);
        }

        [ActionDescription("GetFileName")]
        public IActionResult GetFileName([FromServices] WtmFileProvider fp, Guid id, string _DONOT_USE_CS, string _DONOT_USE_FREF = null)
        {
            var refused = CreateFileDc("GetFileName", id.ToString(), FilePurpose.Name, _DONOT_USE_CS, _DONOT_USE_FREF, out var dc);
            refused ??= AuthorizeFile("GetFileName", id.ToString(), FilePurpose.Name, _DONOT_USE_FREF, dc);
            if (refused != null)
            {
                return refused;
            }
            return Ok(fp.GetFileName(id.ToString(), dc));
        }

        [ActionDescription("GetFile")]
        public async Task<IActionResult> GetFile([FromServices] WtmFileProvider fp, string id, bool stream = false, string _DONOT_USE_CS = null, int? width = null, int? height = null, string _DONOT_USE_FREF = null)
        {
            var purpose = stream ? FilePurpose.View : FilePurpose.Download;
            var refused = CreateFileDc("GetFile", id, purpose, _DONOT_USE_CS, _DONOT_USE_FREF, out var dc);
            refused ??= AuthorizeFile("GetFile", id, purpose, _DONOT_USE_FREF, dc);
            if (refused != null)
            {
                return refused;
            }
            var file = fp.GetFile(id, true, dc);
            if (file == null)
            {
                return new EmptyResult();
            }
            Stream rv = null;
            try
            {
                rv = file.DataStream;
                // the file is looked at only when a size is asked for: identified first, decoded only within the limits of
                // ImageUploadLimits (see ImageDecodeGuard); outside them (or when it is no picture) the stored file is served as it is
                if (rv != null && (width != null || height != null))
                {
                    var processed = ImageDecodeGuard.Resize(rv, width, height, ImageLimits());
                    if (processed.Resized != null)
                    {
                        rv.Dispose();
                        processed.Buffered?.Dispose();
                        rv = processed.Resized;
                    }
                    else
                    {
                        if (processed.Buffered != null)
                        {
                            // the stream of the provider has been read to the end: serve the buffered copy
                            rv.Dispose();
                            rv = processed.Buffered;
                        }
                        else if (rv.CanSeek == false)
                        {
                            // too large to buffer, and a stream that cannot seek has been read in part: open the file again
                            rv.Dispose();
                            rv = fp.GetFile(id, true, dc)?.DataStream;
                            if (rv == null)
                            {
                                return new EmptyResult();
                            }
                        }
                        // not a failure of the request: the stored file is served
                        LogImageRejected("GetFile", processed.Error, LogLevel.Information);
                    }
                }
            }
            catch { }
            var ext = file.FileExt.ToLower();
            var contenttype = FileServing.GetContentType(ext);
            if (rv.CanSeek)
            {
                rv.Position = 0;
            }
            // the browser must use the type the server states, never its own guess
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (stream == false)
            {
                return File(rv, contenttype, file.FileName ?? (Guid.NewGuid().ToString() + ext));
            }
            else
            {
                // a file a browser would run as part of the site (HTML, SVG, XML, script) is sent as an attachment, never shown inline
                var head = new byte[512];
                var read = rv.Read(head, 0, head.Length);
                if (rv.CanSeek)
                {
                    rv.Position = 0;
                }
                else
                {
                    // a stream that cannot seek goes on after the bytes that were looked at
                    rv = new SpaFileApi.PrefixedStream(head, read, rv);
                }
                var active = FileServing.IsActiveContent(ext, head.AsSpan(0, read));
                if (ext == "mp4")
                {
                    return File(rv, contenttype, enableRangeProcessing: true);
                }
                else
                {
                    Response.ContentType = contenttype;
                    Response.Headers.TryAdd("Content-Disposition", $"{(active ? "attachment" : "inline")}; filename=\"{HttpUtility.UrlEncode(file.FileName)}\"");
                    await rv.CopyToAsync(Response.Body);
                    rv.Dispose();
                    return new EmptyResult();
                }
            }
        }

        [ActionDescription("ViewFile")]
        public IActionResult ViewFile([FromServices] WtmFileProvider fp, string id, string width, string _DONOT_USE_CS = null, string _DONOT_USE_FREF = null)
        {
            var connection = Wtm.ResolveFileConnection(_DONOT_USE_CS, id, FilePurpose.View, _DONOT_USE_FREF);
            if (connection.Allowed == false)
            {
                return RefuseFileRequest("ViewFile", "the connection is not allowed");
            }
            var dc = Wtm.CreateDC(cskey: connection.Key);
            var refused = AuthorizeFile("ViewFile", id, FilePurpose.View, _DONOT_USE_FREF, dc);
            if (refused != null)
            {
                return refused;
            }
            var file = fp.GetFile(id, false, dc);
            if (file == null)
            {
                return new EmptyResult();
            }
            // the file is shown through GetFile: the link the page gets carries a reference of its own
            var src = HtmlAttributeEncoder.Encode(Wtm.GetFileUrl(file.GetID(), FilePurpose.View, connection.Key));
            // only a number is written; anything else is left out, as if no width had been given
            var widthStyle = int.TryParse(width, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var widthPixels) && widthPixels > 0 ? $"width:{widthPixels}px" : "";
            string html = string.Empty;
            var ext = file.FileExt.ToLower();
            if (ext == "pdf")
            {
                html = $@"
<embed src=""{src}"" width=""100%"" height=""100%"" type=""application/pdf"" ></embed>
            ";
            }
            else if (ext == "mp4")
            {
                html = $@"<video id='FileObject' controls='controls' style='{widthStyle}'  border=0 src='{src}'></video>";
            }
            else
            {
                html = $@"<img id='FileObject' style='flex:auto;{widthStyle}'  border=0 src='{src}'/>";
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
                return Content($@"<title>{pagetitle}</title>
<iframe src='{url}' frameborder='0' class='layadmin-iframe'></iframe>");
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
        public string GetGithubStarts()
        {
            return Wtm.ReadFromCache<string>("githubstar", () =>
            {
                var s = Wtm.CallAPI<Github>("github", "/repos/dotnetcore/wtm").Result.Data;
                return s == null ? "" : s.stargazers_count.ToString();
            }, 1800);
        }

        [AllowAnonymous]
        [ResponseCache(Duration = 3600)]
        public ActionResult GetGithubInfo()
        {
            var rv = Wtm.ReadFromCache<string>("githubinfo", () =>
            {
                var s = Wtm.CallAPI<Github>("github", "/repos/dotnetcore/wtm").Result;
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
        public ActionResult GetVerifyCode()
        {
            var chkCode = _securityCode.GetRandomEnDigitalText(4);
            //写入Session用于验证码校验，可以对校验码进行加密，提高安全�?
            HttpContext.Session.Set<string>("verify_code", chkCode);
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
            return rv;
        }

        /// <summary>
        /// The answer of a successful UEditor upload. Built by the serializer, so a file name with quotes or backslashes cannot change the structure
        /// of the answer; ordinary values give the same members as before.
        /// </summary>
        public static string BuildUEditorSuccess(string url, string fileName)
        {
            return JsonSerializer.Serialize(new { Code = 200, Msg = "success", Data = new { src = url, FileName = fileName } },
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }

        [AllRights]
        [HttpPost]
        [ActionDescription("UploadForLayUIUEditor")]
        public IActionResult UploadForLayUIUEditor([FromServices] WtmFileProvider fp, string _DONOT_USE_CS = "default", string groupName = null, string subdir = null, string _DONOT_USE_FREF = null)
        {
            var connection = Wtm.ResolveFileConnection(_DONOT_USE_CS, null, FilePurpose.Upload, _DONOT_USE_FREF);
            if (connection.Allowed == false)
            {
                return RefuseFileRequest("UploadForLayUIUEditor", "the connection is not allowed");
            }
            IWtmFile file = null;
            if (Request.Form.Files != null && Request.Form.Files.Count() > 0)
            {
                //通过文件流方式上传附�?
                var FileData = Request.Form.Files[0];
                file = fp.Upload(FileData.FileName, FileData.Length, FileData.OpenReadStream(), groupName, subdir, FileEndpointGuard.MarkEmbedded(null), dc: Wtm.CreateDC(cskey: connection.Key));
            }
            else if (Request.Form.Keys != null && Request.Form.ContainsKey("FileID"))
            {
                //通过Base64方式上传附件
                var FileData = Convert.FromBase64String(Request.Form["FileID"]);
                MemoryStream MS = new MemoryStream(FileData);
                file = fp.Upload("SCRAWL_" + DateTime.Now.ToString("yyyyMMddHHmmssttt") + ".jpg", FileData.Length, MS, groupName, subdir, FileEndpointGuard.MarkEmbedded(null), dc: Wtm.CreateDC(cskey: connection.Key));
                MS.Dispose();
            }



            if (file != null)
            {
                string url = EmbeddedFileUrl(file, connection.Key);
                return Content(BuildUEditorSuccess(url, file.FileName));

            }
            else
            {
                return Content($"{{\"code\": 1 , \"msg\": \"{MvcProgram._localizer["Sys.UploadFailed"]}\", \"data\": {{\"src\": \"\"}}}}");

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
                new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), Secure = Request.IsHttps }
            );

            return FFResult().AddCustomScript("location.reload();");
        }

        /// <summary>
        /// Switches the tenant the signed-in user works in. Not public: an anonymous caller is sent to the sign-in page by the framework's
        /// privilege filter (like any other signed-in only action), and the action itself still refuses a caller without a user.
        /// The target is checked by <see cref="WTMContext.SetCurrentTenant"/>; a refused target changes nothing (the user, the cookie)
        /// and is answered with a message instead of the reload script. Stays a GET: pages of existing applications call it that way.
        /// </summary>
        public async Task<IActionResult> SetTenant(string tenant)
        {
            if (Wtm.LoginUserInfo == null)
            {
                return new ContentResult() { Content = MvcProgram._localizer["Sys.NeedLogin"], ContentType = "text/html", StatusCode = StatusCodes.Status401Unauthorized };
            }
            if (Wtm.SetCurrentTenant(tenant == "" ? null : tenant) == false)
            {
                return FFResult().Alert(MvcProgram._localizer["Sys.NoPrivilege"]);
            }
            var principal = Wtm.LoginUserInfo.CreatePrincipal();
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, null);
            return FFResult().AddCustomScript("location.reload();");
        }


        [Public]
        public IActionResult SetLanguageForBlazor(string culture, string redirect)
        {
            Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
                new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), Secure = Request.IsHttps }
            );

            return Content(BuildBlazorRedirectScript(redirect), "text/html");
        }

        /// <summary>
        /// Builds the script that navigates to the (URL-decoded) redirect target.
        /// The decoded value is JS-string-encoded before being placed in the script literal;
        /// the navigation target itself is unchanged.
        /// </summary>
        public static string BuildBlazorRedirectScript(string redirect)
        {
            return $"<script>window.location.href='{JsStringEncoder.Escape(HttpUtility.UrlDecode(redirect))}';</script>";
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
            if (string.IsNullOrEmpty(redirect))
            {
                redirect = "/";
            }
            if (Wtm?.LoginUserInfo != null)
            {
                var principal = Wtm.LoginUserInfo.CreatePrincipal();
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, null);
            }
            return Content(BuildRemoteEntryScript(redirect), "text/html");
        }

        /// <summary>
        /// Builds the script that navigates to the (URL-decoded) redirect target.
        /// The decoded value is JS-string-encoded before being placed in the script literal;
        /// the navigation target itself is unchanged.
        /// </summary>
        public static string BuildRemoteEntryScript(string redirect)
        {
            return $"<script>window.location.href='{JsStringEncoder.Escape(HttpUtility.UrlDecode(redirect))}'</script>";
        }

        /// <summary>
        /// The role code of the administrator role. Every site is created with it, and the framework's own administrator-only features
        /// (the workflow designer page, the data privilege scope) recognise administrators by it.
        /// </summary>
        private const string AdministratorRoleCode = "001";

        /// <summary>True when the signed-in user holds the administrator role (false when nobody is signed in).</summary>
        public static bool IsAdministrator(LoginUserInfo user)
        {
            return user?.Roles != null && user.Roles.Any(x => x != null && x.RoleCode == AdministratorRoleCode);
        }

        /// <summary>
        /// The cache of other accounts is cleared only on behalf of an administrator. Returns null when the caller may go on, otherwise the
        /// answer to send (the same form as the privilege filter uses for an action the caller has no privilege for).
        /// </summary>
        private IActionResult RefuseUnlessAdministrator()
        {
            if (IsAdministrator(Wtm.LoginUserInfo))
            {
                return null;
            }
            if (Request.Headers.ContainsKey("Authorization"))
            {
                return Forbid(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);
            }
            return new ContentResult() { Content = MvcProgram._localizer["Sys.NoPrivilege"], ContentType = "text/html", StatusCode = 200 };
        }

        // these three are not pages of a menu: the privilege filter lets every signed-in user through the controller, so each checks the role itself
        [AllRights]
        [HttpPost]
        public async Task<ActionResult> RemoveUserCacheByAccount(string[] itcode)
        {
            var refused = RefuseUnlessAdministrator();
            if (refused != null)
            {
                return (ActionResult)refused;
            }
            await Wtm.RemoveUserCache(itcode);
            return Ok();
        }

        [AllRights]
        [HttpPost]
        public async Task<ActionResult> RemoveUserCacheByRole(string[] rolecode)
        {
            var refused = RefuseUnlessAdministrator();
            if (refused != null)
            {
                return (ActionResult)refused;
            }
            await Wtm.RemoveUserCacheByRole(rolecode);
            return Ok();
        }

        [AllRights]
        [HttpPost]
        public async Task<ActionResult> RemoveUserCacheByGroup(string[] groupcode)
        {
            var refused = RefuseUnlessAdministrator();
            if (refused != null)
            {
                return (ActionResult)refused;
            }
            await Wtm.RemoveUserCacheByGroup(groupcode);
            return Ok();
        }
    }
}
