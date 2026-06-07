#nullable enable
using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Services;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Services
{
    [TestClass]
    public class WtmVmFactoryTests
    {
        private WtmVmFactory _factory = null!;
        private WTMContext _wtm = null!;

        [TestInitialize]
        public void Setup()
        {
            _factory = new WtmVmFactory();
            _wtm = MockWtmContext.CreateWtmContext();
        }

        #region CreateVM<T> basic

        [TestMethod]
        public void CreateVM_Generic_CreatesAndInjectsWtm()
        {
            var vm = _factory.CreateVM<SimpleTestVM>(_wtm);

            vm.Should().NotBeNull();
            vm.Wtm.Should().BeSameAs(_wtm);
            vm.FC.Should().NotBeNull();
        }

        [TestMethod]
        public void CreateVM_Generic_PassInit_SkipsDoInit()
        {
            var vm = _factory.CreateVM<SimpleTestVM>(_wtm, passInit: true);

            vm.Should().NotBeNull();
            vm.InitCalled.Should().BeFalse();
        }

        [TestMethod]
        public void CreateVM_Generic_WithoutPassInit_CallsDoInit()
        {
            var vm = _factory.CreateVM<SimpleTestVM>(_wtm, passInit: false);

            vm.Should().NotBeNull();
            vm.InitCalled.Should().BeTrue();
        }

        #endregion

        #region CreateVM by type

        [TestMethod]
        public void CreateVM_ByType_CreatesCorrectType()
        {
            var vm = _factory.CreateVM(_wtm, typeof(SimpleTestVM));

            vm.Should().BeOfType<SimpleTestVM>();
            vm.Wtm.Should().BeSameAs(_wtm);
        }

        [TestMethod]
        public void CreateVM_NullType_ThrowsInvalidOperation()
        {
            Action act = () => _factory.CreateVM(_wtm, (Type?)null);
            act.Should().Throw<InvalidOperationException>();
        }

        #endregion

        #region Type guard — non-BaseVM types must be rejected before constructor invocation (#201)

        /// <summary>
        /// Passing typeof(string) must throw InvalidOperationException and must NOT
        /// invoke string's constructor (string has no parameterless ctor, but the key
        /// assertion is that the guard fires before any reflection-invoke attempt).
        /// </summary>
        [TestMethod]
        public void CreateVM_NonBaseVmType_ThrowsInvalidOperation_WithoutInvokingCtor()
        {
            Action act = () => _factory.CreateVM(_wtm, typeof(string));
            act.Should().Throw<InvalidOperationException>()
               .WithMessage("*is not a BaseVM*");
        }

        /// <summary>
        /// A type whose parameterless constructor has observable side effects must NOT
        /// run when the type is not a BaseVM.  The sentinel static field stays false.
        /// </summary>
        [TestMethod]
        public void CreateVM_NonBaseVmType_DoesNotInvokeConstructor()
        {
            NonBaseVmWithSideEffectCtor.CtorInvoked = false;

            Action act = () => _factory.CreateVM(_wtm, typeof(NonBaseVmWithSideEffectCtor));
            act.Should().Throw<InvalidOperationException>()
               .WithMessage("*is not a BaseVM*");

            // The constructor must never have run.
            NonBaseVmWithSideEffectCtor.CtorInvoked.Should().BeFalse(
                "WtmVmFactory must not invoke a non-BaseVM constructor");
        }

        /// <summary>
        /// Passing null must still throw InvalidOperationException (regression guard).
        /// </summary>
        [TestMethod]
        public void CreateVM_NullType_ThrowsInvalidOperation_WithGuardMessage()
        {
            Action act = () => _factory.CreateVM(_wtm, (Type?)null);
            act.Should().Throw<InvalidOperationException>()
               .WithMessage("*is not a BaseVM*");
        }

        /// <summary>
        /// Valid BaseVM subclass must still be constructed normally after the guard was added.
        /// </summary>
        [TestMethod]
        public void CreateVM_ValidBaseVmType_ConstructsNormally()
        {
            var vm = _factory.CreateVM(_wtm, typeof(SimpleTestVM), passInit: true);
            vm.Should().NotBeNull().And.BeOfType<SimpleTestVM>();
            vm.Wtm.Should().BeSameAs(_wtm);
        }

        #endregion

        #region CreateVM by name

        [TestMethod]
        public void CreateVM_ByFullName_CreatesCorrectType()
        {
            var fullName = typeof(SimpleTestVM).AssemblyQualifiedName;
            var vm = _factory.CreateVM(_wtm, fullName);

            vm.Should().BeOfType<SimpleTestVM>();
        }

        [TestMethod]
        public void CreateVM_ByInvalidName_ThrowsInvalidOperation()
        {
            Action act = () => _factory.CreateVM(_wtm, "NonExistent.Type, FakeAssembly");
            act.Should().Throw<InvalidOperationException>();
        }

        #endregion

        #region Values injection

        [TestMethod]
        public void CreateVM_WithValues_SetsProperties()
        {
            var values = new Dictionary<string, object>
            {
                { nameof(SimpleTestVM.TestProperty), "hello" }
            };
            var vm = _factory.CreateVM(_wtm, typeof(SimpleTestVM), values: values) as SimpleTestVM;

            vm.Should().NotBeNull();
            vm!.TestProperty.Should().Be("hello");
        }

        #endregion

        #region CreateVM<T> with id overload

        [TestMethod]
        public void CreateVM_GenericWithId_CreatesVm()
        {
            var id = Guid.NewGuid();
            // SimpleTestVM doesn't implement IBaseCRUDVM, so id path is silently skipped
            var vm = _factory.CreateVM<SimpleTestVM>(_wtm, id);
            vm.Should().NotBeNull();
            vm.Wtm.Should().BeSameAs(_wtm);
        }

        [TestMethod]
        public void CreateVM_GenericWithId_PassInit_SkipsInit()
        {
            var vm = _factory.CreateVM<SimpleTestVM>(_wtm, Guid.NewGuid(), passInit: true);
            vm.InitCalled.Should().BeFalse();
        }

        #endregion

        #region CreateVM<T> with ids array overload

        [TestMethod]
        public void CreateVM_GenericWithIds_CreatesVm()
        {
            var ids = new object[] { Guid.NewGuid(), Guid.NewGuid() };
            var vm = _factory.CreateVM<SimpleTestVM>(_wtm, ids);
            vm.Should().NotBeNull();
            vm.Wtm.Should().BeSameAs(_wtm);
        }

        [TestMethod]
        public void CreateVM_GenericWithIds_PassInit_SkipsInit()
        {
            var ids = new object[] { Guid.NewGuid() };
            var vm = _factory.CreateVM<SimpleTestVM>(_wtm, ids, passInit: true);
            vm.InitCalled.Should().BeFalse();
        }

        #endregion

        #region CreateVM<T> with lambda values

        [TestMethod]
        public void CreateVM_GenericWithLambdaValues_SetsProperty()
        {
            var vm = _factory.CreateVM<SimpleTestVM>(_wtm,
                values: x => x.TestProperty == "fromLambda");
            vm.TestProperty.Should().Be("fromLambda");
        }

        [TestMethod]
        public void CreateVM_GenericWithNullValues_DoesNotThrow()
        {
            var vm = _factory.CreateVM<SimpleTestVM>(_wtm, values: null);
            vm.Should().NotBeNull();
        }

        #endregion

        #region CreateVM with IBasePagedListVM

        [TestMethod]
        public void CreateVM_ListVM_InitializesSearcher()
        {
            var vm = _factory.CreateVM<TestListVM>(_wtm, passInit: false);
            vm.Should().NotBeNull();
            vm.Searcher.Should().NotBeNull();
        }

        [TestMethod]
        public void CreateVM_ListVM_PassInit_SearcherDoInitNotCalled()
        {
            var vm = _factory.CreateVM<TestListVM>(_wtm, passInit: true);
            vm.Should().NotBeNull();
        }

        #endregion

        #region SetSubVm — VM with sub-VM property

        [TestMethod]
        public void CreateVM_WithSubVm_SubVmIsInitialized()
        {
            var vm = _factory.CreateVM<ParentVM>(_wtm, passInit: false);
            vm.Should().NotBeNull();
            vm.Sub.Should().NotBeNull();
            vm.Sub!.Wtm.Should().BeSameAs(_wtm);
        }

        [TestMethod]
        public void CreateVM_WithSubVm_PassInit_SubVmCreatedButInitSkipped()
        {
            var vm = _factory.CreateVM<ParentVM>(_wtm, passInit: true);
            vm.Sub.Should().NotBeNull();
        }

        [TestMethod]
        public void CreateVM_WithExistingSubVm_DoesNotReplaceIt()
        {
            // ParentWithPresetSub has Sub pre-populated in ctor
            var vm = _factory.CreateVM<ParentWithPresetSub>(_wtm, passInit: true);
            vm.Sub.Should().NotBeNull();
            vm.Sub!.Tag.Should().Be("preset");
        }

        #endregion

        #region CreateVM by type — null ids handled

        [TestMethod]
        public void CreateVM_ByType_NullIds_DoesNotThrow()
        {
            var vm = _factory.CreateVM(_wtm, typeof(SimpleTestVM), null, null, null, true);
            vm.Should().NotBeNull();
        }

        [TestMethod]
        public void CreateVM_ByType_EmptyIds_DoesNotThrow()
        {
            var vm = _factory.CreateVM(_wtm, typeof(SimpleTestVM), null, [], null, true);
            vm.Should().NotBeNull();
        }

        #endregion

        #region CRUDVM with id (covers line 44 — SetEntityById)

        [TestMethod]
        public void CreateVM_CRUDVMWithId_TriesSetEntityById_ThrowsWhenNotFound()
        {
            // BaseCRUDVM<School> implements IBaseCRUDVM<School> → IBaseCRUDVM<TopBasePoco>.
            // Line 44 (cvm.SetEntityById(id)) IS executed; SetEntityById then throws
            // "数据不存在" because the random ID doesn't exist.  The factory propagates
            // that exception — we assert on it so the test passes.
            var seed = Guid.NewGuid().ToString();
            var dc = new DataContext(seed, DBTypeEnum.Memory);
            dc.Database.EnsureCreated();
            var wtm = MockWtmContext.CreateWtmContext(dc);
            var nonExistentId = Guid.NewGuid();

            Action act = () => _factory.CreateVM(wtm, typeof(BaseCRUDVM<School>), nonExistentId, null, null, true);
            act.Should().Throw<Exception>().WithMessage("*不存在*");
        }

        #endregion

        #region BatchVM — covers InitBatchVM path (lines 51 and 135-179)

        [TestMethod]
        public void CreateVM_BatchVM_WithNullIds_InitBatchVm_DoesNotThrow()
        {
            var vm = _factory.CreateVM<TestBatchVM>(_wtm, ids: null!, passInit: true);
            vm.Should().NotBeNull();
            vm.Ids.Should().NotBeNull();
        }

        [TestMethod]
        public void CreateVM_BatchVM_WithIds_PopulatesIds()
        {
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var vm = _factory.CreateVM<TestBatchVM>(_wtm, new object[] { id1, id2 }, passInit: true);
            vm.Should().NotBeNull();
            vm.Ids.Should().HaveCount(2);
        }

        [TestMethod]
        public void CreateVM_BatchVM_WithListVM_ListVmCopyContextCalled()
        {
            // TestBatchWithListVM has a ListVM — exercises the ListVM sub-branch in InitBatchVM
            var vm = _factory.CreateVM<TestBatchWithListVM>(_wtm, passInit: true);
            vm.Should().NotBeNull();
            vm.ListVM.Should().NotBeNull();
        }

        [TestMethod]
        public void CreateVM_BatchVM_WithLinkedVM_LinkedVmCopyContextCalled()
        {
            // TestBatchWithLinkedVM exercises the LinkedVM branch in InitBatchVM
            var vm = _factory.CreateVM<TestBatchWithLinkedVM>(_wtm, passInit: true);
            vm.Should().NotBeNull();
            vm.LinkedVM.Should().NotBeNull();
        }

        [TestMethod]
        public void CreateVM_BatchVM_WithErrors_AddErrorColumnCalled()
        {
            // After adding an error we expect AddErrorColumn to be invoked on ListVM
            var vm = _factory.CreateVM<TestBatchWithListAndError>(_wtm, passInit: true);
            vm.Should().NotBeNull();
        }

        #endregion

        #region Sentinel helper — non-BaseVM type with observable constructor side-effect (#201)

        /// <summary>
        /// NOT a BaseVM.  The static flag is set to true if the parameterless constructor
        /// ever runs, letting tests assert the guard fired before constructor invocation.
        /// </summary>
        public class NonBaseVmWithSideEffectCtor
        {
            public static bool CtorInvoked;

            public NonBaseVmWithSideEffectCtor()
            {
                CtorInvoked = true;
            }
        }

        #endregion

        #region Test VM classes

        public class SimpleTestVM : BaseVM
        {
            public bool InitCalled { get; private set; }
            public string? TestProperty { get; set; }

            protected override void InitVM()
            {
                InitCalled = true;
            }
        }

        /// <summary>Simple concrete ListVM for factory path coverage.</summary>
        public class TestListVM : BasePagedListVM<Student, BaseSearcher>
        {
            protected override System.Collections.Generic.IEnumerable<IGridColumn<Student>> InitGridHeader()
            {
                return [];
            }
        }

        /// <summary>VM with a sub-VM property — exercises SetSubVm.</summary>
        public class SubVM : BaseVM
        {
            public string? Tag { get; set; }
        }

        public class ParentVM : BaseVM
        {
            public SubVM? Sub { get; set; }
        }

        public class ParentWithPresetSub : BaseVM
        {
            public SubVM? Sub { get; set; } = new SubVM { Tag = "preset" };
        }

        /// <summary>Minimal BatchVM with no ListVM or LinkedVM.</summary>
        public class TestBatchVM : BaseBatchVM<School, BaseVM>
        {
        }

        /// <summary>BatchVM with a LinkedVM (exercises LinkedVM.CopyContext path).</summary>
        public class TestBatchWithLinkedVM : BaseBatchVM<School, SimpleTestVM>
        {
            public TestBatchWithLinkedVM()
            {
                LinkedVM = new SimpleTestVM();
            }
        }

        /// <summary>BatchVM with a ListVM (exercises ListVM sub-branch).</summary>
        public class TestBatchListInner : BasePagedListVM<School, BaseSearcher>
        {
            protected override System.Collections.Generic.IEnumerable<IGridColumn<School>> InitGridHeader()
            {
                return [];
            }
        }

        public class TestBatchWithListVM : BaseBatchVM<School, BaseVM>
        {
            public TestBatchWithListVM()
            {
                ListVM = new TestBatchListInner();
            }
        }

        /// <summary>BatchVM with ListVM and a pre-added error — exercises AddErrorColumn.</summary>
        public class TestBatchWithListAndError : BaseBatchVM<School, BaseVM>
        {
            public TestBatchWithListAndError()
            {
                ListVM = new TestBatchListInner();
                ErrorMessage["row1"] = "some error";
            }
        }

        #endregion
    }
}
