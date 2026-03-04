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
        /// Resolve with the FullName of an unannotated VM throws InvalidOperationException.
        /// </summary>
        [TestMethod]
        public void Throws_for_unannotated_vm()
        {
            var registry = BuildRegistry();
            Assert.ThrowsException<InvalidOperationException>(
                () => registry.Resolve(typeof(NotAnnotatedListVM).FullName));
        }

        /// <summary>
        /// Resolve with an unknown type name throws InvalidOperationException.
        /// </summary>
        [TestMethod]
        public void Throws_for_unknown_type()
        {
            var registry = BuildRegistry();
            Assert.ThrowsException<InvalidOperationException>(
                () => registry.Resolve("Some.Unknown.Type"));
        }
    }
}
