#nullable enable
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace WalkingTec.Mvvm.Core.Extensions
{
    public static class ListVMExtension
    {
        // grid-002: Strict allowlist for __bgcolor / __forecolor values injected into
        // inline <script> CSS calls and style attributes. A DB-driven color field can
        // carry script/CSS injection payloads (e.g. "red;}</style><script>alert(1)").
        // Only hex colors (#RGB / #RRGGBB / #RRGGBBAA) and a fixed set of CSS named
        // colors are permitted. Any value that does not match is dropped (returns null).
        private static readonly Regex _hexColorRegex =
            new Regex(@"^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // CSS Level 4 named colors (https://www.w3.org/TR/css-color-4/#named-colors).
        // Only ASCII alphanumeric — safe to use directly in attribute values and JS strings.
        // Perf(#663): _namedColors is private and read-only after initialization (only
        // .Contains is ever called on it) - FrozenSet<T> is a drop-in, faster read path
        // for this shape. The OrdinalIgnoreCase comparer must be passed explicitly to
        // ToFrozenSet: it does NOT inherit the comparer from the source collection, so
        // omitting it would silently make color matching case-sensitive.
        private static readonly FrozenSet<string> _namedColors = new[]
        {
            "aliceblue","antiquewhite","aqua","aquamarine","azure","beige","bisque","black",
            "blanchedalmond","blue","blueviolet","brown","burlywood","cadetblue","chartreuse",
            "chocolate","coral","cornflowerblue","cornsilk","crimson","cyan","darkblue",
            "darkcyan","darkgoldenrod","darkgray","darkgreen","darkgrey","darkkhaki",
            "darkmagenta","darkolivegreen","darkorange","darkorchid","darkred","darksalmon",
            "darkseagreen","darkslateblue","darkslategray","darkslategrey","darkturquoise",
            "darkviolet","deeppink","deepskyblue","dimgray","dimgrey","dodgerblue","firebrick",
            "floralwhite","forestgreen","fuchsia","gainsboro","ghostwhite","gold","goldenrod",
            "gray","green","greenyellow","grey","honeydew","hotpink","indianred","indigo",
            "ivory","khaki","lavender","lavenderblush","lawngreen","lemonchiffon","lightblue",
            "lightcoral","lightcyan","lightgoldenrodyellow","lightgray","lightgreen","lightgrey",
            "lightpink","lightsalmon","lightseagreen","lightskyblue","lightslategray",
            "lightslategrey","lightsteelblue","lightyellow","lime","limegreen","linen",
            "magenta","maroon","mediumaquamarine","mediumblue","mediumorchid","mediumpurple",
            "mediumseagreen","mediumslateblue","mediumspringgreen","mediumturquoise",
            "mediumvioletred","midnightblue","mintcream","mistyrose","moccasin","navajowhite",
            "navy","oldlace","olive","olivedrab","orange","orangered","orchid","palegoldenrod",
            "palegreen","paleturquoise","palevioletred","papayawhip","peachpuff","peru","pink",
            "plum","powderblue","purple","rebeccapurple","red","rosybrown","royalblue",
            "saddlebrown","salmon","sandybrown","seagreen","seashell","sienna","silver",
            "skyblue","slateblue","slategray","slategrey","snow","springgreen","steelblue",
            "tan","teal","thistle","tomato","turquoise","violet","wheat","white","whitesmoke",
            "yellow","yellowgreen","transparent"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Validates a color value against a strict allowlist (hex or CSS named color).
        /// Returns the validated color string (normalized with '#' prefix if hex), or
        /// null if the value does not match — preventing CSS/JS injection via color fields.
        /// </summary>
        internal static string? ValidateColor(string? color)
        {
            if (string.IsNullOrWhiteSpace(color))
                return null;

            var trimmed = color.Trim();

            // Add '#' prefix if it looks like a bare hex string without it
            if (!trimmed.StartsWith('#') &&
                Regex.IsMatch(trimmed, @"^[0-9A-Fa-f]{3}$|^[0-9A-Fa-f]{6}$|^[0-9A-Fa-f]{8}$"))
            {
                trimmed = "#" + trimmed;
            }

            if (_hexColorRegex.IsMatch(trimmed))
                return trimmed;

            if (_namedColors.Contains(trimmed))
                return trimmed;

            // Value does not match — drop it to prevent injection
            return null;
        }
        /// <summary>
        /// 获取Jason格式的列表数据
        /// </summary>
        /// <param name="self">是否需要对数据进行Json编码</param>
        /// <param name="returnColumnObject">不在后台进行ColumnFormatInfo的转化，而是直接输出ColumnFormatInfo的json结构到前端，由前端处理，默认False</param>
        /// <param name="enumToString"></param>
        /// <returns>Json格式的数据</returns>
        public static string GetDataJson<T>(this IBasePagedListVM<T, BaseSearcher> self, bool returnColumnObject = false, bool enumToString = true) where T : TopBasePoco, new()
        {
            var sb = new StringBuilder();
            self.GetHeaders();
            if (self.IsSearched == false)
            {
                self.DoSearch();
            }
            var el = self.GetEntityList().ToList();
            //如果列表主键都为0，则生成自增主键，避免主键重复
            if (el.All(x => {
                var id = x.GetID();
                if(id == null || (id is Guid gid && gid == Guid.Empty) || (id is int iid && iid==0) || (id is long lid && lid == 0))
                {
                    return true;
                }
                else
                {
                    return false;
                }
            } ))
            {
                el.ForEach(x => x.ID = Guid.NewGuid());
            }
            //循环生成列表数据
            // Perf(#674): flatten the (possibly multi-level) header tree into its bottom
            // columns ONCE for the whole result set instead of once per row — BottomChildren
            // recursively rebuilds a List on every access, so re-flattening inside the
            // per-row loop was O(rows * columns) tree walks. The flattened column set is
            // identical for every row (same TModel, same GridHeaders instance), so hoisting
            // it above the loop is behaviour-identical.
            var flatColumns = self.GetHeaders().SelectMany(h => h.BottomChildren).ToList();
            for (int x = 0; x < el.Count; x++)
            {
                var sou = el[x];
                sb.Append(self.GetSingleDataJsonCore(sou, returnColumnObject, x, enumToString, flatColumns));
                if (x < el.Count - 1)
                {
                    sb.Append(',');
                }
            }
            return $"[{sb}]";
        }

        private static string GetFormatResult(BaseVM? vm, ColumnFormatInfo? info)
        {
            string rv = "";
            if (vm == null || vm.UIService == null || info == null) return rv;
            switch (info.FormatType)
            {
                case ColumnFormatTypeEnum.Dialog:
                    rv = vm.UIService.MakeDialogButton(info.ButtonType, info.Url ?? "", info.Text ?? "", info.Width, info.Height, info.Title, info.ButtonID, info.ShowDialog, info.Resizable, info.Maxed, info.ButtonClass, info.Style)?.ToString() ?? "";
                    break;
                case ColumnFormatTypeEnum.Button:
                    rv = vm.UIService.MakeButton(info.ButtonType, info.Url ?? "", info.Text ?? "", info.Width, info.Height, info.Title, info.ButtonID, info.Resizable, info.Maxed, vm.ViewDivId, info.ButtonClass, info.Style, info.RType)?.ToString() ?? "";
                    break;
                case ColumnFormatTypeEnum.Download:
                    if (info.FileID == null)
                    {
                        rv = "";
                    }
                    else
                    {
                        rv = vm.UIService.MakeDownloadButton(info.ButtonType, info.FileID.Value, info.Text, vm.CurrentCS ?? "default", info.ButtonClass, info.Style)?.ToString() ?? "";
                    }
                    break;
                case ColumnFormatTypeEnum.ViewPic:
                    if (info.FileID == null)
                    {
                        rv = "";
                    }
                    else
                    {
                        rv = vm.UIService.MakeViewButton(info.ButtonType, info.FileID.Value, info.Text, info.Width, info.Height, info.Title, info.Resizable, vm.CurrentCS ?? "default", info.Maxed, info.ButtonClass, info.Style)?.ToString() ?? "";
                    }
                    break;
                case ColumnFormatTypeEnum.Script:
                    rv = vm.UIService.MakeScriptButton(info.ButtonType, info.Text ?? "", info.Script ?? "", info.ButtonID, info.Url, info.ButtonClass, info.Style)?.ToString() ?? "";
                    break;
                case ColumnFormatTypeEnum.Html:
                    rv = info.Html ?? "";
                    break;
                default:
                    break;
            }
            return rv;
        }

        /// <summary>
        /// 生成单条数据的Json格式
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="self"></param>
        /// <param name="obj">数据</param>
        /// <param name="returnColumnObject">不在后台进行ColumnFormatInfo的转化，而是直接输出ColumnFormatInfo的json结构到前端，由前端处理，默认False</param>
        /// <param name="index">index</param>
        /// <param name="enumToString"></param>
        /// <returns>Json格式的数据</returns>
        public static string GetSingleDataJson<T>(this IBasePagedListVM<T, BaseSearcher> self, object obj, bool returnColumnObject, int index = 0, bool enumToString = true) where T : TopBasePoco
        {
            return self.GetSingleDataJsonCore(obj, returnColumnObject, index, enumToString, flatColumns: null);
        }

        /// <summary>
        /// Core implementation shared by <see cref="GetSingleDataJson{T}"/> and the per-row
        /// loop in <see cref="GetDataJson{T}"/>. Perf(#674): accepts an optional pre-flattened
        /// bottom-column list so callers that render many rows (or nested tree-grid child
        /// rows, which share the same TModel header tree) can compute the flatten once and
        /// reuse it, instead of re-walking <see cref="IGridColumn{T}.BottomChildren"/> for
        /// every row. When null, flattens on demand — identical to the pre-#674 behaviour —
        /// so single-row callers (e.g. add-row endpoints) are unaffected.
        /// </summary>
        private static string GetSingleDataJsonCore<T>(this IBasePagedListVM<T, BaseSearcher> self, object obj, bool returnColumnObject, int index, bool enumToString, List<IGridColumn<T>>? flatColumns) where T : TopBasePoco
        {
            bool inner = false;
            var sb = new StringBuilder();
            var RowBgColor = string.Empty;
            var RowColor = string.Empty;
            if (obj is not T sou)
            {
                sou = self.CreateEmptyEntity();
            }
            RowBgColor = self.SetFullRowBgColor(sou);
            RowColor = self.SetFullRowColor(sou);
            var isSelected = self.GetIsSelected(sou);
            //循环所有列
            sb.Append('{');
            bool containsID = false;
            bool addHiddenID = false;
            Dictionary<string, (string, string)> colorcolumns = new Dictionary<string, (string, string)>();
            var cols = flatColumns ?? self.GetHeaders().SelectMany(h => h.BottomChildren).ToList();
            foreach (var col in cols)
            {
                inner = false;
                if (col.ColumnType != GridColumnTypeEnum.Normal)
                {
                    continue;
                }
                if (col.FieldName?.ToLower() == "id")
                {
                    containsID = true;
                }
                var backColor = col.GetBackGroundColor(sou);
                //获取ListVM中设定的单元格前景色
                var foreColor = col.GetForeGroundColor(sou);

                if (backColor == string.Empty)
                {
                    backColor = RowBgColor;
                }
                if (foreColor == string.Empty)
                {
                    foreColor = RowColor;
                }
                (string? bgcolor, string? forecolor) colors = (null, null);
                if (backColor != string.Empty)
                {
                    colors.bgcolor = backColor;
                }
                if (foreColor != string.Empty)
                {
                    colors.forecolor = foreColor;
                }
                if (string.IsNullOrEmpty(colors.bgcolor) == false || string.IsNullOrEmpty(colors.forecolor) == false)
                {
                    if (col.Field != null && colors.bgcolor != null && colors.forecolor != null) { colorcolumns.Add(col.Field, (colors.bgcolor, colors.forecolor)); }
                }
                //设定列名，如果是主键ID，则列名为id，如果不是主键列，则使用f0，f1,f2...这种方式命名，避免重复
                var ptype = col.FieldType;
                if (col.Field?.ToLower() == "children" && typeof(IEnumerable<T>).IsAssignableFrom(ptype))
                {
                    var children = (col.GetObject(obj) as IEnumerable<T>)?.ToList();
                    if (children == null || children.Count == 0)
                    {
                        continue;
                    }
                }
                var html = string.Empty;

                if (col.EditType == EditTypeEnum.Text || col.EditType == null)
                {
                    if (typeof(IEnumerable<T>).IsAssignableFrom(ptype))
                    {
                        var children = (col.GetObject(obj) as IEnumerable<T>)?.ToList();
                        if (children != null)
                        {
                            html = "[";
                            for (int i = 0; i < children.Count; i++)
                            {
                                var item = children[i];
                                // Perf(#674): nested tree-grid child rows share the same TModel
                                // header tree as the parent row — reuse the already-flattened
                                // column list instead of re-walking BottomChildren per child row.
                                html += self.GetSingleDataJsonCore(item, returnColumnObject, 0, enumToString, cols);
                                if (i < children.Count - 1)
                                {
                                    html += ",";
                                }
                            }
                            html += "]";
                        }
                        else
                        {
                            //html = "[]";
                        }
                        inner = true;
                    }
                    else
                    {
                        if (returnColumnObject == true)
                        {
                            html = col.GetText(sou, false).ToString();
                        }
                        else
                        {
                            var info = col.GetText(sou);

                            if (info is ColumnFormatInfo)
                            {
                                html = GetFormatResult(self as BaseVM, info as ColumnFormatInfo);
                            }
                            else if (info is List<ColumnFormatInfo> list)
                            {
                                var temp = string.Empty;
                                foreach (var item in list)
                                {
                                    temp += GetFormatResult(self as BaseVM, item);
                                    temp += "&nbsp;&nbsp;";
                                }
                                html = temp;
                            }
                            else
                            {
                                html = info.ToString();
                            }
                        }

                        //如果列是布尔值，直接返回true或false，让前台生成CheckBox
                        if (ptype == typeof(bool) || ptype == typeof(bool?))
                        {
                            if(enumToString == false)
                            {
                                html = html?.ToLower() ?? "";
                                inner = true;
                            }
                            else if (returnColumnObject == false)
                            {
                                if (html?.ToLower() == "true")
                                {
                                    html = (self as BaseVM)?.UIService?.MakeCheckBox(true, isReadOnly: true)?.ToString() ?? "";
                                }
                                if (html?.ToLower() == "false" || html == string.Empty)
                                {
                                    html = (self as BaseVM)?.UIService?.MakeCheckBox(false, isReadOnly: true)?.ToString() ?? "";
                                }
                            }
                            else
                            {
                                if (html != null && html != string.Empty)
                                {
                                    html = html.ToLower();
                                }
                            }
                        }
                        //如果列是枚举，直接使用枚举的文本作为多语言的Key查询多语言文字
                        else if (ptype != null && ptype.IsEnumOrNullableEnum())
                        {
                            if (enumToString == true)
                            {
                                string enumdisplay = ptype != null ? PropertyHelper.GetEnumDisplayName(ptype, html) : "";
                                if (string.IsNullOrEmpty(enumdisplay) == false)
                                {
                                    html = enumdisplay;
                                }
                            }
                        }
                        //If this column is a class or list, html will be set to a json string, sest inner to true to remove the "
                        if (returnColumnObject == true && ptype?.Namespace?.Equals("System") == false && ptype != null && ptype.IsEnumOrNullableEnum() == false)
                        {
                            inner = true;
                        }
                    }
                    if (enumToString == false && string.IsNullOrEmpty(html))
                    {
                        continue;
                    }

                }
                else
                {
                    string val = col.GetText(sou)?.ToString() ?? "";
                    string name = $"{self.DetailGridPrix}[{index}].{col.Field}";
                    switch (col.EditType)
                    {
                        case EditTypeEnum.TextBox:
                            html = (self as BaseVM)?.UIService?.MakeTextBox(name, val,null,col.IsReadOnly)?.ToString() ?? "";
                            break;
                        case EditTypeEnum.CheckBox:
                            _ = bool.TryParse(val, out bool nb);
                            html = (self as BaseVM)?.UIService?.MakeCheckBox(nb, null, name, "true",col.IsReadOnly)?.ToString() ?? "";
                            break;
                        case EditTypeEnum.ComboBox:
                            html = (self as BaseVM)?.UIService?.MakeCombo(name, col.ListItems, val,null,col.IsReadOnly)?.ToString() ?? "";
                            break;
                        case EditTypeEnum.Datetime:
                            html = (self as BaseVM)?.UIService?.MakeDateTime(name, val,null, col.IsReadOnly,col.DateType)?.ToString() ?? "";
                            break;
                        default:
                            break;
                    }
                }
                if (string.IsNullOrEmpty(self.DetailGridPrix) == false && addHiddenID == false)
                {
                    html += $@"<input hidden name='{self.DetailGridPrix}[{index}].ID' value='{sou.GetID()}'/>";
                    addHiddenID = true;
                }
                if (inner == false)
                {
                    html = "\"" + (html?.RemoveSpecialChar() ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                }
                sb.Append($"\"{col.Field}\":");
                sb.Append(html);
                sb.Append(',');
            }
            sb.Append($"\"TempIsSelected\":\"{ (isSelected == true ? "1" : "0") }\"");
            foreach (var cc in colorcolumns)
            {
                // grid-002: validate color values against a strict allowlist (hex or CSS named color)
                // before emitting __bgcolor/__forecolor into the JSON. A DB-driven color field could
                // carry script/CSS injection payloads. ValidateColor drops invalid values (returns null).
                if (string.IsNullOrEmpty(cc.Value.Item1) == false)
                {
                    var bg = ValidateColor(cc.Value.Item1);
                    if (bg != null)
                    {
                        sb.Append($",\"{cc.Key}__bgcolor\":\"{bg}\"");
                    }
                }
                if (string.IsNullOrEmpty(cc.Value.Item2) == false)
                {
                    var fore = ValidateColor(cc.Value.Item2);
                    if (fore != null)
                    {
                        // HTML-encode the forecolor when used in a style attribute context
                        // so that quote/angle-bracket payloads cannot break attribute boundaries.
                        sb.Append($",\"{cc.Key}__forecolor\":\"{WebUtility.HtmlEncode(fore)}\"");
                    }
                }
            }
            if (containsID == false)
            {
                sb.Append($",\"ID\":\"{(sou as dynamic).ID}\"");
            }
            // 标识当前行数据是否被选中
            sb.Append($@",""LAY_CHECKED"":{sou.Checked.ToString().ToLower()}");
            sb.Append(string.Empty);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Get json format string of ListVM's search result
        /// </summary>
        /// <typeparam name="T">Model type</typeparam>
        /// <param name="self">a listvm</param>
        /// <param name="PlainText">true to return plain text, false to return formated html, such as checkbox,buttons ...</param>
        /// <param name="enumToString">use enum display name</param>
        /// <param name="func">other key,value needed to be returned</param>
        /// <returns>json string</returns>
        public static string GetJson<T>(this IBasePagedListVM<T, BaseSearcher> self, bool PlainText = true, bool enumToString = true, Func<Dictionary<string, object>>? func = null) where T : TopBasePoco, new()
        {
            if(self.Searcher.IsPlainText != null)
            {
                PlainText = self.Searcher.IsPlainText.Value;
            }
            if (self.Searcher.IsEnumToString != null)
            {
                enumToString = self.Searcher.IsEnumToString.Value;
            }
            if (!self.IsSearched) self.DoSearch();

            StringBuilder builder = new("{", capacity: 1024);
            var dic = func?.Invoke();

            // 如果用户的附加字典不为空，则添加用户自定义的信息
            if (dic != null) foreach (var item in dic) builder.Append($"\"{item.Key}\":\"{item.Value}\",");

            // 设置wtm必要的数据
            builder
                .Append($"\"Code\":200,")
                .Append($"\"Count\":{self.Searcher.Count},")
                .Append($"\"Data\":{self.GetDataJson(PlainText, enumToString)},")
                .Append($"\"Msg\":\"success\",")
                .Append($"\"Page\":{self.Searcher.Page},")
                .Append($"\"PageCount\":{self.Searcher.PageCount}")
                .Append('}');
            return builder.ToString();
        }

        public static object GetJsonForApi<T>(this IBasePagedListVM<T, BaseSearcher> self, bool PlainText = true) where T : TopBasePoco, new()
        {
            return new { Data = self.GetEntityList(), Count = self.Searcher.Count, PageCount = self.Searcher.PageCount, Page = self.Searcher.Page, Msg = "success", Code = 200 };
        }


        public static string GetError<T>(this IBasePagedListVM<T, BaseSearcher> self) where T : TopBasePoco, new()
        {
            // Escape backslash and double-quote so the error text cannot break the JSON string,
            // mirroring the same escaping used in GetSingleDataJson (~line 313).
            var rawError = (self as BaseVM)?.MSD?.GetFirstError() ?? "";
            var escapedError = rawError.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return $@"{{""Data"":{{}},""Count"":0,""Page"":0,""PageCount"":0,""Msg"":""{escapedError} "",""Code"":400}}";
        }


    }
}
