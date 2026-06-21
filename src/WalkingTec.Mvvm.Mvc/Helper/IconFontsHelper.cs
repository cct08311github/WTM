using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    public static class IconFontsHelper
    {
        private static List<ComboSelectListItem> _iconFontItems;
        /// <summary>
        /// Returns the flat icon-font list. Defensively initialises the backing field to an
        /// empty list when accessed before <see cref="GenerateIconFont"/> has run (#464).
        /// </summary>
        public static List<ComboSelectListItem> IconFontItems
        {
            get
            {
                // #464: guard against access before GenerateIconFont populates the static cache.
                _iconFontItems ??= new List<ComboSelectListItem>();
                foreach (var item in _iconFontItems.Where(x => x.Selected == true))
                    item.Selected = false;
                return _iconFontItems;
            }
        }

        private static Dictionary<string, List<MenuItem>> _iconFontDicItems;
        /// <summary>
        /// Returns the icon-font dictionary keyed by font-family name. Defensively initialises
        /// to an empty dictionary when accessed before <see cref="GenerateIconFont"/> has run (#464).
        /// </summary>
        public static Dictionary<string, List<MenuItem>> IconFontDicItems
        {
            // #464: guard against null when GenerateIconFont was never called.
            get => _iconFontDicItems ??= new Dictionary<string, List<MenuItem>>();
        }

        public static void GenerateIconFont(params string[] dirs)
        {
            var baseDirs = new string[] { "wwwroot/font", "wwwroot/layui" };
            if(dirs.Length > 0)
            {
                baseDirs = dirs;
            }
            var iconFontHashSet = new HashSet<string>();
            var IconFontDic = new Dictionary<string, string[]>();
            foreach (var dir in baseDirs)
            {
                if (Directory.Exists(dir))
                {
                    RecursiveDir(dir, iconFontHashSet, IconFontDic);
                }
            }
            var iconFonts = iconFontHashSet.ToArray();

            _iconFontItems = iconFontHashSet.Select(x => new ComboSelectListItem
            {
                Text = x,
                Value = x,
                Icon = x
            }).ToList();

            _iconFontDicItems = new Dictionary<string, List<MenuItem>>();
            foreach (var key in IconFontDic.Keys)
            {
                IconFontDicItems.Add(key, IconFontDic[key].Select(x => new MenuItem
                {
                    Text = x,
                    Value = x,
                    Icon = $"{key} {x}"
                }).ToList());
            }
        }

        private static void RecursiveDir(string dirPath, HashSet<string> iconFonts, Dictionary<string, string[]> iconFontDic)
        {
            var dirs = Directory.GetDirectories(dirPath);
            foreach (var dir in dirs)
            {
                RecursiveDir(dir, iconFonts, iconFontDic);
            }

            var files = Directory.GetFiles(dirPath, "*.css");
            foreach (var cssPath in files)
            {
                ResolveIconfont(cssPath, iconFonts, iconFontDic);
            }
        }

        /// <summary>
        /// 解析
        /// </summary>
        private static void ResolveIconfont(string cssPath, HashSet<string> iconFonts, Dictionary<string, string[]> iconFontDic)
        {
            var file = File.ReadAllText(cssPath).Replace("\r\n", string.Empty);

            // 找到自定义的 iconfont
            var regex = new Regex("@font-face\\s{0,}{\\s{0,}('|\"|)font-family(\\1)\\s{0,}:\\s{0,}('|\"|)([a-zA-Z0-9-_.#]{1,})(\\3)\\s{0,};");
            var iconMatchs = regex.Matches(file);
            foreach (Match iconfontItem in iconMatchs)
            {
                // icon family name
                var iconName = iconfontItem.Groups[4].ToString();

                var itemRegex = new Regex($".({iconName}-([a-zA-Z0-9-_.#]{{1,}}))\\s{{0,}}:before\\s{{0,}}{{");
                var itemMatchs = itemRegex.Matches(file);

                List<string> iconFontItems = [];
                foreach (Match item in itemMatchs)
                {
                    iconFontItems.Add(item.Groups[1].ToString());
                }

                if (iconFontItems.Count > 0)
                {
                    iconFonts.Add(iconName);
                    iconFontDic.Add(iconName, iconFontItems.OrderBy(x => x).ToArray());
                }
            }
        }
    }
}
