// CG-03/04: UIEnum.VUE is marked [Obsolete] to surface a deprecation warning to
// consumers. This file is the internal back-compat implementation that must
// continue to reference UIEnum.VUE so that existing code that selects Vue 2
// still generates output. Suppress CS0618 file-wide here rather than scattering
// per-call-site suppressions through the generator switch branches.
#pragma warning disable CS0618 // UIEnum.VUE is [Obsolete] — internal back-compat, generation still works
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Helper;

namespace WalkingTec.Mvvm.Mvc
{
    public enum ApiAuthMode
    {
        [Display(Name = "Both Jwt and Cookie")]
        Both,
        [Display(Name = "Jwt")]
        Jwt,
        [Display(Name = "Cookie")]
        Cookie
    }

    [ReInit(ReInitModes.ALWAYS)]
    public partial class CodeGenVM : BaseVM
    {
        public CodeGenListVM FieldList { get; set; }

        public List<FieldInfo> FieldInfos { get; set; }

        public string PreviewFile { get; set; }

        public UIEnum UI { get; set; }

        /// <summary>
        /// CG-03/04: Non-null when a deprecated UI target has been selected.
        /// Surfaced to the user in the Gen/DoGen views as a visible warning.
        /// Generation still proceeds (backwards-compat); this is advisory only.
        /// </summary>
        [ValidateNever()]
        [BindNever()]
        public string? DeprecationWarning
        {
            get
            {
                if (UI == UIEnum.VUE)
                {
                    return "Warning: Vue 2 is end-of-life (EOL December 2023). " +
                           "Code generation will complete, but you should migrate to VUE3 or Blazor. " +
                           "UIEnum.VUE will be removed in a future major version.";
                }
                return null;
            }
        }

        /// <summary>
        /// CG-03/04: True when the Vue 2 (EOL) UI target is active.
        /// Views use this property instead of comparing directly against UIEnum.VUE,
        /// so the Razor view compiler does not emit CS0618 for the [Obsolete] member.
        /// </summary>
        [ValidateNever()]
        [BindNever()]
        public bool IsVue2Ui => UI == UIEnum.VUE;

        [Display(Name = "Codegen.GenApi")]
        public bool IsApi { get; set; }

        [Display(Name = "Codegen.EnableAnalysis")]
        public bool EnableAnalysis { get; set; }

        /// <summary>
        /// CG-09: When true, apply a name-based smart heuristic for search fields:
        /// string properties whose names end with "Code", "No", or "Id" get
        /// CheckEqual instead of CheckContain. Explicit [SearchField(Operator=...)]
        /// always overrides this heuristic. Default: false (off; preserves current output).
        /// </summary>
        public bool UseSmartSearchDefaults { get; set; } = false;

        [Display(Name = "Codegen.AuthMode")]
        public ApiAuthMode AuthMode { get; set; }

        public string ModelName
        {
            get
            {
                return SelectedModel?.Split(',').FirstOrDefault()?.Split('.').LastOrDefault() ?? "";
            }
        }
        [Display(Name = "Codegen.ModelNS")]
        [ValidateNever()]
        public string ModelNS => SelectedModel?.Split(',').FirstOrDefault()?.Split('.').SkipLast(1).ToSepratedString(seperator: ".");
        [Display(Name = "Codegen.ModuleName")]
        [Required(ErrorMessage = "Validate.{0}required")]
        // Prevent code/JSON injection: allow Unicode letters (covers CJK, Latin, etc.),
        // Unicode digits, underscores, hyphens, and literal spaces (U+0020 only, not \s,
        // to exclude newlines/tabs). \p{L} and \p{N} accept CJK module names like "用户管理"
        // while still blocking quotes, backslashes, angle-brackets and other injection chars.
        [RegularExpression(@"^[\p{L}\p{N}_\- ]+$", ErrorMessage = "Codegen.ModuleNameInvalid")]
        public string ModuleName { get; set; }
        [RegularExpression("^[A-Za-z_]+$", ErrorMessage = "Codegen.EnglishOnly")]
        public string Area { get; set; }
        [ValidateNever()]
        [BindNever()]
        public List<ComboSelectListItem> AllModels { get; set; }
        [Required(ErrorMessage = "Validate.{0}required")]
        [Display(Name = "_Admin.SelectedModel")]
        public string SelectedModel { get; set; }
        // [BindNever] prevents any HTTP model-binding from overriding this value;
        // it is set server-side only (AppDomain.CurrentDomain.BaseDirectory) by
        // _CodeGenController.  Allowing model-binding would let a crafted POST set
        // an arbitrary base directory and turn MainDir — and all derived paths — into
        // a user-controlled value, defeating SafeCombine's boundary checks.
        [ValidateNever()]
        [BindNever()]
        public string EntryDir { get; set; }


        public string _mainDir;
        // [BindNever] prevents HTTP model-binding from overriding this value.
        // MainDir is the write root for all generated files — VmDir, ShareDir, ControllerDir,
        // ViewDir, and every SafeCombine call anchors from this property. Allowing model-binding
        // would let a crafted POST supply an arbitrary root, defeating SafeCombine's boundary
        // checks in the same way EntryDir was already protected. The setter is kept for
        // server-side / internal use only (see CodeGenAnalysisTests._mainDir direct assignment).
        [ValidateNever()]
        [BindNever()]
        public string MainDir
        {
            get
            {
                if (_mainDir == null)
                {
                    int? index = EntryDir?.IndexOf($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}");
                    if (index == null || index < 0)
                    {
                        index = EntryDir?.IndexOf($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}");
                    }

                    if (index == null || index < 0)
                    {
                        // BaseDirectory does not contain \bin\Debug\ or \bin\Release\
                        // (e.g. dotnet run from repo root, or published app).
                        // Fall back to the current working directory, which is typically
                        // the project source root in development.
                        _mainDir = Directory.GetCurrentDirectory();
                    }
                    else
                    {
                        _mainDir = EntryDir!.Substring(0, index.Value);
                    }
                }
                return _mainDir;
            }
            set
            {
                _mainDir = value;
            }
        }

        public string _vmdir;
        [ValidateNever()]
        public string VmDir
        {
            get
            {
                if (_vmdir == null)
                {
                    var up = Directory.GetParent(MainDir);
                    var vmdir = up.GetDirectories().Where(x => x.Name.ToLower().EndsWith(".viewmodel")).FirstOrDefault();
                    if (vmdir == null)
                    {
                        if (string.IsNullOrEmpty(Area))
                        {
                            var path = SafePathHelper.SafeCombine(
                                MainDir,
                                Path.Combine("ViewModels", SanitizePathComponent(ModelName) + "VMs"));
                            vmdir = Directory.CreateDirectory(path);
                        }
                        else
                        {
                            var path = SafePathHelper.SafeCombine(
                                MainDir,
                                Path.Combine("Areas", SanitizePathComponent(Area), "ViewModels", SanitizePathComponent(ModelName) + "VMs"));
                            vmdir = Directory.CreateDirectory(path);
                        }
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(Area))
                        {
                            var path = SafePathHelper.SafeCombine(
                                vmdir.FullName,
                                SanitizePathComponent(ModelName) + "VMs");
                            vmdir = Directory.CreateDirectory(path);
                        }
                        else
                        {
                            var path = SafePathHelper.SafeCombine(
                                vmdir.FullName,
                                Path.Combine(SanitizePathComponent(Area), SanitizePathComponent(ModelName) + "VMs"));
                            vmdir = Directory.CreateDirectory(path);
                        }

                    }
                    _vmdir = vmdir.FullName;
                }
                return _vmdir;
            }
        }

        public string _sharedir;
        [ValidateNever()]
        public string ShareDir
        {
            get
            {
                if (_sharedir == null)
                {
                    var up = Directory.GetParent(MainDir);
                    var sharedir = up.GetDirectories().Where(x => x.Name.ToLower().EndsWith(".shared")).FirstOrDefault();
                    if (sharedir == null)
                    {
                        // No sibling *.shared project found (e.g. Blazor project without the
                        // conventional shared project, or a non-standard solution layout).
                        // Fall back to a "Shared/Pages" folder under MainDir, mirroring how
                        // VmDir falls back to a folder under MainDir when *.viewmodel is absent.
                        if (string.IsNullOrEmpty(Area))
                        {
                            var path = SafePathHelper.SafeCombine(
                                MainDir,
                                Path.Combine("Shared", "Pages", SanitizePathComponent(ModelName)));
                            sharedir = Directory.CreateDirectory(path);
                        }
                        else
                        {
                            var path = SafePathHelper.SafeCombine(
                                MainDir,
                                Path.Combine("Shared", "Pages", SanitizePathComponent(Area), SanitizePathComponent(ModelName)));
                            sharedir = Directory.CreateDirectory(path);
                        }
                    }
                    else if (string.IsNullOrEmpty(Area))
                    {
                        var path = SafePathHelper.SafeCombine(
                            sharedir.FullName,
                            Path.Combine("Pages", SanitizePathComponent(ModelName)));
                        sharedir = Directory.CreateDirectory(path);
                    }
                    else
                    {
                        var path = SafePathHelper.SafeCombine(
                            sharedir.FullName,
                            Path.Combine("Pages", SanitizePathComponent(Area), SanitizePathComponent(ModelName)));
                        sharedir = Directory.CreateDirectory(path);
                    }

                    _sharedir = sharedir.FullName;
                }
                return _sharedir;
            }
        }


