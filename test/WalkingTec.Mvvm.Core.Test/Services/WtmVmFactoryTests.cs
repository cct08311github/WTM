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

        #endregion
    }
}
