#nullable enable
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.ConfigOptions;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.TagHelpers.LayUI.Common
{
    public sealed class LayuiUIService : IUIService
    {
        private readonly WtmUIOptions _uiOptions;

        // Issue #470 Slice M: DI-injected IOptions<WtmUIOptions> (registered via
        // services.Configure<WtmUIOptions>(...) in FrameworkServiceExtension.
        // AddWtmContext, resolved lazily by AddSingleton<IUIService, LayuiUIService>)
        // — the same flag Slices J/K/L read via BaseFieldTag.UIConfig /
        // WtmUIOptionsHolder, just reached through constructor injection since
        // LayuiUIService is DI-activated rather than a TagHelper. The optional
        // default keeps `new LayuiUIService()` working for existing callers
        // (flag OFF, same as pre-Slice-M default behavior).
        public LayuiUIService(IOptions<WtmUIOptions>? uiOptions = null)
        {
            _uiOptions = uiOptions?.Value ?? new WtmUIOptions();
        }

        // Issue #470 Slice M: matches a bare no-arg function-call expression only
        // (e.g. "myFunc()") — see MakeScriptButton for the rationale (mirrors
        // SubmitButtonTagHelper's stricter-than-usual identifier check, since a
        // developer 'script' can be an arbitrary compound expression and
        // truncating it would silently drop behavior, not just callback args).
        private static readonly Regex _bareCallRegex = new(@"^[A-Za-z_$][\w$]*\(\)$", RegexOptions.Compiled);

        // Issue #470 Slice M: shared anchor builder for the island (data-wtm-click)
        // render path — Link/Button styling is identical to the legacy anchor
        // markup each Make* method already emits; only the click-wiring attribute
        // (onclick='...' vs data-wtm-click='...' + data-wtm-*) differs between the
        // flag-OFF and flag-ON paths.
        private static string BuildAnchor(ButtonTypesEnum buttonType, string buttonID, string clickAttr, string encodedButtonText, string? buttonClass, string? style)
        {
            string rv = "";
            if (buttonType == ButtonTypesEnum.Link)
            {
                rv = $"<a id='{buttonID}' {clickAttr} style='{style ?? "color:blue;cursor:pointer"}' class='{buttonClass ?? ""}'>{encodedButtonText}</a>";
            }
            if (buttonType == ButtonTypesEnum.Button)
            {
                rv = $"<a id='{buttonID}' {clickAttr} style='{style ?? ""}' class='layui-btn {(string.IsNullOrEmpty(buttonClass) ? "layui-btn-primary layui-btn-xs" : $"{buttonClass}")}'>{encodedButtonText}</a>";
            }
            return rv;
        }

        public string MakeDialogButton(ButtonTypesEnum buttonType, string url, string buttonText, int? width, int? height, string? title = null, string? buttonID = null, bool showDialog = true, bool resizable = true, bool max = false, string? buttonClass = null,string? style=null)
        {
            if (buttonID == null)
            {
                buttonID = Guid.NewGuid().ToString();
            }

            // Issue #470 Slice M: opt-in (WtmUIOptions.UseSelectIslandRender,
            // default OFF — the SAME flag #470 Slices J/K/L use) delegated
            // data-wtm-click dispatch instead of a generated per-button
            // <script>function x{guid}click(){...}</script> + onclick. This branch
            // returns early so the flag-OFF path below is completely untouched —
            // byte-identical to pre-Slice-M output.
            if (_uiOptions.UseSelectIslandRender)
            {
                var encodedButtonTextIsland = WebUtility.HtmlEncode(buttonText);
                string clickAttrIsland;
                if (showDialog == true)
                {
                    var windowidIsland = Guid.NewGuid().ToNoSplitString();
                    clickAttrIsland = $"data-wtm-click='openDialog' data-wtm-url='{WebUtility.HtmlEncode(url)}' data-wtm-winid='{WebUtility.HtmlEncode(windowidIsland)}' data-wtm-title='{WebUtility.HtmlEncode(title ?? "")}' data-wtm-width='{WebUtility.HtmlEncode(width?.ToString() ?? "")}' data-wtm-height='{WebUtility.HtmlEncode(height?.ToString() ?? "")}' data-wtm-max='{max.ToString().ToLower()}'";
                }
                else
                {
                    clickAttrIsland = $"data-wtm-click='runAction' data-wtm-url='{WebUtility.HtmlEncode(url)}'";
                }
                return BuildAnchor(buttonType, buttonID, clickAttrIsland, encodedButtonTextIsland, buttonClass, style);
            }

            var innerClick = "";
            string windowid = Guid.NewGuid().ToString();
            if (showDialog == true)
            {
                innerClick = $"ff.OpenDialog('{url}','{Guid.NewGuid().ToNoSplitString()}','{title ?? ""}',{width?.ToString() ?? "null"},{height?.ToString() ?? "null"},undefined,{max.ToString().ToLower()});";
            }
            else
            {
                innerClick = $"ff.RunAction('{url}');";  // Issue #789 Phase 3C: CSP-safe dispatcher (replaces inline AJAX + eval)
            }
            string funcname = $"x{buttonID.Replace("-", "")}click";
            var click = $"<script>function {funcname}(){{{innerClick};return false;}}</script>";
            // TLU-SEC-004: HtmlEncode buttonText (HTML text node context) to prevent XSS
            // when a button label is populated from user/DB data.
            var encodedButtonText = WebUtility.HtmlEncode(buttonText);
            string rv = "";
            if (buttonType == ButtonTypesEnum.Link)
            {
                rv = $"<a id='{buttonID}' onclick='{funcname}()' style='{style ?? "color:blue;cursor:pointer"}' class='{buttonClass ?? ""}'>{encodedButtonText}</a>";
            }
            if (buttonType == ButtonTypesEnum.Button)
            {
                rv = $"<a id='{buttonID}' onclick='{funcname}()' style='{style ?? ""}' class='layui-btn {(string.IsNullOrEmpty(buttonClass) ? "layui-btn-primary layui-btn-xs" : $"{buttonClass}")}'>{encodedButtonText}</a>";
            }
            rv += click;
            return rv;
        }

        public string MakeDownloadButton(ButtonTypesEnum buttonType, Guid fileID, string? buttonText = null, string _DONOT_USE_CS = "default", string? buttonClass = null, string? style = null)
        {
            // Issue #470 Slice M: MakeDownloadButton never emitted any inline
            // <script>/onclick — it is a plain <a href='...'> — so there is nothing
            // to islandify here; it is already flag-independent and eval-free.
            // TLU-SEC-004: HtmlEncode buttonText in HTML text node context.
            var encodedButtonText = WebUtility.HtmlEncode(buttonText);
            string rv = "";
            if (buttonType == ButtonTypesEnum.Link)
            {
                rv = $"<a  style='{style ?? "color:blue;cursor:pointer"}' class='{buttonClass ?? ""}' href='/_Framework/GetFile/{fileID}?_DONOT_USE_CS={_DONOT_USE_CS}'>{encodedButtonText}</a>";
            }
            if (buttonType == ButtonTypesEnum.Button)
            {
                rv = $"<a  style='{style ?? ""}' class='layui-btn {(string.IsNullOrEmpty(buttonClass) ? "layui-btn-primary layui-btn-xs" : $"{buttonClass}")}' href='/_Framework/GetFile/{fileID}?_DONOT_USE_CS={_DONOT_USE_CS}'>{encodedButtonText}</a>";
            }
            return rv;
        }

        public string MakeCheckBox(bool ischeck, string? text = null, string? name = null, string? value = null, bool isReadOnly = false)
        {
            var disable = isReadOnly ? " disabled='' class='layui-disabled'" : " ";
            var selected = ischeck ? " checked" : " ";
            return $@"<input lay-skin='primary' type='checkbox' name='{WebUtility.HtmlEncode(name ?? "")}' id='{(name == null ? "" : Utils.GetIdByName(name))}' value='{WebUtility.HtmlEncode(value ?? "")}' title='{WebUtility.HtmlEncode(text ?? "")}' {selected} {disable}/>";
        }

        public string MakeRadio(bool ischeck, string? text = null, string? name = null, string? value = null, bool isReadOnly = false)
        {
            var selected = ischeck ? " checked" : " ";
            var disable = isReadOnly ? " disabled='' class='layui-disabled'" : " ";
            return $@"<input lay-skin='primary' type='radio' name='{WebUtility.HtmlEncode(name ?? "")}' id='{(name == null ? "" : Utils.GetIdByName(name))}' value='{WebUtility.HtmlEncode(value ?? "")}' title='{WebUtility.HtmlEncode(text ?? "")}' {selected} {disable}/>";
        }

        public string MakeCombo(string? name = null, List<ComboSelectListItem>? value = null, string? selectedValue = null, string? emptyText = null, bool isReadOnly = false)
        {
            var disable = isReadOnly ? " disabled='' class='layui-disabled'" : " ";
            var sb = new StringBuilder();
            sb.Append($"<select name='{WebUtility.HtmlEncode(name ?? "")}' id='{(name == null ? "" : Utils.GetIdByName(name))}' class='layui-input' style='height:28px'   {disable} lay-ignore>");
            if (string.IsNullOrEmpty(emptyText) == false)
            {
                sb.Append($@"
<option value=''>{WebUtility.HtmlEncode(emptyText)}</option>");
            }
            if (value != null)
            {
                foreach (var item in value)
                {
                    if (string.Equals(item.Value?.ToString(), selectedValue, StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append($@"
<option value='{WebUtility.HtmlEncode(item.Value?.ToString() ?? "")}' selected>{WebUtility.HtmlEncode(item.Text ?? "")}</option>");

                    }
                    else
                    {
                        sb.Append($@"
<option value='{WebUtility.HtmlEncode(item.Value?.ToString() ?? "")}'>{WebUtility.HtmlEncode(item.Text ?? "")}</option>");
                    }
                }
            }
            sb.Append($@"
</select>
");
            return sb.ToString();
        }

        public string MakeTextBox(string? name = null, string? value = null, string? emptyText = null, bool isReadOnly = false)
        {
            var disable = isReadOnly ? " disabled='' class='layui-disabled'" : " ";
            return $@"<input class='layui-input' style='height:28px'  name='{WebUtility.HtmlEncode(name ?? "")}' id='{(name == null ? "" : Utils.GetIdByName(name))}' value='{WebUtility.HtmlEncode(value ?? "")}' {disable} />";
        }

        public string MakeDateTime(string? name = null, string? value = null, string? emptyText = null, bool isReadOnly = false, DateTimeTypeEnum? dateType = DateTimeTypeEnum.DateTime)
        {
            var id = (name == null ? "" : Utils.GetIdByName(name));
            var effectiveDateType = (dateType ?? DateTimeTypeEnum.DateTime).ToString().ToLower();
            var disable = isReadOnly ? " disabled='' class='layui-disabled'" : " ";
            if (string.IsNullOrEmpty(value) == false)
            {
                DateTime p = DateTime.MinValue;
                DateTime.TryParse(value, out p);
                if(p == DateTime.MinValue)
                {
                    value = "";
                }
            }

            // Issue #470 Slice M: grid-cell wiring — opt-in (UseSelectIslandRender,
            // default OFF) delegated data-wtm-click='dateClick' dispatch of the
            // SAME fixed ff.SetGridCellDate(id, type) call, instead of a raw
            // onclick='ff.SetGridCellDate("...","...")' attribute built from
            // un-HtmlEncoded interpolation. Early return keeps the flag-OFF path
            // below byte-identical to pre-Slice-M output.
            if (_uiOptions.UseSelectIslandRender)
            {
                return $@"<input class='layui-input' style='height:28px'  name='{WebUtility.HtmlEncode(name ?? "")}' id='{id}' value='{WebUtility.HtmlEncode(value ?? "")}' {disable} data-wtm-click='dateClick' data-wtm-date-id='{WebUtility.HtmlEncode(id)}' data-wtm-date-type='{WebUtility.HtmlEncode(effectiveDateType)}'/>";
            }

            return $@"<input class='layui-input' style='height:28px'  name='{WebUtility.HtmlEncode(name ?? "")}' id='{id}' value='{WebUtility.HtmlEncode(value ?? "")}' {disable}  onclick='ff.SetGridCellDate(""{id}"",""{effectiveDateType}"")'/>";
        }


        public string MakeButton(ButtonTypesEnum buttonType, string url, string buttonText, int? width, int? height, string? title = null, string? buttonID = null, bool resizable = true, bool max = false, string currentdivid = "", string? buttonClass = null, string? style = null, RedirectTypesEnum rtype= RedirectTypesEnum.Layer)
        {
            if (buttonID == null)
            {
                buttonID = Guid.NewGuid().ToString();
            }

            // Issue #470 Slice M: same opt-in delegated data-wtm-click dispatch as
            // MakeDialogButton — see that method's comment for the rationale.
            // Early return keeps the flag-OFF path below byte-identical.
            if (_uiOptions.UseSelectIslandRender)
            {
                var encodedButtonTextIsland = WebUtility.HtmlEncode(buttonText);
                string clickAttrIsland = "";
                switch (rtype)
                {
                    case RedirectTypesEnum.Layer:
                        var windowidIsland = Guid.NewGuid().ToNoSplitString();
                        clickAttrIsland = $"data-wtm-click='openDialog' data-wtm-url='{WebUtility.HtmlEncode(url)}' data-wtm-winid='{WebUtility.HtmlEncode(windowidIsland)}' data-wtm-title='{WebUtility.HtmlEncode(title ?? "")}' data-wtm-width='{WebUtility.HtmlEncode(width?.ToString() ?? "")}' data-wtm-height='{WebUtility.HtmlEncode(height?.ToString() ?? "")}' data-wtm-max='{max.ToString().ToLower()}'";
                        break;
                    case RedirectTypesEnum.Self:
                        clickAttrIsland = $"data-wtm-click='bgRequest' data-wtm-url='{WebUtility.HtmlEncode(url)}' data-wtm-divid='{WebUtility.HtmlEncode(currentdivid)}'";
                        break;
                    case RedirectTypesEnum.NewWindow:
                        clickAttrIsland = $"data-wtm-click='loadPage' data-wtm-url='{WebUtility.HtmlEncode(url)}' data-wtm-title='{WebUtility.HtmlEncode(title ?? "")}' data-wtm-newwindow='true'";
                        break;
                    case RedirectTypesEnum.NewTab:
                        clickAttrIsland = $"data-wtm-click='loadPage' data-wtm-url='{WebUtility.HtmlEncode(url)}' data-wtm-title='{WebUtility.HtmlEncode(title ?? "")}' data-wtm-newwindow='false'";
                        break;
                    default:
                        break;
                }
                return BuildAnchor(buttonType, buttonID, clickAttrIsland, encodedButtonTextIsland, buttonClass, style);
            }

            var innerClick = "";
            string windowid = Guid.NewGuid().ToString();

            switch (rtype)
            {
                case RedirectTypesEnum.Layer:
                    innerClick = $"ff.OpenDialog('{url}','{Guid.NewGuid().ToNoSplitString()}','{title ?? ""}',{width?.ToString() ?? "null"},{height?.ToString() ?? "null"},undefined,{max.ToString().ToLower()});";
                    break;
                case RedirectTypesEnum.Self:
                    innerClick = $"ff.BgRequest('{url}',undefined,'{currentdivid}');";
                    break;
                case RedirectTypesEnum.NewWindow:
                    innerClick = $"ff.LoadPage('{url}',true,'{title ?? ""}');";
                    break;
                case RedirectTypesEnum.NewTab:
                    innerClick = $"ff.LoadPage('{url}',false,'{title ?? ""}');";
                    break;
                default:
                    break;
            }
            string funcname = $"x{buttonID.Replace("-", "")}click";
            var click = $"<script>function {funcname}(){{{innerClick};return false;}}</script>";
            // TLU-SEC-004: HtmlEncode buttonText in HTML text node context.
            var encodedButtonText = WebUtility.HtmlEncode(buttonText);
            string rv = "";
            if (buttonType == ButtonTypesEnum.Link)
            {
                rv = $"<a id='{buttonID}' onclick='{funcname}()' style='{style ?? "color:blue;cursor:pointer"}' class='{buttonClass ?? ""}'>{encodedButtonText}</a>";
            }
            if (buttonType == ButtonTypesEnum.Button)
            {
                rv = $"<a id='{buttonID}' onclick='{funcname}()' style='{style ?? ""}' class='layui-btn {(string.IsNullOrEmpty(buttonClass) ? "layui-btn-primary layui-btn-xs" : $"{buttonClass}")}'>{encodedButtonText}</a>";
            }
            rv += click;
            return rv;
        }

        public string MakeViewButton(ButtonTypesEnum buttonType, Guid fileID, string? buttonText = null, int? width = null, int? height = null, string? title = null, bool resizable = true, string _DONOT_USE_CS = "default", bool maxed = false, string? buttonClass = null, string? style = null)
        {
            var  buttonID = Guid.NewGuid().ToString();
            var url = $"/_Framework/GetFile/{fileID}?_DONOT_USE_CS={_DONOT_USE_CS}";

            // Issue #470 Slice M: same opt-in delegated data-wtm-click dispatch —
            // 'view' invokes the SAME fixed layui.layer.photos(...) call
            // ff._buttonAction.view (framework_layui.js) makes. Early return keeps
            // the flag-OFF path below byte-identical.
            if (_uiOptions.UseSelectIslandRender)
            {
                var encodedButtonTextIsland = WebUtility.HtmlEncode(buttonText);
                var clickAttrIsland = $"data-wtm-click='view' data-wtm-url='{WebUtility.HtmlEncode(url)}'";
                switch (buttonType)
                {
                    case ButtonTypesEnum.Button:
                        return $"<a id='{buttonID}' {clickAttrIsland} style='{style ?? ""}' class='layui-btn {(string.IsNullOrEmpty(buttonClass) ? "layui-btn-primary layui-btn-xs" : $"{buttonClass}")}'>{encodedButtonTextIsland}</a>";
                    case ButtonTypesEnum.Link:
                        return $"<a id='{buttonID}' {clickAttrIsland} style='{style ?? "color:blue;cursor:pointer"}' class='{buttonClass ?? ""}'>{encodedButtonTextIsland}</a>";
                    case ButtonTypesEnum.Img:
                        return $"<img src='{url}&width={width??50}&height={height??50}' id='{buttonID}' {clickAttrIsland} style='{style ?? "color:blue;cursor:pointer"}' class='{buttonClass ?? ""}'/>";
                    default:
                        return "";
                }
            }

            var innerClick = "";
            string windowid = Guid.NewGuid().ToString();
            innerClick = $"layui.layer.photos({{photos: {{data: [{{src: '{url}'}}]}},anim: 5}});";
            string funcname = $"x{buttonID.Replace("-", "")}click";
            var click = $"<script>function {funcname}(){{{innerClick};return false;}}</script>";
            // TLU-SEC-004: HtmlEncode buttonText in HTML text node context.
            var encodedButtonText = WebUtility.HtmlEncode(buttonText);
            string rv = "";
            switch (buttonType)
            {
                case ButtonTypesEnum.Button:
                    rv = $"<a id='{buttonID}' onclick='{funcname}()' style='{style ?? ""}' class='layui-btn {(string.IsNullOrEmpty(buttonClass) ? "layui-btn-primary layui-btn-xs" : $"{buttonClass}")}'>{encodedButtonText}</a>";
                    break;
                case ButtonTypesEnum.Link:
                    rv = $"<a id='{buttonID}' onclick='{funcname}()' style='{style ?? "color:blue;cursor:pointer"}' class='{buttonClass ?? ""}'>{encodedButtonText}</a>";
                    break;
                case ButtonTypesEnum.Img:
                    rv = $"<img src='{url}&width={width??50}&height={height??50}' id='{buttonID}' onclick='{funcname}()' style='{style ?? "color:blue;cursor:pointer"}' class='{buttonClass ?? ""}'/>";
                    break;
                default:
                    break;
            }
            rv += click;
            return rv;
        }

        public string MakeScriptButton(ButtonTypesEnum buttonType, string buttonText, string script = "", string? buttonID = null, string? url = null, string? buttonClass = null, string? style=null)
        {
            if (buttonID == null)
            {
                buttonID = Guid.NewGuid().ToString();
            }
            // TLU-SEC-004: HtmlEncode buttonText in HTML text node context.
            var encodedButtonText = WebUtility.HtmlEncode(buttonText);

            // Issue #470 Slice M: MakeScriptButton's 'script' is a DEVELOPER-
            // SUPPLIED expression (the exception to the "these are all fixed
            // framework calls" rule the rest of this file follows) — never
            // eval'd, always run as real inline JS. Under the flag, ONLY a bare
            // no-arg function-call expression (e.g. "myFunc()") is treated as
            // island-safe and resolved through the SAME guarded
            // ff._resolveGuardedWindowFn(name) every other named-callback action
            // uses; anything else (a raw statement, multiple statements, a call
            // with arguments) is NOT reducible to a safe delegated dispatch and
            // keeps the exact legacy inline <script> path, with a console.warn
            // surfaced so the fallback is visible during migration.
            if (_uiOptions.UseSelectIslandRender)
            {
                var trimmedScript = (script ?? "").Trim();
                if (trimmedScript.Length > 0 && _bareCallRegex.IsMatch(trimmedScript))
                {
                    var fnName = trimmedScript[..^2];
                    var clickAttrIsland = $"data-wtm-click='scriptCall' data-wtm-fn='{WebUtility.HtmlEncode(fnName)}'";
                    return BuildAnchor(buttonType, buttonID, clickAttrIsland, encodedButtonText, buttonClass, style);
                }

                var warn = $"console.warn('[WTM] MakeScriptButton #{buttonID}: script is not a bare no-arg function call — UseSelectIslandRender is ON but island render was skipped for this button; keeping the legacy inline script. See #470 Slice M.');\n";
                var warnedClick = $"<script>{warn}$('#{buttonID}').on('click',function(){{{script};return false;}});</script>";
                string rvWarned = "";
                if (buttonType == ButtonTypesEnum.Link)
                {
                    rvWarned = $"<a id='{buttonID}'  style='{style ?? "color:blue;cursor:pointer"}' class='{buttonClass ?? ""}'>{encodedButtonText}</a>";
                }
                if (buttonType == ButtonTypesEnum.Button)
                {
                    rvWarned = $"<a id='{buttonID}' style='{style ?? ""}' class='layui-btn {(string.IsNullOrEmpty(buttonClass) ? "layui-btn-primary layui-btn-xs" : $"{buttonClass}")}'>{encodedButtonText}</a>";
                }
                rvWarned += warnedClick;
                return rvWarned;
            }

            var innerClick = script;
            var click = $"<script>$('#{buttonID}').on('click',function(){{{innerClick};return false;}});</script>";
            string rv = "";
            if (buttonType == ButtonTypesEnum.Link)
            {
                rv = $"<a id='{buttonID}'  style='{style ?? "color:blue;cursor:pointer"}' class='{buttonClass ?? ""}'>{encodedButtonText}</a>";
            }
            if (buttonType == ButtonTypesEnum.Button)
            {
                rv = $"<a id='{buttonID}' style='{style ?? ""}' class='layui-btn {(string.IsNullOrEmpty(buttonClass) ? "layui-btn-primary layui-btn-xs" : $"{buttonClass}")}'>{encodedButtonText}</a>";
            }
            rv += click;
            return rv;
        }
    }
}