        public string _testdir;
        [ValidateNever()]
        public string TestDir
        {
            get
            {
                if (_testdir == null)
                {
                    var up = Directory.GetParent(MainDir);
                    var testdir = up.GetDirectories().Where(x => x.Name.ToLower().EndsWith(".test")).FirstOrDefault();
                    _testdir = testdir?.FullName;
                }
                return _testdir;
            }
        }


        public string _controllerdir;
        [ValidateNever()]
        public string ControllerDir
        {
            get
            {
                if (_controllerdir == null)
                {
                    if (string.IsNullOrEmpty(Area))
                    {
                        var path = SafePathHelper.SafeCombine(MainDir, "Controllers");
                        _controllerdir = Directory.CreateDirectory(path).FullName;
                    }
                    else
                    {
                        var path = SafePathHelper.SafeCombine(
                            MainDir,
                            Path.Combine("Areas", SanitizePathComponent(Area), "Controllers"));
                        _controllerdir = Directory.CreateDirectory(path).FullName;
                    }
                }
                return _controllerdir;
            }
        }

        public string _viewdir;
        [ValidateNever()]
        public string ViewDir
        {
            get
            {
                if (_viewdir == null)
                {
                    if (string.IsNullOrEmpty(Area))
                    {
                        var path = SafePathHelper.SafeCombine(
                            MainDir,
                            Path.Combine("Views", SanitizePathComponent(ModelName)));
                        _viewdir = Directory.CreateDirectory(path).FullName;
                    }
                    else
                    {
                        var path = SafePathHelper.SafeCombine(
                            MainDir,
                            Path.Combine("Areas", SanitizePathComponent(Area), "Views", SanitizePathComponent(ModelName)));
                        _viewdir = Directory.CreateDirectory(path).FullName;
                    }
                }
                return _viewdir;
            }
        }


        private string _mainNs;
        public string MainNS
        {
            get
            {
                int index = MainDir.LastIndexOf(Path.DirectorySeparatorChar);
                if (index > 0)
                {
                    _mainNs = MainDir[(index + 1)..];
                }
                else
                {
                    _mainNs = MainDir;
                }
                return _mainNs;
            }
            set
            {
                _mainNs = value;
            }

        }

        private string _controllerNs;
        [Display(Name = "Codegen.ControllerNs")]
        [ValidateNever()]
        public string ControllerNs
        {
            get
            {
                if (_controllerNs == null)
                {
                    _controllerNs = MainNS + ".Controllers";
                }
                return _controllerNs;
            }
            set
            {
                _controllerNs = value;
            }
        }

        private string _testNs;
        [Display(Name = "Codegen.TestNs")]
        [ValidateNever()]
        public string TestNs
        {
            get
            {
                if (_testNs == null)
                {
                    _testNs = MainNS + ".Test";
                }
                return _testNs;
            }
            set
            {
                _testNs = value;
            }
        }

        private string _dataNs;
        [Display(Name = "Codegen.DataNs")]
        [ValidateNever()]
        public string DataNs
        {
            get
            {
                if (_dataNs == null)
                {
                    var up = Directory.GetParent(MainDir);
                    var vmdir = up.GetDirectories().Where(x => x.Name.ToLower().EndsWith(".dataaccess")).FirstOrDefault();
                    if (vmdir == null)
                    {
                        _dataNs = MainNS;
                    }
                    else
                    {
                        _dataNs = MainNS + ".DataAccess"; ;
                    }
                }
                return _dataNs;
            }
            set
            {
                _dataNs = value;
            }
        }


        private string _vmNs;
        [Display(Name = "Codegen.VMNs")]
        [ValidateNever()]
        public string VMNs
        {
            get
            {
                if (_vmNs == null)
                {
                    var up = Directory.GetParent(MainDir);
                    var vmdir = up.GetDirectories().Where(x => x.Name.ToLower().EndsWith(".viewmodel")).FirstOrDefault();
                    if (vmdir == null)
                    {
                        if (string.IsNullOrEmpty(Area))
                        {
                            _vmNs = MainNS + $".ViewModels.{ModelName}VMs";
                        }
                        else
                        {
                            _vmNs = MainNS + $".{Area}.ViewModels.{ModelName}VMs";
                        }
                    }
                    else
                    {
                        int index = vmdir.FullName.LastIndexOf(Path.DirectorySeparatorChar);
                        if (index > 0)
                        {
                            _vmNs = vmdir.FullName[(index + 1)..];
                        }
                        else
                        {
                            _vmNs = vmdir.FullName;
                        }
                        if (string.IsNullOrEmpty(Area))
                        {
                            _vmNs += $".{ModelName}VMs";
                        }
                        else
                        {
                            _vmNs += $".{Area}.{ModelName}VMs";
                        }
                    }
                }
                return _vmNs;
            }
            set
            {
                _vmNs = value;
            }
        }

        protected override void InitVM()
        {
            if (string.IsNullOrEmpty(SelectedModel) == false)
            {
                foreach (var item in ConfigInfo.Connections)
                {
                    var dc = item.CreateDC();
                    Type t = typeof(DbSet<>).MakeGenericType(Type.GetType(SelectedModel));
                    var exist = dc.GetType().GetSingleProperty(x => x.PropertyType == t);
                    if (exist != null)
                    {
                        this.DC = dc;
                    }
                }

            }

            FieldList = new CodeGenListVM();
            FieldList.CopyContext(this);
        }
        /// <summary>
        /// 在 Model source file 中自動插入 [Dimension] / [Measure] attribute
        /// </summary>
        public string InjectAnalysisAttributes()
        {
            if (!EnableAnalysis) return "";

            var analysisFields = FieldInfos?.Where(x => x.IsDimensionField || x.IsMeasureField).ToList();
            if (analysisFields == null || analysisFields.Count == 0) return "";

            // Find Model source file
            string modelName = SelectedModel?.Split(',').FirstOrDefault()?.Split('.').LastOrDefault() ?? "";
            if (string.IsNullOrEmpty(modelName)) return "Error: Cannot resolve model name.";

            string modelFileName = modelName + ".cs";
            string modelFilePath = FindModelFile(MainDir, modelFileName);
            if (modelFilePath == null)
            {
                return $"Warning: Cannot find {modelFileName}. Please manually add [Dimension]/[Measure] attributes.";
            }

            // Verify that the resolved model file is contained within MainDir.
            // FindModelFile searches AllDirectories up to 5 levels ABOVE MainDir, so a
            // same-named .cs file anywhere in the solution tree can be returned.
            // SafeCombine anchored to the found file's own directory would not detect this;
            // we must compare canonical paths against MainDir directly.
            string canonicalMainDir = Path.GetFullPath(MainDir);
            string canonicalModelFile = Path.GetFullPath(modelFilePath);
            // Ensure separator-terminated prefix so "/foobar" does not match "/foo"
            string mainDirPrefix = canonicalMainDir.EndsWith(Path.DirectorySeparatorChar)
                ? canonicalMainDir
                : canonicalMainDir + Path.DirectorySeparatorChar;
            if (!canonicalModelFile.StartsWith(mainDirPrefix, StringComparison.Ordinal)
                && !string.Equals(canonicalModelFile, canonicalMainDir, StringComparison.Ordinal))
            {
                return $"Error: Model file '{canonicalModelFile}' is outside the project root '{canonicalMainDir}'. " +
                       "Please manually add [Dimension]/[Measure] attributes.";
            }

            string content = File.ReadAllText(modelFilePath, Encoding.UTF8);
            string originalContent = content;
            bool modified = false;

            // Add using if missing
            if (!content.Contains("using WalkingTec.Mvvm.Core.Analysis;"))
            {
                var lastUsingMatch = Regex.Match(
                    content,
                    @"^using [^;]+;\s*$",
                    RegexOptions.Multiline | RegexOptions.RightToLeft);
                if (lastUsingMatch.Success)
                {
                    int insertPos = lastUsingMatch.Index + lastUsingMatch.Length;
                    content = content.Insert(insertPos, "\nusing WalkingTec.Mvvm.Core.Analysis;");
                    modified = true;
                }
            }

            // Try to resolve model type for DateTime detection
            Type modelType = Type.GetType(SelectedModel);

            foreach (var field in analysisFields)
            {
                string attrName = field.IsDimensionField ? "Dimension" : "Measure";

                var propMatch = Regex.Match(content, @"\bpublic\s+\S+\??\s+" + Regex.Escape(field.FieldName) + @"\s*\{");
                if (!propMatch.Success) continue;

                int propIndex = propMatch.Index;
                int prevBrace = content.LastIndexOf('}', propIndex);
                int prevSemi = content.LastIndexOf(';', propIndex);
                int prevOpen = content.LastIndexOf('{', propIndex);
                
                int startIdx = Math.Max(Math.Max(prevBrace, prevSemi), prevOpen);
                if (startIdx == -1) startIdx = 0;
                
                string block = content.Substring(startIdx, propIndex - startIdx);
                bool alreadyHasAttr = block.Contains("[" + attrName + "]") || block.Contains("[" + attrName + "(");
                if (alreadyHasAttr) continue;

                // Build attribute string
                string attrStr;
                if (field.IsDimensionField)
                {
                    bool isDateTime = false;
                    if (modelType != null)
                    {
                        var propType = modelType.GetSingleProperty(field.FieldName)?.PropertyType;
                        if (propType != null)
                        {
                            var underlying = Nullable.GetUnderlyingType(propType) ?? propType;
                            isDateTime = underlying == typeof(DateTime);
                        }
                    }
                    else
                    {
                        // Fallback: check source text for DateTime type
                        isDateTime = Regex.IsMatch(
                            content,
                            @"public\s+DateTime\??\s+" + Regex.Escape(field.FieldName) + @"\s");
                    }

                    attrStr = isDateTime
                        ? "[Dimension(Hierarchy = DateHierarchy.Month)]"
                        : "[Dimension]";
                }
                else
                {
                    attrStr = "[Measure]";
                }

                int indentStart = propIndex - 1;
                while (indentStart >= 0 && (content[indentStart] == ' ' || content[indentStart] == '\t'))
                {
                    indentStart--;
                }
                string indent = content.Substring(indentStart + 1, propIndex - indentStart - 1);
                if (string.IsNullOrEmpty(indent)) indent = "        ";

                string insertion = attrStr + "\n" + indent;
                content = content.Insert(propIndex, insertion);
                modified = true;
            }

            if (modified && content != originalContent)
            {
                File.WriteAllText(modelFilePath, content, Encoding.UTF8);
                return $"Analysis attributes injected into {modelFilePath}";
            }

            return "";
        }

