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
        public void Default_resolver_returns_ServerSide_for_SqlServer()
        {
            var req = new AnalysisQueryRequest();
            var strategy = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.SqlServer, req);
            Assert.IsInstanceOfType(strategy, typeof(ServerSideGroupByStrategy));
        }

        [TestMethod]
        public void Default_resolver_returns_ServerSide_for_Oracle()
        {
            var req = new AnalysisQueryRequest();
            var strategy = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.Oracle, req);
            Assert.IsInstanceOfType(strategy, typeof(ServerSideGroupByStrategy));
        }

        [TestMethod]
        public void Default_resolver_returns_ServerSide_for_MySql()
        {
            var req = new AnalysisQueryRequest();
            var strategy = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.MySql, req);
            Assert.IsInstanceOfType(strategy, typeof(ServerSideGroupByStrategy));
        }

        [TestMethod]
        public void Default_resolver_returns_ServerSide_for_PgSql()
        {
            var req = new AnalysisQueryRequest();
            var strategy = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.PgSql, req);
            Assert.IsInstanceOfType(strategy, typeof(ServerSideGroupByStrategy));
        }

        [TestMethod]
        public void Default_resolver_returns_InProcess_for_Memory()
        {
            var req = new AnalysisQueryRequest();
            var strategy = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.Memory, req);
            Assert.IsInstanceOfType(strategy, typeof(InProcessGroupByStrategy));
        }

        [TestMethod]
        public void SqlServer_returns_same_ServerSide_instance_across_calls()
        {
            var req = new AnalysisQueryRequest();
            var s1 = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.SqlServer, req);
            var s2 = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.Oracle, req);
            Assert.AreSame(s1, s2, "ServerSide should be a singleton instance.");
        }

        [TestMethod]
        public void InProcess_returns_distinct_instances_across_calls()
        {
            var req = new AnalysisQueryRequest();
            // Each Resolve() call for SQLite/Memory must return a NEW InProcessGroupByStrategy
            // so that LastMaterializeCount cannot race between concurrent requests (#186).
            var s1 = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.SQLite, req);
            var s2 = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.SQLite, req);
            Assert.AreNotSame(s1, s2,
                "Resolve() must return a fresh InProcessGroupByStrategy per call — " +
                "sharing a single instance would cause LastMaterializeCount to race " +
                "across concurrent OLAP requests.");
        }

        [TestMethod]
        public void InProcess_returns_distinct_instances_for_Memory()
        {
            var req = new AnalysisQueryRequest();
            var s1 = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.Memory, req);
            var s2 = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.Memory, req);
            Assert.AreNotSame(s1, s2,
                "Resolve() must return a fresh InProcessGroupByStrategy per call (Memory path).");
        }

        [TestMethod]
        public void ServerSide_returns_same_instance_for_all_server_side_dbs()
        {
            var req = new AnalysisQueryRequest();
            var s1 = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.SqlServer, req);
            var s2 = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.Oracle, req);
            var s3 = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.MySql, req);
            var s4 = GroupByStrategyResolver.Default.Resolve(DBTypeEnum.PgSql, req);
            Assert.AreSame(s1, s2, "SqlServer and Oracle should share ServerSide singleton.");
            Assert.AreSame(s1, s3, "MySql should share ServerSide singleton.");
            Assert.AreSame(s1, s4, "PgSql should share ServerSide singleton.");
        }
    }
}
