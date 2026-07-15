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
        public string GenerateTest()
        {
            var rv = "";
            if (TestDir != null)
            {
                Type modelType = Type.GetType(SelectedModel);
                if (UI == UIEnum.LayUI && IsApi == false)
                {
                    if (typeof(IBasePoco).IsAssignableFrom(modelType) || typeof(IPersistPoco).IsAssignableFrom(modelType))
                    {
                        rv = GetResource($"ControllerTest.txt").Replace("$cns$", ControllerNs).Replace("$tns$", TestNs).Replace("$vns$", VMNs).Replace("$model$", ModelName).Replace("$mns$", ModelNS).Replace("$dns$", DataNs);
                    }
                    else
                    {
                        rv = GetResource($"ControllerTestTopPoco.txt").Replace("$cns$", ControllerNs).Replace("$tns$", TestNs).Replace("$vns$", VMNs).Replace("$model$", ModelName).Replace("$mns$", ModelNS).Replace("$dns$", DataNs);
                    }
                }
                else
                {
                    if (typeof(IBasePoco).IsAssignableFrom(modelType) || typeof(IPersistPoco).IsAssignableFrom(modelType))
                    {
                        rv = GetResource($"ApiTest.txt").Replace("$cns$", ControllerNs).Replace("$tns$", TestNs).Replace("$vns$", VMNs).Replace("$model$", ModelName).Replace("$mns$", ModelNS).Replace("$dns$", DataNs).Replace("$classnamel$", $"{ModelName}{(IsApi == true ? "Api" : "")}");
                    }
                    else
                    {
                        rv = GetResource($"ApiTestTopPoco.txt").Replace("$cns$", ControllerNs).Replace("$tns$", TestNs).Replace("$vns$", VMNs).Replace("$model$", ModelName).Replace("$mns$", ModelNS).Replace("$dns$", DataNs).Replace("$classnamel$", $"{ModelName}{(IsApi == true ? "Api" : "")}");
                    }
                }
                var modelprops = modelType.GetRandomValues();
                var batchpros = FieldInfos.Where(x => x.IsBatchField == true).ToList();
                string cpros = "";
                string epros = "";
                string pros = "";
                string mpros = "";
                string assert = "";
                string eassert = "";
                string fc = "";
                string add = "";
                string linkedpros = "";
                string linkedfc = "";
                string meassert = "";
                List<Type> addexist = new List<Type>();
                foreach (var pro in modelprops)
                {
                    if (pro.Value == "$fk$")
                    {
                        var fktype = modelType.GetSingleProperty(pro.Key[0..^2])?.PropertyType;
                        add += GenerateAddFKModel(pro.Key[0..^2], fktype, addexist);
                    }
                }

                foreach (var pro in modelprops)
                {
                    if (pro.Value == "$fk$")
                    {
                        var fktype = modelType.GetSingleProperty(pro.Key[0..^2])?.PropertyType;
                        if (fktype == null) continue;
                        cpros += $@"
            v.{pro.Key} = Add{fktype.Name}();";
                        pros += $@"
                v.{pro.Key} = Add{fktype.Name}();";
                        mpros += $@"
                v1.{pro.Key} = Add{fktype.Name}();";
                    }
                    else
                    {
                        cpros += $@"
            v.{pro.Key} = {pro.Value};";
                        pros += $@"
                v.{pro.Key} = {pro.Value};";
                        mpros += $@"
                v1.{pro.Key} = {pro.Value};";
                        assert += $@"
                Assert.AreEqual(data.{pro.Key}, {pro.Value});";
                    }
                    fc += $@"
            vm.FC.Add(""Entity.{pro.Key}"", """");";

                }

                var modelpros2 = modelType.GetRandomValues();
                foreach (var pro in modelpros2)
                {
                    //if (pro.Key.ToLower() == "id")
                    //{
                    //    continue;
                    //}

                    if (pro.Value == "$fk$")
                    {
                        mpros += $@"
                v2.{pro.Key} = v1.{pro.Key}; ";

                    }
                    else
                    {
                        mpros += $@"
                v2.{pro.Key} = {pro.Value};";
                        if (pro.Key.ToLower() != "id")
                        {
                            epros += $@"
            v.{pro.Key} = {pro.Value};";
                            eassert += $@"
                Assert.AreEqual(data.{pro.Key}, {pro.Value});";
                        }
                    }
                }

                var modelpros3 = modelType.GetRandomValues();
                foreach (var pro in modelpros3)
                {
                    if (batchpros.Any(x => x.FieldName == pro.Key) && pro.Key.ToLower() != "id")
                    {
                        linkedpros += $@"
            vm.LinkedVM.{pro.Key} = {pro.Value};";
                        linkedfc += $@"
            vm.FC.Add(""LinkedVM.{pro.Key}"", """");";
                        meassert += $@"
                Assert.AreEqual(data1.{pro.Key}, {pro.Value});";
                        meassert += $@"
                Assert.AreEqual(data2.{pro.Key}, {pro.Value});";
                    }
                }


                string del = $"Assert.AreEqual(data, null);";
                string mdel = @"Assert.AreEqual(data1, null);
            Assert.AreEqual(data2, null);";
                if (typeof(IPersistPoco).IsAssignableFrom(modelType))
                {
                    del = $"Assert.AreEqual(data, null);";
                    mdel = @"Assert.AreEqual(data1, null);
            Assert.AreEqual(data2, null);";
                }

                rv = rv.Replace("$cpros$", cpros).Replace("$epros$", epros).Replace("$pros$", pros).Replace("$mpros$", mpros)
                    .Replace("$assert$", assert).Replace("$eassert$", eassert).Replace("$fc$", fc).Replace("$add$", add).Replace("$del$", del).Replace("$mdel$", mdel)
                    .Replace("$linkedpros$", linkedpros).Replace("$linkedfc$", linkedfc).Replace("$meassert$", meassert);

                rv = GetRelatedNamespace(FieldInfos.Where(x => string.IsNullOrEmpty(x.RelatedField) == false).ToList(), rv);
            }
            return rv;
        }
        private string GenerateAddFKModel(string keyname, Type t, List<Type> exist)
        {
            if (t == null) return "";
            if (exist == null)
            {
                exist = new List<Type>();
            }
            if (exist.Contains(t) == true)
            {
                return "";
            }
            exist.Add(t);
            var modelprops = t.GetRandomValues();
            var mname = t.Name?.Split(',').FirstOrDefault()?.Split('.').LastOrDefault() ?? "";
            string cpros = "";
            string rv = "";
            foreach (var pro in modelprops)
            {
                if (pro.Value == "$fk$")
                {
                    var fktype = t.GetSingleProperty(pro.Key[0..^2])?.PropertyType;
                    if (fktype != null && fktype != t)
                    {
                        rv += GenerateAddFKModel(pro.Key[0..^2], fktype, exist);
                    }
                }
            }


            foreach (var pro in modelprops)
            {
                if (pro.Value == "$fk$")
                {
                    var fktype = t.GetSingleProperty(pro.Key[0..^2])?.PropertyType;
                    if (fktype != null && fktype != t)
                    {
                        cpros += $@"
                v.{pro.Key} = Add{fktype.Name}();";
                    }
                }
                else
                {
                    cpros += $@"
                v.{pro.Key} = {pro.Value};";
                }
            }
            var idpro = t.GetSingleProperty("ID");
            rv += $@"
        private {idpro.PropertyType.Name} Add{t.Name}()
        {{
            {mname} v = new {mname}();
            using (var context = new DataContext(_seed, DBTypeEnum.SQLite, $""Data Source={{_seed}};Mode=Memory;Cache=Shared""))
            {{
                try{{
{cpros}
                context.Set<{mname}>().Add(v);
                context.SaveChanges();
                }}
                catch{{}}
            }}
            return v.ID;
        }}
";
            return rv;
        }
    }
}
