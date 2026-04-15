#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Models;

namespace WalkingTec.Mvvm.Core.Support.FileHandlers
{
    public class WtmFileProvider
    {
        public string? SaveMode { get; set; }
        private static Dictionary<string, ConstructorInfo>? _handlers;
        private  static ConstructorInfo? _defaultHandler;
        private WTMContext _wtm;
        public static Func<IWtmFileHandler, string>? _subDirFunc;

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
                if (cons == null) continue;
                var nameattr = item.GetCustomAttribute<DisplayAttribute>();
                string name = "";
                if (nameattr == null)
                {
                    name = "FileHandler" + count;
                    count++;
                }
                else
                {
                    name = nameattr.Name ?? "FileHandler" + count++;
                }
                name = name.ToLower();
                if (name == config.FileUploadOptions.SaveFileMode?.ToString()?.ToLower())
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

        public IWtmFileHandler CreateFileHandler(string? saveMode = null, IDataContext? dc = null)
        {
            ConstructorInfo? ci = null;
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
                if (_handlers != null && _handlers.ContainsKey(saveMode))
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
                return (ci.Invoke(new object[] { _wtm }) as IWtmFileHandler)!;
            }
        }

        public  IWtmFile? Upload(string? fileName, long fileLength, Stream data, string? group = null, string? subdir = null, string? extra = null, string? saveMode = null, IDataContext? dc = null)
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

            // Sanitize group and subdir to prevent path traversal attacks
            // Allow only alphanumeric, underscore, hyphen, and period
            group = SanitizePathComponent(group);
            subdir = SanitizePathComponent(subdir);

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
                    file.UploadTime = _wtm.TimeProvider.GetLocalNow().DateTime;
                    file.SaveMode = string.IsNullOrEmpty(saveMode) == true ? _wtm.ConfigInfo.FileUploadOptions.SaveFileMode! : saveMode;
                    file.ExtraInfo = extra;
                    var ext = string.Empty;
                    if (string.IsNullOrEmpty(fileName) == false)
                    {
                        var dotPos = fileName.LastIndexOf('.');
                        ext = fileName[(dotPos + 1)..];
                    }
                    file.FileExt = ext;
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

        public IWtmFile? GetFile(string id, bool withData = true, IDataContext? dc = null)
        {
            IWtmFile? rv;
            if (dc == null)
            {
                dc = _wtm.CreateDC();
            }
            rv = dc.Set<FileAttachment>().IgnoreQueryFilters().CheckID(id).Select(x => new FileAttachment
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

        public void DeleteFile(string id, IDataContext? dc = null)
        {
            FileAttachment? file = null;
            if (dc == null)
            {
                dc = _wtm.CreateDC();
            }
            file = dc.Set<FileAttachment>().IgnoreQueryFilters().CheckID(id)
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
                catch (Exception ex)
                {
                    CoreProgram.GetLogger("WtmFileProvider")?.LogWarning(ex, "DeleteFile failed for FileAttachment '{FileId}' (name '{FileName}')", file.ID, file.FileName);
                }
            }

        }


        public string GetFileName(string id, IDataContext? dc = null)
        {
            string? rv;
            if (dc == null)
            {
                dc = _wtm.CreateDC();
            }
            rv = dc.Set<FileAttachment>().IgnoreQueryFilters().CheckID(id).Select(x => x.FileName).FirstOrDefault();
            if(rv == null)
            {
                rv = "unknown";
            }
            rv = rv.Replace("<", "").Replace(">", "").Replace(" ", "");
            return rv;
        }

        /// <summary>
        /// Sanitizes a path component to prevent path traversal attacks.
        /// Allows only alphanumeric characters, underscores, hyphens, and periods.
        /// </summary>
        private static string? SanitizePathComponent(string? input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            // Remove any path traversal sequences and dangerous characters
            var sanitized = System.Text.RegularExpressions.Regex.Replace(input, @"[^a-zA-Z0-9_\-\.]", "");
            return string.IsNullOrEmpty(sanitized) ? null : sanitized;
        }

    }
}
