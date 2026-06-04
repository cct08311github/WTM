using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.CodeGen
{
    [TestClass]
    public class CodeGenAnalysisTests
    {
        private string _tempDir;

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "wtm_codegen_test_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }

        [TestMethod]
        public void InjectAnalysisAttributes_Adds_Dimension_And_Measure()
        {
            // Arrange
            var modelContent = @"using System;
namespace TestApp.Models
{
    public class Student : BasePoco
    {
        public string Name { get; set; }
        public decimal Score { get; set; }
    }
}";
            var modelPath = Path.Combine(_tempDir, "Student.cs");
            File.WriteAllText(modelPath, modelContent);

            var vm = CreateCodeGenVM("Student", _tempDir, new List<FieldInfo>
            {
                new FieldInfo { FieldName = "Name", IsDimensionField = true },
                new FieldInfo { FieldName = "Score", IsMeasureField = true }
            });

            // Act
            var result = vm.InjectAnalysisAttributes();

            // Assert
            var content = File.ReadAllText(modelPath);
            Assert.IsTrue(content.Contains("[Dimension]"), "Should contain [Dimension]");
            Assert.IsTrue(content.Contains("[Measure]"), "Should contain [Measure]");
            Assert.IsTrue(content.Contains("using WalkingTec.Mvvm.Core.Analysis;"), "Should contain analysis using");
            Assert.IsTrue(result.Contains("injected"), "Should report success");
        }

        [TestMethod]
        public void InjectAnalysisAttributes_Idempotent_No_Duplicate()
        {
            // Arrange
            var modelContent = @"using System;
using WalkingTec.Mvvm.Core.Analysis;
namespace TestApp.Models
{
    public class Student : BasePoco
    {
        [Dimension]
        public string Name { get; set; }
    }
}";
            var modelPath = Path.Combine(_tempDir, "Student.cs");
            File.WriteAllText(modelPath, modelContent);

            var vm = CreateCodeGenVM("Student", _tempDir, new List<FieldInfo>
            {
                new FieldInfo { FieldName = "Name", IsDimensionField = true }
            });

            // Act
            vm.InjectAnalysisAttributes();

            // Assert
            var result = File.ReadAllText(modelPath);
            var count = result.Split("[Dimension]").Length - 1;
            Assert.AreEqual(1, count, "Should not duplicate [Dimension]");
        }

        [TestMethod]
        public void InjectAnalysisAttributes_EnableAnalysis_False_Does_Nothing()
        {
            var vm = new CodeGenVM { EnableAnalysis = false };
            var result = vm.InjectAnalysisAttributes();
            Assert.AreEqual("", result);
        }

        [TestMethod]
        public void InjectAnalysisAttributes_Missing_File_Returns_Warning()
        {
            var vm = CreateCodeGenVM("NonExistent", _tempDir, new List<FieldInfo>
            {
                new FieldInfo { FieldName = "Name", IsDimensionField = true }
            });

            var result = vm.InjectAnalysisAttributes();
            Assert.IsTrue(result.Contains("Warning") || result.Contains("Cannot find"),
                $"Should return warning, got: {result}");
        }

        [TestMethod]
        public void InjectAnalysisAttributes_Adds_Using_Statement()
        {
            // Arrange — file without the analysis using
            var modelContent = @"using System;
using WalkingTec.Mvvm.Core;
namespace TestApp.Models
{
    public class Order : BasePoco
    {
        public string Region { get; set; }
    }
}";
            var modelPath = Path.Combine(_tempDir, "Order.cs");
            File.WriteAllText(modelPath, modelContent);

            var vm = CreateCodeGenVM("Order", _tempDir, new List<FieldInfo>
            {
                new FieldInfo { FieldName = "Region", IsDimensionField = true }
            });

            // Act
            vm.InjectAnalysisAttributes();

            // Assert
            var content = File.ReadAllText(modelPath);
            Assert.IsTrue(content.Contains("using WalkingTec.Mvvm.Core.Analysis;"),
                "Should add analysis using statement");
        }

        [TestMethod]
        public void InjectAnalysisAttributes_DateTime_Gets_Hierarchy()
        {
            // Arrange — Type.GetType will return null for test types,
            // so fallback to source text detection
            var modelContent = @"using System;
namespace TestApp.Models
{
    public class Order : BasePoco
    {
        public DateTime OrderDate { get; set; }
    }
}";
            var modelPath = Path.Combine(_tempDir, "Order.cs");
            File.WriteAllText(modelPath, modelContent);

            var vm = CreateCodeGenVM("Order", _tempDir, new List<FieldInfo>
            {
                new FieldInfo { FieldName = "OrderDate", IsDimensionField = true }
            });

            // Act
            vm.InjectAnalysisAttributes();

            // Assert
            var content = File.ReadAllText(modelPath);
            Assert.IsTrue(content.Contains("[Dimension(Hierarchy = DateHierarchy.Month)]"),
                "DateTime dimension should have Hierarchy = DateHierarchy.Month");
        }

        [TestMethod]
        public void InjectAnalysisAttributes_No_Fields_Returns_Empty()
        {
            var vm = CreateCodeGenVM("Student", _tempDir, new List<FieldInfo>());
            var result = vm.InjectAnalysisAttributes();
            Assert.AreEqual("", result);
        }

        [TestMethod]
        public void FindModelFile_Finds_File_In_Directory()
        {
            // Arrange
            var modelPath = Path.Combine(_tempDir, "MyModel.cs");
            File.WriteAllText(modelPath, "public class MyModel {}");

            var vm = new CodeGenVM();

            // Act
            var result = vm.FindModelFile(_tempDir, "MyModel.cs");

            // Assert
            Assert.IsNotNull(result);
            Assert.IsTrue(result.EndsWith("MyModel.cs"));
        }

        [TestMethod]
        public void FindModelFile_Excludes_Bin_Obj()
        {
            // Arrange — file only in bin directory
            var binDir = Path.Combine(_tempDir, "bin", "Debug");
            Directory.CreateDirectory(binDir);
            File.WriteAllText(Path.Combine(binDir, "MyModel.cs"), "// in bin");

            var vm = new CodeGenVM();

            // Act
            var result = vm.FindModelFile(_tempDir, "MyModel.cs");

            // Assert
            Assert.IsNull(result, "Should not find file in bin directory");
        }

        [TestMethod]
        public void FindModelFile_Returns_Null_For_Missing()
        {
            var vm = new CodeGenVM();
            var result = vm.FindModelFile(_tempDir, "DoesNotExist.cs");
            Assert.IsNull(result);
        }

        // ---------------------------------------------------------------
        // Bug #122 — Security: MainDir is HTTP model-bindable (arbitrary write root)
        // ---------------------------------------------------------------

        [TestMethod]
        public void MainDir_Property_Has_BindNever_Attribute()
        {
            // Arrange
            var prop = typeof(CodeGenVM).GetProperty(nameof(CodeGenVM.MainDir));

            // Act — use Attribute.GetCustomAttribute to avoid requiring 'using System.Reflection'
            // which would create a FieldInfo ambiguity with WalkingTec.Mvvm.Mvc.FieldInfo.
            var attr = prop is not null
                ? Attribute.GetCustomAttribute(prop, typeof(BindNeverAttribute))
                : null;

            // Assert
            Assert.IsNotNull(attr,
                "MainDir must carry [BindNever] to prevent HTTP model-binding from " +
                "overriding the server-derived write root and bypassing SafeCombine boundary checks.");
        }

        [TestMethod]
        public void EntryDir_Property_Has_BindNever_Attribute_Unchanged()
        {
            // Guard: EntryDir should already have [BindNever] and must not have been touched.
            var prop = typeof(CodeGenVM).GetProperty(nameof(CodeGenVM.EntryDir));
            var attr = prop is not null
                ? Attribute.GetCustomAttribute(prop, typeof(BindNeverAttribute))
                : null;
            Assert.IsNotNull(attr,
                "EntryDir must still carry [BindNever] — it should not have been modified.");
        }

        // ---------------------------------------------------------------
        // Bug #122 — NRE: ShareDir crashes when no *.shared sibling exists
        // ---------------------------------------------------------------

        [TestMethod]
        public void ShareDir_No_Shared_Sibling_Falls_Back_To_MainDir_Without_NRE()
        {
            // Arrange — temp directory tree without any *.shared sibling:
            //   <root>/
            //     MyApp/          ← MainDir (no *.shared next to it)
            var root = Path.Combine(Path.GetTempPath(), "wtm_sharedir_test_" + Guid.NewGuid().ToString("N")[..8]);
            var mainDir = Path.Combine(root, "MyApp");
            Directory.CreateDirectory(mainDir);
            try
            {
                var vm = new CodeGenVM
                {
                    _mainDir = mainDir,
                    SelectedModel = "TestApp.Models.Product, TestAssembly"
                };

                // Act — must not throw NullReferenceException
                string shareDir = null!;
                try
                {
                    shareDir = vm.ShareDir;
                }
                catch (NullReferenceException ex)
                {
                    Assert.Fail($"ShareDir threw NullReferenceException when no *.shared sibling exists: {ex.Message}");
                }

                // Assert — fallback path should be within MainDir and contain the model name
                Assert.IsNotNull(shareDir, "ShareDir must return a non-null path.");
                Assert.IsTrue(shareDir.StartsWith(mainDir, StringComparison.OrdinalIgnoreCase),
                    $"Fallback ShareDir '{shareDir}' should be inside MainDir '{mainDir}'.");
                Assert.IsTrue(Directory.Exists(shareDir),
                    "ShareDir fallback path should have been created.");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void ShareDir_No_Shared_Sibling_With_Area_Falls_Back_To_MainDir_Without_NRE()
        {
            // Arrange — same layout but with an Area set
            var root = Path.Combine(Path.GetTempPath(), "wtm_sharedir_area_test_" + Guid.NewGuid().ToString("N")[..8]);
            var mainDir = Path.Combine(root, "MyApp");
            Directory.CreateDirectory(mainDir);
            try
            {
                var vm = new CodeGenVM
                {
                    _mainDir = mainDir,
                    Area = "Admin",
                    SelectedModel = "TestApp.Models.Product, TestAssembly"
                };

                string shareDir = null!;
                try
                {
                    shareDir = vm.ShareDir;
                }
                catch (NullReferenceException ex)
                {
                    Assert.Fail($"ShareDir threw NullReferenceException (with Area) when no *.shared sibling exists: {ex.Message}");
                }

                Assert.IsNotNull(shareDir, "ShareDir must return a non-null path when Area is set.");
                Assert.IsTrue(shareDir.StartsWith(mainDir, StringComparison.OrdinalIgnoreCase),
                    $"Fallback ShareDir '{shareDir}' should be inside MainDir '{mainDir}'.");
                Assert.IsTrue(Directory.Exists(shareDir),
                    "ShareDir fallback path (with Area) should have been created.");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void ShareDir_With_Shared_Sibling_Uses_It()
        {
            // Arrange — tree with a *.shared sibling:
            //   <root>/
            //     MyApp/          ← MainDir
            //     MyApp.Shared/   ← the shared project
            var root = Path.Combine(Path.GetTempPath(), "wtm_sharedir_exists_test_" + Guid.NewGuid().ToString("N")[..8]);
            var mainDir = Path.Combine(root, "MyApp");
            var sharedDir = Path.Combine(root, "MyApp.Shared");
            Directory.CreateDirectory(mainDir);
            Directory.CreateDirectory(sharedDir);
            try
            {
                var vm = new CodeGenVM
                {
                    _mainDir = mainDir,
                    SelectedModel = "TestApp.Models.Product, TestAssembly"
                };

                string shareDir = vm.ShareDir;

                // Should be inside the *.shared sibling, not inside MainDir
                Assert.IsTrue(shareDir.StartsWith(sharedDir, StringComparison.OrdinalIgnoreCase),
                    $"ShareDir '{shareDir}' should be inside the *.shared sibling '{sharedDir}' when it exists.");
                Assert.IsTrue(Directory.Exists(shareDir), "ShareDir path should have been created.");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        private CodeGenVM CreateCodeGenVM(string modelName, string mainDir, List<FieldInfo> fields)
        {
            return new CodeGenVM
            {
                EnableAnalysis = true,
                FieldInfos = fields,
                _mainDir = mainDir,
                SelectedModel = $"TestApp.Models.{modelName}, TestAssembly"
            };
        }

        // ---------------------------------------------------------------
        // Bug #135 — M26: InjectAnalysisAttributes write-boundary containment
        // ---------------------------------------------------------------

        /// <summary>
        /// M26: A same-named .cs file placed ABOVE MainDir (reachable by FindModelFile's
        /// AllDirectories + 5-level climb) must be rejected with an error message rather
        /// than written-to.  This was the bug: SafeCombine was anchored to the discovered
        /// file's OWN directory, not to MainDir, so out-of-tree files slipped through.
        /// </summary>
        [TestMethod]
        public void InjectAnalysisAttributes_RejectsFileOutsideMainDir()
        {
            // Arrange:
            //   <root>/
            //     Attacker.cs    ← same name as model, BUT outside MainDir
            //     MainDir/       ← the write root (no matching file here)
            var root = Path.Combine(Path.GetTempPath(), "wtm_m26_boundary_" + Guid.NewGuid().ToString("N")[..8]);
            var mainDir = Path.Combine(root, "MainDir");
            Directory.CreateDirectory(mainDir);

            // Place the target file one level ABOVE MainDir — FindModelFile will find it
            // when it climbs up from MainDir.
            var attackerFilePath = Path.Combine(root, "Attacker.cs");
            var attackerContent = @"using System;
namespace Attacker
{
    public class Attacker { public string Name { get; set; } }
}";
            File.WriteAllText(attackerFilePath, attackerContent);

            var vm = new CodeGenVM
            {
                EnableAnalysis = true,
                FieldInfos = new List<FieldInfo>
                {
                    new FieldInfo { FieldName = "Name", IsDimensionField = true }
                },
                _mainDir = mainDir,
                SelectedModel = "Attacker.Attacker, TestAssembly"  // model name = "Attacker"
            };

            try
            {
                // Act
                var result = vm.InjectAnalysisAttributes();

                // Assert: must not succeed silently; must report an error indicating the
                // file is outside the project root.
                Assert.IsTrue(
                    result.StartsWith("Error:", StringComparison.Ordinal),
                    $"Expected an error indicating out-of-tree file, got: '{result}'");

                // The attacker file must NOT have been modified.
                var afterContent = File.ReadAllText(attackerFilePath);
                Assert.AreEqual(attackerContent, afterContent,
                    "The out-of-tree .cs file must not have been written by InjectAnalysisAttributes.");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        /// <summary>
        /// M26 boundary — file legitimately inside a subdirectory of MainDir: must be found
        /// and written (happy-path boundary test).
        /// </summary>
        [TestMethod]
        public void InjectAnalysisAttributes_AcceptsFileInsideMainDir()
        {
            // Arrange: model file lives in a Models sub-folder of MainDir.
            var root = Path.Combine(Path.GetTempPath(), "wtm_m26_inside_" + Guid.NewGuid().ToString("N")[..8]);
            var mainDir = Path.Combine(root, "MainDir");
            var modelsDir = Path.Combine(mainDir, "Models");
            Directory.CreateDirectory(modelsDir);

            var modelContent = @"using System;
namespace TestApp.Models
{
    public class Widget : BasePoco
    {
        public string Color { get; set; }
        public decimal Price { get; set; }
    }
}";
            File.WriteAllText(Path.Combine(modelsDir, "Widget.cs"), modelContent);

            var vm = CreateCodeGenVM("Widget", mainDir, new List<FieldInfo>
            {
                new FieldInfo { FieldName = "Color", IsDimensionField = true },
                new FieldInfo { FieldName = "Price", IsMeasureField = true }
            });

            try
            {
                // Act
                var result = vm.InjectAnalysisAttributes();

                // Assert: must succeed and report injection.
                Assert.IsTrue(result.Contains("injected", StringComparison.OrdinalIgnoreCase),
                    $"Expected success message containing 'injected', got: '{result}'");

                var written = File.ReadAllText(Path.Combine(modelsDir, "Widget.cs"));
                Assert.IsTrue(written.Contains("[Dimension]"), "Should contain [Dimension]");
                Assert.IsTrue(written.Contains("[Measure]"), "Should contain [Measure]");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        // ---------------------------------------------------------------
        // Bug #135 — M27: ModuleName [RegularExpression] validation
        // ---------------------------------------------------------------

        /// <summary>
        /// M27: ModuleName property must carry a [RegularExpression] attribute that rejects
        /// characters capable of injecting content into JS object literals and JSON strings
        /// (e.g. single-quote, double-quote, backslash, angle-bracket).
        /// </summary>
        [TestMethod]
        public void ModuleName_RegularExpression_Attribute_RejectsInjectionCharacters()
        {
            // Verify the attribute is present on the property.
            var prop = typeof(CodeGenVM).GetProperty(nameof(CodeGenVM.ModuleName));
            Assert.IsNotNull(prop, "ModuleName property must exist.");

            var regexAttr = prop.GetCustomAttributes(typeof(RegularExpressionAttribute), inherit: false)
                                .Cast<RegularExpressionAttribute>()
                                .FirstOrDefault();
            Assert.IsNotNull(regexAttr,
                "ModuleName must carry [RegularExpression] to prevent JS/JSON injection.");

            // Values that must FAIL validation (each contains an injection-capable character).
            string[] badValues = [
                "'; alert(1); //",          // JS single-quote injection
                @"""text"",""evil"":""val", // JSON double-quote injection
                @"foo\bar",                  // backslash
                "<script>alert(1)</script>", // angle-bracket HTML/JS
                "abc\ndef",                  // newline
            ];

            foreach (var bad in badValues)
            {
                var ctx = new ValidationContext(new object()) { MemberName = "ModuleName" };
                var result = regexAttr.GetValidationResult(bad, ctx);
                Assert.IsNotNull(result,
                    $"ModuleName value '{bad}' should fail [RegularExpression] validation but was accepted.");
            }
        }

        /// <summary>
        /// M27: Valid ModuleName values (letters, digits, underscores, hyphens, spaces)
        /// must pass the [RegularExpression] validation — i.e., the fix must not break
        /// legitimate module names that include spaces, hyphens, or CJK characters.
        /// WTM is a Chinese-origin framework; module names like "用户管理" are common and
        /// must be accepted. The regex uses \p{L}\p{N} (Unicode letter/digit categories)
        /// rather than [A-Za-z0-9] to allow CJK and other Unicode scripts.
        /// </summary>
        [TestMethod]
        public void ModuleName_RegularExpression_Attribute_AcceptsValidNames()
        {
            var prop = typeof(CodeGenVM).GetProperty(nameof(CodeGenVM.ModuleName));
            Assert.IsNotNull(prop, "ModuleName property must exist.");

            var regexAttr = prop.GetCustomAttributes(typeof(RegularExpressionAttribute), inherit: false)
                                .Cast<RegularExpressionAttribute>()
                                .FirstOrDefault();
            Assert.IsNotNull(regexAttr, "ModuleName must carry [RegularExpression].");

            // Values that must PASS validation.
            string[] goodValues = [
                "Student",
                "OrderManagement",
                "My Module",         // space allowed
                "HR-Admin",          // hyphen allowed
                "Module_01",         // underscore and digit
                "A",
                "用户管理",           // CJK — WTM is Chinese-origin; this must pass
                "订单 管理",          // CJK with space
            ];

            foreach (var good in goodValues)
            {
                var ctx = new ValidationContext(new object()) { MemberName = "ModuleName" };
                var result = regexAttr.GetValidationResult(good, ctx);
                Assert.IsNull(result,
                    $"ModuleName value '{good}' should pass [RegularExpression] validation but was rejected.");
            }
        }
    }
}
