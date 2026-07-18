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
        public string GenerateVUEView(string name, List<string> apineeded)
        {
            var rv = GetResource($"{name}.txt", "Spa.Vue")
                .Replace("$modelname$", ModelName.ToLower());
            if (apineeded == null)
            {
                apineeded = new List<string>();
            }
            Type modelType = GetSelectedModelType();
            if (name == "config")
            {
                StringBuilder fieldstr = new StringBuilder();
                StringBuilder enumstr = new StringBuilder();
                var pros = FieldInfos.Where(x => x.IsListField == true || x.IsSearcherField == true).ToList();
                fieldstr.Append(Environment.NewLine);
                List<PropertyInfo> existSubPro = new List<PropertyInfo>();
                List<string> existEnum = new List<string>();
                int rowheight = 30;
                for (int i = 0; i < pros.Count; i++)
                {
                    var item = pros[i];
                    var mpro = modelType.GetSingleProperty(item.FieldName)
                        ?? throw new InvalidOperationException($"Property '{item.FieldName}' not found on model type '{modelType.Name}'.");
                    string label = mpro.GetPropertyDisplayName();
                    string render = "";
                    string newname = item.FieldName;
                    if (mpro.PropertyType.IsBoolOrNullableBool())
                    {
                        render = "columnsRenderBoolean";
                    }
                    if (string.IsNullOrEmpty(item.RelatedField) == false)
                    {
                        var subtype = GetRelatedType(item);
                        string prefix = "";
                        if (subtype == typeof(FileAttachment))
                        {
                            if (item.FieldName.ToLower().Contains("photo") || item.FieldName.ToLower().Contains("pic") || item.FieldName.ToLower().Contains("icon"))
                            {
                                render = "columnsRenderImg";
                                rowheight = 110;
                            }
                            else
                            {
                                render = "columnsRenderDownload";
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

                    else
                    {
                        var proType = modelType.GetSingleProperty(item.FieldName)?.PropertyType
                            ?? throw new InvalidOperationException($"Property '{item.FieldName}' not found on model type '{modelType.Name}'.");
                        Type checktype = proType;
                        if (proType.IsNullable())
                        {
                            checktype = proType.GetGenericArguments()[0];
                        }
                        if (checktype.IsEnum())
                        {
                            if (existEnum.Contains(checktype.Name) == false)
                            {
                                var es = checktype.ToListItems();
                                enumstr.AppendLine($@"export const {item.FieldName}Types: Array<any> = [");
                                for (int a = 0; a < es.Count; a++)
                                {
                                    var e = es[a];
                                    enumstr.Append($@"  {{ Text: ""{e.Text}"", Value: ""{e.Value}"" }}");
                                    if (a < es.Count - 1)
                                    {
                                        enumstr.Append(',');
                                    }
                                    enumstr.AppendLine();
                                }
                                enumstr.AppendLine($@"];");
                                existEnum.Add(checktype.Name);
                            }
                        }
                    }
                    fieldstr.Append($@"
    {{
        key: ""{newname}"",
        label: ""{label}""");

                    if (render != "")
                    {
                        fieldstr.Append($@",
        isSlot: true ");
                    }
                    fieldstr.Append($@"
    }}");
                    fieldstr.Append(',');
                }
                return rv.Replace("$fields$", fieldstr.ToString()).Replace("$rowheight$", rowheight.ToString()).Replace("$enums$", enumstr.ToString());
            }
            if (name == "views.dialog-form")
            {
                StringBuilder fieldstr = new StringBuilder();
                List<string> actions = new List<string>();
                List<string> enums = new List<string>();
                var pros = FieldInfos.Where(x => x.IsFormField == true).ToList();

                //生成表单model
                for (int i = 0; i < pros.Count; i++)
                {
                    var item = pros[i];
                    var property = modelType.GetSingleProperty(item.FieldName);
                    string label = property.GetPropertyDisplayName();
                    bool isrequired = property.IsPropertyRequired();
                    var fktest = GetDC().GetFKName2(modelType, item.FieldName);
                    if (string.IsNullOrEmpty(fktest) == false)
                    {
                        isrequired = modelType.GetSingleProperty(fktest).IsPropertyRequired();
                    }
                    string rules = "rules: []";
                    if (isrequired == true)
                    {
                        rules = $@"rules: [{{ required: true, message: ""{label}""+this.$t(""form.notnull""),trigger: ""blur"" }}]";
                    }
                    if (string.IsNullOrEmpty(item.RelatedField) == false && string.IsNullOrEmpty(item.SubIdField) == true)
                    {
                        var fk = GetDC().GetFKName2(modelType, item.FieldName);
                        fieldstr.AppendLine($@"             ""Entity.{fk}"":{{");
                    }
                    else
                    {
                        fieldstr.AppendLine($@"             ""Entity.{item.FieldName}"":{{");
                    }
                    fieldstr.AppendLine($@"                 label: ""{label}"",");
                    fieldstr.AppendLine($@"                 {rules},");
                    if (string.IsNullOrEmpty(item.RelatedField) == false)
                    {
                        var subtype = GetRelatedType(item);
                        if (item.SubField == "`file")
                        {
                            if (item.FieldName.ToLower().Contains("photo") || item.FieldName.ToLower().Contains("pic") || item.FieldName.ToLower().Contains("icon"))
                            {
                                fieldstr.AppendLine($@"                type: ""wtmUploadImg"",
                    props: {{
                        isHead: true,
                        imageStyle: {{ width: ""100px"", height: ""100px"" }}
                    }}
");
                            }
                            else
                            {
                                fieldstr.AppendLine($@"                type: ""upload""");
                            }
                        }
                        else
                        {
                            if (string.IsNullOrEmpty(item.SubIdField) == true)
                            {
                                fieldstr.AppendLine($@"                    type: ""select"",
                    children: this.get{subtype.Name}Data,
                    props: {{
                        clearable: true
                    }}");
                            }
                            else
                            {
                                fieldstr.AppendLine($@"                    type: ""transfer"",
                    mapKey: ""{item.SubIdField}"",
                    props: {{
                        data: this.get{subtype.Name}Data.map(item => ({{
                            key: item.Value,
                            label: item.Text
                        }})),
                        titles: [this.$t(""form.all""), this.$t(""form.selected"")],
                        filterable: true,
                        filterMethod: filterMethod
                    }},
                    span: 24,
                    defaultValue: []");

                            }
                            apineeded.Add($"get{subtype.Name}");
                            actions.Add($"get{subtype.Name}");
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
                            fieldstr.AppendLine($@"                    type: ""switch""");
                        }
                        else if (checktype.IsEnum())
                        {
                            fieldstr.AppendLine($@"                    type: ""select"",
                    children: {item.FieldName}Types,
                    props: {{
                        clearable: true
                    }}");

                            enums.Add(item.FieldName + "Types");
                        }
                        else if (checktype.IsNumber())
                        {
                            fieldstr.AppendLine($@"                    type: ""input""");
                        }
                        else if (checktype == typeof(string))
                        {
                            fieldstr.AppendLine($@"                    type: ""input""");
                        }
                        else if (checktype == typeof(DateTime))
                        {
                            fieldstr.AppendLine($@"                    type: ""datePicker""");
                        }
                    }
                    fieldstr.Append("            }");
                    if (i < pros.Count - 1)
                    {
                        fieldstr.Append(',');
                    }
                    fieldstr.Append(Environment.NewLine);
                }
                string a1 = "";
                string a2 = "";
                foreach (var item in actions.Distinct())
                {
                    a1 += $@"    @Action
    {item};
    @State
    {item}Data;
";
                    a2 += $@"        this.{item}();
";
                }
                string import = "";
                if (enums.Count > 0)
                {
                    import = $@"import {{ {enums.Distinct().ToSepratedString()} }} from ""../config"";";
                }
                return rv.Replace("$fields$", fieldstr.ToString()).Replace("$actions$", a1).Replace("$runactions$", a2).Replace("$import$", import);
            }

            if (name == "index")
            {
                StringBuilder fieldstr2 = new StringBuilder();
                StringBuilder actions = new StringBuilder();
                List<string> acts = new List<string>();
                List<string> enums = new List<string>();
                var pros2 = FieldInfos.Where(x => x.IsSearcherField == true || x.IsListField).ToList();
                int searchcount = 0;
                for (int i = 0; i < pros2.Count; i++)
                {

                    var item = pros2[i];
                    if (item.IsListField == true)
                    {
                        var mpro = modelType.GetSingleProperty(item.FieldName)
                            ?? throw new InvalidOperationException($"Property '{item.FieldName}' not found on model type '{modelType.Name}'.");
                        if (mpro.PropertyType.IsBoolOrNullableBool())
                        {
                            actions.AppendLine($@"      <template #{item.FieldName}=""rowData"">
        <el-switch :value=""rowData.row.{item.FieldName} === 'true' || rowData.row.{item.FieldName} === true"" disabled />
      </template>
");
                        }
                        if (string.IsNullOrEmpty(item.RelatedField) == false)
                        {
                            var subtype = GetRelatedType(item);
                            var fk = GetDC().GetFKName2(modelType, item.FieldName);
                            if (subtype == typeof(FileAttachment))
                            {
                                if (item.FieldName.ToLower().Contains("photo") || item.FieldName.ToLower().Contains("pic") || item.FieldName.ToLower().Contains("icon"))
                                {
                                    actions.AppendLine($@"      <template #{fk}=""rowData"">
        <el-image v-if=""!!rowData.row.{fk}"" style=""width: 100px; height: 100px"" :src=""'/api/_file/downloadFile/'+rowData.row.{fk}"" fit=""cover"" />
      </template>
");
                                }
                                else
                                {
                                    actions.AppendLine($@"      <template #{fk}=""rowData"">
        <el-link icon=""el-icon-edit"" v-if=""!!rowData.row.{fk}"" :href=""'/api/_file/downloadFile/'+rowData.row.{fk}"">{{{{ $t(""table.download"")}}}}</el-link>
      </template>
");
                                }
                            }
                        }
                    }
                    if (item.IsSearcherField == true)
                    {
                        if (item.SubField == "`file")
                        {
                            continue;
                        }
                        searchcount++;
                        var property = modelType.GetSingleProperty(item.FieldName);
                        string label = property.GetPropertyDisplayName();
                        string rules = "rules: []";

                        if (string.IsNullOrEmpty(item.RelatedField) == false)
                        {
                            if (string.IsNullOrEmpty(item.SubIdField) == true)
                            {
                                var fk = GetDC().GetFKName2(modelType, item.FieldName);
                                fieldstr2.AppendLine($@"                ""{fk}"":{{");
                            }
                            else
                            {
                                fieldstr2.AppendLine($@"                ""Selected{item.FieldName}IDs"":{{");
                            }
                        }
                        else
                        {
                            fieldstr2.AppendLine($@"                ""{item.FieldName}"":{{");
                        }
                        fieldstr2.AppendLine($@"                    label: ""{label}"",");
                        fieldstr2.AppendLine($@"                    {rules},");
                        if (string.IsNullOrEmpty(item.RelatedField) == false)
                        {
                            var subtype = GetRelatedType(item);
                            if (string.IsNullOrEmpty(item.SubIdField) == true)
                            {
                                fieldstr2.AppendLine($@"                    type: ""select"",
                    children: this.get{subtype.Name}Data,
                    props: {{
                        clearable: true,
                        placeholder: '全部'
                    }}");

                            }
                            else
                            {
                                fieldstr2.AppendLine($@"                    type: ""select"",
                    children: this.get{subtype.Name}Data,
                    props: {{
                        clearable: true ,
                        multiple: true,
                        ""collapse-tags"": true
                    }}");

                            }
                            apineeded.Add($"get{subtype.Name}");
                            acts.Add($"get{subtype.Name}");
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
                                fieldstr2.AppendLine($@"                    type: ""switch""");
                            }
                            else if (checktype.IsEnum())
                            {
                                fieldstr2.AppendLine($@"                    type: ""select"",
                    children: {item.FieldName}Types,
                    props: {{
                        clearable: true,
                        placeholder: this.$t(""form.all"")
                    }}");

                                enums.Add(item.FieldName + "Types");
                            }
                            else if (checktype.IsNumber())
                            {
                                fieldstr2.AppendLine($@"                    type: ""input""");
                            }
                            else if (checktype == typeof(string))
                            {
                                fieldstr2.AppendLine($@"                    type: ""input""");
                            }
                            else if (checktype == typeof(DateTime))
                            {
                                fieldstr2.AppendLine($@"                    type: ""datePicker"",
                    span: 12,
                    props: {{
                            type: ""datetimerange"",
                        ""value-format"": ""yyyy-MM-dd HH:mm:ss"",
                        ""range-separator"": ""-"",
                        ""start-placeholder"": this.$t(""table.startdate""),
                        ""end-placeholder"": this.$t(""table.enddate"")
                    }}");
                            }
                        }
                        if (searchcount > 2)
                        {
                            fieldstr2.AppendLine("                    ,isHidden: !this.isActive");
                        }
                        fieldstr2.Append("              },");
                        fieldstr2.Append(Environment.NewLine);
                    }
                }

                string a1 = "";
                string a2 = "";
                foreach (var item in acts.Distinct())
                {
                    a1 += $@"    @Action
    {item};
    @State
    {item}Data;
";
                    a2 += $@"        this.{item}();
";
                }


                return rv.Replace("$fields$", fieldstr2.ToString()).Replace("$actions$", actions.ToString()).Replace("$enums$", enums.Distinct().ToSepratedString())
                    .Replace("$acts$", a1).Replace("$runactions$", a2);

            }
            if (name == "store.api")
            {
                StringBuilder fieldstr = new StringBuilder();
                StringBuilder efieldstr = new StringBuilder();

                var apis = apineeded.Distinct().ToList();
                for (int i = 0; i < apis.Count; i++)
                {
                    var item = apis[i];
                    fieldstr.AppendLine($@"const {item} = {{
  url: reqPath + ""{item}s"",
  method: ""get"",
  dataType: ""array""
}}; ");
                    efieldstr.AppendLine($"{item},");
                }
                return rv.Replace("$fields$", fieldstr.ToString()).Replace("$efields$", efieldstr.ToString());
            }

            return rv;
        }
    }
}
