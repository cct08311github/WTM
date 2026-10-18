using System;
using System.IO;
using System.Linq;

namespace WalkingTec.Mvvm.Core.Support.FileHandlers
{
    /// <summary>
    /// Shared rules for turning client supplied names into values that are safe to store or to use in a path / object key.
    /// </summary>
    public static class FileNameSanitizer
    {
        private const int MaxExtensionLength = 10;

        /// <summary>
        /// Returns the last segment of the client supplied name, treating both '/' and '\' as separators.
        /// </summary>
        public static string GetLeafName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return fileName;
            }
            return Path.GetFileName(fileName.Replace('\\', '/'));
        }

        /// <summary>
        /// Use only the leaf of the client supplied name and keep the extension only when it is short and alphanumeric.
        /// </summary>
        public static string GetSafeExtension(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return string.Empty;
            }
            var leaf = GetLeafName(fileName);
            var ext = leaf.Substring(leaf.LastIndexOf('.') + 1);
            if (ext.Length == 0 || ext.Length > MaxExtensionLength || ext.All(char.IsAsciiLetterOrDigit) == false)
            {
                return string.Empty;
            }
            return ext;
        }

        /// <summary>
        /// Build an object key (always '/' separated) from an optional relative sub directory and a generated file name.
        /// Rooted sub directories (leading separator, drive letter, UNC) and ".." segments are rejected.
        /// </summary>
        public static string BuildObjectKey(string subdir, string fileNameWithoutDirectory)
        {
            var header = string.Empty;
            if (string.IsNullOrEmpty(subdir) == false)
            {
                header = subdir.Replace('\\', '/');
                if (header[0] == '/' || (header.Length >= 2 && char.IsAsciiLetter(header[0]) && header[1] == ':'))
                {
                    throw new ArgumentException("The sub directory must be a relative path.", nameof(subdir));
                }
                if (header.Split('/').Any(x => x == ".."))
                {
                    throw new ArgumentException("The sub directory must not contain parent directory segments.", nameof(subdir));
                }
            }
            if (header.Length == 0)
            {
                return fileNameWithoutDirectory;
            }
            return header.EndsWith('/') ? header + fileNameWithoutDirectory : header + "/" + fileNameWithoutDirectory;
        }
    }
}
