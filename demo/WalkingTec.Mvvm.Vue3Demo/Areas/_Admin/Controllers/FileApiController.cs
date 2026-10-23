// WTM默认页面 Wtm buidin page
using System;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Support.FileHandlers;
using WalkingTec.Mvvm.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WalkingTec.Mvvm.Admin.Api
{
    /// <summary>
    /// The file API of the single page application. Every action is one call of <see cref="SpaFileApi"/>, which owns the rules (references,
    /// <c>FileEndpoints:Mode</c>, connections, response headers); only the decoding limits for pictures are the application's
    /// (<see cref="ImageDecodeGuard"/>). Keep the <c>_DONOT_USE_FREF</c> parameters and the <see cref="FileEndpointAttribute"/> markers when you copy it.
    /// </summary>
    [AuthorizeJwtWithCookie]
    [ApiController]
    [Route("api/_file")]
    [AllRights]
    [ActionDescription("_Admin.FileApi")]
    public class FileApiController : BaseApiController
    {
        [HttpPost("[action]")]
        [ActionDescription("UploadFile")]
        [FileEndpoint]
        public IActionResult Upload([FromServices] WtmFileProvider fp, string sm = null, string groupName = null, string subdir = null, string extra = null, string csName = null, string _DONOT_USE_FREF = null)
            => SpaFileApi.Upload(this, fp, sm, groupName, subdir, extra, csName, _DONOT_USE_FREF);

        [HttpPost("[action]")]
        [ActionDescription("UploadPic")]
        [FileEndpoint]
        public IActionResult UploadImage([FromServices] WtmFileProvider fp, int? width = null, int? height = null, string sm = null, string groupName = null, string subdir = null, string extra = null, string csName = null, string _DONOT_USE_FREF = null)
            => SpaFileApi.UploadImage(this, fp, width, height, sm, groupName, subdir, extra, csName, _DONOT_USE_FREF, ResizeImage);

        /// <summary>The upload of the rich text editor: the file is embedded in the content, so it stays reachable by its id.</summary>
        [HttpPost("[action]")]
        [ActionDescription("UploadPic")]
        [FileEndpoint]
        public IActionResult UploadForEditor([FromServices] WtmFileProvider fp, string sm = null, string groupName = null, string subdir = null, string extra = null, string csName = null, string _DONOT_USE_FREF = null)
            => SpaFileApi.UploadForEditor(this, fp, sm, groupName, subdir, extra, csName, _DONOT_USE_FREF);

        [HttpGet("[action]/{id}")]
        [ActionDescription("GetFileName")]
        [Public]
        [FileEndpoint(PublicWhenFilesPublic = true)]
        public IActionResult GetFileName([FromServices] WtmFileProvider fp, string id, string csName = null, string _DONOT_USE_FREF = null)
            => SpaFileApi.GetFileName(this, fp, id, csName, _DONOT_USE_FREF);

        [HttpGet("[action]/{id}")]
        [ActionDescription("GetFile")]
        [Public]
        [FileEndpoint(PublicWhenFilesPublic = true)]
        public IActionResult GetFile([FromServices] WtmFileProvider fp, string id, string csName = null, int? width = null, int? height = null, string _DONOT_USE_FREF = null)
            => SpaFileApi.GetFile(this, fp, id, csName, width, height, _DONOT_USE_FREF, ResizeImage);

        /// <summary>The name, extension, length and upload time of a file; never its data, path or handler information.</summary>
        [HttpGet("[action]/{id}")]
        [ActionDescription("GetFileName")]
        [Public]
        [FileEndpoint(PublicWhenFilesPublic = true)]
        public IActionResult GetFileInfo(string id, string csName = null, string _DONOT_USE_FREF = null)
            => SpaFileApi.GetFileInfo(this, id, csName, _DONOT_USE_FREF);

        [HttpGet("[action]/{id}")]
        [ActionDescription("GetUserPhoto")]
        [Public]
        [FileEndpoint(PublicWhenFilesPublic = true)]
        public IActionResult GetUserPhoto([FromServices] WtmFileProvider fp, string id, string csName = null, int? width = null, int? height = null, string _DONOT_USE_FREF = null)
            => SpaFileApi.GetUserPhoto(this, fp, id, csName, width, height, _DONOT_USE_FREF, ResizeImage);

        [HttpGet("[action]/{id}")]
        [ActionDescription("DownloadFile")]
        [Public]
        [FileEndpoint(PublicWhenFilesPublic = true)]
        public IActionResult DownloadFile([FromServices] WtmFileProvider fp, string id, string csName = null, string _DONOT_USE_FREF = null)
            => SpaFileApi.DownloadFile(this, fp, id, csName, _DONOT_USE_FREF);

        /// <summary>
        /// Deletes a file the caller uploaded: needs the delete reference (<c>DeleteRef</c>) of the upload answer. GET is kept for older clients.
        /// </summary>
        [HttpGet("[action]/{id}")]
        [HttpPost("[action]/{id}")]
        [HttpDelete("[action]/{id}")]
        [ActionDescription("DeleteFile")]
        [FileEndpoint]
        public IActionResult DeletedFile([FromServices] WtmFileProvider fp, string id, string csName = null, string _DONOT_USE_FREF = null)
            => SpaFileApi.DeleteFile(this, fp, id, csName, _DONOT_USE_FREF);

        // Mitigation for ImageSharp advisories (#78): format allowlist and size/dimension limits before anything is decoded (the rules live in the framework)
        private SpaImageResult ResizeImage(Stream source, int? width, int? height)
        {
            var limits = ImageDecodeLimits.FromConfiguration(HttpContext.RequestServices.GetService<IConfiguration>());
            return ImageDecodeGuard.Resize(source, width, height, limits);
        }
    }
}
