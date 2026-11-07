using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Models;

namespace WalkingTec.Mvvm.Core.Support.FileHandlers
{
    public class WtmFileProvider
    {
        public string SaveMode { get; set; }
        private static Dictionary<string, ConstructorInfo> _handlers;
        private  static ConstructorInfo _defaultHandler;
        private WTMContext _wtm;
        public static Func<IWtmFileHandler, string> _subDirFunc;

        public WtmFileProvider(WTMContext wtm)
        {
            _wtm = wtm;
        }

        public static void Init(Configs config, GlobalData gd)
        {
            _handlers = new Dictionary<string, ConstructorInfo>();
            var types = gd.GetTypesAssignableFrom<IWtmFileHandler>();
            int count = 1;
            foreach (var item in types)
            {
                var cons = item.GetConstructor(new Type[] { typeof(WTMContext)});
                var nameattr = item.GetCustomAttribute<DisplayAttribute>();
                string name = "";
                if (nameattr == null)
                {
                    name = "FileHandler" + count;
                    count++;
                }
                else
                {
                    name = nameattr.Name;
                }
                name = name.ToLower();
                if (name == config.FileUploadOptions.SaveFileMode.ToString().ToLower())
                {
                    _defaultHandler = cons;
                }
                _handlers.Add(name, cons);
            }
            if (_defaultHandler == null && types.Count > 0)
            {
                _defaultHandler = types[0].GetConstructor(new Type[] { typeof(WTMContext) });
            }

        }

        public IWtmFileHandler CreateFileHandler(string saveMode = null, IDataContext dc = null)
        {
            ConstructorInfo ci = null;
            if (dc != null)
            {
                _wtm.DC = dc;
            }
            if (string.IsNullOrEmpty(saveMode))
            {
                ci = _defaultHandler;
            }
            else
            {
                saveMode = saveMode.ToLower();
                if (_handlers.ContainsKey(saveMode))
                {
                    ci = _handlers[saveMode];
                }
            }
            if (ci == null)
            {
                return new WtmDataBaseFileHandler(_wtm);
            }
            else
            {
                return ci.Invoke(new object[] { _wtm }) as IWtmFileHandler;
            }
        }

        public  IWtmFile Upload(string fileName, long fileLength, Stream data, string group = null, string subdir = null, string extra = null, string saveMode = null, IDataContext dc =null)
        {
            if (dc == null)
            {
                dc = _wtm.CreateDC();
            }
            var fh = CreateFileHandler(saveMode, dc);
            if(fileName == null)
            {
                fileName = "unknown";
            }
            fileName = fileName.Replace("<", "").Replace(">","").Replace(" ", "");
            fileName = FileNameSanitizer.GetLeafName(fileName);
            if (fh is WtmDataBaseFileHandler lfh)
            {
                return lfh.UploadToDB(fileName, fileLength, data, group, subdir, extra);
            }
            else
            {
                var rv = fh.Upload(fileName, fileLength, data, group, subdir, extra);
                if (string.IsNullOrEmpty(rv.path) == false)
                {
                    FileAttachment file = new FileAttachment();
                    file.FileName = fileName;
                    file.Length = fileLength;
                    file.UploadTime = DateTime.Now;
                    file.SaveMode = string.IsNullOrEmpty(saveMode) == true ? _wtm.ConfigInfo.FileUploadOptions.SaveFileMode : saveMode;
                    file.ExtraInfo = extra;
                    file.FileExt = FileNameSanitizer.GetSafeExtension(fileName);
                    file.Path = rv.path;
                    file.HandlerInfo = rv.handlerInfo;
                    file.TenantCode = _wtm.LoginUserInfo?.CurrentTenant;
                    dc.AddEntity(file);
                    dc.SaveChanges();
                    return file;
                }
                else
                {
                    return null;
                }
            }
        }

        public IWtmFile GetFile(string id, bool withData = true, IDataContext dc = null)
        {
            IWtmFile rv;
            if (dc == null)
            {
                dc = _wtm.CreateDC();
            }
            rv = dc.Set<FileAttachment>().CheckID(id).Select(x => new FileAttachment
            {
                ID = x.ID,
                ExtraInfo = x.ExtraInfo,
                FileExt = x.FileExt,
                FileName = x.FileName,
                Length = x.Length,
                Path = x.Path,
                SaveMode = x.SaveMode,
                UploadTime = x.UploadTime
            }).FirstOrDefault();
            if (rv != null && withData == true)
            {
                try
                {
                    var fh = CreateFileHandler(rv.SaveMode, dc);
                    rv.DataStream = fh.GetFileData(rv);
                }
                catch {
                    rv = null;
                }
            }
            return rv;

        }

