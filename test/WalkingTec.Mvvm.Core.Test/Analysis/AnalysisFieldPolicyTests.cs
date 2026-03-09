using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    [TestClass]
    public class AnalysisFieldPolicyTests
    {
        [TestMethod]
        public void DefaultPolicy_returns_all_fields()
        {
            var policy = new DefaultAnalysisFieldPolicy();
            var fields = new List<AnalysisFieldMeta>
            {
                new AnalysisFieldMeta { FieldName = "F1" },
                new AnalysisFieldMeta { FieldName = "F2" }
            };
            var user = new ClaimsPrincipal();

            var result = policy.Filter(fields, user).ToList();

            Assert.AreEqual(2, result.Count);
        }

        private class CustomPolicy : IAnalysisFieldPolicy
        {
            public IEnumerable<AnalysisFieldMeta> Filter(IEnumerable<AnalysisFieldMeta> fields, ClaimsPrincipal user)
            {
                return fields.Where(f => f.FieldName != "Secret");
            }
        }

        [TestMethod]
        public void CustomPolicy_filters_fields_correctly()
        {
            var policy = new CustomPolicy();
            var fields = new List<AnalysisFieldMeta>
            {
                new AnalysisFieldMeta { FieldName = "Public" },
                new AnalysisFieldMeta { FieldName = "Secret" }
            };
            var user = new ClaimsPrincipal();

            var result = policy.Filter(fields, user).ToList();

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("Public", result[0].FieldName);
        }
    }
}
