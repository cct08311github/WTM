#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Models;

namespace WalkingTec.Mvvm.Core.Support.FileHandlers
{
    [Display(Name = "database")]
    public class WtmDataBaseFileHandler : WtmFileHandlerBase
    {
        private static string _modeName = "database";

        public WtmDataBaseFileHandler(WTMContext wtm) : base(wtm)
        {
        }

        public override Stream? GetFileData(IWtmFile file)
        {
            // #859: this is called ONLY from WtmFileProvider.GetFile, and only AFTER that
            // method's own query has already resolved (and, depending on
            // FileUploadOptions.EnforceTenantFileScope, already tenant-scoped) the
            // FileAttachment row `file` came from — GetFileData is not a public entry point a
            // caller-controlled id can reach directly (no other call site exists in this repo).
            // Re-applying the global ITenant filter here, unconditionally, independent of that
            // same EnforceTenantFileScope flag, used to disagree with the already-completed
            // outer resolution whenever EnforceTenantFileScope=false (the pre-#859 default, still
            // a supported explicit opt-out) legitimately let GetFile resolve a cross-tenant row:
            // this query would then find nothing, return null, and the controller would crash
            // with a NullReferenceException on the resulting null DataStream instead of serving
            // the file the outer query had already authorized — a "database" SaveMode-only
            // inconsistency with WtmLocalFileHandler (and, before #1055 removed it, an
            // object-storage handler), which apply no such second filter and correctly honour
            // the opt-out. IgnoreQueryFilters() here trusts the caller's already-completed
            // authorization decision, matching that other handler, so the opt-out works for every SaveMode.
            var rv = wtm.DC.Set<FileAttachment>().IgnoreQueryFilters().CheckID(file.GetID()).FirstOrDefault();
            if (rv != null)
            {
                return new MemoryStream(((FileAttachment)rv).FileData!);
            }
            return null;
        }


        public  IWtmFile UploadToDB(string fileName, long fileLength, Stream data, string? groupName = null, string? subdir = null, string? extra = null)
        {
            FileAttachment file = new FileAttachment();
            file.FileName = fileName;
            file.Length = fileLength;
            file.UploadTime = wtm.TimeProvider.GetLocalNow().DateTime;
            file.SaveMode = _modeName;
            file.ExtraInfo = extra;
            file.TenantCode = wtm.LoginUserInfo?.CurrentTenant;
            var ext = string.Empty;
            if (string.IsNullOrEmpty(fileName) == false)
            {
                var dotPos = fileName.LastIndexOf('.');
                ext = fileName.Substring(dotPos + 1);
            }
            file.FileExt = ext;
            using (var dataStream = new MemoryStream())
            {
                data.CopyTo(dataStream);
                file.FileData = dataStream.ToArray();
            }
            wtm.DC.AddEntity(file);
            wtm.DC.SaveChanges();
            return file;
        }
    }
}
