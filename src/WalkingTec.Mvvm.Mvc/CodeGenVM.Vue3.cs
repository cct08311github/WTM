#nullable enable
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
        public string GenerateVue3View(string name)
        {
            string pagepath = string.IsNullOrEmpty(Area) ? $"/{ModelName}" : $"/{Area.ToLower()}/{ModelName}";
          
            var rv = GetResource($"{name}.txt", "Spa.Vue3")
                .Replace("$modelname$", ModelName)
                .Replace("$vmnamespace$", VMNs)
                .Replace("$des$", ModuleName)
                .Replace("$controllername$", $"{ControllerNs};{ModelName}")
                .Replace("$pagepath$", pagepath);
            Type modelType = GetSelectedModelType();
            if (name == "Index")
            {
                StringBuilder fieldstr = new StringBuilder();
                StringBuilder fieldstr2 = new StringBuilder();
                StringBuilder searchentity = new StringBuilder();
                var pros = FieldInfos.Where(x => x.IsListField == true).ToList();
                var pros2 = FieldInfos.Where(x => x.IsSearcherField == true).ToList();
                List<PropertyInfo> existSubPro = new List<PropertyInfo>();
                Dictionary<string, string> apis = new Dictionary<string, string>();
                Dictionary<string, string> multiapis = new Dictionary<string, string>();
                for (int i = 0; i < pros.Count; i++)
                {
                    var item = pros[i];
                    var mpro = modelType.GetSingleProperty(item.FieldName)
                        ?? throw new InvalidOperationException($"Property '{item.FieldName}' not found on model type '{modelType.Name}'.");
                    string template = "text";
                    string newname = item.FieldName;
                    var property = mpro;
                    string label = property.GetPropertyDisplayName();
                    if (mpro.PropertyType.IsBoolOrNullableBool())
                    {
                        template = "switch";
                    }
                    //if (mpro.PropertyType == typeof(DateTime) || mpro.PropertyType == typeof(DateTime?))
                    //{
                    //    render = "FormatString=\"yyyy-MM-dd HH: mm: ss\"";

                    //}
                    if (string.IsNullOrEmpty(item.RelatedField) == false)
                    {
                        var subtype = GetRelatedType(item);
                        string prefix = "";
                        if (subtype == typeof(FileAttachment))
                        {
                            if (item.FieldName.ToLower().Contains("photo") || item.FieldName.ToLower().Contains("pic") || item.FieldName.ToLower().Contains("icon") || item.FieldName.ToLower().Contains("zhaopian") || item.FieldName.ToLower().Contains("tupian"))
                            {
                                template = "image";
                            }
                            else
                            {

                            }
                            var fk = GetDC().GetFKName2(modelType, item.FieldName);
                            newname = fk;
                        }
                        else
                        {
                            var subpro = GetRelatedProperty(subtype, item);
                            existSubPro.Add(subpro);
                            int count = existSubPro.Where(x => x.Name == subpro.Name).Count();
                            if (count > 1)
                            {
                                prefix = count + "";
                            }
                            newname = item.SubField + "_view" + prefix;
                        }
                    }
                    fieldstr.Append($@"
        {{title:'{label}',key: '{newname}',type: '{template}',isCheck: true}},");

                }
                for (int i = 0; i < pros2.Count; i++)
                {
                    string controltype = "input";
                    string sitems = "";
                    string bindfield = "";
                    string ph = "";
                    var item = pros2[i];
                    var property = modelType.GetSingleProperty(item.FieldName);
                    string label = property.GetPropertyDisplayName();
                    if (item.SubField == "`file")
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(item.RelatedField) == false)
                    {
                        if (string.IsNullOrEmpty(item.SubIdField) == true)
                        {
                            var fk = GetDC().GetFKName2(modelType, item.FieldName);
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

                    searchentity.AppendLine($@"			{bindfield}: null,");


                    if (string.IsNullOrEmpty(item.RelatedField) == false)
                    {
                        var subtype = GetRelatedType(item);
                        var tempname = $"All{subtype.Name}s";
                        if (string.IsNullOrEmpty(item.SubIdField) == true)
                        {
                            controltype = "select";
                            sitems += $@"
                       <el-option v-for=""item in state{ModelName}.{tempname}"" :key=""item.Value"" :value=""item.Value"" :label=""item.Text""></el-option>";
                        }
                        else
                        {
                            ph += " multiple";
                        }
                        ph += $" :data=\"state{ModelName}.{tempname}\"";
                        if (apis.ContainsKey(tempname) == false && multiapis.ContainsKey(tempname) == false)
                        {
                            if (controltype == "select")
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
                        var proType = modelType.GetSingleProperty(item.FieldName)?.PropertyType
                            ?? throw new InvalidOperationException($"Property '{item.FieldName}' not found on model type '{modelType.Name}'.");
                        Type checktype = proType;
                        if (proType.IsNullable())
                        {
                            checktype = proType.GetGenericArguments()[0];
                        }
                        if (checktype == typeof(bool))
                        {
                            controltype = "select";
                            sitems = $@"
                <el-option :key=""1"" :value=true :label=""$t('message._system.common.vm.tips_bool_true')""></el-option>
                <el-option :key=""0"" :value=false :label=""$t('message._system.common.vm.tips_bool_false')""></el-option>
                ";
                        }
                        else if (checktype.IsEnum())
                        {
                            controltype = "select";
                            var es = checktype.ToListItems();
                            for (int a = 0; a < es.Count; a++)
                            {
                                var e = es[a];
                                sitems += $@"
                <el-option key=""{e.Value}"" value=""{e.Value}"" label=""{e.Text}""></el-option>";
                            }

                        }
                        else if (checktype.IsNumber())
                        {
                            controltype = "input-number";
                        }
                        else if (checktype == typeof(string))
                        {
                        }
                        else if (checktype == typeof(DateTime))
                        {
                            controltype = "date-picker";
                        }
                    }
                    if (controltype == "Select" || controltype == "MultiSelect")
                    {
                        ph = "PlaceHolder=\"@WtmBlazor.Localizer[\"Sys.All\"]\"";
                    }
                    //<{controltype} @bind-Value=""@SearchModel.{bindfield}"" {sitems} {ph}/>
                    fieldstr2.Append($@"
    <el-col :xs=""24"" :lg=""12"" class=""mb20"">
        <el-form-item ref=""{bindfield}_FormItem"" prop=""{bindfield}"" label=""{label}"">
            <el-{controltype} v-model=""searchData{ModelName}.{bindfield}""{ph} clearable>{sitems}</el-{controltype}>
        </el-form-item>
    </el-col>");
                }
                StringBuilder apiinit = new StringBuilder();
                StringBuilder fieldinit = new StringBuilder();
                foreach (var item in apis)
                {
                    apiinit.Append(@$"
    other.getSelectList('{item.Value}',[],false).then(x=>{{state{ModelName}.{item.Key} = x}});
");
                    fieldinit.Append($@"
    {item.Key}: [] as any[],");

                }
                foreach (var item in multiapis)
                {
                    apiinit.Append(@$"
    other.getSelectList('{item.Value}',[],false).then(x=>{{state{ModelName}.{item.Key} = x}});
");
                    fieldinit.Append($@"
    {item.Key}: [] as any[],");
                }


                return rv.Replace("$columns$", fieldstr.ToString()).Replace("$searchentity$", searchentity.ToString()).Replace("$searchfields$", fieldstr2.ToString()).Replace("$init$", apiinit.ToString()).Replace("$fieldinit$", fieldinit.ToString());
            }

            if (name == "Create" || name == "Edit" || name == "Details")
            {
                StringBuilder fieldstr = new StringBuilder();
                StringBuilder fieldentityinit = new StringBuilder();
                StringBuilder selectfieldinit = new StringBuilder();
                var pros = FieldInfos.Where(x => x.IsFormField == true).ToList();

                if (name != "Create")
                {
                    fieldentityinit.AppendLine($@"			ID: null,");
                }
                //生成表单model
                Dictionary<string, string> apis = new Dictionary<string, string>();
                Dictionary<string, string> multiapis = new Dictionary<string, string>();


                for (int i = 0; i < pros.Count; i++)
                {
                    var item = pros[i];
                    string controltype = "el-input";
                    string sitems = "";
                    string bindfield = "";
                    string ph = "";
                    var property = modelType.GetSingleProperty(item.FieldName);
                    string label = property.GetPropertyDisplayName();
                    bool isrequired = property.IsPropertyRequired();
                    var fktest = GetDC().GetFKName2(modelType, item.FieldName);
                    if (string.IsNullOrEmpty(fktest) == false)
                    {
                        isrequired = modelType.GetSingleProperty(fktest).IsPropertyRequired();
                    }
                    string rules = "";
                    if (isrequired == true)
                    {
                        rules = $@" :rules=""[{{ required: true, message:'{label}为必填项',trigger:'blur'}}]""";
                    }
                    if (string.IsNullOrEmpty(item.RelatedField) == false && string.IsNullOrEmpty(item.SubIdField) == true)
                    {
                        var fk = GetDC().GetFKName2(modelType, item.FieldName);
                        fieldentityinit.AppendLine($@"			{fk}: null,");
                    }
                    else
                    {
                        fieldentityinit.AppendLine($@"			{item.FieldName}: null,");
                    }



                    if (string.IsNullOrEmpty(item.RelatedField) == false)
                    {
                        if (string.IsNullOrEmpty(item.SubIdField) == true)
                        {
                            var fk = GetDC().GetFKName2(modelType, item.FieldName);
                            bindfield = "Entity." + fk;
                        }
                        else
                        {
                            bindfield = $"Selected{item.FieldName}IDs";
                            selectfieldinit.AppendLine($@"			{bindfield}: [],");
                        }
                    }
                    else
                    {
                        bindfield = "Entity." + item.FieldName;
                    }

                    if (string.IsNullOrEmpty(item.RelatedField) == false)
                    {
                        var subtype = GetRelatedType(item);
                        if (item.SubField == "`file")
                        {
                            if (item.FieldName.ToLower().Contains("photo") || item.FieldName.ToLower().Contains("pic") || item.FieldName.ToLower().Contains("icon") || item.FieldName.ToLower().Contains("zhaopian") || item.FieldName.ToLower().Contains("tupian"))
                            {
                                controltype = "wtm-upload-image";
                            }
                            else
                            {
                                controltype = "wtm-upload-file";
                            }
                        }
                        else
                        {
                            var tempname = $"All{subtype.Name}s";
                            if (string.IsNullOrEmpty(item.SubIdField) == true)
                            {
                                controltype = "el-select";
                                sitems = $@"
                       <el-option v-for=""item in state{ModelName}.{tempname}"" :key=""item.Value"" :value=""item.Value"" :label=""item.Text""></el-option>";
                            }
                            else
                            {
                                controltype = "el-transfer";
                                ph += $@" :data=""state{ModelName}.{tempname}""";
                            }

                            //sitems = $"Items=\"@{tempname}\"";
                            if (apis.ContainsKey(tempname) == false && multiapis.ContainsKey(tempname) == false)
                            {
                                if (controltype == "el-select")
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
                        var proType = modelType.GetSingleProperty(item.FieldName)?.PropertyType
                            ?? throw new InvalidOperationException($"Property '{item.FieldName}' not found on model type '{modelType.Name}'.");
                        Type checktype = proType;
                        if (proType.IsNullable())
                        {
                            checktype = proType.GetGenericArguments()[0];
                        }
                        if (checktype == typeof(bool))
                        {
                            controltype = "el-select";
                            sitems = $@"
                <el-option :key=""1"" :value=true :label=""$t('message._system.common.vm.tips_bool_true')""></el-option>
                <el-option :key=""0"" :value=false :label=""$t('message._system.common.vm.tips_bool_false')""></el-option>
                ";
                        }
                        else if (checktype.IsEnum())
                        {
                            controltype = "el-select";
                            var es = checktype.ToListItems();
                            for (int a = 0; a < es.Count; a++)
                            {
                                var e = es[a];
                                sitems += $@"
                <el-option key=""{e.Value}"" value=""{e.Value}"" label=""{e.Text}""></el-option>";
                            }

                        }
                        else if (checktype.IsNumber())
                        {
                            controltype = "el-input-number";
                        }
                        else if (checktype == typeof(string))
                        {
                        }
                        else if (checktype == typeof(DateTime))
                        {
                            controltype = "el-date-picker";
                        }
                    }


                    if (name == "Details")
                    {
                        ph += " disabled";
                    }
                    if (controltype == "el-transfer")
                    {
                        fieldstr.Append($@"
    <el-col :xs=""24"" :lg=""24"" class=""mb20"">
        <el-form-item ref=""{bindfield.Replace(".", "_")}_FormItem"" prop=""{bindfield}"" label=""{label}""{rules}>
            <{controltype} v-model=""state{ModelName}.vmModel.{bindfield}""{ph} clearable>{sitems}</{controltype}>
        </el-form-item>
    </el-col>");
                    }
                    else
                    {

                        fieldstr.Append($@"
    <el-col :xs=""24"" :lg=""12"" class=""mb20"">
        <el-form-item ref=""{bindfield.Replace(".", "_")}_FormItem"" prop=""{bindfield}"" label=""{label}""{rules}>
            <{controltype} v-model=""state{ModelName}.vmModel.{bindfield}""{ph} clearable>{sitems}</{controltype}>
        </el-form-item>
    </el-col>");
                    }
                }

                StringBuilder apiinit = new StringBuilder();
                StringBuilder fieldinit = new StringBuilder();
                foreach (var item in apis)
                {
                    apiinit.Append(@$"
    other.getSelectList('{item.Value}',[],false).then(x=>{{state{ModelName}.{item.Key} = x}});
");
                    fieldinit.Append($@"
    {item.Key}: [] as any[],");

                }
                foreach (var item in multiapis)
                {
                    apiinit.Append(@$"
    other.getSelectList('{item.Value}',[],false).then(x=>{{state{ModelName}.{item.Key} = x}});
");
                    fieldinit.Append($@"
    {item.Key}: [] as any[],");
                }

                return rv.Replace("$formfields$", fieldstr.ToString()).Replace("$fieldinit$", fieldinit.ToString()).Replace("$selectfieldinit$", selectfieldinit.ToString())
                    .Replace("$fieldentityinit$", fieldentityinit.ToString()).Replace("$init$", apiinit.ToString());
            }

            if (name == "indexapi")
            {

                return rv;
            }

            return rv;
        }
    }
}