        public void DeleteFile(string id, IDataContext dc = null)
        {
            FileAttachment file = null;
            if (dc == null)
            {
                dc = _wtm.CreateDC();
            }
            file = dc.Set<FileAttachment>().CheckID(id)
                .Select(x => new FileAttachment
                {
                    ID = x.ID,
                    ExtraInfo = x.ExtraInfo,
                    FileExt = x.FileExt,
                    FileName = x.FileName,
                    Path = x.Path,
                    SaveMode = x.SaveMode,
                    Length = x.Length,
                    UploadTime = x.UploadTime
                })
                .FirstOrDefault();
            if (file != null)
            {
                try
                {
                    dc.Set<FileAttachment>().Remove(file);
                    dc.SaveChanges();
                    var fh = CreateFileHandler(file.SaveMode, dc);
                    fh.DeleteFile(file);
                }
                catch { }
            }

        }


        /// <summary>
        /// The single place where the files a form asks to remove (<c>BaseVM.DeletedFileIds</c>) are deleted. An id is deleted only when it is in
        /// <paramref name="linkedIds"/> (the files the stored record refers to, read before the save), unless
        /// <c>FileEndpoints:LegacyVmFileDeletion</c> is on or the registered <see cref="IFileDeletionPolicy"/> allows it; the policy can also deny.
        /// Skipped ids are logged. Returns the number of files deleted.
        /// </summary>
        /// <param name="requestedIds">the ids sent by the client</param>
        /// <param name="linkedIds">the files the stored record refers to; null or empty when there is no such record</param>
        /// <param name="dc">data context used for the deletion</param>
        /// <param name="operation">name of the operation, for the policy and the log</param>
        public int DeleteRequestedFiles(IEnumerable<string> requestedIds, ISet<Guid> linkedIds, IDataContext dc, string operation)
        {
            if (requestedIds == null)
            {
                return 0;
            }
            var legacy = _wtm.ConfigInfo?.FileEndpoints?.LegacyVmFileDeletion == true;
            var logger = _wtm.LoggerFactory?.CreateLogger<WtmFileProvider>();
            var deleted = 0;
            var seen = new HashSet<Guid>();
            foreach (var item in requestedIds)
            {
                if (string.IsNullOrWhiteSpace(item))
                {
                    continue;
                }
                if (Guid.TryParse(item, out var id) == false)
                {
                    // not an id of this store: the earlier code found nothing to delete for it either. With the compatibility setting
                    // on it is still handed to the store as before, after the policy had its say.
                    if (legacy)
                    {
                        var rawDecision = FileDeletionPolicyRunner.Ask(_wtm, new FileDeletionRequest { FileId = Guid.Empty, RawFileId = item, IsLinked = false, Operation = operation }, logger);
                        if (rawDecision == FileDeletionDecision.Deny)
                        {
                            logger?.LogInformation("{Operation}: the file {FileId} was not deleted: the file deletion policy denied it (user {User})", operation, LogText.Sanitize(item, 64), LogText.Sanitize(_wtm.LoginUserInfo?.ITCode, 100));
                            continue;
                        }
                        DeleteFile(item, dc);
                    }
                    continue;
                }
                if (seen.Add(id) == false)
                {
                    continue;
                }
                var linked = linkedIds != null && linkedIds.Contains(id);
                var decision = FileDeletionPolicyRunner.Ask(_wtm, new FileDeletionRequest { FileId = id, IsLinked = linked, Operation = operation }, logger);
                if (decision == FileDeletionDecision.Deny)
                {
                    logger?.LogInformation("{Operation}: the file {FileId} was not deleted: the file deletion policy denied it (user {User})", operation, id, LogText.Sanitize(_wtm.LoginUserInfo?.ITCode, 100));
                    continue;
                }
                if (decision == FileDeletionDecision.Allow || legacy || linked)
                {
                    DeleteFile(id.ToString(), dc);
                    deleted++;
                }
                else
                {
                    logger?.LogInformation("{Operation}: the file {FileId} was not deleted: the saved record does not refer to it (user {User})", operation, id, LogText.Sanitize(_wtm.LoginUserInfo?.ITCode, 100));
                }
            }
            return deleted;
        }

        public string GetFileName(string id, IDataContext dc = null)
        {
            string rv;
            if (dc == null)
            {
                dc = _wtm.CreateDC();
            }
            rv = dc.Set<FileAttachment>().CheckID(id).Select(x => x.FileName).FirstOrDefault();
            if(rv == null)
            {
                rv = "unknown";
            }
            rv = rv.Replace("<", "").Replace(">", "").Replace(" ", "");
            return rv;
        }

    }
}
