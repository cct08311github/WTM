// WTM默认页面 Wtm buidin page
using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Admin.Api
{
    [AuthorizeJwtWithCookie]
    [ApiController]
    [Route("api/_file")]
    [AllRights]
    [ActionDescription("_Admin.FileApi")]
    public class FileApiController : BaseApiController
    {
        // #830: validates a client-supplied csName BEFORE it reaches Wtm.CreateDC(cskey:) —
        // same guard _FrameworkController applies at every one of its CreateDC call sites (see
        // _FrameworkController.cs's private IsKnownConnectionKey wrapper and
        // WTMContext.IsKnownConnectionKey's own doc comment: "Use to validate a client-supplied
        // connection-string key BEFORE CreateDC(cskey:) ... to prevent cross-DB access"). This
        // demo controller is scaffolded into every downstream app; before #830 none of its eight
        // csName parameters were checked here at all.
        private bool IsKnownConnectionKey(string csName) => Wtm.IsKnownConnectionKey(csName);

        [HttpPost("[action]")]
        [ActionDescription("UploadFile")]
        public IActionResult Upload([FromServices] WtmFileProvider fp, string sm = null, string groupName = null, string subdir = null, string extra = null, string csName = null)
        {
            if (!IsKnownConnectionKey(csName))
            {
                return BadRequest("Unknown connection string key");
            }
            var FileData = Request.Form.Files[0];
            var file = fp.Upload(FileData.FileName, FileData.Length, FileData.OpenReadStream(), groupName, subdir, extra, sm, Wtm.CreateDC(cskey: csName));
            string rv = System.Text.Json.JsonSerializer.Serialize(new { Id = file.GetID(), Name = file.FileName, errno = 0, data = new { url = $"/api/_file/getfile/{file.GetID()}" } });
            return Ok(rv);
        }

        [HttpPost("[action]")]
        [ActionDescription("UploadPic")]
        public IActionResult UploadImage([FromServices] WtmFileProvider fp, int? width = null, int? height = null, string sm = null, string groupName = null, string subdir = null, string extra = null, string csName = null)
        {
            if (!IsKnownConnectionKey(csName))
            {
                return BadRequest("Unknown connection string key");
            }
            if (width == null && height == null)
            {
                return Upload(fp, sm, groupName, csName);
            }
            var FileData = Request.Form.Files[0];

            Image oimage = Image.Load(FileData.OpenReadStream());
            if (oimage == null)
            {
                return BadRequest(Localizer["Sys.UploadFailed"]);
            }
            if (width == null)
            {
                width = height * oimage.Width / oimage.Height;
            }
            if (height == null)
            {
                height = width * oimage.Height / oimage.Width;
            }
            MemoryStream ms = new MemoryStream();
            oimage.Mutate(x => x.Resize(width.Value, height.Value));
            oimage.SaveAsJpeg(ms);
            ms.Position = 0;
            var file = fp.Upload(FileData.FileName, FileData.Length, ms, groupName, subdir, extra, sm, Wtm.CreateDC(cskey: csName));
            oimage.Dispose();
            ms.Dispose();

            if (file != null)
            {
                string rv = System.Text.Json.JsonSerializer.Serialize(new { Id = file.GetID(), Name = file.FileName, errno = 0, data = new { url = $"/api/_file/getfile/{file.GetID()}" } });
                return Ok(rv);
            }
            return BadRequest(Localizer["Sys.UploadFailed"]);

        }

        // #830: [Public] removed — GetFileName/GetFile/GetFileInfo/GetUserPhoto/DownloadFile
        // were all unauthenticated (IAllowAnonymous). Combined with
        // FileUploadOptions.EnforceTenantFileScope defaulting to false (WtmFileProvider.GetFile
        // uses IgnoreQueryFilters() in that mode), any caller who could guess or enumerate a
        // FileAttachment GUID could read another tenant's file content with no login at all.
        // The class-level [AuthorizeJwtWithCookie] + [AllRights] now applies uniformly: any
        // authenticated user (regardless of per-page privilege) can reach these actions, but an
        // anonymous caller cannot.
        [HttpGet("[action]/{id}")]
        [ActionDescription("GetFileName")]
        public IActionResult GetFileName([FromServices] WtmFileProvider fp, string id, string csName = null)
        {
            if (!IsKnownConnectionKey(csName))
            {
                return BadRequest("Unknown connection string key");
            }
            return Ok(fp.GetFileName(id, Wtm.CreateDC(cskey: csName)));
        }

        [HttpGet("[action]/{id}")]
        [ActionDescription("GetFile")]
        public async Task<IActionResult> GetFile([FromServices] WtmFileProvider fp, string id, string csName = null, int? width = null, int? height = null)
        {
            if (!IsKnownConnectionKey(csName))
            {
                return BadRequest("Unknown connection string key");
            }
            var file = fp.GetFile(id, true, Wtm.CreateDC(cskey: csName));


            if (file == null)
            {
                return BadRequest(Localizer["Sys.FileNotFound"]);
            }
            try
            {
                if (width != null || height != null)
                {
                    Image oimage = Image.Load(file.DataStream);
                    if (oimage != null)
                    {
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
                        ms.Position = 0;
                        // Security (#563, port of #530): the resized output is always re-encoded
                        // as JPEG here, but this endpoint used to be [Public] and previously sent
                        // no Content-Type header at all, letting the browser MIME-sniff the
                        // response body. Pin the Content-Type explicitly and set nosniff.
                        Response.ContentType = "image/jpeg";
                        Response.Headers["X-Content-Type-Options"] = "nosniff";
                        await ms?.CopyToAsync(Response.Body);
                        file.DataStream.Dispose();
                        ms.Dispose();
                        oimage.Dispose();
                        return new EmptyResult();
                    }
                }
            }
            catch { }

            var ext = file.FileExt.ToLower();
            if (ext == "mp4")
            {
                return File(file.DataStream, "video/mpeg4", enableRangeProcessing: true);
            }
            else
            {
                // Security (#563, port of #530): this endpoint used to be [Public] and streamed
                // the raw upload bytes with no Content-Type header, letting the browser
                // MIME-sniff an uploaded file (e.g. text/html or image/svg+xml) as active
                // content in the app's origin (stored XSS). GetSafeStreamContentType forces
                // anything outside the image whitelist to application/octet-stream, and nosniff
                // pins the browser to that value — same hardening as
                // WalkingTec.Mvvm.Mvc._FrameworkController.GetFile.
                var provider = new FileExtensionContentTypeProvider();
                if (!provider.TryGetContentType(file.FileName, out var contenttype))
                {
                    contenttype = "application/octet-stream";
                }
                Response.ContentType = _FrameworkController.GetSafeStreamContentType(ext, contenttype);
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                await file.DataStream?.CopyToAsync(Response.Body);
                file.DataStream.Dispose();
                return new EmptyResult();
            }
        }

        [HttpGet("[action]/{id}")]
        [ActionDescription("GetFileInfo")]
        public IActionResult GetFileInfo([FromServices] WtmFileProvider fp, string id, string csName = null)
        {
            if (!IsKnownConnectionKey(csName))
            {
                return BadRequest("Unknown connection string key");
            }
            // #830: previously queried dc.Set<FileAttachment>() directly, bypassing
            // WtmFileProvider entirely — and with it, the same tenant-scope handling every other
            // read in this controller goes through (see WtmFileProvider.GetFile's WTM-SEC-003
            // comment) and the provider-level authorization seam #827 plans to add there. It also
            // returned the WHOLE FileAttachment entity (Path/HandlerInfo/TenantCode — internal
            // storage details a caller asking "does this file exist / what is it called" has no
            // need for). Route the read through WtmFileProvider.GetFile(..., withData: false,
            // ...) — the same call every other metadata-only read in this controller uses — and
            // project only the fields a caller of "file info" actually needs.
            var file = fp.GetFile(id, false, Wtm.CreateDC(cskey: csName));
            if (file == null)
            {
                return BadRequest(Localizer["Sys.FileNotFound"]);
            }
            return Ok(new
            {
                Id = file.GetID(),
                file.FileName,
                file.FileExt,
                file.Length,
                file.UploadTime,
                file.ExtraInfo
            });
        }

        [HttpGet("[action]/{id}")]
        [ActionDescription("GetUserPhoto")]
        public async Task<IActionResult> GetUserPhoto([FromServices] WtmFileProvider fp, string id, string csName = null, int? width = null, int? height = null)
        {
            if (ConfigInfo.HasMainHost && Wtm.LoginUserInfo?.CurrentTenant == null)
            {
                return Redirect(Wtm.ConfigInfo.MainHost+ Request.Path);
            }
            // csName is validated inside GetFile before it reaches Wtm.CreateDC(cskey:) — no
            // separate check needed here since every path through this method ends up there.
            return await this.GetFile(fp,id, csName, width, height);
        }


        [HttpGet("[action]/{id}")]
        [ActionDescription("DownloadFile")]
        public IActionResult DownloadFile([FromServices] WtmFileProvider fp, string id, string csName = null)
        {
            if (!IsKnownConnectionKey(csName))
            {
                return BadRequest("Unknown connection string key");
            }
            var file = fp.GetFile(id, true, Wtm.CreateDC(cskey:csName));
            if (file == null)
            {
                return BadRequest(Localizer["Sys.FileNotFound"]);
            }
            var ext = file.FileExt.ToLower();
            var provider = new FileExtensionContentTypeProvider();
            string contentType;
            if (!provider.TryGetContentType(file.FileName, out contentType))
            {
                contentType = "application/octet-stream";
            }
            return File(file.DataStream, contentType, file.FileName ?? (Guid.NewGuid().ToString() + ext));
        }

        [HttpPost("[action]/{id}")]
        [ActionDescription("DeleteFile")]
        public IActionResult DeletedFile([FromServices] WtmFileProvider fp, string id, string csName = null)
        {
            if (!IsKnownConnectionKey(csName))
            {
                return BadRequest("Unknown connection string key");
            }
            // #830: DeleteFileTenantScoped, not DeleteFile — see its doc comment in
            // WtmFileProvider.cs. This is the PRIMARY control that stops a caller authenticated
            // as tenant A from deleting tenant B's FileAttachment row, independent of
            // FileUploadOptions.EnforceTenantFileScope's default (false). Also switched from
            // [HttpGet] to [HttpPost]: a GET performing a delete is itself a defect (CSRF via a
            // plain <img>/<a> tag, browser prefetch, link scanners) separate from the
            // authorization gap.
            fp.DeleteFileTenantScoped(id, Wtm.CreateDC(cskey: csName));
            return Ok(true);
        }
    }
}
