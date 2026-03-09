#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    [TestClass]
    public class GroupByStrategyResolverTests
    {
        [TestMethod]
        public void Default_resolver_returns_InProcess_for_SQLite()
        {
            var req = new AnalysisQueryRequest();
            var strategy = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.SQLite, req);
            Assert.IsInstanceOfType(strategy, typeof(InProcessGroupByStrategy));
        }

        [TestMethod]
        public void Default_resolver_returns_InProcess_for_SqlServer()
        {
            var req = new AnalysisQueryRequest();
            var strategy = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.SqlServer, req);
            Assert.IsInstanceOfType(strategy, typeof(InProcessGroupByStrategy));
        }

        [TestMethod]
        public void Default_resolver_returns_InProcess_for_Oracle()
        {
            var req = new AnalysisQueryRequest();
            var strategy = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.Oracle, req);
            Assert.IsInstanceOfType(strategy, typeof(InProcessGroupByStrategy));
        }

        [TestMethod]
        public void Default_resolver_returns_same_instance_across_calls()
        {
            var req = new AnalysisQueryRequest();
            var s1 = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.SQLite, req);
            var s2 = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.SqlServer, req);
            Assert.AreSame(s1, s2, "Phase 1 should return the same singleton InProcess instance.");
        }
    }
}
