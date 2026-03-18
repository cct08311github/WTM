#nullable disable
using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    [TestClass]
    public class AnalysisVmRegistryTests
    {
        private class TestModel : TopBasePoco
        {
            [Dimension(DisplayName = "類別")]
            public string Category { get; set; }
        }

        private class TestSearcher : BaseSearcher { }

        [EnableAnalysis]
        private class AnnotatedListVM : BasePagedListVM<TestModel, TestSearcher> { }

        private class NotAnnotatedListVM : BasePagedListVM<TestModel, TestSearcher> { }

        private AnalysisVmRegistry BuildRegistry()
        {
            var registry = new AnalysisVmRegistry();
            registry.Build(new[] { Assembly.GetExecutingAssembly() });
            return registry;
        }

        /// <summary>
        /// After Build(), Resolve with the FullName of the annotated VM returns the correct type.
        /// </summary>
        [TestMethod]
        public void Resolves_annotated_vm()
        {
            var registry = BuildRegistry();
            var resolved = registry.Resolve(typeof(AnnotatedListVM).FullName);
            Assert.AreEqual(typeof(AnnotatedListVM), resolved);
        }

        /// <summary>
        /// Resolve with the FullName of an unannotated VM throws AnalysisVmNotFoundException.
        /// </summary>
        [TestMethod]
        public void Throws_for_unannotated_vm()
        {
            var registry = BuildRegistry();
            Assert.ThrowsException<AnalysisVmNotFoundException>(
                () => registry.Resolve(typeof(NotAnnotatedListVM).FullName));
        }

        /// <summary>
        /// Resolve with an unknown type name throws AnalysisVmNotFoundException.
        /// </summary>
        [TestMethod]
        public void Throws_for_unknown_type()
        {
            var registry = BuildRegistry();
            var ex = Assert.ThrowsException<AnalysisVmNotFoundException>(
                () => registry.Resolve("Some.Unknown.Type"));
            Assert.AreEqual("Some.Unknown.Type", ex.VmTypeName);
        }

        /// <summary>
        /// Resolve with empty string throws InvalidOperationException (bad request, not not-found).
        /// </summary>
        [TestMethod]
        public void Throws_InvalidOperation_for_empty_type_name()
        {
            var registry = BuildRegistry();
            var ex = Assert.ThrowsException<InvalidOperationException>(
                () => registry.Resolve(string.Empty));
            Assert.AreEqual("VM type name is required.", ex.Message);
        }

        /// <summary>
        /// Resolve with null throws InvalidOperationException (bad request, not not-found).
        /// </summary>
        [TestMethod]
        public void Throws_InvalidOperation_for_null_type_name()
        {
            var registry = BuildRegistry();
            var ex = Assert.ThrowsException<InvalidOperationException>(
                () => registry.Resolve(null));
            Assert.AreEqual("VM type name is required.", ex.Message);
        }

        /// <summary>
        /// Resolve with whitespace throws InvalidOperationException (bad request, not not-found).
        /// </summary>
        [TestMethod]
        public void Throws_InvalidOperation_for_whitespace_type_name()
        {
            var registry = BuildRegistry();
            var ex = Assert.ThrowsException<InvalidOperationException>(
                () => registry.Resolve("   "));
            Assert.AreEqual("VM type name is required.", ex.Message);
        }
    }
}
