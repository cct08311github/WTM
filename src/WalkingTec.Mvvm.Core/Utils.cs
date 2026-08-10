#nullable enable
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using NPOI.HSSF.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Support;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core
{
    public class Utils
    {
        // Lock objects for thread-safe lazy initialisation of the three static caches.
        // Using separate locks avoids unnecessary contention between unrelated fields.
        private static readonly object _allAssembliesLock = new();
        private static readonly object _allModelsLock = new();
        private static readonly object _allVMsLock = new();

        private static List<Assembly>? _allAssemblies;
        private static List<Type>? _allModels;
        public static string GetCurrentComma()
        {
            if (CultureInfo.CurrentUICulture.Name == "zh-cn")
            {
                return "：";
            }
            else
            {
                return ":";
            }
        }

        public static List<Assembly> GetAllAssembly()
        {
            // Double-checked lock: avoids the observable empty-list window that existed when
            // _allAssemblies was assigned to [] before being populated via AddRange.
            if (_allAssemblies == null)
            {
                lock (_allAssembliesLock)
                {
                    if (_allAssemblies == null)
                    {
                        string? path = null;
                        string? singlefile = null;
                        try
                        {
                            path = Assembly.GetEntryAssembly()?.Location;
                        }
                        catch (Exception ex)
                        {
                            CoreProgram.GetLogger("Utils")?.LogDebug(ex, "GetEntryAssembly().Location failed (single-file publish?); falling back to process main module");
                        }
                        if (string.IsNullOrEmpty(path))
                        {
                            singlefile = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                            path = Path.GetDirectoryName(singlefile);
                        }
                        path ??= AppContext.BaseDirectory;
                        var dirPath = Path.GetDirectoryName(path) ?? path;
                        var dir = new DirectoryInfo(dirPath);

                        var dlls = dir.GetFiles("*.dll", SearchOption.TopDirectoryOnly);
                        string[] systemdll = new string[]
                        {
                        "Microsoft.",
                        "System.",
                        "Swashbuckle.",
                        "ICSharpCode",
                        "Newtonsoft.",
                        "Oracle.",
                        "MySql.",
                        "SQLitePCLRaw.",
                        "BouncyCastle.",
                        "FreeSql.",
                        "Google.Protobuf.dll",
                        "Humanizer.dll",
                        "IdleBus.dll",
                        "K4os.",
                        "MySql.Data.",
                        "Npgsql.",
                        "NPOI.",
                        "netstandard",
                        "VueCliMiddleware"
                        };

                        var filtered = dlls.Where(x => systemdll.Any(y => x.Name.StartsWith(y)) == false);
                        foreach (var dll in filtered)
                        {
                            try
                            {
                                AssemblyLoadContext.Default.LoadFromAssemblyPath(dll.FullName);
                            }
                            catch (Exception ex)
                            {
                                CoreProgram.GetLogger("Utils")?.LogDebug(ex, "LoadFromAssemblyPath failed for '{Dll}'; skipping", dll.FullName);
                            }
                        }
                        List<Assembly> dlllist = [.. AssemblyLoadContext.Default.Assemblies.Where(x => systemdll.All(y => !(x.FullName ?? string.Empty).StartsWith(y)))];
                        // Assign fully-populated list atomically so no thread sees an empty intermediate state.
                        _allAssemblies = dlllist;
                    }
                }
            }
            return _allAssemblies;
        }

        public static List<Type> GetAllModels()
        {
            // Double-checked lock for thread-safe lazy initialisation.
            if (_allModels == null)
            {
                lock (_allModelsLock)
                {
                    if (_allModels == null)
                    {
                        var modelAsms = Utils.GetAllAssembly();
                        List<Type> allTypes = [];// 所有 DbSet<> 的泛型类型
                                                        // 获取所有 DbSet<T> 的泛型类型 T
                        foreach (var asm in modelAsms)
                        {
                            try
                            {
                                List<Type> dcModule = [.. asm.GetExportedTypes().Where(x => typeof(DbContext).IsAssignableFrom(x))];
                                if (dcModule != null && dcModule.Count > 0)
                                {
                                    foreach (var module in dcModule)
                                    {
                                        foreach (var pro in module.GetProperties())
                                        {
                                            if (pro.PropertyType.IsGeneric(typeof(DbSet<>)))
                                            {
                                                if (!allTypes.Contains(pro.PropertyType.GenericTypeArguments[0], new TypeComparer()))
                                                {
                                                    allTypes.Add(pro.PropertyType.GenericTypeArguments[0]);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                CoreProgram.GetLogger("Utils")?.LogDebug(ex, "GetAllModels: scanning assembly '{Asm}' threw; skipping", asm.FullName);
                            }
                        }
                        _allModels = allTypes;
                    }
                }
            }
            return _allModels;
        }

        private static List<Type>? _allVMs;
        public static List<Type> GetAllVms()
        {
            // Double-checked lock for thread-safe lazy initialisation.
            if (_allVMs == null)
            {
                lock (_allVMsLock)
                {
                    if (_allVMs == null)
                    {
                        var modelAsms = Utils.GetAllAssembly();
                        List<Type> allTypes = [];// 所有 DbSet<> 的泛型类型
                                                        // 获取所有 DbSet<T> 的泛型类型 T
                        foreach (var asm in modelAsms)
                        {
                            try
                            {
                                List<Type> dcModule = [.. asm.GetExportedTypes().Where(x => typeof(BaseVM).IsAssignableFrom(x))];
                                allTypes.AddRange(dcModule);
                            }
                            catch (Exception ex)
                            {
                                CoreProgram.GetLogger("Utils")?.LogDebug(ex, "GetAllVms: scanning assembly '{Asm}' threw; skipping", asm.FullName);
                            }
                        }
                        _allVMs = allTypes;
                    }
                }
            }
            return _allVMs;

        }

        public static SimpleMenu? FindMenu(string? url, List<SimpleMenu>? menus)
        {
            if (url == null)
            {
                return null;
            }
            url = url.ToLower();
            if (menus == null)
            {
                return null;
            }
            //寻找菜单中是否有与当前判断的url完全相同的
            var menu = menus.Where(x => x.Url != null && x.Url.ToLower() == url).FirstOrDefault();

            //如果没有，抹掉当前url的参数，用不带参数的url比对
            if (menu == null)
            {
                var pos = url.IndexOf("?");
                if (pos > 0)
                {
                    url = url.Substring(0, pos);
                    menu = menus.Where(x => x.Url != null && (x.Url.ToLower() == url || x.Url.ToLower() + "async" == url)).FirstOrDefault();
                }
            }

            //如果还没找到，则判断url是否为/controller/action/id这种格式，如果是则抹掉/id之后再对比
            if (menu == null && url.EndsWith("/index"))
            {
                url = url.Substring(0, url.Length - 6);
                menu = menus.Where(x => x.Url != null && x.Url.ToLower() == url).FirstOrDefault();
            }
            if (menu == null && url.EndsWith("/indexasync"))
            {
                url = url.Substring(0, url.Length - 11);
                menu = menus.Where(x => x.Url != null && x.Url.ToLower() == url).FirstOrDefault();
            }
            return menu;
        }


        public static string GetIdByName(string fieldName)
        {
            return fieldName == null ? "" : fieldName.Replace(".", "_").Replace("[", "_").Replace("]", "_").Replace("-","minus");
        }

        public static void CheckDifference<T>(IEnumerable<T> oldList, IEnumerable<T> newList, out IEnumerable<T> ToRemove, out IEnumerable<T> ToAdd) where T : TopBasePoco
        {
            List<T> tempToRemove = [];
            List<T> tempToAdd = [];
            oldList = oldList ?? [];
            newList = newList ?? [];
            foreach (var oldItem in oldList)
            {
                bool exist = false;
                foreach (var newItem in newList)
                {
                    if (oldItem.GetID().ToString() == newItem.GetID().ToString())
                    {
                        exist = true;
                        break;
                    }
                }
                if (exist == false)
                {
                    tempToRemove.Add(oldItem);
                }
            }
            foreach (var newItem in newList)
            {
                bool exist = false;
                foreach (var oldItem in oldList)
                {
                    if (newItem.GetID().ToString() == oldItem.GetID().ToString())
                    {
                        exist = true;
                        break;
                    }
                }
                if (exist == false)
                {
                    tempToAdd.Add(newItem);
                }
            }
            ToRemove = tempToRemove.AsEnumerable();
            ToAdd = tempToAdd.AsEnumerable();
        }

        public static short GetExcelColor(string color)
        {
            List<Type> colors = [.. typeof(HSSFColor).GetNestedTypes()];
            foreach (var col in colors)
            {
                var pro = col.GetField("hexString");
                if (pro == null)
                {
                    continue;
                }
                var hex = pro.GetValue(null) as string;
                if (string.IsNullOrEmpty(hex))
                {
                    continue;
                }
                var rgb = hex.Split(':');
                for (int i = 0; i < rgb.Length; i++)
                {
                    if (rgb[i].Length > 2)
                    {
                        rgb[i] = rgb[i].Substring(0, 2);
                    }
                }
                int r = Convert.ToInt16(rgb[0], 16);
                int g = Convert.ToInt16(rgb[1], 16);
                int b = Convert.ToInt16(rgb[2], 16);

                if (color.Length == 8)
                {
                    color = color.Substring(2);
                }
                string c1 = color.Substring(0, 2);
                string c2 = color.Substring(2, 2);
                string c3 = color.Substring(4, 2);

                int r1 = Convert.ToInt16(c1, 16);
                int g1 = Convert.ToInt16(c2, 16);
                int b1 = Convert.ToInt16(c3, 16);


                if (r == r1 && g == g1 && b == b1)
                {
                    var indexField = col.GetField("index");
                    var indexValue = indexField?.GetValue(null);
                    if (indexValue is short shortIndex)
                    {
                        return shortIndex;
                    }
                }
            }
            return HSSFColor.COLOR_NORMAL;
        }

        /// <summary>
        /// 获取Bool类型的下拉框
        /// </summary>
        /// <param name="boolType"></param>
        /// <param name="defaultValue"></param>
        /// <param name="trueText"></param>
        /// <param name="falseText"></param>
        /// <param name="selectText"></param>
        /// <returns></returns>
        public static List<ComboSelectListItem> GetBoolCombo(BoolComboTypes boolType, bool? defaultValue = null, string? trueText = null, string? falseText = null, string? selectText = null)
        {
            List<ComboSelectListItem> rv = [];
            string yesText = "";
            string noText = "";
            switch (boolType)
            {
                case BoolComboTypes.YesNo:
                    yesText = CoreProgram._localizer != null ? (string)CoreProgram._localizer["Sys.Yes"] : "";
                    noText = CoreProgram._localizer != null ? (string)CoreProgram._localizer["Sys.No"] : "";
                    break;
                case BoolComboTypes.ValidInvalid:
                    yesText = CoreProgram._localizer != null ? (string)CoreProgram._localizer["Sys.Valid"] : "";
                    noText = CoreProgram._localizer != null ? (string)CoreProgram._localizer["Sys.Invalid"] : "";
                    break;
                case BoolComboTypes.MaleFemale:
                    yesText = CoreProgram._localizer != null ? (string)CoreProgram._localizer["Sys.Male"] : "";
                    noText = CoreProgram._localizer != null ? (string)CoreProgram._localizer["Sys.Female"] : "";
                    break;
                case BoolComboTypes.HaveNotHave:
                    yesText = CoreProgram._localizer != null ? (string)CoreProgram._localizer["Sys.Have"] : "";
                    noText = CoreProgram._localizer != null ? (string)CoreProgram._localizer["Sys.NotHave"] : "";
                    break;
                case BoolComboTypes.Custom:
                    yesText = trueText ?? (CoreProgram._localizer != null ? (string)CoreProgram._localizer["Sys.Yes"] : "");
                    noText = falseText ?? (CoreProgram._localizer != null ? (string)CoreProgram._localizer["Sys.No"] : "");
                    break;
                default:
                    break;
            }
            ComboSelectListItem yesItem = new ComboSelectListItem()
            {
                Text = yesText,
                Value = "true"
            };
            if (defaultValue == true)
            {
                yesItem.Selected = true;
            }
            ComboSelectListItem noItem = new ComboSelectListItem()
            {
                Text = noText,
                Value = "false"
            };
            if (defaultValue == false)
            {
                noItem.Selected = true;
            }
            if(selectText != null)
            {
                rv.Add(new ComboSelectListItem { Text = selectText, Value = "" });
            }
            rv.Add(yesItem);
            rv.Add(noItem);
            return rv;
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static string ZipAndBase64Encode(string input)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(input);
            MemoryStream inputms = new MemoryStream(buffer);
            MemoryStream outputms = new MemoryStream();
            using (GZipStream zip = new GZipStream(outputms, CompressionMode.Compress))
            {
                inputms.CopyTo(zip);
            }
            byte[] rv = outputms.ToArray();
            inputms.Dispose();
            return Convert.ToBase64String(rv);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static string UnZipAndBase64Decode(string input)
        {
            byte[] inputstr = Convert.FromBase64String(input);
            MemoryStream inputms = new MemoryStream(inputstr);
            MemoryStream outputms = new MemoryStream();
            using (GZipStream zip = new GZipStream(inputms, CompressionMode.Decompress))
            {
                zip.CopyTo(outputms);
            }
            byte[] rv = outputms.ToArray();
            outputms.Dispose();
            return Encoding.UTF8.GetString(rv);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static string EncodeScriptJson(string input)
        {
            if (input == null)
            {
                return "";
            }
            else
            {
                return input.Replace(Environment.NewLine, "").Replace("\"", "\\\\\\\"").Replace("'", "\\'");
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="path"></param>
        public static void DeleteFile(string path)
        {
            try
            {
                System.IO.File.Delete(path);
            }
            catch (Exception ex)
            {
                CoreProgram.GetLogger("Utils")?.LogWarning(ex, "DeleteFile failed for '{Path}'", path);
            }
        }

        #region 格式化文本  add by wuwh 2014.6.12
        /// <summary>
        /// 格式化文本
        /// </summary>
        /// <param name="text">要格式化的字符串</param>
        /// <param name="isCode">是否是纯代码</param>
        /// <returns></returns>
        public static string FormatText(string text, bool isCode = false)
        {

            if (isCode)
            {
                return FormatCode(text);
            }
            else
            {
                #region 截取需要格式化的代码段
                List<int> listInt = [];
                int index = 0;
                int _index;
                while (true)
                {
                    _index = text.IndexOf("&&", index);
                    index = _index + 1;
                    if (_index >= 0 && _index <= text.Length)
                    {
                        listInt.Add(_index);
                    }
                    else
                    {
                        break;
                    }
                }

                List<string> listStr = [];
                for (int i = 0; i + 1 < listInt.Count; i++)
                {
                    string temp = text.Substring(listInt[i] + 2, listInt[i + 1] - listInt[i] - 2);

                    listStr.Add(temp);
                    i++;
                }
                #endregion

                #region 格式化代码段
                //先将 <  >以及空格替换掉，防止下面替换出现 html标签后出现问题
                for (int i = 0; i < listStr.Count; i++)
                {
                    //将 &&代码&&  替换成&&1&&
                    text = text.Replace("&&" + listStr[i] + "&&", FormatCode(listStr[i]));
                }
                #endregion

                return text;
            }
        }
        #endregion

        #region 格式化代码  edit by wuwh
        /// <summary>
        /// 格式化代码 
        /// </summary>
        /// <param name="code"></param>
        /// <returns></returns>
        public static string FormatCode(string code)
        {
            //先将 <  >以及空格替换掉，防止下面替换出现 html标签后出现问题
            code = code.Replace("<", "&lt;").Replace(">", "&gt;").Replace(" ", "&nbsp;");
            string csKeyWords = "abstract|as|base|bool|break|byte|case|catch|char|checked|class|const|continue|decimal|default|delegate|do|double|else|enum|event|explicit|extern|false|finally|fixed|float|for|foreach|from|get|goto|group|if|implicit|in|int|interface|internal|into|is|join|let|lock|long|namespace|new|null|object|operator|orderby|out|override|params|partial|private|protected|public|readonly|ref|return|sbyte|sealed|select|set|short|sizeof|stackalloc|static|string|struct|switch|this|throw|true|try|typeof|uint|ulong|unchecked|unsafe|ushort|using|value|var|virtual|void|volatile|where|while|yield";

            string r1 = "(#if DBG[\\s\\S]+?#endif)";
            string r2 = "(#[a-z ]*)";
            string r3 = "(///\\ *<[/\\w]+>)";
            string r4 = "(/\\*[\\s\\S]*?\\*/)";//匹配三杠注释
            string r5 = "(//.*)";//匹配双杠注释
            string r6 = @"(@?"".*?"")";//匹配字符串
            string r7 = "('.*?')";//匹配字符串
            string r8 = "\\b(" + csKeyWords + ")\\b";//匹配关键字
            //string r9 = "class&nbsp;(.+)&nbsp;";//匹配类
            //string r10 = "&lt;(.+)&gt;";//匹配泛型类

            string rs = $"{r1}|{r2}|{r3}|{r4}|{r5}|{r6}|{r7}|{r8}";
            //string rs = string.Format("{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}", r1, r2, r3, r4, r5, r6, r7, r8, r9,r10);

            //<font color=#44C796>$9$10</font>
            string rr = "<font color=#808080>$1$2$3</font><font color=#008000>$4$5</font><font color=#A31515>$6$7</font><font color=#0000FF>$8</font>";

            Regex re = new Regex(rs, RegexOptions.None);
            code = Regex.Replace(code, rs, rr);
            //替换换行符"\r\n"   以及"\r"  "\n"  
            code = code.Replace("\r\n", "<br>").Replace("\n", "").Replace("\r", "<br>");
            //取消空标签
            //|<font color=#44C796></font>C#类的颜色
            code = Regex.Replace(code, "<font color=#808080></font>|<font color=#008000></font>|<font color=#A31515></font>|<font color=#0000FF></font>", "");

            return code;
        }
        #endregion

        #region 读取txt文件
        /// <summary>
        /// 读取文件
        /// </summary>
        /// <param name="path">文件路径绝对</param>
        /// <returns></returns>
        public static string ReadTxt(string path)
        {
            string result = string.Empty;

            if (File.Exists(path))
            {
                using (Stream fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (TextReader sr = new StreamReader(fs, UnicodeEncoding.UTF8))
                    {
                        result = sr.ReadToEnd();
                    }
                }
            }

            return result;
        }
        #endregion

        /// <summary>
        /// 得到目录下所有文件
        /// </summary>
        /// <param name="dirpath"></param>
        /// <returns></returns>
        public static List<string> GetAllFileName(string dirpath)
        {
            DirectoryInfo dir = new DirectoryInfo(dirpath);
            var files = dir.GetFileSystemInfos();
            return [.. files.Select(x => x.Name)];
        }

        #region add by wuwh 2014.10.18  递归获取目录下所有文件
        /// <summary>
        /// 递归获取目录下所有文件
        /// </summary>
        /// <param name="dirPath"></param>
        /// <param name="allFiles"></param>
        /// <returns></returns>
        public static List<string> GetAllFilePathRecursion(string dirPath, List<string> allFiles)
        {
            if (allFiles == null)
            {
                allFiles = [];
            }
            string[] subPaths = Directory.GetDirectories(dirPath);
            foreach (var item in subPaths)
            {
                GetAllFilePathRecursion(item, allFiles);
            }
            allFiles.AddRange(Directory.GetFiles(dirPath));

            return allFiles;
        }
        #endregion


        /// <summary>
        /// ConvertToColumnXType
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static string ConvertToColumnXType(Type type)
        {
            if (type == typeof(bool) || type == typeof(bool?))
            {
                return "checkcolumn";
            }
            else if (type == typeof(DateTime) || type == typeof(DateTime?))
            {
                return "datecolumn";
            }
            else if (type == typeof(decimal) || type == typeof(decimal?) || type == typeof(double) || type == typeof(double?) || type == typeof(int) || type == typeof(int?) || type == typeof(long) || type == typeof(long?))
            {
                return "numbercolumn";
            }
            return "textcolumn";
        }


        public static string? GetCS(string? cs, string? mode, Configs config)
        {
            if(cs == null)
            {
                return null;
            }

            if (config.Connections.Any(x => x.Key?.ToLower() == cs.ToLower() && x.Enabled) == false)
            {
                cs = "default";
            }
            int index = cs.LastIndexOf("_");
            if (index > 0)
            {
                cs = cs.Substring(0, index);
            }
            if (mode?.ToLower() == "read")
            {
                List<string?> reads = [.. config.Connections.Where(x => x.Key?.StartsWith(cs + "_") == true && x.Enabled).Select(x => x.Key)];
                if (reads.Count > 0)
                {
                    Random r = new Random();
                    var v = r.Next(0, reads.Count);
                    cs = reads[v];
                }
            }
            return cs;
        }

        public static string GetUrlByFileAttachmentId(IDataContext dc, Guid? fileAttachmentId, bool isIntranetUrl = false, string? urlHeader = null)
        {
            string url = string.Empty;
            if (fileAttachmentId == null)
            {
                return url;
            }
            var fileAttachment = dc.Set<FileAttachment>().Where(x => x.ID == fileAttachmentId.Value).FirstOrDefault();
            if (fileAttachment != null)
            {
                url = "/_Framework/GetFile/" + fileAttachmentId.ToString();

            }
            return url;
        }

        #region 加解密

        /// <summary>
        /// 使用 AES-256-CBC 加密字串。每次加密產生隨機 IV，並將 IV 前置於密文中。
        /// 等同於呼叫 <see cref="EncryptString(string, string, CipherAlgorithm)"/> 並傳入
        /// <see cref="CipherAlgorithm.Aes256Cbc"/>。
        /// </summary>
        /// <param name="stringToEncrypt">要加密的字串</param>
        /// <param name="encryptKey">加密金鑰（任意長度，內部以 SHA-256 衍生 32 位元組金鑰）</param>
        /// <returns>Base64 編碼的 (IV + 密文)，或空字串（若輸入為空）</returns>
        public static string EncryptString(string stringToEncrypt, string encryptKey)
        {
            return EncryptString(stringToEncrypt, encryptKey, CipherAlgorithm.Aes256Cbc);
        }

        /// <summary>
        /// 依指定演算法加密字串。
        /// <para>
        /// <see cref="CipherAlgorithm.Aes256Cbc"/>（預設、建議）：AES-256-CBC，PKCS7 填充，每次加密產生
        /// 隨機 IV 並前置於密文中。
        /// </para>
        /// <para>
        /// <see cref="CipherAlgorithm.LegacyDes"/>：產生 8.x 世代的舊版 DES 密文（委派給
        /// <see cref="EncryptStringLegacy(string, string)"/>）。僅供需要保留回滾到 8.x 世代能力的下游
        /// 部署明示 opt-in 使用；DES 已被視為不安全的加密演算法，不得用於加密新資料。
        /// </para>
        /// </summary>
        /// <param name="stringToEncrypt">要加密的字串</param>
        /// <param name="encryptKey">加密金鑰</param>
        /// <param name="algorithm">要使用的加密演算法</param>
        /// <returns>Base64 編碼的密文，或空字串（若輸入為空）</returns>
        public static string EncryptString(string stringToEncrypt, string encryptKey, CipherAlgorithm algorithm)
        {
            if (algorithm == CipherAlgorithm.LegacyDes)
            {
#pragma warning disable CS0618
                return EncryptStringLegacy(stringToEncrypt, encryptKey);
#pragma warning restore CS0618
            }

            if (string.IsNullOrEmpty(stringToEncrypt))
            {
                return "";
            }

            using var aes = CreateAes(encryptKey);
            aes.GenerateIV();
            byte[] iv = aes.IV;
            byte[] plainBytes = UTF8Encoding.UTF8.GetBytes(stringToEncrypt);

            using var encryptStream = new MemoryStream();
            using (var cryptoStream = new CryptoStream(encryptStream, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                cryptoStream.Write(plainBytes, 0, plainBytes.Length);
                cryptoStream.FlushFinalBlock();
            }

            byte[] cipherBytes = encryptStream.ToArray();
            byte[] result = new byte[iv.Length + cipherBytes.Length];
            Buffer.BlockCopy(iv, 0, result, 0, iv.Length);
            Buffer.BlockCopy(cipherBytes, 0, result, iv.Length, cipherBytes.Length);

            return Convert.ToBase64String(result);
        }

        /// <summary>
        /// 使用 AES-256-CBC 解密字串。若 AES 解密拋出例外，或解密結果未通過
        /// <see cref="LooksLikePlaintext(byte[])"/> 的明文合理性檢查，自動嘗試舊版 DES 解密（向後相容）。
        /// <para>
        /// 背景：僅以「AES 是否拋出例外」判斷是否該退回 DES 並不足夠——PKCS7 unpadding 在用錯誤金鑰／IV
        /// 解密時，仍有機率（約每 256 次一次；若再排除長度不是 16 倍數的密文，實際約每 512 次一次）
        /// 「碰巧」通過 padding 檢查而不拋例外，此時會回傳一段呼叫端無法與正確解密結果區分的亂碼。
        /// 因此本方法額外要求 AES 解密結果通過 <see cref="LooksLikePlaintext(byte[])"/> 的啟發式檢查才會被採用；
        /// 這個檢查同樣不是密碼學保證，只是把上述機率再降低，殘餘機率並未歸零，詳見該方法的說明。
        /// </para>
        /// </summary>
        /// <param name="stringToDecrypt">要解密的字串（Base64 編碼）</param>
        /// <param name="encryptKey">解密金鑰</param>
        /// <returns>解密後的明文，或空字串（若輸入為空或解密失敗）</returns>
        public static string DecryptString(string stringToDecrypt, string encryptKey)
        {
            DecryptRoute route = ClassifyDecryptRoute(stringToDecrypt, out byte[]? fullCipher);

            switch (route)
            {
                case DecryptRoute.Empty:
                case DecryptRoute.NotBase64:
                    return "";

                case DecryptRoute.LegacyDesShort:
                    // AES-256-CBC requires at least 16 bytes for IV + at least 16 bytes for one
                    // cipher block. Below that threshold this cannot possibly be AES-256-CBC
                    // ciphertext produced by EncryptString, so this remains a deterministic (not
                    // heuristic) fallback to legacy DES — unlike the AES-attempt branch below,
                    // there is no ambiguity to resolve here.
#pragma warning disable CS0618
                    return DecryptStringLegacy(stringToDecrypt, encryptKey);
#pragma warning restore CS0618

                case DecryptRoute.AesAttempted:
                default:
                    // Try AES-256-CBC first. A successful decrypt (no CryptographicException) is
                    // not by itself proof that encryptKey was the right key: see the class
                    // remarks above and LooksLikePlaintext's remarks for why the result is
                    // additionally screened before being trusted. Anything that fails either
                    // check falls back to legacy DES, exactly as a hard AES failure always has.
                    if (TryAesDecrypt(fullCipher!, encryptKey, out byte[] candidate) && LooksLikePlaintext(candidate))
                    {
                        return Encoding.UTF8.GetString(candidate);
                    }

#pragma warning disable CS0618
                    return DecryptStringLegacy(stringToDecrypt, encryptKey);
#pragma warning restore CS0618
            }
        }

        /// <summary>
        /// 回報 <see cref="DecryptString(string, string)"/> 對 <paramref name="cipherText"/>
        /// 這段輸入實際會走哪一條解密路徑（見 <see cref="DecryptRoute"/>）。
        /// <para>
        /// <b>這個方法回答的是「<c>DecryptString</c> 會怎麼處理這段輸入」，不是「這段密文是用什麼
        /// 演算法加密的」——後者無法單從密文本身判斷，這正是 issue #1085 這個 API 存在的原因。</b>
        /// 判準與 <c>DecryptString</c> 內部實際使用的是同一份邏輯（共用私有的
        /// <c>ClassifyDecryptRoute</c>），不是另外複製一份平行判斷，所以兩者不會漂移。
        /// </para>
        /// <para>
        /// <b>典型用法</b>：下游應用程式想在啟動時，對設定檔中每一條已加密的連線字串做健檢，
        /// 對「會走機率性 AES 嘗試路徑、而其中很可能其實是舊版 DES 密文」的項目提出警告
        /// ——只有 <see cref="DecryptRoute.AesAttempted"/> 需要被這樣看待，因為那是唯一非確定性的
        /// 分類；<see cref="DecryptRoute.LegacyDesShort"/> 雖然名字裡有 "LegacyDes"，
        /// 但它的路徑是確定性的，不需要警告：
        /// <code>
        /// foreach (var (name, cipherText) in connectionStrings)
        /// {
        ///     if (Utils.GetDecryptRoute(cipherText) == DecryptRoute.AesAttempted)
        ///     {
        ///         // 這條會先嘗試 AES；如果它其實是（長度剛好 &gt;= 32 bytes 的）legacy DES
        ///         // 密文，DecryptString 多半仍會正確退回 DES，但不保證——值得記錄以便追蹤。
        ///         logger.LogWarning("{Name}: cipher text takes the probabilistic AES-attempt route", name);
        ///     }
        /// }
        /// </code>
        /// </para>
        /// </summary>
        /// <param name="cipherText">要分類的密文字串（與 <see cref="DecryptString(string, string)"/>
        /// 的 <c>stringToDecrypt</c> 參數相同來源）</param>
        /// <returns>DecryptString 會採用的解密路徑</returns>
        public static DecryptRoute GetDecryptRoute(string? cipherText)
        {
            return ClassifyDecryptRoute(cipherText, out _);
        }

        /// <summary>
        /// <see cref="DecryptString(string, string)"/> 與 <see cref="GetDecryptRoute(string?)"/>
        /// 共用的單一判準來源：兩者都呼叫這個方法決定路徑，避免各自維護一份可能漂移的平行邏輯。
        /// </summary>
        /// <param name="stringToDecrypt">要分類的字串</param>
        /// <param name="fullCipher">當回傳 <see cref="DecryptRoute.LegacyDesShort"/> 或
        /// <see cref="DecryptRoute.AesAttempted"/> 時，Base64 解碼後的位元組；其餘情況為
        /// <c>null</c>（呼叫端不需要、也不應該在其他分類下使用這個值）</param>
        /// <returns>分類結果</returns>
        private static DecryptRoute ClassifyDecryptRoute(string? stringToDecrypt, out byte[]? fullCipher)
        {
            if (string.IsNullOrEmpty(stringToDecrypt))
            {
                fullCipher = null;
                return DecryptRoute.Empty;
            }

            try
            {
                fullCipher = Convert.FromBase64String(stringToDecrypt.Replace(" ", "+"));
            }
            catch (FormatException)
            {
                fullCipher = null;
                return DecryptRoute.NotBase64;
            }

            return fullCipher.Length < 32 ? DecryptRoute.LegacyDesShort : DecryptRoute.AesAttempted;
        }

        /// <summary>
        /// 嘗試以 AES-256-CBC 解密 <paramref name="fullCipher"/>（前 16 bytes 為 IV，其餘為密文）。
        /// 呼叫端須保證 <c>fullCipher.Length &gt;= 32</c>。
        /// </summary>
        /// <param name="fullCipher">IV（16 bytes）+ 密文</param>
        /// <param name="encryptKey">解密金鑰</param>
        /// <param name="candidate">解密並完成 PKCS7 unpadding 後的位元組；解密失敗時為空陣列</param>
        /// <returns>是否在沒有拋出 <see cref="CryptographicException"/> 的情況下完成解密——
        /// 不代表 <paramref name="candidate"/> 就是正確的原始明文，僅代表 AES 本身沒有偵測到錯誤。</returns>
        private static bool TryAesDecrypt(byte[] fullCipher, string encryptKey, out byte[] candidate)
        {
            byte[] iv = new byte[16];
            byte[] cipherBytes = new byte[fullCipher.Length - 16];
            Buffer.BlockCopy(fullCipher, 0, iv, 0, 16);
            Buffer.BlockCopy(fullCipher, 16, cipherBytes, 0, cipherBytes.Length);

            try
            {
                using var aes = CreateAes(encryptKey);
                aes.IV = iv;

                var decryptStream = new MemoryStream();
                var cryptoStream = new CryptoStream(decryptStream, aes.CreateDecryptor(), CryptoStreamMode.Write);

                try
                {
                    cryptoStream.Write(cipherBytes, 0, cipherBytes.Length);
                    cryptoStream.FlushFinalBlock();
                    candidate = decryptStream.ToArray();
                    return true;
                }
                finally
                {
                    decryptStream.Dispose();
                    try { cryptoStream.Dispose(); } catch { /* suppress dispose errors */ }
                }
            }
            catch (CryptographicException)
            {
                candidate = [];
                return false;
            }
        }

        /// <summary>
        /// 啟發式檢查一段位元組是否「像」合法的解密明文。
        /// <para>
        /// <b>這仍然是啟發式判斷，不是密碼學保證，殘餘的誤判機率並未歸零。</b>
        /// 目前的判準是：(1) 整段位元組必須是合法的 UTF-8 編碼；(2) 解碼後的字元中，除了
        /// <c>\t</c>／<c>\r</c>／<c>\n</c> 之外不得含有其他控制字元。用錯誤金鑰／IV 解密任意密文，
        /// 剛好解出一段通過以上兩項檢查的位元組序列，機率遠低於「單純沒有拋出例外」，但不是零——
        /// 呼叫端（<see cref="DecryptString"/>）把這個方法的「通過」當成比完全不檢查更可信的信號使用，
        /// 不能當成已排除誤判的證明。
        /// </para>
        /// </summary>
        /// <param name="candidate">要檢查的位元組（AES 解密並完成 unpadding 後、尚未轉字串前）</param>
        /// <returns>是否通過啟發式明文檢查</returns>
        internal static bool LooksLikePlaintext(byte[] candidate)
        {
            if (candidate.Length == 0)
            {
                // Decrypting to an empty string is a legitimate outcome (EncryptString("") returns
                // "" before ever reaching the cipher, but a *non-empty* AES ciphertext can still
                // legitimately decrypt to an empty payload), so this does not count against it.
                return true;
            }

            if (!System.Text.Unicode.Utf8.IsValid(candidate))
            {
                return false;
            }

            // candidate is now known to be well-formed UTF-8, so this GetString cannot lossily
            // substitute U+FFFD replacement characters the way a non-strict decode of invalid
            // input would.
            string text = Encoding.UTF8.GetString(candidate);
            foreach (char c in text)
            {
                if (char.IsControl(c) && c != '\t' && c != '\r' && c != '\n')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 使用舊版 DES 解密字串。僅供向後相容遷移期間使用。
        /// </summary>
        /// <param name="stringToDecrypt">要解密的字串（Base64 編碼）</param>
        /// <param name="encryptKey">解密金鑰</param>
        /// <returns>解密後的明文，或空字串</returns>
        [Obsolete("Legacy DES decryption retained for backward compatibility. Use EncryptString/DecryptString (AES-256) for new data.")]
        public static string DecryptStringLegacy(string stringToDecrypt, string encryptKey)
        {
            if (string.IsNullOrEmpty(stringToDecrypt))
            {
                return "";
            }

            try
            {
                byte[] bytIn = Convert.FromBase64String(stringToDecrypt.Replace(" ", "+"));

                using var des = CreateLegacyDes(encryptKey);
                var decryptStream = new MemoryStream();
                var cryptoStream = new CryptoStream(decryptStream, des.CreateDecryptor(), CryptoStreamMode.Write);

                try
                {
                    cryptoStream.Write(bytIn, 0, bytIn.Length);
                    cryptoStream.FlushFinalBlock();
                    return UTF8Encoding.UTF8.GetString(decryptStream.ToArray());
                }
                finally
                {
                    decryptStream.Dispose();
                    try { cryptoStream.Dispose(); } catch { /* suppress dispose errors */ }
                }
            }
            catch (CryptographicException)
            {
                return "";
            }
            catch (FormatException)
            {
                return "";
            }
        }

        /// <summary>
        /// 使用舊版 DES 加密字串（8.x 世代格式）。
        /// <para>
        /// 唯一目的是讓需要回滾到 8.x 世代的部署明示 opt-in 產生舊格式密文；DES 已被視為不安全的加密演算法
        /// （56 位元有效金鑰、已知可被暴力破解），絕不得用於加密新資料。一般情境請改用
        /// <see cref="EncryptString(string, string)"/>（AES-256）。
        /// </para>
        /// <para>
        /// 與 <see cref="DecryptStringLegacy(string, string)"/> 共用同一個 <c>CreateLegacyDes</c> provider
        /// 與金鑰／IV 衍生邏輯，確保產出的密文可被舊版本或本版 <see cref="DecryptString(string, string)"/>
        /// 的 DES fallback 正確解回。
        /// </para>
        /// </summary>
        /// <param name="stringToEncrypt">要加密的字串</param>
        /// <param name="encryptKey">加密金鑰</param>
        /// <returns>Base64 編碼的密文，或空字串（若輸入為空）</returns>
        [Obsolete("Legacy DES encryption retained only to let deployments that need to roll back to the 8.x generation opt in to producing the old ciphertext format. DES is cryptographically broken and must never be used for new data. Use EncryptString(string, string) (AES-256) instead.")]
        public static string EncryptStringLegacy(string stringToEncrypt, string encryptKey)
        {
            if (string.IsNullOrEmpty(stringToEncrypt))
            {
                return "";
            }

            byte[] plainBytes = UTF8Encoding.UTF8.GetBytes(stringToEncrypt);

            using var des = CreateLegacyDes(encryptKey);
            using var encryptStream = new MemoryStream();
            using (var cryptoStream = new CryptoStream(encryptStream, des.CreateEncryptor(), CryptoStreamMode.Write))
            {
                cryptoStream.Write(plainBytes, 0, plainBytes.Length);
                cryptoStream.FlushFinalBlock();
            }

            return Convert.ToBase64String(encryptStream.ToArray());
        }

        /// <summary>
        /// 建立 AES-256-CBC 加密器，使用 SHA-256 從使用者金鑰衍生 32 位元組金鑰。
        /// </summary>
        private static Aes CreateAes(string key)
        {
            var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.KeySize = 256;
            aes.Key = SHA256.HashData(UTF8Encoding.UTF8.GetBytes(key));
            return aes;
        }

        /// <summary>
        /// 建立舊版 DES 加密器（僅供向後相容解密使用）。
        /// </summary>
        private static DES CreateLegacyDes(string key)
        {
            var dCrypter = DES.Create();

            string sTemp;
            if (dCrypter.LegalKeySizes.Length > 0)
            {
                int moreSize = dCrypter.LegalKeySizes[0].MinSize;
                if (key.Length > 8)
                {
                    key = key.Substring(0, 8);
                }
                sTemp = key.PadRight(moreSize / 8, ' ');
            }
            else
            {
                sTemp = key;
            }
            byte[] bytKey = UTF8Encoding.UTF8.GetBytes(sTemp);

            dCrypter.Key = bytKey;
            dCrypter.IV = bytKey;

            return dCrypter;
        }

        #endregion

        #region MD5加密
        /// <summary>
        /// 字符串MD5加密
        /// </summary>
        /// <param name="str"></param>
        /// <returns>返回大写32位MD5值</returns>
        [Obsolete("Use PasswordHashHelper.HashPassword() for passwords. MD5 is cryptographically broken for password storage.")]
        public static string GetMD5String(string str)
        {
            if(str == null)
            {
                return "";
            }
            byte[] buffer = Encoding.UTF8.GetBytes(str);

            return MD5String(buffer);
        }

        /// <summary>
        /// 流MD5加密
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        public static string GetMD5Stream(Stream stream)
        {
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(stream);
            var sb = new StringBuilder(32);
            foreach (byte b in hash)
                sb.Append(b.ToString("X2"));
            return sb.ToString();
        }

        /// <summary>
        /// 文件MD5加密
        /// </summary>
        /// <param name="path"></param>
        /// <returns>返回大写32位MD5值</returns>
        public static string GetMD5File(string path)
        {
            if (File.Exists(path))
            {
                using (FileStream fs = new FileStream(path, FileMode.Open))
                {
                    return GetMD5Stream(fs);
                }
            }
            else
            {
                return string.Empty;
            }
        }

        private static string MD5String(byte[] buffer)
        {
            using var md5 = MD5.Create();
            byte[] cryptBuffer = md5.ComputeHash(buffer);
            StringBuilder sb = new StringBuilder();
            foreach (byte item in cryptBuffer)
            {
                sb.Append(item.ToString("X2"));
            }
            return sb.ToString();
        }
        #endregion

        /// <summary>
        /// 重新处理 返回所有ispage模块
        /// </summary>
        /// <param name="modules"></param>
        /// <param name="submit">是否需要action</param>
        /// <returns></returns>
        public static List<SimpleModule> ResetModule(List<SimpleModule> modules, bool submit = true)
        {
            var m = modules.Select(x => new SimpleModule
            {
                ActionDes = x.ActionDes,
                Actions = [.. x.Actions?.Select(y => new SimpleAction
                {
                    ActionDes = y.ActionDes,
                    ActionName = y.ActionName,
                    Url = y.Url,
                    MethodName = y.MethodName,
                    IgnorePrivillege = y.IgnorePrivillege,
                    ID = y.ID,
                    Module = y.Module,
                    ModuleId = y.ModuleId,
                    Parameter = y.Parameter,
                    ParasToRunTest = y.ParasToRunTest
                })],
                Area = x.Area,
                AreaId = x.AreaId,
                ClassName = x.ClassName,
                _name = x._name,
                ID = x.ID,
                IgnorePrivillege = x.IgnorePrivillege,
                IsApi = x.IsApi,
                ModuleName = x.ModuleName,
                NameSpace = x.NameSpace,
            }).ToList();
            var mCount = m.Count;
            List<SimpleModule> toRemove = [];
            for (int i = 0; i < mCount; i++)
            {
                var pages = m[i].Actions?.Where(x => x.ActionDes?.IsPage == true).ToList();
                if (pages != null)
                {
                    for (int j = 0; j < pages.Count; j++)
                    {
                        if (j == 0 && m[i].Actions != null && !m[i].Actions!.Any(x => x.MethodName?.ToLower() == "index"))
                        {
                            m.Add(new SimpleModule
                            {
                                ModuleName = (pages[j].ActionDes?._localizer != null && pages[j].ActionDes?.Description != null ? pages[j].ActionDes?._localizer![pages[j].ActionDes!.Description]?.Value : pages[j].ActionDes?.Description) ?? "",
                                NameSpace = m[i].NameSpace,
                                ClassName = pages[j].MethodName,
                                Actions = m[i].Actions,
                                Area = m[i].Area
                            });
                            if (submit)
                                m[i].Actions?.Remove(pages[j]);
                            toRemove.Add(m[i]);
                        }
                        else
                        {
                            if (pages[j].MethodName?.ToLower() != "index")
                            {
                                m.Add(new SimpleModule
                                {
                                    ModuleName = (pages[j].ActionDes?._localizer != null && pages[j].ActionDes?.Description != null ? pages[j].ActionDes?._localizer![pages[j].ActionDes!.Description]?.Value : pages[j].ActionDes?.Description) ?? "",
                                    NameSpace = m[i].NameSpace,
                                    ClassName = pages[j].Module?.ClassName + pages[j].MethodName,
                                    Actions = submit ? new List<SimpleAction>() : new List<SimpleAction>() { pages[j] },
                                    Area = m[i].Area
                                });
                                m[i].Actions?.Remove(pages[j]);
                            }
                        }
                    }
                }
            }
            toRemove.ForEach(x => m.Remove(x));
            return m;
        }

    }
}
