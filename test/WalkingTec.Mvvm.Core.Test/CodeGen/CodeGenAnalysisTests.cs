using System;
using System.IO;
using System.Collections.Generic;
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
    }
}
