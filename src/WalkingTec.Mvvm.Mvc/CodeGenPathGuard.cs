using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Input validation and output-path containment used by the development-time code generator.
    /// Names are validated as identifiers and every resolved output path must stay inside the
    /// directories the generator is designed to write to.
    /// </summary>
    public static class CodeGenPathGuard
    {
        private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>
        /// True for a non-empty ASCII identifier: a letter or underscore followed by letters, digits or underscores.
        /// </summary>
        public static bool IsIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool letter = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
                bool digit = c >= '0' && c <= '9';
                if (letter == false && (digit == false || i == 0))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// True when the value is empty or an identifier (used for the optional Area).
        /// </summary>
        public static bool IsOptionalIdentifier(string value)
        {
            return string.IsNullOrEmpty(value) || IsIdentifier(value);
        }

        /// <summary>
        /// True when the free-text display name is empty or contains no path separators, parent-directory
        /// sequences, control characters or characters that are invalid in a file name.
        /// Non-ASCII text such as Chinese module names is allowed.
        /// </summary>
        public static bool IsSafeDisplayText(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return true;
            }
            if (value.Contains("..", StringComparison.Ordinal))
            {
                return false;
            }
            var invalid = Path.GetInvalidFileNameChars();
            foreach (var c in value)
            {
                if (char.IsControl(c) || invalid.Contains(c) || c == '/' || c == '\\')
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// True when <paramref name="path"/> equals <paramref name="root"/> or lies below it, after normalization.
        /// </summary>
        public static bool IsInside(string root, string path)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path))
            {
                return false;
            }
            var rootFull = Path.GetFullPath(root);
            var target = Path.GetFullPath(path);
            if (string.Equals(Path.TrimEndingDirectorySeparator(rootFull), Path.TrimEndingDirectorySeparator(target), PathComparison))
            {
                return true;
            }
            // A filesystem root ("/" or "D:\") already ends with a separator, do not append a second one.
            var prefix = Path.EndsInDirectorySeparator(rootFull) ? rootFull : rootFull + Path.DirectorySeparatorChar;
            return target.StartsWith(prefix, PathComparison);
        }

        /// <summary>
        /// Returns the normalized full path when it lies inside one of the allowed roots, otherwise throws.
        /// </summary>
        public static string EnsureInside(IEnumerable<string> roots, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new InvalidOperationException("The code generator output path is empty.");
            }
            var full = Path.GetFullPath(path);
            if (roots != null && roots.Any(x => IsInside(x, full)))
            {
                return full;
            }
            throw new InvalidOperationException("The code generator output path is outside of the allowed directories.");
        }

        /// <summary>
        /// Resolves the selected model against the models of the configured data contexts.
        /// The model must be one of <paramref name="allowedModels"/> and derive from <see cref="TopBasePoco"/>.
        /// </summary>
        public static bool TryResolveModel(string selectedModel, IEnumerable<Type> allowedModels, out Type modelType)
        {
            modelType = null;
            if (string.IsNullOrWhiteSpace(selectedModel) || allowedModels == null)
            {
                return false;
            }
            var allowed = allowedModels.Where(x => x != null).ToList();
            // The combobox submits the assembly qualified name of a listed model, match it without loading anything.
            var type = allowed.FirstOrDefault(x => string.Equals(x.AssemblyQualifiedName, selectedModel, StringComparison.Ordinal));
            if (type == null)
            {
                try
                {
                    var resolved = Type.GetType(selectedModel);
                    type = resolved != null && allowed.Contains(resolved) ? resolved : null;
                }
                catch (Exception)
                {
                    type = null;
                }
            }
            if (type == null || type.IsSubclassOf(typeof(TopBasePoco)) == false)
            {
                return false;
            }
            modelType = type;
            return true;
        }
    }

    /// <summary>
    /// Validates the free-text module name entered in the code generator.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
    public sealed class CodeGenDisplayTextAttribute : ValidationAttribute
    {
        public CodeGenDisplayTextAttribute()
        {
            ErrorMessage = "The module name must not contain path separators, '..' or characters that are invalid in a file name.";
        }

        public override bool IsValid(object value)
        {
            return CodeGenPathGuard.IsSafeDisplayText(value as string);
        }
    }
}