        /// <summary>
        /// Search for a model .cs file starting from startDir and going up
        /// </summary>
        public string FindModelFile(string startDir, string fileName)
        {
            var dir = new DirectoryInfo(startDir);
            int levels = 0;
            while (dir != null && levels < 5)
            {
                try
                {
                    var files = dir.GetFiles(fileName, SearchOption.AllDirectories);
                    var match = files.FirstOrDefault(f =>
                        !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
                    if (match != null) return match.FullName;
                }
                catch
                {
                    // Permission or access errors, try parent
                }

                dir = dir.Parent;
                levels++;
            }
            return null;
        }

        private static string SanitizePathComponent(string? input)
        {
            if (string.IsNullOrEmpty(input)) return input ?? "";
            return System.Text.RegularExpressions.Regex.Replace(input, @"[^a-zA-Z0-9_\-\.]", "");
        }

        // #505 — defense-in-depth: FieldName and SubField are model-bound strings that
        // get interpolated raw into generated C#/Razor source.  Validate them as C#
        // identifiers once, at the start of generation, so a bad name throws clearly
        // before any file is emitted.  SubField may be the "`file" sentinel (handled
        // via its own early-return guards throughout the generator) — those callers
        // never reach identifier-interpolation, so we skip validation for that value.
        private static readonly System.Text.RegularExpressions.Regex _identifierPattern =
            new(@"^[A-Za-z_][A-Za-z0-9_]*$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string ValidateIdentifier(string? name, string ctx)
        {
            if (string.IsNullOrEmpty(name) || !_identifierPattern.IsMatch(name))
                throw new ArgumentException($"Invalid code-gen identifier '{name}' for {ctx}.");
            return name;
        }

        /// <summary>
        /// Validates all FieldName and SubField values in <paramref name="fields"/> before
        /// any source file is emitted.  Throws <see cref="ArgumentException"/> on the first
        /// invalid identifier so the caller gets a clear message rather than malformed output.
        /// </summary>
        private static void ValidateFieldIdentifiers(IEnumerable<FieldInfo> fields)
        {
            foreach (var f in fields)
            {
                ValidateIdentifier(f.FieldName, $"FieldName on field '{f.FieldName}'");
                // SubField is optional; skip the "`file" sentinel which is handled
                // separately by generation code and never interpolated as an identifier.
                if (!string.IsNullOrEmpty(f.SubField) && f.SubField != "`file")
                {
                    ValidateIdentifier(f.SubField, $"SubField on field '{f.FieldName}'");
                }
            }
        }

        // ---------------------------------------------------------------------------
        // CG-08: Build the fluent method chain appended after MakeGridHeader(x => x.Foo)
        // when [ListColumn] is present on the property, or when the type-based width
        // heuristic applies (absent attribute = current auto behavior, zero extra chain).
        // ---------------------------------------------------------------------------
        private static string BuildGridHeaderChain(
            string fieldName,
            PropertyInfo? prop,
            ListColumnAttribute? attr,
            bool isFkLookup = false)
        {
            // Compute the width to emit.
            int width = 0;
            if (attr != null && attr.Width > 0)
            {
                width = attr.Width;
            }
            else
            {
                // Type-based heuristic (only applies when no explicit [ListColumn] Width given).
                if (prop != null)
                {
                    var propType = prop.PropertyType;
                    var baseType = propType.IsNullable() ? propType.GetGenericArguments()[0] : propType;
                    if (isFkLookup)
                        width = 180;
                    else if (baseType == typeof(string))
                        width = 150;
                    else if (baseType == typeof(DateTime))
                        width = 160;
                    else if (baseType == typeof(bool))
                        width = 80;
                    else if (baseType == typeof(int) || baseType == typeof(long) ||
                             baseType == typeof(decimal) || baseType == typeof(double) ||
                             baseType == typeof(float) || baseType == typeof(short))
                        width = 100;
                }
            }

            if (attr == null)
            {
                // No attribute — emit only the width heuristic when non-zero.
                return width > 0 ? $".SetWidth({width})" : "";
            }

            // Attribute present — emit only non-default modifier calls.
            var chain = new StringBuilder();
            int emitWidth = attr.Width > 0 ? attr.Width : width;
            if (emitWidth > 0)
                chain.Append($".SetWidth({emitWidth})");
            if (attr.Align != GridColumnAlignEnum.Auto)
                chain.Append($".SetAlign(GridColumnAlignEnum.{attr.Align})");
            if (!attr.Sort)
                chain.Append(".SetSort(false)");
            if (attr.Hide)
                chain.Append(".SetHide(true)");
            if (attr.Fixed != GridColumnFixedEnum.None)
                chain.Append($".SetFixed(GridColumnFixedEnum.{attr.Fixed})");
            if (attr.ShowTotal)
                chain.Append(".SetShowTotal(true)");
            return chain.ToString();
        }

        /// <summary>
        /// Returns a JSON-safe string value (without surrounding quotes) for use inside
        /// a double-quoted JSON string literal. Defense-in-depth: the [RegularExpression]
        /// attribute on ModuleName is the primary gate; this escaping handles any value
        /// that reaches this code path at runtime.
        /// </summary>
        private static string EscapeForJson(string? input)
        {
            if (input is null) return "";
            // JsonSerializer.Serialize produces a JSON string including outer quotes; strip them.
            string serialized = JsonSerializer.Serialize(input);
            return serialized.Length >= 2
                ? serialized[1..^1]  // strip leading and trailing '"'
                : "";
        }

        /// <summary>
        /// Returns a JS-safe string value for use inside a single-quoted JS string literal.
        /// Escapes backslash and single-quote characters.
        /// </summary>
        private static string EscapeForJsSingleQuoted(string? input)
        {
            if (input is null) return "";
            // Backslash must be escaped first to avoid double-escaping.
            return input.Replace("\\", "\\\\").Replace("'", "\\'");
        }

        // ---------------------------------------------------------------------------
        // CG-02: regenerate-safe two-zone file write helper.
        //   *.Generated.cs  — always overwritten; carries the scaffold body.
        //   *.cs             — written ONLY if it does not exist; keeps dev edits safe.
        // ---------------------------------------------------------------------------
        private static void WriteGeneratedZone(string generatedPath, string content)
        {
            File.WriteAllText(generatedPath, content, Encoding.UTF8);
        }

        private static void WritePartialZone(string partialPath, string partialContent)
        {
            if (!File.Exists(partialPath))
            {
                File.WriteAllText(partialPath, partialContent, Encoding.UTF8);
            }
            // If the file already exists, leave it untouched so developer edits survive re-gen.
        }

        /// <summary>
        /// Derives a minimal empty-partial shell from a generated file's namespace/class
        /// declaration so the companion *.cs compiles together with *.Generated.cs.
        /// </summary>
        private static string MakeEmptyPartial(string generatedContent, string generatedFileName)
        {
            // Extract the first namespace and first public partial class line via simple text scan.
            string ns = "";
            string classDecl = "";
            foreach (var line in generatedContent.Split('\n'))
            {
                var trimmed = line.Trim();
                if (ns == "" && trimmed.StartsWith("namespace "))
                    ns = trimmed["namespace ".Length..].TrimEnd('{', ' ', '\r');
                if (classDecl == "" && trimmed.Contains("public partial class "))
                {
                    // Keep only up to the opening brace.
                    int brace = trimmed.IndexOf('{');
                    classDecl = brace >= 0 ? trimmed[..brace].TrimEnd() : trimmed;
                }
                if (ns != "" && classDecl != "")
                    break;
            }
            if (ns == "" || classDecl == "")
                return $"// Hand-edit partial for {generatedFileName}\n// (auto-generated stub; customize here)\n";

            return $"// Hand-edit partial for {generatedFileName}\n// This file is NEVER overwritten by code generation.\n" +
                   $"// Add your customizations here.\n\n" +
                   $"namespace {ns}\n{{\n    // {classDecl}\n    // {{\n    // }}\n}}\n";
        }

        public void DoGen()
        {
            // All file-write paths that incorporate user-supplied segments (ModelName, Area)
            // are resolved through SafePathHelper.SafeCombine so that
            // a path-traversal attempt is caught before any I/O takes place.
            string safeModelName = SanitizePathComponent(ModelName);
            string safeModelNameLower = safeModelName.ToLower();
            string safeArea = SanitizePathComponent(Area);

            // #505 — defense-in-depth: validate all FieldName/SubField values before
            // any source is emitted so a malicious or malformed identifier fails fast.
            if (FieldInfos != null)
            {
                ValidateFieldIdentifiers(FieldInfos);
            }

            // CG-02: Controller → two-zone split
            string controllerSuffix = $"{safeModelName}{(IsApi == true ? "Api" : "")}Controller";
            string controllerGenPath = SafePathHelper.SafeCombine(ControllerDir, $"{controllerSuffix}.Generated.cs");
            string controllerPartialPath = SafePathHelper.SafeCombine(ControllerDir, $"{controllerSuffix}.cs");
            string controllerGenContent = GenerateController();
            WriteGeneratedZone(controllerGenPath, controllerGenContent);
            WritePartialZone(controllerPartialPath, MakeEmptyPartial(controllerGenContent, $"{controllerSuffix}.Generated.cs"));

            // CG-02: VM files → two-zone split
            void WriteVmZones(string vmSuffix, string vmContent)
            {
                string genPath = SafePathHelper.SafeCombine(VmDir, $"{vmSuffix}.Generated.cs");
                string partialPath = SafePathHelper.SafeCombine(VmDir, $"{vmSuffix}.cs");
                WriteGeneratedZone(genPath, vmContent);
                WritePartialZone(partialPath, MakeEmptyPartial(vmContent, $"{vmSuffix}.Generated.cs"));
            }

            string vmPrefix = $"{safeModelName}{(IsApi == true ? "Api" : "")}";
            WriteVmZones($"{vmPrefix}VM", GenerateVM("CrudVM"));
            WriteVmZones($"{vmPrefix}ListVM", GenerateVM("ListVM"));
            WriteVmZones($"{vmPrefix}BatchVM", GenerateVM("BatchVM"));
            WriteVmZones($"{vmPrefix}ImportVM", GenerateVM("ImportVM"));
            WriteVmZones($"{vmPrefix}Searcher", GenerateVM("Searcher"));

            if (IsApi == false)
            {
                if (UI == UIEnum.LayUI)
                {
                    File.WriteAllText(SafePathHelper.SafeCombine(ViewDir, "Index.cshtml"), GenerateView("ListView"), Encoding.UTF8);
                    File.WriteAllText(SafePathHelper.SafeCombine(ViewDir, "Create.cshtml"), GenerateView("CreateView"), Encoding.UTF8);
                    File.WriteAllText(SafePathHelper.SafeCombine(ViewDir, "Edit.cshtml"), GenerateView("EditView"), Encoding.UTF8);
                    File.WriteAllText(SafePathHelper.SafeCombine(ViewDir, "Delete.cshtml"), GenerateView("DeleteView"), Encoding.UTF8);
                    File.WriteAllText(SafePathHelper.SafeCombine(ViewDir, "Details.cshtml"), GenerateView("DetailsView"), Encoding.UTF8);
                    File.WriteAllText(SafePathHelper.SafeCombine(ViewDir, "Import.cshtml"), GenerateView("ImportView"), Encoding.UTF8);
                    File.WriteAllText(SafePathHelper.SafeCombine(ViewDir, "BatchEdit.cshtml"), GenerateView("BatchEditView"), Encoding.UTF8);
                    File.WriteAllText(SafePathHelper.SafeCombine(ViewDir, "BatchDelete.cshtml"), GenerateView("BatchDeleteView"), Encoding.UTF8);
                }
                if (UI == UIEnum.React || UI == UIEnum.VUE)
                {
                    // Compute the per-model ClientApp pages directory.
                    // SafePathHelper.SafeCombine performs a canonical boundary check recognised
                    // by static-analysis tools (CodeQL cs/path-injection).
                    string pagesModelDir = SafePathHelper.SafeCombine(
                        MainDir,
                        Path.Combine("ClientApp", "src", "pages", safeModelNameLower));
                    if (!Directory.Exists(pagesModelDir))
                        Directory.CreateDirectory(pagesModelDir);

                    string pagesModelViewsDir = SafePathHelper.SafeCombine(pagesModelDir, "views");
                    if (!Directory.Exists(pagesModelViewsDir))
                        Directory.CreateDirectory(pagesModelViewsDir);

                    string pagesModelStoreDir = SafePathHelper.SafeCombine(pagesModelDir, "store");
                    if (!Directory.Exists(pagesModelStoreDir))
                        Directory.CreateDirectory(pagesModelStoreDir);

                    if (UI == UIEnum.React)
                    {
                        File.WriteAllText(SafePathHelper.SafeCombine(pagesModelViewsDir, "action.tsx"), GenerateReactView("action"), Encoding.UTF8);
                        File.WriteAllText(SafePathHelper.SafeCombine(pagesModelViewsDir, "forms.tsx"), GenerateReactView("forms"), Encoding.UTF8);
                        File.WriteAllText(SafePathHelper.SafeCombine(pagesModelViewsDir, "models.tsx"), GenerateReactView("models"), Encoding.UTF8);
                        File.WriteAllText(SafePathHelper.SafeCombine(pagesModelViewsDir, "other.tsx"), GenerateReactView("other"), Encoding.UTF8);
                        File.WriteAllText(SafePathHelper.SafeCombine(pagesModelViewsDir, "search.tsx"), GenerateReactView("search"), Encoding.UTF8);
                        File.WriteAllText(SafePathHelper.SafeCombine(pagesModelViewsDir, "table.tsx"), GenerateReactView("table"), Encoding.UTF8);
                        File.WriteAllText(SafePathHelper.SafeCombine(pagesModelStoreDir, "index.ts"), GetResource("index.txt", "Spa.React.store").Replace("$modelname$", ModelName.ToLower()), Encoding.UTF8);
                        File.WriteAllText(SafePathHelper.SafeCombine(pagesModelDir, "index.tsx"), GetResource("index.txt", "Spa.React").Replace("$modelname$", ModelName.ToLower()), Encoding.UTF8);
                        File.WriteAllText(SafePathHelper.SafeCombine(pagesModelDir, "style.less"), GetResource("style.txt", "Spa.React").Replace("$modelname$", ModelName.ToLower()), Encoding.UTF8);
                    }
                    if (UI == UIEnum.VUE)
                    {
                        List<string> apipneeded = new List<string>();
                        File.WriteAllText(SafePathHelper.SafeCombine(pagesModelDir, "index.vue"), GenerateVUEView("index", apipneeded), Encoding.UTF8);
                        File.WriteAllText(SafePathHelper.SafeCombine(pagesModelDir, "config.ts"), GenerateVUEView("config", apipneeded), Encoding.UTF8);
                        File.WriteAllText(SafePathHelper.SafeCombine(pagesModelViewsDir, "dialog-form.vue"), GenerateVUEView("views.dialog-form", apipneeded), Encoding.UTF8);
                        File.WriteAllText(SafePathHelper.SafeCombine(pagesModelStoreDir, "index.ts"), GetResource("index.txt", "Spa.Vue.store").Replace("$modelname$", ModelName.ToLower()), Encoding.UTF8);
                        File.WriteAllText(SafePathHelper.SafeCombine(pagesModelStoreDir, "api.ts"), GenerateVUEView("store.api", apipneeded), Encoding.UTF8);
                    }
                    #region 设置react和vue的默认页面和默认菜单，vue3不需要这部分
                    string pagesIndexPath = SafePathHelper.SafeCombine(
                        MainDir,
                        Path.Combine("ClientApp", "src", "pages", "index.ts"));
                    var index = File.ReadAllText(pagesIndexPath);
                    if (index.Contains($"path: '/{ModelName.ToLower()}'") == false)
                    {
                        if (UI == UIEnum.React)
                        {
                            // EscapeForJsSingleQuoted: defense-in-depth; [RegularExpression] on ModuleName is the primary gate.
                            index = index.Replace("/**WTM**/", $@"
, {ModelName.ToLower()}: {{
        name: '{EscapeForJsSingleQuoted(ModuleName.ToLower())}',
        path: '/{ModelName.ToLower()}',
        controller: '{ControllerNs},{ModelName}',
        component: React.lazy(() => import('./{ModelName.ToLower()}'))
    }}
/**WTM**/
 ");
                        }
                        if (UI == UIEnum.VUE)
                        {
                            // EscapeForJsSingleQuoted: defense-in-depth; [RegularExpression] on ModuleName is the primary gate.
                            index = index.Replace("/**WTM**/", $@"
, {ModelName.ToLower()}: {{
    name: '{EscapeForJsSingleQuoted(ModuleName.ToLower())}',
    path: '/{ModelName.ToLower()}',
    controller: '{ControllerNs},{ModelName}'
    }}
/**WTM**/
 ");

                        }
                        File.WriteAllText(pagesIndexPath, index, Encoding.UTF8);
                    }
                    string menu = "";
                    if (UI == UIEnum.React)
                    {
                        string subMenuPath = SafePathHelper.SafeCombine(
                            MainDir,
                            Path.Combine("ClientApp", "public", "subMenu.json"));
                        menu = File.ReadAllText(subMenuPath);
                        if (menu.Contains($@"""Url"": ""/{ModelName.ToLower()}""") == false)
                        {
                            var i = menu.LastIndexOf("}");
                            // EscapeForJson: defense-in-depth; [RegularExpression] on ModuleName is the primary gate.
                            menu = menu.Insert(i + 1, $@"
,{{
    ""Id"": ""{Guid.NewGuid()}"",
    ""ParentId"": null,
    ""Text"": ""{EscapeForJson(ModuleName.ToLower())}"",
    ""Url"": ""/{ModelName.ToLower()}""
    }}
");
                            File.WriteAllText(subMenuPath, menu, Encoding.UTF8);

                        }
                    }
                    if (UI == UIEnum.VUE)
                    {
                        string subMenuPath = SafePathHelper.SafeCombine(
                            MainDir,
                            Path.Combine("ClientApp", "src", "subMenu.json"));
                        menu = File.ReadAllText(subMenuPath);
                        if (menu.Contains($@"""Url"": ""/{ModelName.ToLower()}""") == false)
                        {
                            var i = menu.LastIndexOf("}");
                            // EscapeForJson: defense-in-depth; [RegularExpression] on ModuleName is the primary gate.
                            menu = menu.Insert(i + 1, $@"
,{{
    ""Id"": ""{Guid.NewGuid()}"",
    ""ParentId"": null,
    ""Text"": ""{EscapeForJson(ModuleName.ToLower())}"",
    ""Url"": ""/{ModelName.ToLower()}""
    }}
");
                            File.WriteAllText(subMenuPath, menu, Encoding.UTF8);

                        }
                    }
                    #endregion
                }

                if (UI == UIEnum.Blazor)
                {
                    File.WriteAllText(SafePathHelper.SafeCombine(ShareDir, "Index.razor"), GenerateBlazorView("Index"), Encoding.UTF8);
                    File.WriteAllText(SafePathHelper.SafeCombine(ShareDir, "Create.razor"), GenerateBlazorView("Create"), Encoding.UTF8);
                    File.WriteAllText(SafePathHelper.SafeCombine(ShareDir, "Edit.razor"), GenerateBlazorView("Edit"), Encoding.UTF8);
                    File.WriteAllText(SafePathHelper.SafeCombine(ShareDir, "Details.razor"), GenerateBlazorView("Details"), Encoding.UTF8);
                    File.WriteAllText(SafePathHelper.SafeCombine(ShareDir, "Import.razor"), GenerateBlazorView("Import"), Encoding.UTF8);
                }
                if (UI == UIEnum.VUE3)
                {
                    //Todo 生成vue3页面
                    string pathvue3 = SafePathHelper.SafeCombine(
                        MainDir,
                        Path.Combine("ClientApp", "src", "views", safeArea.ToLower(), safeModelNameLower));
                    if (!Directory.Exists(pathvue3))
                        Directory.CreateDirectory(pathvue3);

                    string pathapi = SafePathHelper.SafeCombine(
                        MainDir,
                        Path.Combine("ClientApp", "src", "api", safeArea.ToLower(), safeModelName));
                    if (!Directory.Exists(pathapi))
                        Directory.CreateDirectory(pathapi);

                    File.WriteAllText(SafePathHelper.SafeCombine(pathvue3, "index.vue"), GenerateVue3View("Index"), Encoding.UTF8);
                    File.WriteAllText(SafePathHelper.SafeCombine(pathvue3, "create.vue"), GenerateVue3View("Create"), Encoding.UTF8);
                    File.WriteAllText(SafePathHelper.SafeCombine(pathvue3, "edit.vue"), GenerateVue3View("Edit"), Encoding.UTF8);
                    File.WriteAllText(SafePathHelper.SafeCombine(pathvue3, "details.vue"), GenerateVue3View("Details"), Encoding.UTF8);
                    File.WriteAllText(SafePathHelper.SafeCombine(pathvue3, "import.vue"), GenerateVue3View("Import"), Encoding.UTF8);

                    File.WriteAllText(SafePathHelper.SafeCombine(pathapi, "index.ts"), GenerateVue3View("indexapi"), Encoding.UTF8);
                }
            }
            var test = GenerateTest();
            if (test != "" && TestDir != null)
            {
                if (UI == UIEnum.LayUI && IsApi == false)
                {
                    File.WriteAllText(SafePathHelper.SafeCombine(TestDir, $"{safeModelName}ControllerTest.cs"), test, Encoding.UTF8);
                }
                else
                {
                    File.WriteAllText(SafePathHelper.SafeCombine(TestDir, $"{safeModelName}ApiTest.cs"), test, Encoding.UTF8);
                }
            }

            // Inject Analysis Mode attributes into Model source file
            if (EnableAnalysis)
            {
                InjectAnalysisAttributes();
            }
        }

        public string GenerateController()
        {
            string dir = "";
            string jwt = "";
            if (UI == UIEnum.LayUI && IsApi == false)
            {
                dir = "Mvc";
            }
            else
            {
                dir = "Spa";
                if (UI == UIEnum.Blazor)
                {
                    dir = "Spa.Blazor";
                }
                switch (AuthMode)
                {
                    case ApiAuthMode.Both:
                        jwt = "[AuthorizeJwtWithCookie]";
                        break;
                    case ApiAuthMode.Jwt:
                        jwt = "[AuthorizeJwt]";
                        break;
                    case ApiAuthMode.Cookie:
                        jwt = "[AuthorizeCookie]";
                        break;
                    default:
                        break;
                }
            }
            var rv = GetResource("Controller.txt", dir).Replace("$jwt$", jwt).Replace("$vmnamespace$", VMNs).Replace("$namespace$", ControllerNs).Replace("$des$", ModuleName).Replace("$modelname$", ModelName).Replace("$modelnamespace$", ModelNS).Replace("$controllername$", $"{ModelName}{(IsApi == true ? "Api" : "")}");
            if (string.IsNullOrEmpty(Area))
            {
                rv = rv.Replace("$area$", "");
            }
            else
            {
                rv = rv.Replace("$area$", $"[Area(\"{Area}\")]");
            }
            //生成api中获取下拉菜单数据的api
            //如果一个一对多关联其他类的字段是搜索条件或者表单字段，则生成对应的获取关联表数据的api
            if (UI != UIEnum.LayUI || IsApi == true)
            {
                StringBuilder other = new StringBuilder();
                List<FieldInfo> pros = FieldInfos.Where(x => x.IsSearcherField == true || x.IsFormField == true).ToList();
                List<string> existSubPro = new List<string>();
                for (int i = 0; i < pros.Count; i++)
                {
                    var item = pros[i];
                    if ((item.InfoType == FieldInfoType.One2Many || item.InfoType == FieldInfoType.Many2Many) && item.SubField != "`file")
                    {
                        var subtype = Type.GetType(item.RelatedField);
                        var subpro = subtype.GetSingleProperty(item.SubField);
                        var key = subtype.FullName + ":" + subpro.Name;
                        existSubPro.Add(key);
                        int count = existSubPro.Where(x => x == key).Count();
                        if (count == 1)
                        {

                            other.AppendLine($@"
        [HttpGet(""Get{subtype.Name}s"")]
        public ActionResult Get{subtype.Name}s()
        {{
            return Ok(DC.Set<{subtype.Name}>().GetSelectListItems(Wtm, x => x.{item.SubField}));
        }}");
                        }
                    }
                }
                rv = rv.Replace("$other$", other.ToString());
                rv = GetRelatedNamespace(pros, rv);
            }
            return rv;
        }

        public string GenerateVM(string name)
        {
            var rv = GetResource($"{name}.txt").Replace("$modelnamespace$", ModelNS).Replace("$vmnamespace$", VMNs).Replace("$modelname$", ModelName).Replace("$area$", $"{Area ?? ""}").Replace("$classname$", $"{ModelName}{(IsApi == true ? "Api" : "")}");
            if (name == "Searcher" || name == "BatchVM")
            {
                string prostring = "";
                string initstr = "";
                Type modelType = Type.GetType(SelectedModel);
                List<FieldInfo> pros = null;
                if (name == "Searcher")
                {
                    pros = FieldInfos.Where(x => x.IsSearcherField == true).ToList();
                }
                if (name == "BatchVM")
                {
                    pros = FieldInfos.Where(x => x.IsBatchField == true).ToList();
                }
                foreach (var pro in pros)
                {
                    //对于一对一或者一对多的搜索和批量修改字段，需要在vm中生成对应的变量来获取关联表的数据
                    if (pro.InfoType != FieldInfoType.Normal)
                    {
                        var subtype = Type.GetType(pro.RelatedField);
                        if (typeof(TopBasePoco).IsAssignableFrom(subtype) == false || subtype == typeof(FileAttachment))
                        {
                            continue;
                        }
                        if (UI == UIEnum.LayUI && IsApi == false)
                        {
                            var fname = "All" + pro.FieldName + "s";
                            prostring += $@"
        public List<ComboSelectListItem> {fname} {{ get; set; }}";
                            initstr += $@"
            {fname} = DC.Set<{subtype.Name}>().GetSelectListItems(Wtm, y => y.{pro.SubField});";
                        }
                    }

                    //生成普通字段定义
                    var proType = modelType.GetSingleProperty(pro.FieldName);
                    var display = proType.GetCustomAttribute<DisplayAttribute>();
                    if (display != null)
                    {
                        prostring += $@"
        [Display(Name = ""{display.Name}"")]";
                    }
                    string typename = proType.PropertyType.Name;
                    string proname = pro.GetField(DC, modelType);

                    switch (pro.InfoType)
                    {
                        case FieldInfoType.Normal:
                            if (proType.PropertyType.IsNullable())
                            {
                                typename = proType.PropertyType.GetGenericArguments()[0].Name + "?";
                            }
                            else if (proType.PropertyType != typeof(string))
                            {
                                typename = proType.PropertyType.Name + "?";
                            }
                            break;
                        case FieldInfoType.One2Many:
                            typename = pro.GetFKType(DC, modelType);
                            if (typename != "string")
                            {
                                typename += "?";
                            }
                            break;
                        case FieldInfoType.Many2Many:
                            proname = $@"Selected{pro.FieldName}IDs";
                            typename = $"List<{pro.GetFKType(DC, modelType)}>";
                            break;
                        default:
                            break;
                    }
                    if ((typename == "DateTime" || typename == "DateTime?") && name == "Searcher")
                    {
                        typename = "DateRange";
                    }
                    prostring += $@"
        public {typename} {proname} {{ get; set; }}";
                }
                rv = rv.Replace("$pros$", prostring).Replace("$init$", initstr);
                rv = GetRelatedNamespace(pros, rv);
            }
            if (name == "ListVM")
            {
                string headerstring = "";
                string selectstring = "";
                string wherestring = "";
                string subprostring = "";
                string formatstring = "";
                string actionstring = "";

                if (UI == UIEnum.LayUI && IsApi == false)
                {
                    actionstring = $@"
        protected override List<GridAction> InitGridAction()
        {{
            return new List<GridAction>
            {{
                this.MakeStandardAction(""{ModelName}"", GridActionStandardTypesEnum.Create, Localizer[""Sys.Create""],""{Area ?? ""}"", dialogWidth: 800),
                this.MakeStandardAction(""{ModelName}"", GridActionStandardTypesEnum.Edit, Localizer[""Sys.Edit""], ""{Area ?? ""}"", dialogWidth: 800),
                this.MakeStandardAction(""{ModelName}"", GridActionStandardTypesEnum.Delete, Localizer[""Sys.Delete""], ""{Area ?? ""}"", dialogWidth: 800),
                this.MakeStandardAction(""{ModelName}"", GridActionStandardTypesEnum.Details, Localizer[""Sys.Details""], ""{Area ?? ""}"", dialogWidth: 800),
                this.MakeStandardAction(""{ModelName}"", GridActionStandardTypesEnum.BatchEdit, Localizer[""Sys.BatchEdit""], ""{Area ?? ""}"", dialogWidth: 800),
                this.MakeStandardAction(""{ModelName}"", GridActionStandardTypesEnum.BatchDelete, Localizer[""Sys.BatchDelete""], ""{Area ?? ""}"", dialogWidth: 800),
                this.MakeStandardAction(""{ModelName}"", GridActionStandardTypesEnum.Import, Localizer[""Sys.Import""], ""{Area ?? ""}"", dialogWidth: 800),
                this.MakeStandardAction(""{ModelName}"", GridActionStandardTypesEnum.ExportExcel, Localizer[""Sys.Export""], ""{Area ?? ""}""),
            }};
        }}
";
                }

                var pros = FieldInfos.Where(x => x.IsListField == true).ToList();
                Type modelType = Type.GetType(SelectedModel);
                List<PropertyInfo> existSubPro = new List<PropertyInfo>();
                foreach (var pro in pros)
                {
                    if (pro.InfoType == FieldInfoType.Normal)
                    {
                        // CG-08: read [ListColumn] attribute from model property.
                        var modelProp = modelType?.GetSingleProperty(pro.FieldName);
                        var listColAttr = modelProp?.GetCustomAttribute<ListColumnAttribute>();
                        string headerChain = BuildGridHeaderChain(pro.FieldName, modelProp, listColAttr);
                        headerstring += $@"
                this.MakeGridHeader(x => x.{pro.FieldName}){headerChain},";
                        if (pro.FieldName.ToLower() != "id")
                        {
                            selectstring += $@"
                    {pro.FieldName} = x.{pro.FieldName},";
                        }
                    }
                    else
                    {
                        var subtype = Type.GetType(pro.RelatedField);
                        if (subtype == typeof(FileAttachment))
                        {
                            var filefk = DC.GetFKName2(modelType, pro.FieldName);
                            var fileProp = modelType?.GetSingleProperty(filefk);
                            var fileListColAttr = fileProp?.GetCustomAttribute<ListColumnAttribute>();
                            string fileHeaderChain = BuildGridHeaderChain(filefk, fileProp, fileListColAttr);
                            headerstring += $@"
                this.MakeGridHeader(x => x.{filefk}).SetFormat({filefk}Format){fileHeaderChain},";
                            selectstring += $@"
                    {filefk} = x.{filefk},";
                            formatstring += GetResource("HeaderFormat.txt").Replace("$modelname$", ModelName).Replace("$field$", filefk).Replace("$classname$", $"{ModelName}{(IsApi == true ? "Api" : "")}");
                        }
                        else
                        {
                            var subpro = subtype.GetSingleProperty(pro.SubField);
                            existSubPro.Add(subpro);
                            string prefix = "";
                            int count = existSubPro.Where(x => x.Name == subpro.Name).Count();
                            if (count > 1)
                            {
                                prefix = count + "";
                            }
                            string subtypename = subpro.PropertyType.Name;
                            if (subpro.PropertyType.IsNullable())
                            {
                                subtypename = subpro.PropertyType.GetGenericArguments()[0].Name + "?";
                            }

                            var subdisplay = subpro.GetCustomAttribute<DisplayAttribute>();
                            // CG-08: FK lookup columns default to width 180 when no [ListColumn] is present.
                            var fkListColAttr = subpro.GetCustomAttribute<ListColumnAttribute>();
                            string fkHeaderChain = BuildGridHeaderChain(pro.SubField + "_view" + prefix, subpro, fkListColAttr, isFkLookup: true);
                            headerstring += $@"
                this.MakeGridHeader(x => x.{pro.SubField + "_view" + prefix}){fkHeaderChain},";
                            if (pro.InfoType == FieldInfoType.One2Many)
                            {
                                selectstring += $@"
                    {pro.SubField + "_view" + prefix} = x.{pro.FieldName}.{pro.SubField},";
                            }
                            else
                            {
                                var middleType = modelType.GetSingleProperty(pro.FieldName).PropertyType.GenericTypeArguments[0];
                                var middlename = DC.GetPropertyNameByFk(middleType, pro.SubIdField);
                                if (typeof(IPersistPoco).IsAssignableFrom(Type.GetType(pro.RelatedField)))
                                {
                                    selectstring += $@"
                    {pro.SubField + "_view" + prefix} = x.{pro.FieldName}.Where(y=>y.{middlename}.IsValid==true).Select(y=>y.{middlename}.{pro.SubField}).ToSepratedString(null,"",""), ";
                                }
                                else
                                {
                                    selectstring += $@"
                    {pro.SubField + "_view" + prefix} = x.{pro.FieldName}.Select(y=>y.{middlename}.{pro.SubField}).ToSepratedString(null,"",""), ";
                                }
                            }
                            if (subdisplay?.Name != null)
                            {
                                subprostring += $@"
        [Display(Name = ""{subdisplay.Name}"")]";
                            }
                            subprostring += $@"
        public {subtypename} {pro.SubField + "_view" + prefix} {{ get; set; }}";
                        }
                    }

                }
                var wherepros = FieldInfos.Where(x => x.IsSearcherField == true).ToList();
                foreach (var pro in wherepros)
                {
                    if (pro.SubField == "`file")
                    {
                        continue;
                    }
                    var proType = modelType.GetSingleProperty(pro.FieldName)?.PropertyType;

                    switch (pro.InfoType)
                    {
                        case FieldInfoType.Normal:
                            // CG-09: resolve search operator — explicit [SearchField] wins,
                            // then smart-name heuristic (opt-in), then type-based Auto rule.
                            var sfProp = modelType?.GetSingleProperty(pro.FieldName);
                            var sfAttr = sfProp?.GetCustomAttribute<SearchFieldAttribute>();
                            SearchOperator resolvedOp = sfAttr?.Operator ?? SearchOperator.Auto;

                            // Apply smart-name heuristic only when opted-in and operator is still Auto.
                            if (resolvedOp == SearchOperator.Auto && UseSmartSearchDefaults && proType == typeof(string))
                            {
                                var fieldName = pro.FieldName;
                                if (fieldName.EndsWith("Code", StringComparison.OrdinalIgnoreCase) ||
                                    fieldName.EndsWith("No", StringComparison.OrdinalIgnoreCase) ||
                                    fieldName.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
                                {
                                    resolvedOp = SearchOperator.Equal;
                                }
                            }

                            if (resolvedOp == SearchOperator.Contains ||
                                (resolvedOp == SearchOperator.Auto && proType == typeof(string)))
                            {
                                wherestring += $@"
                .CheckContain(Searcher.{pro.FieldName}, x=>x.{pro.FieldName})";
                            }
                            else if (resolvedOp == SearchOperator.Between ||
                                     (resolvedOp == SearchOperator.Auto && (proType == typeof(DateTime) || proType == typeof(DateTime?))))
                            {
                                wherestring += $@"
                .CheckBetween(Searcher.{pro.FieldName}?.GetStartTime(), Searcher.{pro.FieldName}?.GetEndTime(), x => x.{pro.FieldName}, includeMax: false)";
                            }
                            else
                            {
                                // Equal — explicit, smart-name, or Auto type-based default.
                                wherestring += $@"
                .CheckEqual(Searcher.{pro.FieldName}, x=>x.{pro.FieldName})";
                            }
                            break;
                        case FieldInfoType.One2Many:
                            var fk = DC.GetFKName2(modelType, pro.FieldName);
                            wherestring += $@"
                .CheckEqual(Searcher.{fk}, x=>x.{fk})";
                            break;
                        case FieldInfoType.Many2Many:
                            var subtype = Type.GetType(pro.RelatedField);
                            var fk2 = DC.GetFKName(modelType, pro.FieldName);
                            wherestring += $@"
                .CheckWhere(Searcher.Selected{pro.FieldName}IDs,x=>DC.Set<{proType.GetGenericArguments()[0].Name}>().Where(y=>Searcher.Selected{pro.FieldName}IDs.Contains(y.{pro.SubIdField})).Select(z=>z.{fk2}).Contains(x.ID))";
                            break;
                        default:
                            break;
                    }
                }
                rv = rv.Replace("$headers$", headerstring).Replace("$where$", wherestring).Replace("$select$", selectstring).Replace("$subpros$", subprostring).Replace("$format$", formatstring).Replace("$actions$", actionstring);
                if (EnableAnalysis)
                {
                    rv = rv.Replace("$analysisusing$", "\nusing WalkingTec.Mvvm.Core.Analysis;\n");
                    rv = rv.Replace("$analysisattr$", "[EnableAnalysis]\n    ");
                }
                else
                {
                    rv = rv.Replace("$analysisusing$", "");
                    rv = rv.Replace("$analysisattr$", "");
                }
                rv = GetRelatedNamespace(pros, rv);
            }
            if (name == "CrudVM")
            {
                string prostr = "";
                string initstr = "";
                string includestr = "";
                string addstr = "";
                string editstr = "";
                var pros = FieldInfos.Where(x => x.IsFormField == true && string.IsNullOrEmpty(x.RelatedField) == false).ToList();
                foreach (var pro in pros)
                {
                    var subtype = Type.GetType(pro.RelatedField);
                    if (typeof(TopBasePoco).IsAssignableFrom(subtype) == false || subtype == typeof(FileAttachment))
                    {
                        continue;
                    }
                    var fname = "All" + pro.FieldName + "s";
                    if (UI == UIEnum.LayUI)
                    {
                        prostr += $@"
        public List<ComboSelectListItem> {fname} {{ get; set; }}";
                        initstr += $@"
            {fname} = DC.Set<{subtype.Name}>().GetSelectListItems(Wtm, y => y.{pro.SubField});";
                    }
                    includestr += $@"
            SetInclude(x => x.{pro.FieldName});";

                    if (pro.InfoType == FieldInfoType.Many2Many)
                    {
                        Type modelType = Type.GetType(SelectedModel);
                        var protype = modelType.GetSingleProperty(pro.FieldName);
                        prostr += $@"
        [Display(Name = ""{protype.GetPropertyDisplayName()}"")]
        public List<string> Selected{pro.FieldName}IDs {{ get; set; }}";
                        initstr += $@"
            Selected{pro.FieldName}IDs = Entity.{pro.FieldName}?.Select(x => x.{pro.SubIdField}.ToString()).ToList();";
                        addstr += $@"
            Entity.{pro.FieldName} = new List<{protype.PropertyType.GetGenericArguments()[0].Name}>();
            if (Selected{pro.FieldName}IDs != null)
            {{
                foreach (var id in Selected{pro.FieldName}IDs)
                {{
                     {protype.PropertyType.GetGenericArguments()[0].Name} middle = new {protype.PropertyType.GetGenericArguments()[0].Name}();
                    middle.SetPropertyValue(""{pro.SubIdField}"", id);
                    Entity.{pro.FieldName}.Add(middle);
                }}
            }}
";
                        editstr += $@"
            Entity.{pro.FieldName} = new List<{protype.PropertyType.GetGenericArguments()[0].Name}>();
            if(Selected{pro.FieldName}IDs != null )
            {{
                 foreach (var item in Selected{pro.FieldName}IDs)
                {{
                    {protype.PropertyType.GetGenericArguments()[0].Name} middle = new {protype.PropertyType.GetGenericArguments()[0].Name}();
                    middle.SetPropertyValue(""{pro.SubIdField}"", item);
                    Entity.{pro.FieldName}.Add(middle);
                }}
            }}
";
                    }
                }
                if ((UI == UIEnum.LayUI && IsApi == false) || UI == UIEnum.Blazor)
                {
                    rv = rv.Replace("$pros$", prostr).Replace("$init$", initstr).Replace("$include$", includestr).Replace("$add$", addstr).Replace("$edit$", editstr);
                }
                else
                {
                    rv = rv.Replace("$pros$", "").Replace("$init$", "").Replace("$include$", includestr).Replace("$add$", "").Replace("$edit$", "");
                }
                rv = GetRelatedNamespace(pros, rv);
            }
            if (name == "ImportVM")
            {
                string prostring = "";
                string initstr = "";
                Type modelType = Type.GetType(SelectedModel);
                List<FieldInfo> pros = FieldInfos.Where(x => x.IsImportField == true).ToList();
                foreach (var pro in pros)
                {
                    if (pro.InfoType == FieldInfoType.Many2Many)
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(pro.RelatedField) == false)
                    {
                        var subtype = Type.GetType(pro.RelatedField);
                        if (typeof(TopBasePoco).IsAssignableFrom(subtype) == false || subtype == typeof(FileAttachment))
                        {
                            continue;
                        }
                        initstr += $@"
            {pro.FieldName + "_Excel"}.DataType = ColumnDataType.ComboBox;
            {pro.FieldName + "_Excel"}.ListItems = DC.Set<{subtype.Name}>().GetSelectListItems(Wtm, y => y.{pro.SubField});";
                    }
                    var proType = modelType.GetSingleProperty(pro.FieldName);
                    var display = proType.GetCustomAttribute<DisplayAttribute>();
                    var filefk = DC.GetFKName2(modelType, pro.FieldName);
                    if (display != null)
                    {
                        prostring += $@"
        [Display(Name = ""{display.Name}"")]";
                    }
                    if (string.IsNullOrEmpty(pro.RelatedField) == false)
                    {
                        prostring += $@"
        public ExcelPropety {pro.FieldName + "_Excel"} = ExcelPropety.CreateProperty<{ModelName}>(x => x.{filefk});";
                    }
                    else
                    {
                        prostring += $@"
        public ExcelPropety {pro.FieldName + "_Excel"} = ExcelPropety.CreateProperty<{ModelName}>(x => x.{pro.FieldName});";
                    }

                    // CG-07: carry [ImportConfig] overrides into generated InitVM post-assignment.
                    var importCfg = proType.GetCustomAttribute<ImportConfigAttribute>();
                    string excelVarName = pro.FieldName + "_Excel";
                    if (importCfg != null)
                    {
                        // RequiredOnImport → flip IsNullAble to false (not-nullable = required).
                        if (importCfg.RequiredOnImport)
                        {
                            initstr += $@"
            {excelVarName}.IsNullAble = false;";
                        }
                        // ColumnHeader override.
                        if (!string.IsNullOrEmpty(importCfg.ColumnHeader))
                        {
                            initstr += $@"
            {excelVarName}.ColumnName = ""{importCfg.ColumnHeader}"";";
                        }
                        // DataType override (only when explicitly set, not the default Dynamic).
                        if (importCfg.DataType != ColumnDataType.Dynamic)
                        {
                            initstr += $@"
            {excelVarName}.DataType = ColumnDataType.{importCfg.DataType};";
                        }
                    }
                    // CG-07: auto-carry model validation constraints ([Required] / [StringLength] / [RegularExpression]).
                    // These mirror what ExcelPropety.CreateProperty already reads from the expression tree,
                    // so we emit explicit overrides only when the attribute is present on the model property.
                    var reqAttr = proType.GetCustomAttribute<System.ComponentModel.DataAnnotations.RequiredAttribute>();
                    if (reqAttr != null && importCfg?.RequiredOnImport != true)
                    {
                        // Only set if ImportConfig didn't already set it.
                        initstr += $@"
            {excelVarName}.IsNullAble = false;";
                    }
                    var slAttr = proType.GetCustomAttribute<StringLengthAttribute>();
                    if (slAttr != null)
                    {
                        if (slAttr.MaximumLength > 0)
                        {
                            initstr += $@"
            {excelVarName}.MaxValuseOrLength = ""{slAttr.MaximumLength}"";";
                        }
                        if (slAttr.MinimumLength > 0)
                        {
                            initstr += $@"
            {excelVarName}.MinValueOrLength = ""{slAttr.MinimumLength}"";";
                        }
                    }
                }
                rv = rv.Replace("$pros$", prostring).Replace("$init$", initstr);
                rv = GetRelatedNamespace(pros, rv);

            }
            return rv;
        }

        public string GetResource(string fileName, string subdir = "")
        {
            //获取编译在程序中的Controller原始代码文本
            Assembly assembly = Assembly.GetExecutingAssembly();
            string loc = "";
            if (string.IsNullOrEmpty(subdir))
            {
                loc = $"WalkingTec.Mvvm.Mvc.GeneratorFiles.{fileName}";
            }
            else
            {
                loc = $"WalkingTec.Mvvm.Mvc.GeneratorFiles.{subdir}.{fileName}";
            }
            var textStreamReader = new StreamReader(assembly.GetManifestResourceStream(loc));
            string content = textStreamReader.ReadToEnd();
            textStreamReader.Close();
            return content;
        }

        private string GetRelatedNamespace(List<FieldInfo> pros, string s)
        {
            string otherns = @"";
            Type modelType = Type.GetType(SelectedModel);
            foreach (var pro in pros)
            {
                Type proType = null;

                if (string.IsNullOrEmpty(pro.RelatedField))
                {
                    proType = modelType.GetSingleProperty(pro.FieldName)?.PropertyType;
                }
                else
                {
                    proType = Type.GetType(pro.RelatedField);
                }
                string prons = proType.Namespace;
                if (proType.IsNullable())
                {
                    prons = proType.GetGenericArguments()[0].Namespace;
                }
                if (s.Contains($"using {prons};") == false && otherns.Contains($"using {prons};") == false)
                {
                    otherns += $@"using {prons};
";
                }

            }

            return s.Replace("$othernamespace$", otherns);
        }

    }

    public enum FieldInfoType { Normal, One2Many, Many2Many }

    public class FieldInfo
    {
        public string FieldName { get; set; }
        public string RelatedField { get; set; }

        public bool IsSearcherField { get; set; }

        public bool IsListField { get; set; }

        public bool IsFormField { get; set; }

        public bool IsImportField { get; set; }
        public bool IsBatchField { get; set; }
        public bool IsDimensionField { get; set; }
        public bool IsMeasureField { get; set; }

        public FieldInfoType InfoType
        {
            get
            {
                if (string.IsNullOrEmpty(RelatedField))
                {
                    return FieldInfoType.Normal;
                }
                else
                {
                    if (string.IsNullOrEmpty(SubIdField))
                    {
                        return FieldInfoType.One2Many;
                    }
                    else
                    {
                        return FieldInfoType.Many2Many;
                    }
                }
            }
        }

        /// <summary>
        /// 字段关联的类名
        /// </summary>
        public string SubField { get; set; }
        /// <summary>
        /// 多对多关系时，记录中间表关联到主表的字段名称
        /// </summary>
        public string SubIdField { get; set; }

        public string GetField(IDataContext DC, Type modelType)
        {
            if (this.InfoType == FieldInfoType.One2Many)
            {
                var fk = DC.GetFKName2(modelType, this.FieldName);
                return fk;
            }
            else
            {
                return this.FieldName;
            }
        }

        public string GetFKType(IDataContext DC, Type modelType)
        {
            Type fktype = null;
            if (this.InfoType == FieldInfoType.One2Many)
            {
                var fk = this.GetField(DC, modelType);
                fktype = modelType.GetSingleProperty(fk)?.PropertyType;
            }
            if (this.InfoType == FieldInfoType.Many2Many)
            {
                var middletype = modelType.GetSingleProperty(this.FieldName)?.PropertyType;
                fktype = middletype.GetGenericArguments()[0].GetSingleProperty(this.SubIdField)?.PropertyType;
            }
            var typename = "string";

            if (fktype == typeof(short) || fktype == typeof(short?))
            {
                typename = "short";
            }
            if (fktype == typeof(int) || fktype == typeof(int?))
            {
                typename = "int";
            }
            if (fktype == typeof(long) || fktype == typeof(long?))
            {
                typename = "long";
            }
            if (fktype == typeof(Guid) || fktype == typeof(Guid?))
            {
                typename = "Guid";
            }

            return typename;

        }
    }
}
