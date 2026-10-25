using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>
    /// Every i18n key that the Vue3Demo (server and SPA) looks up has a text in English and Simplified Chinese (#355).
    /// A key without a text is shown as the key itself (for example "_Admin.GroupManager" as a column title).
    /// The test reads the sources of the repository, so it needs no running site.
    /// </summary>
    [TestClass]
    public class Vue3DemoI18nKeysTest
    {
        private static readonly string Root = FindRepoRoot();
        private static readonly string DemoDir = Path.Combine(Root, "demo", "WalkingTec.Mvvm.Vue3Demo");
        private static readonly string ClientApp = Path.Combine(DemoDir, "ClientApp");

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WalkingTec.Mvvm.sln")))
            {
                dir = dir.Parent;
            }
            Assert.IsNotNull(dir, "WalkingTec.Mvvm.sln was not found above " + AppContext.BaseDirectory);
            return dir.FullName;
        }

        private static IEnumerable<string> Files(string dir, params string[] extensions)
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name == "bin" || name == "obj" || name == "node_modules" || name == "ClientApp")
                {
                    continue;
                }
                foreach (var file in Files(sub, extensions))
                {
                    yield return file;
                }
            }
        }

        private static Dictionary<string, string> Resx(string language)
        {
            var doc = XDocument.Load(Path.Combine(DemoDir, "Resources", "Program." + language + ".resx"));
            return doc.Root.Elements("data").ToDictionary(x => (string)x.Attribute("name"), x => (string)x.Element("value") ?? string.Empty);
        }

        // ---- server: Display(Name), ErrorMessage, Localizer["..."], ActionDescription("...") and the like ----

        private static readonly Regex ServerKey = new Regex(
            "\"((?:Sys|_Admin|Codegen|CodeGen|Login|MenuKey|Validate|Reg)\\.[A-Za-z0-9_{}.\\-]+)\"", RegexOptions.Compiled);

        /// <summary>The keys of the resource file that the code of the Vue3Demo, Core and Mvc looks up, with their first location.</summary>
        private static SortedDictionary<string, string> ServerKeysInCode()
        {
            var keys = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var dirs = new[]
            {
                DemoDir,
                Path.Combine(Root, "src", "WalkingTec.Mvvm.Core"),
                Path.Combine(Root, "src", "WalkingTec.Mvvm.Mvc"),
            };
            foreach (var file in dirs.SelectMany(d => Files(d, ".cs")))
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (Match m in ServerKey.Matches(lines[i]))
                    {
                        if (!keys.ContainsKey(m.Groups[1].Value))
                        {
                            keys[m.Groups[1].Value] = Path.GetRelativePath(Root, file) + ":" + (i + 1);
                        }
                    }
                }
            }
            return keys;
        }

        [TestMethod]
        public void ServerKeysInCode_AreFound()
        {
            // guards the scan itself: a path or pattern change must not turn the checks below into no-ops
            var keys = ServerKeysInCode();
            Assert.IsTrue(keys.Count > 100, "found only " + keys.Count + " keys");
            foreach (var key in new[] { "Login.Password", "_Admin.GroupManager", "_Admin.HasMainHost", "_Admin.Job", "_Admin.WorkflowApi", "_Admin.Children", "_Admin.FileName" })
            {
                Assert.IsTrue(keys.ContainsKey(key), key + " is expected to be used by the code");
            }
        }

        [TestMethod]
        [DataRow("en")]
        [DataRow("zh")]
        public void ServerKeysInCode_HaveATextInTheResourceFile(string language)
        {
            var resx = Resx(language);
            var missing = ServerKeysInCode()
                .Where(k => !resx.TryGetValue(k.Key, out var v) || string.IsNullOrWhiteSpace(v))
                .Select(k => k.Key + " (" + k.Value + ")")
                .ToList();
            Assert.AreEqual(0, missing.Count, "Program." + language + ".resx has no text for: " + string.Join(", ", missing));
        }

        [TestMethod]
        [DataRow("en")]
        [DataRow("zh")]
        public void ResourceFile_HasNoNamesThatDifferOnlyInCase(string language)
        {
            // resgen treats the names case-insensitively: of two such entries one is dropped at build time (warning MSB3568)
            var path = Path.Combine(DemoDir, "Resources", "Program." + language + ".resx");
            var duplicates = XDocument.Load(path).Root.Elements("data")
                .Select(x => (string)x.Attribute("name"))
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => string.Join(" / ", g))
                .ToList();
            Assert.AreEqual(0, duplicates.Count, "duplicate names in Program." + language + ".resx: " + string.Join(", ", duplicates));
        }

        [TestMethod]
        public void ServerKeys_ReportedInIssue355_HaveReadableTexts()
        {
            var en = Resx("en");
            var zh = Resx("zh");
            var expected = new Dictionary<string, (string En, string Zh)>
            {
                ["Login.Password"] = ("Password", "密码"),
                ["_Admin.Children"] = ("Children", "子项"),
                ["_Admin.FileName"] = ("File name", "文件名"),
                ["_Admin.GroupManager"] = ("Group Manager", "部门主管"),
                ["_Admin.Job"] = ("Job", "作业"),
                ["_Admin.WorkflowApi"] = ("Workflow Api", "工作流Api"),
            };
            foreach (var item in expected)
            {
                Assert.AreEqual(item.Value.En, en[item.Key], item.Key + " (en)");
                Assert.AreEqual(item.Value.Zh, zh[item.Key], item.Key + " (zh)");
            }
            StringAssert.Contains(en["_Admin.HasMainHost"], "main site");
            StringAssert.Contains(zh["_Admin.HasMainHost"], "主站");
        }

        // ---- SPA: $t('message...') and the titles in <script name="message..."> ----

        private static readonly Regex SpaKey = new Regex(
            @"(?:\$t|\bt|global\.t)\(\s*(['""`])(message\.[A-Za-z0-9_.\-]+)\1", RegexOptions.Compiled);

        private static readonly Regex SpaPageTitle = new Regex(
            @"<script[^>]*?\bname=""(message\.[A-Za-z0-9_.\-]+)[;""]", RegexOptions.Compiled);

        private static SortedDictionary<string, string> SpaKeysInCode()
        {
            var keys = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var src = Path.Combine(ClientApp, "src");
            var i18n = Path.Combine(src, "i18n") + Path.DirectorySeparatorChar;
            foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".vue") || f.EndsWith(".ts")) && !f.StartsWith(i18n)))
            {
                var text = File.ReadAllText(file);
                var found = SpaKey.Matches(text).Select(m => m.Groups[2].Value)
                    .Concat(SpaPageTitle.Matches(text).Select(m => m.Groups[1].Value));
                foreach (var key in found)
                {
                    if (!keys.ContainsKey(key))
                    {
                        keys[key] = Path.GetRelativePath(ClientApp, file);
                    }
                }
            }
            return keys;
        }

        /// <summary>The messages of one locale: all files lang/{locale}.ts and pages/**/{locale}.ts merged, as the SPA does.</summary>
        private static Dictionary<string, string> SpaMessages(string locale)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var dir = Path.Combine(ClientApp, "src", "i18n");
            foreach (var file in Directory.EnumerateFiles(dir, locale + ".ts", SearchOption.AllDirectories))
            {
                foreach (var item in TsObject.Parse(File.ReadAllText(file), file))
                {
                    result.TryAdd("message." + item.Key, item.Value);
                }
            }
            return result;
        }

        [TestMethod]
        public void SpaKeysInCode_AreFound()
        {
            var keys = SpaKeysInCode();
            Assert.IsTrue(keys.Count > 100, "found only " + keys.Count + " keys");
            Assert.IsTrue(keys.ContainsKey("message._system.common.vm.add"));
            Assert.IsTrue(SpaMessages("en").Count > 100, "the English locale files were not read");
        }

        [TestMethod]
        [DataRow("en")]
        [DataRow("zh-cn")]
        public void SpaKeysInCode_HaveATextInTheLocale(string locale)
        {
            var messages = SpaMessages(locale);
            var missing = SpaKeysInCode()
                .Where(k => !messages.TryGetValue(k.Key, out var v) || string.IsNullOrWhiteSpace(v))
                .Select(k => k.Key + " (" + k.Value + ")")
                .ToList();
            Assert.AreEqual(0, missing.Count, "the " + locale + " locale files have no text for: " + string.Join(", ", missing));
        }

        [TestMethod]
        public void SpaPageTitles_UseTheSemicolonSeparatorOfTheMenuBuild()
        {
            // wtmbuild.ts splits the name of the script at ';'; a ',' ends up in the title ("...vm.edit,false") and the title has no text
            var wrong = Directory.EnumerateFiles(Path.Combine(ClientApp, "src", "views"), "*.vue", SearchOption.AllDirectories)
                .Select(f => (File: f, Match: Regex.Match(File.ReadAllText(f), @"<script[^>]*?\bname=""([^""]*)""")))
                .Where(x => x.Match.Success && x.Match.Groups[1].Value.Contains(',') && x.Match.Groups[1].Value.StartsWith("message."))
                .Select(x => Path.GetRelativePath(ClientApp, x.File))
                .ToList();
            Assert.AreEqual(0, wrong.Count, "script name with ',' in: " + string.Join(", ", wrong));
        }

        /// <summary>Reads the object literal of a locale file (export default { a: { b: 'text' } }) into dotted keys.</summary>
        private sealed class TsObject
        {
            private readonly string _s;
            private readonly string _file;
            private int _i;

            private TsObject(string s, string file)
            {
                _s = s;
                _file = file;
            }

            public static Dictionary<string, string> Parse(string text, string file)
            {
                var p = new TsObject(text, file);
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                var start = text.IndexOf("export default", StringComparison.Ordinal);
                Assert.IsTrue(start >= 0, file + " has no 'export default'");
                p._i = start + "export default".Length;
                p.SkipSpace();
                p.Expect('{');
                p.ReadBody(string.Empty, result);
                return result;
            }

            private void ReadBody(string prefix, Dictionary<string, string> result)
            {
                while (true)
                {
                    SkipSpace();
                    if (_i >= _s.Length)
                    {
                        Assert.Fail(_file + ": unexpected end of file");
                    }
                    if (_s[_i] == '}')
                    {
                        _i++;
                        return;
                    }
                    if (_s[_i] == ',')
                    {
                        _i++;
                        continue;
                    }
                    var key = ReadKey();
                    SkipSpace();
                    Expect(':');
                    SkipSpace();
                    var path = prefix.Length == 0 ? key : prefix + "." + key;
                    if (_s[_i] == '{')
                    {
                        _i++;
                        ReadBody(path, result);
                    }
                    else if (_s[_i] == '\'' || _s[_i] == '"' || _s[_i] == '`')
                    {
                        result[path] = ReadString();
                    }
                    else
                    {
                        // number, boolean: not a text
                        var begin = _i;
                        while (_i < _s.Length && _s[_i] != ',' && _s[_i] != '}' && _s[_i] != '\n')
                        {
                            _i++;
                        }
                        result[path] = _s.Substring(begin, _i - begin).Trim();
                    }
                }
            }

            private string ReadKey()
            {
                if (_s[_i] == '\'' || _s[_i] == '"')
                {
                    return ReadString();
                }
                var begin = _i;
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '$' || _s[_i] == '-'))
                {
                    _i++;
                }
                Assert.IsTrue(_i > begin, _file + ": key expected at offset " + begin + " near '" + _s.Substring(begin, Math.Min(30, _s.Length - begin)) + "'");
                return _s.Substring(begin, _i - begin);
            }

            private string ReadString()
            {
                var quote = _s[_i++];
                var sb = new StringBuilder();
                while (_i < _s.Length && _s[_i] != quote)
                {
                    if (_s[_i] == '\\' && _i + 1 < _s.Length)
                    {
                        _i++;
                    }
                    sb.Append(_s[_i++]);
                }
                _i++;
                return sb.ToString();
            }

            private void Expect(char c)
            {
                Assert.IsTrue(_i < _s.Length && _s[_i] == c, _file + ": '" + c + "' expected at offset " + _i + " near '" + _s.Substring(Math.Min(_i, _s.Length), Math.Min(30, Math.Max(0, _s.Length - _i))) + "'");
                _i++;
            }

            private void SkipSpace()
            {
                while (_i < _s.Length)
                {
                    if (char.IsWhiteSpace(_s[_i]))
                    {
                        _i++;
                    }
                    else if (_s[_i] == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/')
                    {
                        while (_i < _s.Length && _s[_i] != '\n')
                        {
                            _i++;
                        }
                    }
                    else if (_s[_i] == '/' && _i + 1 < _s.Length && _s[_i + 1] == '*')
                    {
                        var end = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                        _i = end < 0 ? _s.Length : end + 2;
                    }
                    else
                    {
                        return;
                    }
                }
            }
        }
    }
}
