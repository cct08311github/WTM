#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;

// Pull in the FieldInfo from Mvc (not System.Reflection).
using MvcFieldInfo = WalkingTec.Mvvm.Mvc.FieldInfo;

namespace WalkingTec.Mvvm.Core.Test.CodeGen
{
    /// <summary>
    /// Tests for the CodeGen Engine v2 features (Issue #203):
    ///   CG-02 — two-zone regeneration safety (*.Generated.cs + companion *.cs)
    ///   CG-05 — async controller/CrudVM template actions
    ///   CG-06 — SQLite shared-memory test fixture (all four test templates)
    ///   CG-07 — ImportVM carries [Required]/[StringLength] + [ImportConfig] overrides
    ///   CG-08 — [ListColumn] → grid header fluent chain + type-based width heuristic
    ///   CG-09 — [SearchField].Operator → $where$ CheckContain/Equal/Between
    /// </summary>
    [TestClass]
    public class CodeGenEngineV2Tests
    {
        // Helper: invoke BuildGridHeaderChain via reflection
        private static string InvokeBuildGridHeaderChain(
            string fieldName,
            PropertyInfo? prop,
            ListColumnAttribute? attr,
            bool isFkLookup = false)
        {
            var method = typeof(CodeGenVM).GetMethod(
                "BuildGridHeaderChain",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            return (string)method.Invoke(null, new object?[] { fieldName, prop, attr, isFkLookup })!;
        }

        // =================================================================
        // CG-08: BuildGridHeaderChain — type-based width heuristics (no attr)
        // =================================================================

        private class HeuristicModel
        {
            public string? Name { get; set; }
            public DateTime? CreatedAt { get; set; }
            public bool IsActive { get; set; }
            public int Count { get; set; }
            public decimal Price { get; set; }
            // No explicit annotation
            public object? Unknown { get; set; }
        }

        [TestMethod]
        public void BuildGridHeaderChain_StringProp_NoAttr_EmitsWidth150()
        {
            var prop = typeof(HeuristicModel).GetProperty(nameof(HeuristicModel.Name))!;
            var result = InvokeBuildGridHeaderChain("Name", prop, null);
            Assert.AreEqual(".SetWidth(150)", result,
                "string property with no [ListColumn] should default to width 150");
        }

        [TestMethod]
        public void BuildGridHeaderChain_DateTimeProp_NoAttr_EmitsWidth160()
        {
            var prop = typeof(HeuristicModel).GetProperty(nameof(HeuristicModel.CreatedAt))!;
            var result = InvokeBuildGridHeaderChain("CreatedAt", prop, null);
            Assert.AreEqual(".SetWidth(160)", result,
                "DateTime? property with no [ListColumn] should default to width 160");
        }

        [TestMethod]
        public void BuildGridHeaderChain_BoolProp_NoAttr_EmitsWidth80()
        {
            var prop = typeof(HeuristicModel).GetProperty(nameof(HeuristicModel.IsActive))!;
            var result = InvokeBuildGridHeaderChain("IsActive", prop, null);
            Assert.AreEqual(".SetWidth(80)", result,
                "bool property with no [ListColumn] should default to width 80");
        }

        [TestMethod]
        public void BuildGridHeaderChain_IntProp_NoAttr_EmitsWidth100()
        {
            var prop = typeof(HeuristicModel).GetProperty(nameof(HeuristicModel.Count))!;
            var result = InvokeBuildGridHeaderChain("Count", prop, null);
            Assert.AreEqual(".SetWidth(100)", result,
                "int property with no [ListColumn] should default to width 100");
        }

        [TestMethod]
        public void BuildGridHeaderChain_FkLookup_NoAttr_EmitsWidth180()
        {
            // FK lookup columns should get width 180 regardless of property type.
            var prop = typeof(HeuristicModel).GetProperty(nameof(HeuristicModel.Name))!;
            var result = InvokeBuildGridHeaderChain("Name_view", prop, null, isFkLookup: true);
            Assert.AreEqual(".SetWidth(180)", result,
                "FK lookup column with no [ListColumn] should default to width 180");
        }

        [TestMethod]
        public void BuildGridHeaderChain_NullProp_NoAttr_EmitsEmpty()
        {
            // When prop is null and there's no attr, emit nothing (current-output preservation).
            var result = InvokeBuildGridHeaderChain("SomeField", null, null);
            Assert.AreEqual("", result,
                "null prop with no attr must emit empty chain — current behavior preserved");
        }

        // =================================================================
        // CG-08: BuildGridHeaderChain — [ListColumn] attribute present
        // =================================================================

        private class AnnotatedModel
        {
            [ListColumn(Width = 200)]
            public string? TitleExplicit { get; set; }

            [ListColumn(Align = GridColumnAlignEnum.Center)]
            public string? TitleCenter { get; set; }

            [ListColumn(Sort = false)]
            public string? TitleNoSort { get; set; }

            [ListColumn(Hide = true)]
            public string? TitleHide { get; set; }

            [ListColumn(Fixed = GridColumnFixedEnum.Left)]
            public string? TitleFixed { get; set; }

            [ListColumn(ShowTotal = true)]
            public decimal Amount { get; set; }

            [ListColumn(Width = 120, Align = GridColumnAlignEnum.Right, Sort = false,
                        Hide = false, Fixed = GridColumnFixedEnum.Right, ShowTotal = true)]
            public decimal FullChain { get; set; }

            // All-defaults — should produce no chain (or just the heuristic width)
            [ListColumn]
            public string? DefaultsOnly { get; set; }
        }

        [TestMethod]
        public void BuildGridHeaderChain_ExplicitWidth_EmitsSetWidth()
        {
            var prop = typeof(AnnotatedModel).GetProperty(nameof(AnnotatedModel.TitleExplicit))!;
            var attr = prop.GetCustomAttribute<ListColumnAttribute>()!;
            var result = InvokeBuildGridHeaderChain("TitleExplicit", prop, attr);
            StringAssert.Contains(result, ".SetWidth(200)");
        }

        [TestMethod]
        public void BuildGridHeaderChain_AlignCenter_EmitsSetAlign()
        {
            var prop = typeof(AnnotatedModel).GetProperty(nameof(AnnotatedModel.TitleCenter))!;
            var attr = prop.GetCustomAttribute<ListColumnAttribute>()!;
            var result = InvokeBuildGridHeaderChain("TitleCenter", prop, attr);
            StringAssert.Contains(result, ".SetAlign(GridColumnAlignEnum.Center)");
        }

        [TestMethod]
        public void BuildGridHeaderChain_SortFalse_EmitsSetSortFalse()
        {
            var prop = typeof(AnnotatedModel).GetProperty(nameof(AnnotatedModel.TitleNoSort))!;
            var attr = prop.GetCustomAttribute<ListColumnAttribute>()!;
            var result = InvokeBuildGridHeaderChain("TitleNoSort", prop, attr);
            StringAssert.Contains(result, ".SetSort(false)");
        }

        [TestMethod]
        public void BuildGridHeaderChain_HideTrue_EmitsSetHideTrue()
        {
            var prop = typeof(AnnotatedModel).GetProperty(nameof(AnnotatedModel.TitleHide))!;
            var attr = prop.GetCustomAttribute<ListColumnAttribute>()!;
            var result = InvokeBuildGridHeaderChain("TitleHide", prop, attr);
            StringAssert.Contains(result, ".SetHide(true)");
        }

        [TestMethod]
        public void BuildGridHeaderChain_FixedLeft_EmitsSetFixed()
        {
            var prop = typeof(AnnotatedModel).GetProperty(nameof(AnnotatedModel.TitleFixed))!;
            var attr = prop.GetCustomAttribute<ListColumnAttribute>()!;
            var result = InvokeBuildGridHeaderChain("TitleFixed", prop, attr);
            StringAssert.Contains(result, ".SetFixed(GridColumnFixedEnum.Left)");
        }

        [TestMethod]
        public void BuildGridHeaderChain_ShowTotalTrue_EmitsSetShowTotalTrue()
        {
            var prop = typeof(AnnotatedModel).GetProperty(nameof(AnnotatedModel.Amount))!;
            var attr = prop.GetCustomAttribute<ListColumnAttribute>()!;
            var result = InvokeBuildGridHeaderChain("Amount", prop, attr);
            StringAssert.Contains(result, ".SetShowTotal(true)");
        }

        [TestMethod]
        public void BuildGridHeaderChain_AllDefaults_EmitsHeuristicWidthOnly()
        {
            // [ListColumn] with no overrides → only the heuristic width (150 for string) should appear.
            var prop = typeof(AnnotatedModel).GetProperty(nameof(AnnotatedModel.DefaultsOnly))!;
            var attr = prop.GetCustomAttribute<ListColumnAttribute>()!;
            var result = InvokeBuildGridHeaderChain("DefaultsOnly", prop, attr);
            // Should contain the heuristic width for string
            StringAssert.Contains(result, ".SetWidth(150)",
                "All-default [ListColumn] on string should still emit type-heuristic width");
            // Should NOT contain sort/hide/align/fixed/showTotal for defaults
            Assert.IsFalse(result.Contains(".SetSort("), "Default Sort=true must not emit .SetSort(false)");
            Assert.IsFalse(result.Contains(".SetHide("), "Default Hide=false must not emit .SetHide(true)");
            Assert.IsFalse(result.Contains(".SetAlign("), "Default Align=Auto must not emit .SetAlign(...)");
            Assert.IsFalse(result.Contains(".SetFixed("), "Default Fixed=None must not emit .SetFixed(...)");
            Assert.IsFalse(result.Contains(".SetShowTotal("), "Default ShowTotal=false must not emit .SetShowTotal(true)");
        }

        [TestMethod]
        public void BuildGridHeaderChain_FullChain_EmitsAllModifiers()
        {
            var prop = typeof(AnnotatedModel).GetProperty(nameof(AnnotatedModel.FullChain))!;
            var attr = prop.GetCustomAttribute<ListColumnAttribute>()!;
            var result = InvokeBuildGridHeaderChain("FullChain", prop, attr);
            StringAssert.Contains(result, ".SetWidth(120)");
            StringAssert.Contains(result, ".SetAlign(GridColumnAlignEnum.Right)");
            StringAssert.Contains(result, ".SetSort(false)");
            StringAssert.Contains(result, ".SetFixed(GridColumnFixedEnum.Right)");
            StringAssert.Contains(result, ".SetShowTotal(true)");
        }

        [TestMethod]
        public void BuildGridHeaderChain_UnannotatedModel_StringProp_EmitsWidth150_ExactlyAsCurrentBehavior()
        {
            // Core compatibility test: model with NO [ListColumn] must produce consistent output.
            // This verifies CG-05/06 "change quality not shape" rule.
            var prop = typeof(HeuristicModel).GetProperty(nameof(HeuristicModel.Name))!;
            var result1 = InvokeBuildGridHeaderChain("Name", prop, null);
            var result2 = InvokeBuildGridHeaderChain("Name", prop, null);
            Assert.AreEqual(result1, result2, "Unannotated model output must be deterministic/idempotent");
            Assert.AreEqual(".SetWidth(150)", result1);
        }

        // =================================================================
        // CG-06: Generated test templates use SQLite shared-memory fixture
        // =================================================================

        private static string GetTemplate(string name)
        {
            // Templates are embedded resources in the Mvc assembly (WalkingTec.Mvvm.Mvc).
            // Use CodeGenVM's public GetResource method — it resolves the correct assembly
            // via Assembly.GetExecutingAssembly() which, in the context of the method body
            // in that DLL, returns the Mvc assembly.
            var vm = new CodeGenVM();
            // GetResource replaces '/' path separators with '.' internally by using
            // subdir overloads. For nested paths like "Spa/Controller.txt", use the subdir form.
            var parts = name.Split('/');
            if (parts.Length == 1)
                return vm.GetResource(parts[0]);
            if (parts.Length == 2)
                return vm.GetResource(parts[1], parts[0]);
            // Three-level: e.g. "Spa/Blazor/Controller.txt"
            return vm.GetResource(parts[parts.Length - 1], string.Join(".", parts[..^1]));
        }

        [TestMethod]
        public void ControllerTest_Template_Uses_SQLite_SharedMemory()
        {
            var template = GetTemplate("ControllerTest.txt");
            Assert.IsFalse(template.Contains("DBTypeEnum.Memory"),
                "ControllerTest.txt must not reference DBTypeEnum.Memory (CG-06)");
            StringAssert.Contains(template, "DBTypeEnum.SQLite",
                "ControllerTest.txt must use DBTypeEnum.SQLite");
            StringAssert.Contains(template, "Mode=Memory;Cache=Shared",
                "ControllerTest.txt must use SQLite shared-memory connection string");
            StringAssert.Contains(template, "SqliteConnection",
                "ControllerTest.txt must keep the shared connection alive");
            StringAssert.Contains(template, "[TestCleanup]",
                "ControllerTest.txt must have a cleanup method to dispose the connection");
        }

        [TestMethod]
        public void ApiTest_Template_Uses_SQLite_SharedMemory()
        {
            var template = GetTemplate("ApiTest.txt");
            Assert.IsFalse(template.Contains("DBTypeEnum.Memory"),
                "ApiTest.txt must not reference DBTypeEnum.Memory (CG-06)");
            StringAssert.Contains(template, "DBTypeEnum.SQLite");
            StringAssert.Contains(template, "Mode=Memory;Cache=Shared");
            StringAssert.Contains(template, "SqliteConnection");
            StringAssert.Contains(template, "[TestCleanup]");
        }

        [TestMethod]
        public void ControllerTestTopPoco_Template_Uses_SQLite_SharedMemory()
        {
            var template = GetTemplate("ControllerTestTopPoco.txt");
            Assert.IsFalse(template.Contains("DBTypeEnum.Memory"),
                "ControllerTestTopPoco.txt must not reference DBTypeEnum.Memory (CG-06)");
            StringAssert.Contains(template, "DBTypeEnum.SQLite");
            StringAssert.Contains(template, "Mode=Memory;Cache=Shared");
            StringAssert.Contains(template, "SqliteConnection");
            StringAssert.Contains(template, "[TestCleanup]");
        }

        [TestMethod]
        public void ApiTestTopPoco_Template_Uses_SQLite_SharedMemory()
        {
            var template = GetTemplate("ApiTestTopPoco.txt");
            Assert.IsFalse(template.Contains("DBTypeEnum.Memory"),
                "ApiTestTopPoco.txt must not reference DBTypeEnum.Memory (CG-06)");
            StringAssert.Contains(template, "DBTypeEnum.SQLite");
            StringAssert.Contains(template, "Mode=Memory;Cache=Shared");
            StringAssert.Contains(template, "SqliteConnection");
            StringAssert.Contains(template, "[TestCleanup]");
        }

        // =================================================================
        // CG-05: Generated controller templates use async/await
        // =================================================================

        [TestMethod]
        public void Mvc_Controller_Template_Uses_Async_Actions()
        {
            var template = GetTemplate("Mvc/Controller.txt");
            StringAssert.Contains(template, "async Task<ActionResult>",
                "MVC Controller.txt Create/Edit/Delete must be async");
            StringAssert.Contains(template, "await vm.DoAddAsync()",
                "MVC Controller.txt Create must await DoAddAsync");
            StringAssert.Contains(template, "await vm.DoEditAsync()",
                "MVC Controller.txt Edit must await DoEditAsync");
            StringAssert.Contains(template, "await vm.DoDeleteAsync()",
                "MVC Controller.txt Delete must await DoDeleteAsync");
            StringAssert.Contains(template, "System.Threading.Tasks",
                "MVC Controller.txt must reference System.Threading.Tasks");
        }

        [TestMethod]
        public void Spa_Controller_Template_Uses_Async_Actions()
        {
            var template = GetTemplate("Spa/Controller.txt");
            StringAssert.Contains(template, "async Task<IActionResult>",
                "SPA Controller.txt Add/Edit must be async");
            StringAssert.Contains(template, "await vm.DoAddAsync()",
                "SPA Controller.txt Add must await DoAddAsync");
            StringAssert.Contains(template, "await vm.DoEditAsync(false)",
                "SPA Controller.txt Edit must await DoEditAsync(false)");
            StringAssert.Contains(template, "System.Threading.Tasks",
                "SPA Controller.txt must reference System.Threading.Tasks");
        }

        [TestMethod]
        public void Blazor_Controller_Template_Uses_Async_Actions()
        {
            var template = GetTemplate("Spa/Blazor/Controller.txt");
            StringAssert.Contains(template, "async Task<IActionResult>",
                "Blazor Controller.txt Add/Edit must be async");
            StringAssert.Contains(template, "await vm.DoAddAsync()",
                "Blazor Controller.txt Add must await DoAddAsync");
            StringAssert.Contains(template, "await vm.DoEditAsync(false)",
                "Blazor Controller.txt Edit must await DoEditAsync(false)");
        }

        [TestMethod]
        public void CrudVM_Template_Has_Async_Override_Methods()
        {
            var template = GetTemplate("CrudVM.txt");
            StringAssert.Contains(template, "async Task DoAddAsync()",
                "CrudVM.txt must have async DoAddAsync override");
            StringAssert.Contains(template, "async Task DoEditAsync(bool updateAllFields",
                "CrudVM.txt must have async DoEditAsync override");
            StringAssert.Contains(template, "async Task DoDeleteAsync()",
                "CrudVM.txt must have async DoDeleteAsync override");
            StringAssert.Contains(template, "await base.DoAddAsync()",
                "CrudVM.txt DoAddAsync must call base.DoAddAsync");
            StringAssert.Contains(template, "await base.DoEditAsync(updateAllFields)",
                "CrudVM.txt DoEditAsync must call base.DoEditAsync(updateAllFields)");
            StringAssert.Contains(template, "await base.DoDeleteAsync()",
                "CrudVM.txt DoDeleteAsync must call base.DoDeleteAsync");
        }

        // =================================================================
        // CG-02: Two-zone write helpers — WriteGeneratedZone / WritePartialZone
        // =================================================================

        private static MethodInfo GetWriteMethod(string name, int paramCount)
        {
            foreach (var m in typeof(CodeGenVM).GetMethods(BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (m.Name == name && m.GetParameters().Length == paramCount)
                    return m;
            }
            throw new InvalidOperationException($"Method {name} with {paramCount} params not found");
        }

        [TestMethod]
        public void WriteGeneratedZone_AlwaysOverwrites()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "cg02_gen_" + Guid.NewGuid().ToString("N")[..8] + ".cs");
            try
            {
                var writeGenZone = GetWriteMethod("WriteGeneratedZone", 2);
                writeGenZone.Invoke(null, new object[] { tmp, "v1" });
                writeGenZone.Invoke(null, new object[] { tmp, "v2" });
                Assert.AreEqual("v2", File.ReadAllText(tmp),
                    "WriteGeneratedZone must always overwrite (second write wins)");
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        [TestMethod]
        public void WritePartialZone_WritesOnce_DoesNotOverwriteExistingFile()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "cg02_partial_" + Guid.NewGuid().ToString("N")[..8] + ".cs");
            try
            {
                var writePartialZone = GetWriteMethod("WritePartialZone", 2);
                writePartialZone.Invoke(null, new object[] { tmp, "original" });
                writePartialZone.Invoke(null, new object[] { tmp, "should-be-ignored" });
                Assert.AreEqual("original", File.ReadAllText(tmp),
                    "WritePartialZone must NOT overwrite an existing companion file (dev edits are safe)");
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        [TestMethod]
        public void WritePartialZone_WritesFile_WhenNotExists()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "cg02_new_" + Guid.NewGuid().ToString("N")[..8] + ".cs");
            try
            {
                Assert.IsFalse(File.Exists(tmp), "File must not pre-exist");
                var writePartialZone = GetWriteMethod("WritePartialZone", 2);
                writePartialZone.Invoke(null, new object[] { tmp, "initial" });
                Assert.IsTrue(File.Exists(tmp), "WritePartialZone must create the file when it does not exist");
                Assert.AreEqual("initial", File.ReadAllText(tmp));
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        [TestMethod]
        public void MakeEmptyPartial_ExtractsNamespaceAndClassDecl()
        {
            var makeEmptyPartial = typeof(CodeGenVM).GetMethod(
                "MakeEmptyPartial",
                BindingFlags.NonPublic | BindingFlags.Static)!;

            var generatedContent = @"// <auto-generated/>
namespace MyApp.Controllers
{
    [ApiController]
    public partial class ProductController : BaseApiController
    {
        // body
    }
}";
            var result = (string)makeEmptyPartial.Invoke(null, new object[] { generatedContent, "ProductController.Generated.cs" })!;

            StringAssert.Contains(result, "MyApp.Controllers",
                "MakeEmptyPartial must extract the namespace");
            StringAssert.Contains(result, "public partial class ProductController",
                "MakeEmptyPartial must include the class declaration");
            StringAssert.Contains(result, "ProductController.Generated.cs",
                "MakeEmptyPartial must reference the generated file name");
        }

        // =================================================================
        // CG-09: UseSmartSearchDefaults property is present and defaults false
        // =================================================================

        [TestMethod]
        public void CodeGenVM_UseSmartSearchDefaults_DefaultsToFalse()
        {
            var vm = new CodeGenVM();
            Assert.IsFalse(vm.UseSmartSearchDefaults,
                "UseSmartSearchDefaults must default to false — preserves current output for unannotated models");
        }

        [TestMethod]
        public void CodeGenVM_UseSmartSearchDefaults_Property_IsPublic()
        {
            var prop = typeof(CodeGenVM).GetProperty(nameof(CodeGenVM.UseSmartSearchDefaults));
            Assert.IsNotNull(prop, "UseSmartSearchDefaults property must exist");
            Assert.IsTrue(prop!.CanRead && prop.CanWrite, "UseSmartSearchDefaults must be readable and writable");
        }
    }
}
