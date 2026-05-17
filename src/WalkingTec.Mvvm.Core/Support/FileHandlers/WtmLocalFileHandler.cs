#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Models;

namespace WalkingTec.Mvvm.Core.Support.FileHandlers
{

    [Display(Name = "local")]
    public class WtmLocalFileHandler : WtmFileHandlerBase
    {

        public WtmLocalFileHandler(WTMContext wtm) : base(wtm)
        {
        }

        public override Stream? GetFileData(IWtmFile file)
        {
            var fullPath = ResolveUnderUploadRoot(file.Path);
            return File.OpenRead(fullPath);
        }


        public override (string? path, string? handlerInfo) Upload(string fileName, long fileLength, Stream data, string? group = null, string? subdir = null, string? extra = null)
        {
            var localSettings = wtm.ConfigInfo.FileUploadOptions.Settings.Where(x => x.Key.ToLower() == "local").Select(x => x.Value).FirstOrDefault();

            var groupdir = "";
            if (string.IsNullOrEmpty(group))
            {
                groupdir = localSettings?.FirstOrDefault()?.GroupLocation;
            }
            else {
               groupdir = localSettings?.Where(x => x.GroupName?.ToLower() == group!.ToLower()).FirstOrDefault()?.GroupLocation;
            }
            if (string.IsNullOrEmpty(groupdir))
            {
                groupdir = "./uploads";
            }
            string pathHeader = groupdir;
            if (string.IsNullOrEmpty(subdir) == false)
            {
                pathHeader = Path.Combine(pathHeader, subdir);
            }
            else
            {
                var sub = WtmFileProvider._subDirFunc?.Invoke(this);
                if(string.IsNullOrEmpty(sub)== false)
                {
                    pathHeader = Path.Combine(pathHeader, sub);
                }
            }
            string fulldir = GetFullPath(pathHeader);

            // Path traversal guard: ensure resolved path stays within upload root (#770)
            string uploadRoot = GetFullPath(groupdir);
            // Append separator to prevent prefix false-positive (e.g. /uploads vs /uploads-evil)
            if (!uploadRoot.EndsWith(Path.DirectorySeparatorChar))
                uploadRoot += Path.DirectorySeparatorChar;
            if (!fulldir.StartsWith(uploadRoot, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fulldir, uploadRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException($"Upload path '{subdir}' escapes the upload root directory.");
            }

            if (!Directory.Exists(fulldir))
            {
                Directory.CreateDirectory(fulldir);
            }
            var ext = string.Empty;
            if (string.IsNullOrEmpty(fileName) == false)
            {
                var dotPos = fileName.LastIndexOf('.');
                ext = fileName.Substring(dotPos + 1);
            }
            var filename = $"{Guid.NewGuid().ToNoSplitString()}.{ext}";
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
                    var fullPath = ResolveUnderUploadRoot(file.Path);
                    File.Delete(fullPath);
                }
                catch (UnauthorizedAccessException uae)
                {
                    CoreProgram.GetLogger("WtmLocalFileHandler")?.LogWarning(uae, "DeleteFile blocked: path escapes upload root");
                    throw;
                }
                catch (Exception ex)
                {
                    CoreProgram.GetLogger("WtmLocalFileHandler")?.LogWarning(ex, "DeleteFile failed for path '{Path}'", file?.Path);
                }
            }
        }

        /// <summary>
        /// Resolves a file path and verifies it stays within a configured upload root.
        /// Throws <see cref="UnauthorizedAccessException"/> (without leaking the path) if
        /// the resolved absolute path escapes every candidate upload root.
        /// </summary>
        private string ResolveUnderUploadRoot(string? path)
        {
            if (string.IsNullOrEmpty(path))
                throw new UnauthorizedAccessException("File path is empty.");

            string fullPath = GetFullPath(path);

            var localSettings = wtm.ConfigInfo?.FileUploadOptions?.Settings?
                .Where(x => x.Key.ToLower() == "local")
                .Select(x => x.Value)
                .FirstOrDefault();

            var candidateRoots = new List<string>();
            if (localSettings != null)
            {
                foreach (var s in localSettings)
                {
                    if (!string.IsNullOrEmpty(s.GroupLocation))
                        candidateRoots.Add(GetFullPath(s.GroupLocation));
                }
            }
            if (candidateRoots.Count == 0)
                candidateRoots.Add(GetFullPath("./uploads"));

            foreach (var rootRaw in candidateRoots)
            {
                var root = rootRaw.EndsWith(Path.DirectorySeparatorChar)
                    ? rootRaw
                    : rootRaw + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fullPath, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    return fullPath;
                }
            }

            CoreProgram.GetLogger("WtmLocalFileHandler")?.LogWarning(
                "Path traversal attempt detected: resolved path '{FullPath}' escapes all upload roots.", fullPath);
            throw new UnauthorizedAccessException("Resolved file path escapes the upload root.");
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
