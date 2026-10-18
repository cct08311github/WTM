using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
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

        public override Stream GetFileData(IWtmFile file)
        {
            var rv = wtm.DC.Set<FileAttachment>().CheckID(file.GetID()).FirstOrDefault();
            if (rv != null)
            {
                return new MemoryStream((rv as FileAttachment).FileData);
            }
            return null;
        }


        public  IWtmFile UploadToDB(string fileName, long fileLength, Stream data, string groupName = null, string subdir = null, string extra = null)
        {
            fileName = FileNameSanitizer.GetLeafName(fileName);
            FileAttachment file = new FileAttachment();
            file.FileName = fileName;
            file.Length = fileLength;
            file.UploadTime = DateTime.Now;
            file.SaveMode = _modeName;
            file.ExtraInfo = extra;
            file.TenantCode = wtm.LoginUserInfo?.CurrentTenant;
            file.FileExt = FileNameSanitizer.GetSafeExtension(fileName);
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
