using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Mvc
{
    public partial class CodeGenVM
    {
        public string GenerateBlazorView(string name)
        {
            string pagepath = string.IsNullOrEmpty(Area) ? $"/{ModelName}" : $"/{Area}/{ModelName}";
            if (name != "Index")
            {
                pagepath += $"/{name}";
            }
            if (name == "Edit" || name == "Details")
            {
                pagepath += "/{id}";
            }
            var rv = GetResource($"{name}.txt", "Spa.Blazor")
                .Replace("$modelname$", ModelName)
                .Replace("$vmnamespace$", VMNs)
                .Replace("$des$", ModuleName)
                .Replace("$controllername$", $"{ControllerNs},{ModelName}")
                .Replace("$pagepath$", pagepath);
            Type modelType = Type.GetType(SelectedModel);
            if (name == "Index")
            {
                StringBuilder fieldstr = new StringBuilder();
                StringBuilder fieldstr2 = new StringBuilder();
                var pros = FieldInfos.Where(x => x.IsListField == true).ToList();
                var pros2 = FieldInfos.Where(x => x.IsSearcherField == true).ToList();
                List<PropertyInfo> existSubPro = new List<PropertyInfo>();
                Dictionary<string, string> apis = new Dictionary<string, string>();
                Dictionary<string, string> multiapis = new Dictionary<string, string>();
                for (int i = 0; i < pros.Count; i++)
                {
                    var item = pros[i];
                    var mpro = modelType.GetSingleProperty(item.FieldName);
                    string render = "";
                    string template = "";
                    string newname = item.FieldName;
                    if (mpro.PropertyType.IsBoolOrNullableBool())
                    {
                        if (mpro.PropertyType.IsNullable())
                        {
                            render = "ComponentType=\"@typeof(NullSwitch)\"";
                        }
                        else
                        {
                            render = "ComponentType=\"@typeof(Switch)\"";
                        }
                    }
                    if (mpro.PropertyType == typeof(DateTime) || mpro.PropertyType == typeof(DateTime?))
                    {
                        render = "FormatString=\"yyyy-MM-dd HH: mm: ss\"";

                    }
                    if (string.IsNullOrEmpty(item.RelatedField) == false)
                    {
                        var subtype = Type.GetType(item.RelatedField);
                        string prefix = "";
                        if (subtype == typeof(FileAttachment))
                        {
                            if (item.FieldName.ToLower().Contains("photo") || item.FieldName.ToLower().Contains("pic") || item.FieldName.ToLower().Contains("icon") || item.FieldName.ToLower().Contains("zhaopian") || item.FieldName.ToLower().Contains("tupian"))
                            {
                                template = @"
            <Template Context=""data"">
                <Avatar @key=""data.Value"" Size=""Size.ExtraSmall"" GetUrlAsync=""()=>WtmBlazor.GetBase64Image(data.Value.ToString(),150,150)"" />
            </Template>";
                            }
                            else
                            {
                                template = @"
            <Template Context=""data"">
                @if (data.Value.HasValue){
                    <Button Size=""Size.ExtraSmall"" Text=""@WtmBlazor.Localizer[""Sys.Download""]"" OnClick=""@(async x => await Download($""/api/_file/DownloadFile/{data.Value}"",null, HttpMethodEnum.GET))"" />
                }
            </Template>";
                            }
                            var fk = DC.GetFKName2(modelType, item.FieldName);
                            newname = fk;
                        }
                        else
                        {
                            var subpro = subtype.GetSingleProperty(item.SubField);
                            existSubPro.Add(subpro);
                            int count = existSubPro.Where(x => x.Name == subpro.Name).Count();
                            if (count > 1)
                            {
                                prefix = count + "";
                            }
                            newname = item.SubField + "_view" + prefix;
                        }
                    }
                    if (template == "")
                    {
                        fieldstr.Append($@"
        <TableColumn @bind-Field=""@context.{newname}"" {render} />");
                    }
                    else
                    {
                        fieldstr.Append($@"
        <TableColumn @bind-Field=""@context.{newname}"" {render} >
{template}
        </TableColumn>");
                    }
                }

                for (int i = 0; i < pros2.Count; i++)
                {
                    string controltype = "BootstrapInput";
                    string sitems = "";
                    string bindfield = "";
                    string ph = "";
                    var item = pros2[i];
                    if (item.SubField == "`file")
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(item.RelatedField) == false)
                    {
                        if (string.IsNullOrEmpty(item.SubIdField) == true)
                        {
                            var fk = DC.GetFKName2(modelType, item.FieldName);
                            bindfield = fk;
                        }
                        else
                        {
                            bindfield = $"Selected{item.FieldName}IDs";
                        }
                    }
                    else
                    {
                        bindfield = item.FieldName;
                    }
                    if (string.IsNullOrEmpty(item.RelatedField) == false)
                    {
                        var subtype = Type.GetType(item.RelatedField);
                        if (string.IsNullOrEmpty(item.SubIdField) == true)
                        {
                            controltype = "Select";
                        }
                        else
                        {
                            controltype = "MultiSelect";
                        }
                        var tempname = $"All{subtype.Name}s";
                        sitems = $"Items=\"@{tempname}\"";
                        if (apis.ContainsKey(tempname) == false && multiapis.ContainsKey(tempname) == false)
                        {
                            if (controltype == "Select")
                            {
                                apis.Add(tempname, $"/api/{ModelName}/Get{subtype.Name}s");
                            }
                            else
                            {
                                multiapis.Add(tempname, $"/api/{ModelName}/Get{subtype.Name}s");
                            }
                        }
                    }
                    else
                    {
                        var proType = modelType.GetSingleProperty(item.FieldName)?.PropertyType;
                        Type checktype = proType;
                        if (proType.IsNullable())
                        {
                            checktype = proType.GetGenericArguments()[0];
                        }
                        if (checktype == typeof(bool))
                        {
                            controltype = "Select";
                            sitems = "Items=\"@WtmBlazor.GlobalSelectItems.SearcherBoolItems\"";
                        }
                        else if (checktype.IsEnum())
                        {
                            controltype = "Select";
                        }
                        else if (checktype.IsNumber())
                        {
                            controltype = "BootstrapInputNumber";
                        }
                        else if (checktype == typeof(string))
                        {
                        }
                        else if (checktype == typeof(DateTime))
                        {
                            controltype = "WTDateRange";
                        }
                    }
                    if (controltype == "Select" || controltype == "MultiSelect")
                    {
                        ph = "PlaceHolder=\"@WtmBlazor.Localizer[\"Sys.All\"]\"";
                    }
                    fieldstr2.Append($@"
            <{controltype} @bind-Value=""@SearchModel.{bindfield}"" {sitems} {ph}/>");
                }

                StringBuilder apiinit = new StringBuilder();
                StringBuilder fieldinit = new StringBuilder();
                foreach (var item in apis)
                {
                    apiinit.Append(@$"
        {item.Key} = await WtmBlazor.Api.CallItemsApi(""{item.Value}"", placeholder: WtmBlazor.Localizer[""Sys.All""]);
");
                    fieldinit.Append($@"
    private List<SelectedItem> {item.Key} = new List<SelectedItem>();
");
                }
                foreach (var item in multiapis)
                {
                    apiinit.Append(@$"
        {item.Key} = await WtmBlazor.Api.CallItemsApi(""{item.Value}"");
");
                    fieldinit.Append($@"
    private List<SelectedItem> {item.Key} = new List<SelectedItem>();
");
                }

                return rv.Replace("$columns$", fieldstr.ToString()).Replace("$searchfields$", fieldstr2.ToString()).Replace("$init$", apiinit.ToString()).Replace("$fieldinit$", fieldinit.ToString());
            }


            if (name == "Create" || name == "Edit")
            {
                StringBuilder fieldstr = new StringBuilder();
                var pros = FieldInfos.Where(x => x.IsFormField == true).ToList();

                //生成表单model
                Dictionary<string, string> apis = new Dictionary<string, string>();
                Dictionary<string, string> multiapis = new Dictionary<string, string>();
                for (int i = 0; i < pros.Count; i++)
                {
                    var item = pros[i];
                    string controltype = "BootstrapInput";
                    string sitems = "";
                    string bindfield = "";
                    string ph = "";
                    var property = modelType.GetSingleProperty(item.FieldName);

                    if (string.IsNullOrEmpty(item.RelatedField) == false)
                    {
                        if (string.IsNullOrEmpty(item.SubIdField) == true)
                        {
                            var fk = DC.GetFKName2(modelType, item.FieldName);
                            bindfield = "Entity." + fk;
                        }
                        else
                        {
                            bindfield = $"Selected{item.FieldName}IDs";
                        }
                    }
                    else
                    {
                        bindfield = "Entity." + item.FieldName;
                    }

                    if (string.IsNullOrEmpty(item.RelatedField) == false)
                    {
                        var subtype = Type.GetType(item.RelatedField);
                        if (item.SubField == "`file")
                        {
                            if (item.FieldName.ToLower().Contains("photo") || item.FieldName.ToLower().Contains("pic") || item.FieldName.ToLower().Contains("icon") || item.FieldName.ToLower().Contains("zhaopian") || item.FieldName.ToLower().Contains("tupian"))
                            {
                                controltype = "WTUploadImage";
                            }
                            else
                            {
                                controltype = "WTUploadFile";
                            }
                        }
                        else
                        {
                            if (string.IsNullOrEmpty(item.SubIdField) == true)
                            {
                                controltype = "Select";
                            }
                            else
                            {
                                controltype = "Transfer";
                            }
                            var tempname = $"All{subtype.Name}s";
                            sitems = $"Items=\"@{tempname}\"";
                            if (apis.ContainsKey(tempname) == false && multiapis.ContainsKey(tempname) == false)
                            {
                                if (controltype == "Select")
                                {
                                    apis.Add(tempname, $"/api/{ModelName}/Get{subtype.Name}s");
                                }
                                else
                                {
                                    multiapis.Add(tempname, $"/api/{ModelName}/Get{subtype.Name}s");
                                }
                            }

                        }
                    }
                    else
                    {
                        var proType = modelType.GetSingleProperty(item.FieldName)?.PropertyType;
                        Type checktype = proType;
                        if (proType.IsNullable())
                        {
                            checktype = proType.GetGenericArguments()[0];
                        }
                        if (checktype == typeof(bool))
                        {
                            if (proType.IsNullable())
                            {
                                controltype = "NullSwitch";
                            }
                            else
                            {
                                controltype = "Switch";
                            }
                        }
                        else if (checktype.IsEnum())
                        {
                            controltype = "Select";

                        }
                        else if (checktype.IsNumber())
                        {
                            controltype = "BootstrapInputNumber";
                        }
                        else if (checktype == typeof(string))
                        {
                        }
                        else if (checktype == typeof(DateTime))
                        {
                            controltype = "DateTimePicker";
                        }
                    }
                    if (controltype == "Select" || controltype == "MultiSelect")
                    {
                        ph = "PlaceHolder=\"@WtmBlazor.Localizer[\"Sys.PleaseSelect\"]\"";
                    }
                    if (controltype == "Transfer")
                    {
                        fieldstr.Append($@"
    <Row ColSpan=""2"">
            <{controltype} @bind-Value=""@Model.{bindfield}"" {sitems} {ph}/>
    </Row>");
                    }
                    else
                    {
                        fieldstr.Append($@"
            <{controltype} @bind-Value=""@Model.{bindfield}"" {sitems} {ph}/>");
                    }
                }

                StringBuilder apiinit = new StringBuilder();
                StringBuilder fieldinit = new StringBuilder();
                foreach (var item in apis)
                {
                    apiinit.Append(@$"
        {item.Key} = await WtmBlazor.Api.CallItemsApi(""{item.Value}"", placeholder: WtmBlazor.Localizer[""Sys.PleaseSelect""]);
");
                    fieldinit.Append($@"
    private List<SelectedItem> {item.Key} = new List<SelectedItem>();
");
                }
                foreach (var item in multiapis)
                {
                    apiinit.Append(@$"
        {item.Key} = await WtmBlazor.Api.CallItemsApi(""{item.Value}"");
");
                    fieldinit.Append($@"
    private List<SelectedItem> {item.Key} = new List<SelectedItem>();
");
                }
                return rv.Replace("$formfields$", fieldstr.ToString()).Replace("$fieldinit$", fieldinit.ToString()).Replace("$init$", apiinit.ToString());
            }
            if (name == "Details")
            {
                StringBuilder fieldstr = new StringBuilder();
                var pros = FieldInfos.Where(x => x.IsFormField == true).ToList();

                //生成表单model
                Dictionary<string, string> apis = new Dictionary<string, string>();
                for (int i = 0; i < pros.Count; i++)
                {
                    var item = pros[i];
                    string controltype = "Display";
                    string sitems = "";
                    string bindfield = "";
                    string disabled = "";
                    var property = modelType.GetSingleProperty(item.FieldName);

                    if (string.IsNullOrEmpty(item.RelatedField) == false)
                    {
                        if (string.IsNullOrEmpty(item.SubIdField) == true)
                        {
                            var fk = DC.GetFKName2(modelType, item.FieldName);
                            bindfield = "Entity." + fk;
                        }
                        else
                        {
                            bindfield = $"Selected{item.FieldName}IDs";
                        }
                    }
                    else
                    {
                        bindfield = "Entity." + item.FieldName;
                    }
                    if (string.IsNullOrEmpty(item.RelatedField) == false)
                    {
                        var subtype = Type.GetType(item.RelatedField);
                        if (item.SubField == "`file")
                        {
                            if (item.FieldName.ToLower().Contains("photo") || item.FieldName.ToLower().Contains("pic") || item.FieldName.ToLower().Contains("icon") || item.FieldName.ToLower().Contains("zhaopian") || item.FieldName.ToLower().Contains("tupian"))
                            {
                                controltype = "WTUploadImage";
                            }
                            else
                            {
                                controltype = "WTUploadFile";
                            }
                            disabled = "IsDisabled=\"true\"";
                        }
                        else
                        {
                            var tempname = $"All{subtype.Name}s";
                            sitems = $"Lookup=\"@{tempname}\"";
                            if (apis.ContainsKey(tempname) == false)
                            {
                                apis.Add(tempname, $"/api/{ModelName}/Get{subtype.Name}s");
                            }
                        }
                    }
                    else
                    {
                        var proType = modelType.GetSingleProperty(item.FieldName)?.PropertyType;
                        Type checktype = proType;
                        if (proType.IsNullable())
                        {
                            checktype = proType.GetGenericArguments()[0];
                        }
                        if (checktype == typeof(bool))
                        {
                            if (proType.IsNullable())
                            {
                                controltype = "NullSwitch";
                            }
                            else
                            {
                                controltype = "Switch";
                            }
                            disabled = "IsDisabled=\"true\"";
                        }
                    }
                    if (controltype == "WTUploadFile")
                    {
                        string label = property.GetPropertyDisplayName();
                        fieldstr.Append($@"
                @if (Model.{bindfield}.HasValue){{
                    <div>
                          <label class=""control-label is-display"">{label}</label>
                          <div><Button Size=""Size.Small"" Text=""@WtmBlazor.Localizer[""Sys.Download""]"" OnClick=""@(async x => await Download($""/api/_file/DownloadFile/{{Model.{bindfield}}}"",null, HttpMethodEnum.GET))"" /></div>
                    </div>
                }}
");
                    }
                    else
                    {
                        fieldstr.Append($@"
            <{controltype} @bind-Value=""@Model.{bindfield}"" {sitems} {disabled} ShowLabel=""true""/>");
                    }
                }

                StringBuilder apiinit = new StringBuilder();
                StringBuilder fieldinit = new StringBuilder();
                foreach (var item in apis)
                {
                    apiinit.Append(@$"
        {item.Key} = await WtmBlazor.Api.CallItemsApi(""{item.Value}"", placeholder: WtmBlazor.Localizer[""Sys.All""]);
");
                    fieldinit.Append($@"
    private List<SelectedItem> {item.Key} = new List<SelectedItem>();
");
                }

                return rv.Replace("$formfields$", fieldstr.ToString()).Replace("$fieldinit$", fieldinit.ToString()).Replace("$init$", apiinit.ToString());
            }


            return rv;
        }
    }
}
