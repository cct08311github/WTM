using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// The development-time code generator validates the names it turns into files and keeps every
    /// output path inside the directories it is designed to write to.
    /// </summary>
    [TestClass]
    public class CodeGenPathGuardTest
    {
        private static readonly char Sep = Path.DirectorySeparatorChar;
        private string _base;
        private string _main;

        // the same model the combobox of the generator submits: assembly qualified name
        private static readonly string SchoolModel = typeof(SchoolTop).AssemblyQualifiedName;

        [TestInitialize]
        public void Init()
        {
            _base = Path.Combine(Path.GetTempPath(), "wtm-codegen-" + Guid.NewGuid().ToString("N"));
            _main = Path.Combine(_base, "Sample.Mvc");
            Directory.CreateDirectory(_main);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_base, true); } catch { }
        }

        private CodeGenVM CreateVM(string area = null, string model = null)
        {
            var vm = new CodeGenVM
            {
                SelectedModel = model ?? SchoolModel,
                ModuleName = "School",
                Area = area,
                UI = UIEnum.LayUI,
                IsApi = false,
            };
            vm.UseServerDirectories(Path.Combine(_main, "bin", "Debug", "net10.0") + Sep);
            return vm;
        }

        private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace(Sep, '/');

        #region identifiers and display text

        [DataTestMethod]
        [DataRow("School", true)]
        [DataRow("_School2", true)]
        [DataRow("a", true)]
        [DataRow("", false)]
        [DataRow(null, false)]
        [DataRow("2School", false)]
        [DataRow("School\n", false)]
        [DataRow("Sch ool", false)]
        [DataRow("Sch-ool", false)]
        [DataRow("Sch.ool", false)]
        [DataRow("a/b", false)]
        [DataRow("a\\b", false)]
        [DataRow("..", false)]
        [DataRow("School..", false)]
        [DataRow("學校", false)]
        public void IsIdentifier(string value, bool expected)
        {
            Assert.AreEqual(expected, CodeGenPathGuard.IsIdentifier(value));
        }

        [DataTestMethod]
        [DataRow(null, true)]
        [DataRow("", true)]
        [DataRow("Admin", true)]
        [DataRow("Admin\n", false)]
        [DataRow("..", false)]
        [DataRow("a/b", false)]
        public void IsOptionalIdentifier(string value, bool expected)
        {
            Assert.AreEqual(expected, CodeGenPathGuard.IsOptionalIdentifier(value));
        }

        [DataTestMethod]
        [DataRow(null, true)]
        [DataRow("", true)]
        [DataRow("School", true)]
        [DataRow("學校管理", true)]
        [DataRow("School management 2", true)]
        [DataRow("..", false)]
        [DataRow("a..b", false)]
        [DataRow("a/b", false)]
        [DataRow("a\\b", false)]
        [DataRow("a\0b", false)]
        [DataRow("a\nb", false)]
        public void IsSafeDisplayText(string value, bool expected)
        {
            Assert.AreEqual(expected, CodeGenPathGuard.IsSafeDisplayText(value));
        }

        #endregion

        #region containment

        [TestMethod]
        public void EnsureInside_AllowsRootAndDescendants()
        {
            var roots = new[] { _main };
            Assert.AreEqual(Path.GetFullPath(_main), CodeGenPathGuard.EnsureInside(roots, _main));
            var child = Path.Combine(_main, "Controllers", "SchoolController.cs");
            Assert.AreEqual(Path.GetFullPath(child), CodeGenPathGuard.EnsureInside(roots, child));
            // a trailing separator is the same directory
            Assert.AreEqual(Path.GetFullPath(_main) + Sep, CodeGenPathGuard.EnsureInside(roots, _main + Sep));
        }

        [TestMethod]
        public void EnsureInside_RejectsPathsOutsideTheRoots()
        {
            var roots = new[] { _main };
            var outside = new[]
            {
                _main + Sep + ".." + Sep + "Other" + Sep + "x.cs",
                _main + Sep + "Controllers" + Sep + ".." + Sep + ".." + Sep + "x.cs",
                _main + "-evil" + Sep + "x.cs",
                _base,
                Path.GetFullPath(Path.Combine(_main, "..")),
                Sep + "etc" + Sep + "x.cs",
            };
            foreach (var path in outside)
            {
                Assert.ThrowsException<InvalidOperationException>(() => CodeGenPathGuard.EnsureInside(roots, path), path);
            }
            Assert.ThrowsException<InvalidOperationException>(() => CodeGenPathGuard.EnsureInside(roots, null));
            Assert.ThrowsException<InvalidOperationException>(() => CodeGenPathGuard.EnsureInside(roots, ""));
            Assert.ThrowsException<InvalidOperationException>(() => CodeGenPathGuard.EnsureInside(null, _main));
        }

        #endregion

        #region selected model

        [TestMethod]
        public void TryResolveModel_AcceptsListedTopBasePoco()
        {
            var allowed = new List<Type> { typeof(SchoolTop), typeof(School) };
            Assert.IsTrue(CodeGenPathGuard.TryResolveModel(SchoolModel, allowed, out var type));
            Assert.AreEqual(typeof(SchoolTop), type);
        }

        [TestMethod]
        public void TryResolveModel_RejectsEverythingElse()
        {
            var allowed = new List<Type> { typeof(SchoolTop) };
            // not in the configured models
            Assert.IsFalse(CodeGenPathGuard.TryResolveModel(typeof(School).AssemblyQualifiedName, allowed, out _));
            // in the list but not a TopBasePoco
            Assert.IsFalse(CodeGenPathGuard.TryResolveModel(typeof(string).AssemblyQualifiedName, new List<Type> { typeof(string) }, out _));
            // unknown, malformed, empty
            Assert.IsFalse(CodeGenPathGuard.TryResolveModel("No.Such.Type, NoSuchAssembly", allowed, out _));
            Assert.IsFalse(CodeGenPathGuard.TryResolveModel("Foo/../../Bar, Baz", allowed, out _));
            Assert.IsFalse(CodeGenPathGuard.TryResolveModel("[[[", allowed, out _));
            Assert.IsFalse(CodeGenPathGuard.TryResolveModel("", allowed, out _));
            Assert.IsFalse(CodeGenPathGuard.TryResolveModel(null, allowed, out _));
            Assert.IsFalse(CodeGenPathGuard.TryResolveModel(SchoolModel, null, out _));
        }

        [TestMethod]
        public void GetInputErrors_ReportsEachInvalidField()
        {
            var allowed = new List<Type> { typeof(SchoolTop) };
            Assert.AreEqual(0, CreateVM().GetInputErrors(allowed).Count);
            Assert.AreEqual(0, CreateVM("Admin").GetInputErrors(allowed).Count);
            Assert.AreEqual(0, CreateVM("").GetInputErrors(allowed).Count);

            var vm = CreateVM("../Admin");
            Assert.AreEqual("Area", vm.GetInputErrors(allowed).Single().Key);

            vm = CreateVM("Admin\n");
            Assert.AreEqual("Area", vm.GetInputErrors(allowed).Single().Key);

            vm = CreateVM();
            vm.ModuleName = "a/../b";
            Assert.AreEqual("ModuleName", vm.GetInputErrors(allowed).Single().Key);

            // a Chinese module name is a display name and stays valid
            vm = CreateVM();
            vm.ModuleName = "學校管理";
            Assert.AreEqual(0, vm.GetInputErrors(allowed).Count);

            vm = CreateVM(model: typeof(School).AssemblyQualifiedName);
            Assert.AreEqual("SelectedModel", vm.GetInputErrors(allowed).Single().Key);

            vm = CreateVM(model: "Foo.Bar/../../Baz, Asm");
            Assert.AreEqual("SelectedModel", vm.GetInputErrors().Single().Key);

            vm = CreateVM(model: "");
            Assert.AreEqual("SelectedModel", vm.GetInputErrors().Single().Key);
        }

        #endregion

        #region server side directories

        [TestMethod]
        public void UseServerDirectories_DiscardsClientSuppliedMainDir()
        {
            var evil = Path.Combine(_base, "evil");
            var vm = new CodeGenVM { SelectedModel = SchoolModel, EntryDir = evil, MainDir = evil };
            Assert.AreEqual(evil, vm.MainDir);

            vm.UseServerDirectories(Path.Combine(_main, "bin", "Debug", "net10.0") + Sep);

            Assert.AreEqual(_main, vm.MainDir);
            Assert.AreEqual(Path.Combine(_main, "Controllers"), vm.ControllerDir);
            Assert.IsFalse(Directory.Exists(evil));
        }

        [TestMethod]
        public void UseServerDirectories_DefaultsToTheApplicationBaseDirectory()
        {
            var vm = new CodeGenVM { EntryDir = "/somewhere/else", MainDir = "/somewhere/else" };
            vm.UseServerDirectories();
            Assert.AreEqual(AppDomain.CurrentDomain.BaseDirectory, vm.EntryDir);
            var expected = new CodeGenVM { EntryDir = AppDomain.CurrentDomain.BaseDirectory };
            Assert.AreEqual(expected.MainDir, vm.MainDir);
        }

        [TestMethod]
        public void UseServerDirectories_ResetsCachedDirectories()
        {
            var vm = CreateVM();
            var first = vm.ControllerDir;
            var other = Path.Combine(_base, "Other.Mvc");
            Directory.CreateDirectory(other);
            vm.UseServerDirectories(Path.Combine(other, "bin", "Release", "net10.0") + Sep);
            Assert.AreNotEqual(first, vm.ControllerDir);
            Assert.AreEqual(Path.Combine(other, "Controllers"), vm.ControllerDir);
        }

        #endregion

        #region generated locations are unchanged for valid input

        [TestMethod]
        public void LayUI_Paths_WithoutArea_AreUnchanged()
        {
            var vm = CreateVM();
            Assert.AreEqual(_main, vm.MainDir);
            Assert.AreEqual($"{_main}{Sep}Controllers", vm.ControllerDir);
            Assert.AreEqual($"{_main}{Sep}Views{Sep}SchoolTop", vm.ViewDir);
            Assert.AreEqual($"{_main}{Sep}ViewModels{Sep}SchoolTopVMs", vm.VmDir);
            Assert.IsNull(vm.TestDir);
            Assert.IsTrue(Directory.Exists(vm.ControllerDir));
            Assert.IsTrue(Directory.Exists(vm.ViewDir));
            Assert.IsTrue(Directory.Exists(vm.VmDir));
        }

        [TestMethod]
        public void LayUI_Paths_WithArea_AreUnchanged()
        {
            var vm = CreateVM("Admin");
            Assert.AreEqual($"{_main}{Sep}Areas{Sep}Admin{Sep}Controllers", vm.ControllerDir);
            Assert.AreEqual($"{_main}{Sep}Areas{Sep}Admin{Sep}Views{Sep}SchoolTop", vm.ViewDir);
            Assert.AreEqual($"{_main}{Sep}Areas{Sep}Admin{Sep}ViewModels{Sep}SchoolTopVMs", vm.VmDir);
        }

        [TestMethod]
        public void SiblingProjectDirectories_AreStillUsed()
        {
            var vmProject = Path.Combine(_base, "Sample.ViewModel");
            var testProject = Path.Combine(_base, "Sample.Test");
            var sharedProject = Path.Combine(_base, "Sample.Shared");
            Directory.CreateDirectory(vmProject);
            Directory.CreateDirectory(testProject);
            Directory.CreateDirectory(sharedProject);

            var vm = CreateVM();
            Assert.AreEqual($"{vmProject}{Sep}SchoolTopVMs", vm.VmDir);
            Assert.AreEqual(testProject, vm.TestDir);
            Assert.AreEqual($"{sharedProject}{Sep}Pages{Sep}SchoolTop", vm.ShareDir);

            vm = CreateVM("Admin");
            Assert.AreEqual($"{vmProject}{Sep}Admin{Sep}SchoolTopVMs", vm.VmDir);
            Assert.AreEqual($"{sharedProject}{Sep}Pages{Sep}Admin{Sep}SchoolTop", vm.ShareDir);
        }

        [TestMethod]
        public void DoGen_LayUI_WritesTheSameFilesAsBefore()
        {
            var vm = CreateVM();
            vm.Wtm = MockWtmContext.CreateWtmContext();
            vm.DC = vm.Wtm.DC;
            vm.FieldInfos = new List<FieldInfo>();

            vm.DoGen();

            var files = Directory.GetFiles(_base, "*", SearchOption.AllDirectories).Select(x => Rel(_base, x)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var expected = new[]
            {
                "Sample.Mvc/Controllers/SchoolTopController.cs",
                "Sample.Mvc/ViewModels/SchoolTopVMs/SchoolTopBatchVM.cs",
                "Sample.Mvc/ViewModels/SchoolTopVMs/SchoolTopImportVM.cs",
                "Sample.Mvc/ViewModels/SchoolTopVMs/SchoolTopListVM.cs",
                "Sample.Mvc/ViewModels/SchoolTopVMs/SchoolTopSearcher.cs",
                "Sample.Mvc/ViewModels/SchoolTopVMs/SchoolTopVM.cs",
                "Sample.Mvc/Views/SchoolTop/BatchDelete.cshtml",
                "Sample.Mvc/Views/SchoolTop/BatchEdit.cshtml",
                "Sample.Mvc/Views/SchoolTop/Create.cshtml",
                "Sample.Mvc/Views/SchoolTop/Delete.cshtml",
                "Sample.Mvc/Views/SchoolTop/Details.cshtml",
                "Sample.Mvc/Views/SchoolTop/Edit.cshtml",
                "Sample.Mvc/Views/SchoolTop/Import.cshtml",
                "Sample.Mvc/Views/SchoolTop/Index.cshtml",
            }.OrderBy(x => x, StringComparer.Ordinal).ToList();
            CollectionAssert.AreEqual(expected, files);
        }

        [TestMethod]
        public void DoGen_WithTestProject_WritesTheControllerTest()
        {
            Directory.CreateDirectory(Path.Combine(_base, "Sample.Test"));
            var vm = CreateVM();
            vm.Wtm = MockWtmContext.CreateWtmContext();
            vm.DC = vm.Wtm.DC;
            vm.FieldInfos = new List<FieldInfo>();

            vm.DoGen();

            Assert.IsTrue(File.Exists(Path.Combine(_base, "Sample.Test", "SchoolTopControllerTest.cs")));
        }

        #endregion

        #region invalid input never reaches the file system

        [DataTestMethod]
        [DataRow("../../escape")]
        [DataRow("..")]
        [DataRow("a/b")]
        [DataRow("a\\b")]
        [DataRow("Admin\n")]
        [DataRow("1Admin")]
        public void InvalidArea_IsRejected_AndNothingIsWritten(string area)
        {
            var vm = CreateVM(area);
            Assert.ThrowsException<InvalidOperationException>(() => { var _ = vm.ControllerDir; });
            Assert.ThrowsException<InvalidOperationException>(() => { var _ = vm.ViewDir; });
            Assert.ThrowsException<InvalidOperationException>(() => { var _ = vm.VmDir; });
            Assert.ThrowsException<InvalidOperationException>(() => vm.DoGen());
            AssertNothingWritten();
        }

        [DataTestMethod]
        [DataRow("Foo.Bar/../../../escape, Asm")]
        [DataRow("Foo.Bar..Baz/x, Asm")]
        [DataRow("Foo.Bar\\x, Asm")]
        [DataRow("Foo.Bar-x, Asm")]
        [DataRow(", Asm")]
        [DataRow("")]
        public void InvalidModelName_IsRejected_AndNothingIsWritten(string model)
        {
            var vm = CreateVM(model: model);
            Assert.ThrowsException<InvalidOperationException>(() => { var _ = vm.ControllerDir; });
            Assert.ThrowsException<InvalidOperationException>(() => vm.DoGen());
            AssertNothingWritten();
        }

        [TestMethod]
        public void MainDirCannotBeUsedToEscapeWithDotDot()
        {
            // the output must stay under the resolved directory, a relative segment does not move it
            var vm = CreateVM();
            vm.MainDir = _main + Sep + ".." + Sep + "Other.Mvc";
            var controllerDir = vm.ControllerDir;
            Assert.AreEqual(Path.GetFullPath(Path.Combine(_main, "..", "Other.Mvc", "Controllers")), controllerDir);
            Assert.IsTrue(CodeGenPathGuard.IsInside(vm.MainDir, controllerDir));
        }

        private void AssertNothingWritten()
        {
            Assert.AreEqual(0, Directory.GetFiles(_base, "*", SearchOption.AllDirectories).Length);
            Assert.AreEqual(new[] { _main }.Single(), Directory.GetDirectories(_base).Single());
            Assert.AreEqual(0, Directory.GetDirectories(_main).Length);
        }

        #endregion

        #region requests without directories (#276)

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("/no/bin/folder/here")]
        public void BlankOrUnrecognizedEntryDir_DoesNotThrowWhenValidated(string entryDir)
        {
            // an empty request body binds to a VM whose EntryDir is blank; model validation then reads every
            // property that is not marked ValidateNever, and none of them may throw
            var vm = new CodeGenVM { EntryDir = entryDir };
            var validated = typeof(CodeGenVM).GetProperties()
                .Where(p => p.GetIndexParameters().Length == 0 && p.GetGetMethod() != null)
                .Where(p => p.IsDefined(typeof(Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNeverAttribute), true) == false)
                .ToList();
            Assert.IsTrue(validated.Any(p => p.Name == nameof(CodeGenVM.MainNS)));
            foreach (var p in validated)
            {
                try
                {
                    p.GetValue(vm);
                }
                catch (Exception ex)
                {
                    Assert.Fail($"Reading {p.Name} threw {ex.GetBaseException().GetType().Name}");
                }
            }
        }

        [TestMethod]
        public void MainNS_IsEmpty_WhenThereIsNoMainDir()
        {
            Assert.AreEqual("", new CodeGenVM().MainNS);
            Assert.AreEqual("", new CodeGenVM { EntryDir = "" }.MainNS);
            Assert.IsTrue(string.IsNullOrEmpty(new CodeGenVM { EntryDir = "/no/bin/folder/here" }.MainDir));
        }

        [TestMethod]
        public void MainNS_IsTheMainDirectoryName_WhenKnown()
        {
            var vm = CreateVM();
            Assert.AreEqual("Sample.Mvc", vm.MainNS);
        }

        [TestMethod]
        public void BlankEntryDir_GeneratingIsRefused_AndNothingIsWritten()
        {
            var vm = new CodeGenVM { SelectedModel = SchoolModel, ModuleName = "School" };
            Assert.ThrowsException<InvalidOperationException>(() => vm.DoGen());
            AssertNothingWritten();
        }

        [TestMethod]
        public void BlankEntryDir_IsReplacedByTheServerDirectory()
        {
            // the controller actions always call UseServerDirectories, so a blank value posted by the client is not an error
            var vm = new CodeGenVM { SelectedModel = SchoolModel, ModuleName = "School", EntryDir = "" };
            vm.UseServerDirectories();
            Assert.AreEqual(AppDomain.CurrentDomain.BaseDirectory, vm.EntryDir);
            Assert.AreEqual(0, vm.GetInputErrors().Count);
        }

        #endregion

        #region selected model that is not a type (#353)

        // a data context with the constructor the generator looks for (CS), so InitVM can create it
        public class CodeGenTestDataContext : DataContext
        {
            public CodeGenTestDataContext(CS cs) : base(cs.Value, cs.DbType ?? DBTypeEnum.Memory)
            {
            }
        }

        private static WTMContext CreateWtmWithConnection()
        {
            var configs = new Configs
            {
                Connections = new List<CS>
                {
                    new CS
                    {
                        Key = "default",
                        Value = Guid.NewGuid().ToString("N"),
                        DbType = DBTypeEnum.Memory,
                        DcConstructor = typeof(CodeGenTestDataContext).GetConstructor(new[] { typeof(CS) }),
                    },
                },
            };
            return MockWtmContext.CreateWtmContext(configs: configs);
        }

        [DataTestMethod]
        [DataRow("No.Such.Type, NoSuchAssembly")]
        [DataRow("No.Such.Type")]
        [DataRow("School")]
        [DataRow("Foo/../../Bar, Baz")]
        [DataRow("[[[")]
        [DataRow("   ")]
        public void InitVM_SelectedModelThatIsNotAType_DoesNotThrow(string selectedModel)
        {
            // the posted VM is initialized while the request is bound, before the action can report anything
            var vm = new CodeGenVM { SelectedModel = selectedModel, Wtm = CreateWtmWithConnection() };
            try
            {
                vm.DoInit();
            }
            catch (Exception ex)
            {
                Assert.Fail($"InitVM threw {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}");
            }
            Assert.IsNotNull(vm.FieldList);
        }

        [TestMethod]
        public void InitVM_SelectedModelOfTheConfiguredContext_StillFindsTheDataContext()
        {
            var vm = new CodeGenVM { SelectedModel = SchoolModel, Wtm = CreateWtmWithConnection() };
            vm.DoInit();
            Assert.IsInstanceOfType(vm.DC, typeof(CodeGenTestDataContext));
            Assert.IsNotNull(vm.FieldList);
        }

        [DataTestMethod]
        [DataRow("No.Such.Type, NoSuchAssembly")]
        [DataRow("No.Such.Type")]
        [DataRow("Foo.Bar, Baz")]
        public void GetInputErrors_SelectedModelThatIsNotAType_ReportsTheSelectedModel(string selectedModel)
        {
            var allowed = new List<Type> { typeof(SchoolTop) };
            var vm = CreateVM(model: selectedModel);
            Assert.AreEqual(nameof(CodeGenVM.SelectedModel), vm.GetInputErrors(allowed).Single().Key);
            // without a list of allowed models the name still has to resolve to a type
            Assert.AreEqual(nameof(CodeGenVM.SelectedModel), vm.GetInputErrors().Single().Key);
        }

        [TestMethod]
        public void DoGen_SelectedModelThatIsNotAType_IsRefused_AndNothingIsWritten()
        {
            var vm = CreateVM(model: "No.Such.Type, NoSuchAssembly");
            Assert.ThrowsException<InvalidOperationException>(() => vm.DoGen());
            AssertNothingWritten();
        }

        [TestMethod]
        public void FieldList_SelectedModelThatIsNotAType_HasNoFields()
        {
            var list = new CodeGenListVM { ModelFullName = "No.Such.Type, NoSuchAssembly" };
            Assert.AreEqual(0, list.GetSearchQuery().Count());
            list = new CodeGenListVM { ModelFullName = null };
            Assert.AreEqual(0, list.GetSearchQuery().Count());
        }

        #endregion
    }
}
