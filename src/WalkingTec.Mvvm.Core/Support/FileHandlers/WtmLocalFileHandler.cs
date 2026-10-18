using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Models;

namespace WalkingTec.Mvvm.Core.Support.FileHandlers
{

    [Display(Name = "local")]
    public class WtmLocalFileHandler : WtmFileHandlerBase
    {
        private const string DefaultLocation = "./uploads";
        private const int MaxExtensionLength = 10;

        public WtmLocalFileHandler(WTMContext wtm) : base(wtm)
        {
        }

        public override Stream GetFileData(IWtmFile file)
        {
            var fullPath = GetFullPath(file.Path);
            WarnIfOutsideConfiguredRoots(file, fullPath);
            return File.OpenRead(fullPath);
        }


        public override (string path, string handlerInfo) Upload(string fileName, long fileLength, Stream data, string group = null, string subdir = null, string extra = null)
        {
            var localSettings = GetLocalSettings();

            // Use the requested group when it is configured, otherwise fall back to the first configured group.
            var groupInfo = string.IsNullOrEmpty(group)
                ? null
                : localSettings?.FirstOrDefault(x => x != null && string.Equals(x.GroupName, group, StringComparison.OrdinalIgnoreCase));
            groupInfo ??= localSettings?.FirstOrDefault(x => x != null);
            var groupdir = groupInfo?.GroupLocation;
            if (string.IsNullOrEmpty(groupdir))
            {
                groupdir = DefaultLocation;
            }
            string pathHeader = groupdir;
            if (string.IsNullOrEmpty(subdir) == false)
            {
                pathHeader = CombineSubdir(pathHeader, subdir);
            }
            else
            {
                var sub = WtmFileProvider._subDirFunc?.Invoke(this);
                if(string.IsNullOrEmpty(sub)== false)
                {
                    pathHeader = CombineSubdir(pathHeader, sub);
                }
            }
            string fulldir = GetFullPath(pathHeader);
            if (IsInside(GetFullPath(groupdir), fulldir) == false)
            {
                throw new ArgumentException("The upload directory must be inside the configured storage root.", nameof(subdir));
            }
            if (!Directory.Exists(fulldir))
            {
                Directory.CreateDirectory(fulldir);
            }
            var filename = $"{Guid.NewGuid().ToNoSplitString()}.{GetSafeExtension(fileName)}";
            var fullPath = Path.Combine(fulldir, filename);
            using (var fileStream = File.Create(fullPath))
            {
                data.CopyTo(fileStream);
            }
            data.Dispose();
            return (Path.Combine(pathHeader, filename),"");
        }

        public override void DeleteFile(IWtmFile file)
        {
            if (string.IsNullOrEmpty(file?.Path) == false)
            {
                try
                {
                    var fullPath = GetFullPath(file.Path);
                    WarnIfOutsideConfiguredRoots(file, fullPath);
                    File.Delete(fullPath);
                }
                catch { }
            }
        }

        private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private List<FileHandlerOptions> GetLocalSettings()
        {
            return wtm.ConfigInfo?.FileUploadOptions?.Settings?
                .Where(x => string.Equals(x.Key, "local", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value)
                .FirstOrDefault();
        }

        private static string CombineSubdir(string root, string subdir)
        {
            if (Path.IsPathRooted(subdir) || subdir[0] == '/' || subdir[0] == '\\')
            {
                throw new ArgumentException("The sub directory must be a relative path.", nameof(subdir));
            }
            return Path.Combine(root, subdir);
        }

        /// <summary>
        /// Use only the leaf of the client supplied name and keep the extension only when it is short and alphanumeric.
        /// </summary>
        private static string GetSafeExtension(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return string.Empty;
            }
            var leaf = Path.GetFileName(fileName.Replace('\\', '/'));
            var ext = leaf.Substring(leaf.LastIndexOf('.') + 1);
            if (ext.Length == 0 || ext.Length > MaxExtensionLength || ext.All(char.IsAsciiLetterOrDigit) == false)
            {
                return string.Empty;
            }
            return ext;
        }

        private static bool IsInside(string root, string fullPath)
        {
            var rootFull = Path.GetFullPath(root);
            var target = Path.GetFullPath(fullPath);
            if (string.Equals(Path.TrimEndingDirectorySeparator(rootFull), Path.TrimEndingDirectorySeparator(target), PathComparison))
            {
                return true;
            }
            // A filesystem root ("/" or "D:\") already ends with a separator, do not append a second one.
            var prefix = Path.EndsInDirectorySeparator(rootFull) ? rootFull : rootFull + Path.DirectorySeparatorChar;
            return target.StartsWith(prefix, PathComparison);
        }

        /// <summary>
        /// Stored paths come from the database, so reads and deletes are not refused;
        /// a warning is logged when the path lies outside every configured local root.
        /// </summary>
        private void WarnIfOutsideConfiguredRoots(IWtmFile file, string fullPath)
        {
            try
            {
                var roots = new List<string> { DefaultLocation };
                var settings = GetLocalSettings();
                if (settings != null)
                {
                    roots.AddRange(settings.Where(x => x != null && string.IsNullOrEmpty(x.GroupLocation) == false).Select(x => x.GroupLocation));
                }
                if (roots.Any(x => IsInside(GetFullPath(x), fullPath)))
                {
                    return;
                }
                var logger = wtm.LoggerFactory?.CreateLogger<WtmLocalFileHandler>();
                logger?.LogWarning("The stored path of file {FileId} is outside of the configured local storage roots.", file?.GetID());
            }
            catch
            {
                // Diagnostics must never change the behaviour of reads and deletes.
            }
        }

        private string GetFullPath(string path)
        {
            string rv = "";
            if (path.StartsWith("."))
            {
                rv = Path.Combine(wtm.ConfigInfo.HostRoot, path);
            }
            else
            {
                rv = path;
            }
            rv = Path.GetFullPath(rv);
            return rv ;
        }
    }

}
