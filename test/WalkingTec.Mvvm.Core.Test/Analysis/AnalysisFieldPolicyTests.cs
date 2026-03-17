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
        // ─── Helpers ──────────────────────────────────────────────────────────

        private static ClaimsPrincipal UserWithRoles(params string[] roles)
        {
            var claims = roles.Select(r => new Claim(ClaimTypes.Role, r));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

        private static ClaimsPrincipal AnonymousUser() => new ClaimsPrincipal();

        private static readonly DefaultAnalysisFieldPolicy _policy = new DefaultAnalysisFieldPolicy();

        // ─── AllowedRoles = null/empty → always visible ────────────────────────

        [TestMethod]
        public void DefaultPolicy_returns_all_fields_when_no_AllowedRoles()
        {
            var fields = new List<AnalysisFieldMeta>
            {
                new AnalysisFieldMeta { FieldName = "F1" },
                new AnalysisFieldMeta { FieldName = "F2" }
            };

            var result = _policy.Filter(fields, AnonymousUser()).ToList();

            Assert.AreEqual(2, result.Count);
        }

        [TestMethod]
        public void DefaultPolicy_empty_AllowedRoles_string_is_visible_to_all()
        {
            // AllowedRoles = "" → string.IsNullOrEmpty → true → always visible
            var fields = new List<AnalysisFieldMeta>
            {
                new AnalysisFieldMeta { FieldName = "PublicField", AllowedRoles = "" }
            };

            var result = _policy.Filter(fields, AnonymousUser()).ToList();

            Assert.AreEqual(1, result.Count, "空字串 AllowedRoles 應視為「無限制」");
        }

        // ─── 場景 A：非授權角色應被過濾 ────────────────────────────────────────

        [TestMethod]
        public void DefaultPolicy_filters_field_when_user_lacks_required_role()
        {
            // 財務敏感欄位「淨利潤」只允許 CFO/Finance 角色
            var fields = new List<AnalysisFieldMeta>
            {
                new AnalysisFieldMeta { FieldName = "Revenue", AllowedRoles = null },
                new AnalysisFieldMeta { FieldName = "NetProfit", AllowedRoles = "CFO,Finance" }
            };
            var itUser = UserWithRoles("IT");  // 不在 CFO,Finance 中

            var result = _policy.Filter(fields, itUser).ToList();

            Assert.AreEqual(1, result.Count, "IT 角色用戶應只看到無限制欄位");
            Assert.AreEqual("Revenue", result[0].FieldName);
        }

        [TestMethod]
        public void DefaultPolicy_anonymous_user_cannot_see_role_restricted_field()
        {
            var fields = new List<AnalysisFieldMeta>
            {
                new AnalysisFieldMeta { FieldName = "NetProfit", AllowedRoles = "CFO" }
            };

            var result = _policy.Filter(fields, AnonymousUser()).ToList();

            Assert.AreEqual(0, result.Count, "匿名用戶不應看到有角色限制的欄位");
        }

        // ─── 場景 B：Admin bypass ──────────────────────────────────────────────

        [TestMethod]
        public void DefaultPolicy_admin_sees_all_fields_regardless_of_AllowedRoles()
        {
            var fields = new List<AnalysisFieldMeta>
            {
                new AnalysisFieldMeta { FieldName = "Revenue" },
                new AnalysisFieldMeta { FieldName = "NetProfit", AllowedRoles = "CFO" },
                new AnalysisFieldMeta { FieldName = "BonusPool", AllowedRoles = "HR,Finance" }
            };
            var adminUser = UserWithRoles("Admin");

            var result = _policy.Filter(fields, adminUser).ToList();

            Assert.AreEqual(3, result.Count, "Admin 角色應繞過所有 AllowedRoles 限制");
        }

        [TestMethod]
        public void DefaultPolicy_admin_plus_other_roles_still_bypasses_restriction()
        {
            var fields = new List<AnalysisFieldMeta>
            {
                new AnalysisFieldMeta { FieldName = "SalaryBand", AllowedRoles = "HR" }
            };
            var adminAndCfo = UserWithRoles("Admin", "CFO");

            var result = _policy.Filter(fields, adminAndCfo).ToList();

            Assert.AreEqual(1, result.Count);
        }

        // ─── 場景 C：角色匹配可見 ──────────────────────────────────────────────

        [TestMethod]
        public void DefaultPolicy_user_with_matching_role_can_see_restricted_field()
        {
            var fields = new List<AnalysisFieldMeta>
            {
                new AnalysisFieldMeta { FieldName = "NetProfit", AllowedRoles = "CFO,Finance" }
            };
            var cfoUser = UserWithRoles("CFO");

            var result = _policy.Filter(fields, cfoUser).ToList();

            Assert.AreEqual(1, result.Count, "擁有 CFO 角色的用戶應能看到 CFO 限制欄位");
        }

        [TestMethod]
        public void DefaultPolicy_user_with_second_allowed_role_can_see_field()
        {
            var fields = new List<AnalysisFieldMeta>
            {
                new AnalysisFieldMeta { FieldName = "BudgetVariance", AllowedRoles = "CFO,Finance" }
            };
            var financeUser = UserWithRoles("Finance");  // 第二個允許角色

            var result = _policy.Filter(fields, financeUser).ToList();

            Assert.AreEqual(1, result.Count, "擁有 Finance 角色應能看到 CFO,Finance 限制的欄位");
        }

        // ─── 場景 D：逗號分隔格式解析 ─────────────────────────────────────────

        [TestMethod]
        public void DefaultPolicy_handles_whitespace_around_role_names_in_csv()
        {
            // "CFO, Finance" — 逗號後有空格，必須 Trim() 後比對
            var fields = new List<AnalysisFieldMeta>
            {
                new AnalysisFieldMeta { FieldName = "GrossMargin", AllowedRoles = "CFO, Finance" }
            };
            var financeUser = UserWithRoles("Finance");

            var result = _policy.Filter(fields, financeUser).ToList();

            Assert.AreEqual(1, result.Count, "AllowedRoles 中角色名稱周圍的空白應被修剪");
        }

        [TestMethod]
        public void DefaultPolicy_multiple_roles_none_matching_returns_empty()
        {
            var fields = new List<AnalysisFieldMeta>
            {
                new AnalysisFieldMeta { FieldName = "Headcount", AllowedRoles = "HR,Legal" }
            };
            var itUser = UserWithRoles("IT");

            var result = _policy.Filter(fields, itUser).ToList();

            Assert.AreEqual(0, result.Count, "IT 角色不應匹配 HR,Legal 限制的欄位");
        }

        // ─── 混合場景：部分欄位有限制 ──────────────────────────────────────────

        [TestMethod]
        public void DefaultPolicy_mixed_fields_returns_only_visible_ones()
        {
            // 典型財務 dashboard：公開指標 + 敏感指標
            var fields = new List<AnalysisFieldMeta>
            {
                new AnalysisFieldMeta { FieldName = "TotalRevenue" },             // 無限制
                new AnalysisFieldMeta { FieldName = "Region" },                   // 無限制
                new AnalysisFieldMeta { FieldName = "NetProfit", AllowedRoles = "CFO,Finance" },    // 受限
                new AnalysisFieldMeta { FieldName = "ExecutiveBonus", AllowedRoles = "CEO,CFO" }    // 受限
            };
            var analystUser = UserWithRoles("Analyst");  // 只能看公開欄位

            var result = _policy.Filter(fields, analystUser).ToList();

            Assert.AreEqual(2, result.Count, "Analyst 角色只應看到 2 個無限制欄位");
            CollectionAssert.Contains(result.Select(f => f.FieldName).ToList(), "TotalRevenue");
            CollectionAssert.Contains(result.Select(f => f.FieldName).ToList(), "Region");
        }

        [TestMethod]
        public void DefaultPolicy_cfo_sees_own_restricted_fields_but_not_ceo_only()
        {
            var fields = new List<AnalysisFieldMeta>
            {
                new AnalysisFieldMeta { FieldName = "TotalRevenue" },
                new AnalysisFieldMeta { FieldName = "NetProfit", AllowedRoles = "CFO,Finance" },
                new AnalysisFieldMeta { FieldName = "BoardPack", AllowedRoles = "CEO" }
            };
            var cfoUser = UserWithRoles("CFO");

            var result = _policy.Filter(fields, cfoUser).ToList();

            Assert.AreEqual(2, result.Count, "CFO 應看到 TotalRevenue 和 NetProfit，但看不到 CEO 專屬的 BoardPack");
            CollectionAssert.DoesNotContain(result.Select(f => f.FieldName).ToList(), "BoardPack");
        }

        // ─── CustomPolicy 測試 ────────────────────────────────────────────────

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
