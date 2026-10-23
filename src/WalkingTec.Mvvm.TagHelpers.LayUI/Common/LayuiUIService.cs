using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Options;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.TagHelpers.LayUI.Common
{
    public class LayuiUIService : IUIService
    {
        private readonly IOptionsMonitor<Configs> _configs;

        public LayuiUIService()
        {
        }

        public LayuiUIService(IOptionsMonitor<Configs> configs)
        {
            _configs = configs;
        }

        /// <summary>
        /// Text placed into HTML content; HTML-encoded when UIOptions.EncodeHtmlContent is enabled.
        /// </summary>
        private string HtmlText(string text)
        {
            return _configs?.CurrentValue?.UIOptions?.EncodeHtmlContent == true ? HtmlAttributeEncoder.Encode(text) : text;
        }

        public string MakeDialogButton(ButtonTypesEnum buttonType, string url, string buttonText, int? width, int? height, string title = null, string buttonID = null, bool showDialog = true, bool resizable = true, bool max = false, string buttonClass = null,string style=null)
        {
            if (buttonID == null)
            {
                buttonID = Guid.NewGuid().ToString();
            }
            var innerClick = "";
            string windowid = Guid.NewGuid().ToString();
            if (showDialog == true)
            {
                innerClick = $"ff.OpenDialog('{JsStringEncoder.Escape(url)}','{Guid.NewGuid().ToNoSplitString()}','{JsStringEncoder.Escape(title)}',{width?.ToString() ?? "null"},{height?.ToString() ?? "null"},undefined,{max.ToString().ToLower()});";
            }
            else
            {
                innerClick = $"$.ajax({{cache: false,type: 'GET',url: '{JsStringEncoder.Escape(url)}',async: true,success: function(data, textStatus, request) {{ff.ExecServerScript(data);}} }});";
            }
            string funcname = $"x{buttonID.Replace("-", "")}click";
            var click = $"<script>function {funcname}(){{{innerClick};return false;}}</script>";
            string rv = "";
            if (buttonType == ButtonTypesEnum.Link)
            {
                rv = $"<a id='{HtmlAttributeEncoder.Encode(buttonID)}' onclick='{HtmlAttributeEncoder.Encode(funcname)}()' style='{HtmlAttributeEncoder.Encode(style ?? "color:blue;cursor:pointer")}' class='{HtmlAttributeEncoder.Encode(buttonClass)}'>{HtmlText(buttonText)}</a>";
            }
            if (buttonType == ButtonTypesEnum.Button)
            {
                rv = $"<a id='{HtmlAttributeEncoder.Encode(buttonID)}' onclick='{HtmlAttributeEncoder.Encode(funcname)}()' style='{HtmlAttributeEncoder.Encode(style)}' class='layui-btn {(string.IsNullOrEmpty(buttonClass) ? "layui-btn-primary layui-btn-xs" : $"{HtmlAttributeEncoder.Encode(buttonClass)}")}'>{HtmlText(buttonText)}</a>";
            }
            rv += click;
            return rv;
        }

        public string MakeDownloadButton(ButtonTypesEnum buttonType, Guid fileID, string buttonText = null, string _DONOT_USE_CS = "default", string buttonClass = null, string style = null)
        {
            return MakeDownloadButtonWithReference(buttonType, fileID, null, buttonText, _DONOT_USE_CS, buttonClass, style);
        }

        /// <summary>The link carries the file reference when there is one (see <see cref="FileEndpointGuard"/>).</summary>
        public string MakeDownloadButtonWithReference(ButtonTypesEnum buttonType, Guid fileID, string fileReference, string buttonText = null, string _DONOT_USE_CS = "default", string buttonClass = null, string style = null)
        {
            string href = $"/_Framework/GetFile/{fileID}?_DONOT_USE_CS={HtmlAttributeEncoder.Encode(_DONOT_USE_CS)}{FileReferenceQuery(fileReference, true)}";
            string rv = "";
            if (buttonType == ButtonTypesEnum.Link)
            {
                rv = $"<a  style='{HtmlAttributeEncoder.Encode(style ?? "color:blue;cursor:pointer")}' class='{HtmlAttributeEncoder.Encode(buttonClass)}' href='{href}'>{HtmlText(buttonText)}</a>";
            }
            if (buttonType == ButtonTypesEnum.Button)
            {
                rv = $"<a  style='{HtmlAttributeEncoder.Encode(style)}' class='layui-btn {(string.IsNullOrEmpty(buttonClass) ? "layui-btn-primary layui-btn-xs" : $"{HtmlAttributeEncoder.Encode(buttonClass)}")}' href='{href}'>{HtmlText(buttonText)}</a>";
            }
            return rv;
        }

        /// <summary>The query string part that carries a file reference (empty when there is none). References contain only url safe characters.</summary>
        private static string FileReferenceQuery(string fileReference, bool encodeForAttribute)
        {
            if (string.IsNullOrEmpty(fileReference))
            {
                return string.Empty;
            }
            var part = "&" + FileEndpointGuard.ReferenceParameter + "=" + Uri.EscapeDataString(fileReference);
            return encodeForAttribute ? HtmlAttributeEncoder.Encode(part) : part;
        }

        public string MakeCheckBox(bool ischeck, string text = null, string name = null, string value = null, bool isReadOnly = false)
        {
            var disable = isReadOnly ? " disabled='' class='layui-disabled'" : " ";
            var selected = ischeck ? " checked" : " ";
            return $@"<input lay-skin='primary' type='checkbox' name='{HtmlAttributeEncoder.Encode(name)}' id='{HtmlAttributeEncoder.Encode(name == null ? "" : Utils.GetIdByName(name))}' value='{HtmlAttributeEncoder.Encode(value)}' title='{HtmlAttributeEncoder.Encode(HtmlAttributeEncoder.EncodeOnce(text))}' {selected} {disable}/>";
        }

        public string MakeRadio(bool ischeck, string text = null, string name = null, string value = null, bool isReadOnly = false)
        {
            var selected = ischeck ? " checked" : " ";
            var disable = isReadOnly ? " disabled='' class='layui-disabled'" : " ";
            return $@"<input lay-skin='primary' type='radio' name='{HtmlAttributeEncoder.Encode(name)}' id='{HtmlAttributeEncoder.Encode(name == null ? "" : Utils.GetIdByName(name))}' value='{HtmlAttributeEncoder.Encode(value)}' title='{HtmlAttributeEncoder.Encode(HtmlAttributeEncoder.EncodeOnce(text))}' {selected} {disable}/>";
        }

        public string MakeCombo(string name = null, List<ComboSelectListItem> value = null, string selectedValue = null, string emptyText = null, bool isReadOnly = false)
        {
            var disable = isReadOnly ? " disabled='' class='layui-disabled'" : " ";
            string rv = $"<select name='{HtmlAttributeEncoder.Encode(name)}' id='{HtmlAttributeEncoder.Encode(name == null ? "" : Utils.GetIdByName(name))}' class='layui-input' style='height:28px'   {disable} lay-ignore>";
            if (string.IsNullOrEmpty(emptyText) == false)
            {
                rv += $@"
<option value=''>{HtmlText(emptyText)}</option>";
            }
            if (value != null)
            {
                foreach (var item in value)
                {
                    if (item.Value.ToString().ToLower() == selectedValue?.ToLower())
                    {
                        rv += $@"
<option value='{HtmlAttributeEncoder.Encode(item.Value)}' selected>{HtmlText(item.Text)}</option>";

                    }
                    else
                    {
                        rv += $@"
<option value='{HtmlAttributeEncoder.Encode(item.Value)}'>{HtmlText(item.Text)}</option>";
                    }
                }
            }
            rv += $@"
</select>
";
            return rv;
        }

        public string MakeTextBox(string name = null, string value = null, string emptyText = null, bool isReadOnly = false)
        {
            var disable = isReadOnly ? " disabled='' class='layui-disabled'" : " ";
            return $@"<input class='layui-input' style='height:28px'  name='{HtmlAttributeEncoder.Encode(name)}' id='{HtmlAttributeEncoder.Encode(name == null ? "" : Utils.GetIdByName(name))}' value='{HtmlAttributeEncoder.Encode(value)}' {disable} />";
        }

        public string MakeDateTime(string name = null, string value = null, string emptyText = null, bool isReadOnly = false, DateTimeTypeEnum? dateType = DateTimeTypeEnum.DateTime)
        {
            var id = (name == null ? "" : Utils.GetIdByName(name));
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
            return $@"<input class='layui-input' style='height:28px'  name='{HtmlAttributeEncoder.Encode(name)}' id='{HtmlAttributeEncoder.Encode(id)}' value='{HtmlAttributeEncoder.Encode(value)}' {disable}  onclick='ff.SetGridCellDate(""{JsStringEncoder.Escape(id)}"",""{dateType.ToString().ToLower()}"")'/>";
        }


        public string MakeButton(ButtonTypesEnum buttonType, string url, string buttonText, int? width, int? height, string title = null, string buttonID = null, bool resizable = true, bool max = false, string currentdivid = "", string buttonClass = null, string style = null, RedirectTypesEnum rtype= RedirectTypesEnum.Layer)
        {
            if (buttonID == null)
            {
                buttonID = Guid.NewGuid().ToString();
            }
            var innerClick = "";
            string windowid = Guid.NewGuid().ToString();

            switch (rtype)
            {
                case RedirectTypesEnum.Layer:
                    innerClick = $"ff.OpenDialog('{JsStringEncoder.Escape(url)}','{Guid.NewGuid().ToNoSplitString()}','{JsStringEncoder.Escape(title)}',{width?.ToString() ?? "null"},{height?.ToString() ?? "null"},undefined,{max.ToString().ToLower()});";
                    break;
                case RedirectTypesEnum.Self:
                    innerClick = $"ff.BgRequest('{JsStringEncoder.Escape(url)}',undefined,'{JsStringEncoder.Escape(currentdivid)}');";
                    break;
                case RedirectTypesEnum.NewWindow:
                    innerClick = $"ff.LoadPage('{JsStringEncoder.Escape(url)}',true,'{JsStringEncoder.Escape(title)}');";
                    break;
                case RedirectTypesEnum.NewTab:
                    innerClick = $"ff.LoadPage('{JsStringEncoder.Escape(url)}',false,'{JsStringEncoder.Escape(title)}');";
                    break;
                default:
                    break;
            }
            string funcname = $"x{buttonID.Replace("-", "")}click";
            var click = $"<script>function {funcname}(){{{innerClick};return false;}}</script>";
            string rv = "";
            if (buttonType == ButtonTypesEnum.Link)
            {
                rv = $"<a id='{HtmlAttributeEncoder.Encode(buttonID)}' onclick='{HtmlAttributeEncoder.Encode(funcname)}()' style='{HtmlAttributeEncoder.Encode(style ?? "color:blue;cursor:pointer")}' class='{HtmlAttributeEncoder.Encode(buttonClass)}'>{HtmlText(buttonText)}</a>";
            }
            if (buttonType == ButtonTypesEnum.Button)
            {
                rv = $"<a id='{HtmlAttributeEncoder.Encode(buttonID)}' onclick='{HtmlAttributeEncoder.Encode(funcname)}()' style='{HtmlAttributeEncoder.Encode(style)}' class='layui-btn {(string.IsNullOrEmpty(buttonClass) ? "layui-btn-primary layui-btn-xs" : $"{HtmlAttributeEncoder.Encode(buttonClass)}")}'>{HtmlText(buttonText)}</a>";
            }
            rv += click;
            return rv;
        }

        public string MakeViewButton(ButtonTypesEnum buttonType, Guid fileID, string buttonText = null, int? width = null, int? height = null, string title = null, bool resizable = true, string _DONOT_USE_CS = "default", bool maxed = false, string buttonClass = null, string style = null)
        {
            return MakeViewButtonWithReference(buttonType, fileID, null, buttonText, width, height, title, resizable, _DONOT_USE_CS, maxed, buttonClass, style);
        }

        /// <summary>The link carries the file reference when there is one (see <see cref="FileEndpointGuard"/>).</summary>
        public string MakeViewButtonWithReference(ButtonTypesEnum buttonType, Guid fileID, string fileReference, string buttonText = null, int? width = null, int? height = null, string title = null, bool resizable = true, string _DONOT_USE_CS = "default", bool maxed = false, string buttonClass = null, string style = null)
        {
            var  buttonID = Guid.NewGuid().ToString();
            var innerClick = "";
            string windowid = Guid.NewGuid().ToString();
            var url = $"/_Framework/GetFile/{fileID}?_DONOT_USE_CS={_DONOT_USE_CS}{FileReferenceQuery(fileReference, false)}";
            innerClick = $"layui.layer.photos({{photos: {{data: [{{src: '{JsStringEncoder.Escape(url)}'}}]}},anim: 5}});";
            string funcname = $"x{buttonID.Replace("-", "")}click";
            var click = $"<script>function {funcname}(){{{innerClick};return false;}}</script>";
            string rv = "";
            if (buttonType == ButtonTypesEnum.Link)
            {
                rv = $"<a id='{HtmlAttributeEncoder.Encode(buttonID)}' onclick='{HtmlAttributeEncoder.Encode(funcname)}()' style='{HtmlAttributeEncoder.Encode(style ?? "color:blue;cursor:pointer")}' class='{HtmlAttributeEncoder.Encode(buttonClass)}'>{HtmlText(buttonText)}</a>";
            }
            if (buttonType == ButtonTypesEnum.Button)
            {
                rv = $"<a id='{HtmlAttributeEncoder.Encode(buttonID)}' onclick='{HtmlAttributeEncoder.Encode(funcname)}()' style='{HtmlAttributeEncoder.Encode(style)}' class='layui-btn {(string.IsNullOrEmpty(buttonClass) ? "layui-btn-primary layui-btn-xs" : $"{HtmlAttributeEncoder.Encode(buttonClass)}")}'>{HtmlText(buttonText)}</a>";
            }
            switch (buttonType)
            {
                case ButtonTypesEnum.Button:
                    rv = $"<a id='{HtmlAttributeEncoder.Encode(buttonID)}' onclick='{HtmlAttributeEncoder.Encode(funcname)}()' style='{HtmlAttributeEncoder.Encode(style)}' class='layui-btn {(string.IsNullOrEmpty(buttonClass) ? "layui-btn-primary layui-btn-xs" : $"{HtmlAttributeEncoder.Encode(buttonClass)}")}'>{HtmlText(buttonText)}</a>";
                    break;
                case ButtonTypesEnum.Link:
                    rv = $"<a id='{HtmlAttributeEncoder.Encode(buttonID)}' onclick='{HtmlAttributeEncoder.Encode(funcname)}()' style='{HtmlAttributeEncoder.Encode(style ?? "color:blue;cursor:pointer")}' class='{HtmlAttributeEncoder.Encode(buttonClass)}'>{HtmlText(buttonText)}</a>";
                    break;
                case ButtonTypesEnum.Img:
                    rv = $"<img src='{HtmlAttributeEncoder.Encode(url)}&width={width??50}&height={height??50}' id='{HtmlAttributeEncoder.Encode(buttonID)}' onclick='{HtmlAttributeEncoder.Encode(funcname)}()' style='{HtmlAttributeEncoder.Encode(style ?? "color:blue;cursor:pointer")}' class='{HtmlAttributeEncoder.Encode(buttonClass)}'/>";
                    break;
                default:
                    break;
            }
            rv += click;
            return rv;
        }

        public string MakeScriptButton(ButtonTypesEnum buttonType, string buttonText, string script = "", string buttonID = null, string url = null, string buttonClass = null, string style=null)
        {
            if (buttonID == null)
            {
                buttonID = Guid.NewGuid().ToString();
            }
            var innerClick = script;
            var click = $"<script>$('#{buttonID}').on('click',function(){{{innerClick};return false;}});</script>";
            string rv = "";
            if (buttonType == ButtonTypesEnum.Link)
            {
                rv = $"<a id='{HtmlAttributeEncoder.Encode(buttonID)}'  style='{HtmlAttributeEncoder.Encode(style ?? "color:blue;cursor:pointer")}' class='{HtmlAttributeEncoder.Encode(buttonClass)}'>{HtmlText(buttonText)}</a>";
            }
            if (buttonType == ButtonTypesEnum.Button)
            {
                rv = $"<a id='{HtmlAttributeEncoder.Encode(buttonID)}' style='{HtmlAttributeEncoder.Encode(style)}' class='layui-btn {(string.IsNullOrEmpty(buttonClass) ? "layui-btn-primary layui-btn-xs" : $"{HtmlAttributeEncoder.Encode(buttonClass)}")}'>{HtmlText(buttonText)}</a>";
            }
            rv += click;
            return rv;
        }
    }
}
