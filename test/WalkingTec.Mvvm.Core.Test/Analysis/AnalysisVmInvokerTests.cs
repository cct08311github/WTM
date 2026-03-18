#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Core.Test.Analysis
{
    /// <summary>
    /// AnalysisVmInvoker 完整覆蓋測試。
    /// 涵蓋：方法不存在 / 回傳型別錯誤 / TargetInvocationException 展開 / 正常路徑。
    /// </summary>
    [TestClass]
    public class AnalysisVmInvokerTests
    {
        // ─── GetSearchQuery ────────────────────────────────────────────────

        private class NoMethodVm : BaseVM
        {
            // 沒有 GetSearchQuery() 方法
        }

        /// <summary>VM 沒有 GetSearchQuery() → 拋 InvalidOperationException（含型別名稱）</summary>
        [TestMethod]
        public void GetSearchQuery_throws_when_method_not_found()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() =>
                AnalysisVmInvoker.GetSearchQuery(new NoMethodVm(), typeof(NoMethodVm)));
            StringAssert.Contains(ex.Message, "GetSearchQuery");
        }

        private class NonQueryableVm : BaseVM
        {
            // GetSearchQuery() 存在但回傳非 IQueryable
            public string GetSearchQuery() => "not a queryable";
        }

        /// <summary>GetSearchQuery() 回傳非 IQueryable → 拋 InvalidOperationException</summary>
        [TestMethod]
        public void GetSearchQuery_throws_when_method_returns_non_IQueryable()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() =>
                AnalysisVmInvoker.GetSearchQuery(new NonQueryableVm(), typeof(NonQueryableVm)));
            StringAssert.Contains(ex.Message, "GetSearchQuery");
            StringAssert.Contains(ex.Message, "IQueryable");
        }

        private class ThrowingSearchQueryVm : BaseVM
        {
            public IQueryable GetSearchQuery()
                => throw new ArgumentException("inner error from GetSearchQuery");
        }

        /// <summary>GetSearchQuery() 拋例外 → TargetInvocationException 被展開，回傳 inner exception</summary>
        [TestMethod]
        public void GetSearchQuery_unwraps_TargetInvocationException()
        {
            var ex = Assert.ThrowsException<ArgumentException>(() =>
                AnalysisVmInvoker.GetSearchQuery(new ThrowingSearchQueryVm(), typeof(ThrowingSearchQueryVm)));
            StringAssert.Contains(ex.Message, "inner error from GetSearchQuery");
        }

        private class ValidSearchQueryVm : BaseVM
        {
            private readonly IQueryable _q;
            public ValidSearchQueryVm(IQueryable q) { _q = q; }
            public IQueryable GetSearchQuery() => _q;
        }

        /// <summary>GetSearchQuery() 正常路徑 → 回傳 IQueryable</summary>
        [TestMethod]
        public void GetSearchQuery_happy_path_returns_IQueryable()
        {
            var data = new[] { "a", "b" }.AsQueryable();
            var vm = new ValidSearchQueryVm(data);
            var result = AnalysisVmInvoker.GetSearchQuery(vm, typeof(ValidSearchQueryVm));
            Assert.IsNotNull(result);
        }

        // ─── GetAnalysisFields ─────────────────────────────────────────────

        private class NoAnalysisFieldsVm : BaseVM
        {
            // 沒有 GetAnalysisFields() 方法
        }

        /// <summary>VM 沒有 GetAnalysisFields() → 拋 InvalidOperationException（含型別名稱）</summary>
        [TestMethod]
        public void GetAnalysisFields_throws_when_method_not_found()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() =>
                AnalysisVmInvoker.GetAnalysisFields(new NoAnalysisFieldsVm(), typeof(NoAnalysisFieldsVm)));
            StringAssert.Contains(ex.Message, "GetAnalysisFields");
        }

        private class NullReturningAnalysisFieldsVm : BaseVM
        {
            // 回傳 null — 代表有 method 但回傳 null
#pragma warning disable CS8603
            public IEnumerable<AnalysisFieldMeta> GetAnalysisFields() => null;
#pragma warning restore CS8603
        }

        /// <summary>GetAnalysisFields() 回傳 null → 拋 InvalidOperationException（含型別名稱）</summary>
        [TestMethod]
        public void GetAnalysisFields_throws_when_method_returns_null()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() =>
                AnalysisVmInvoker.GetAnalysisFields(new NullReturningAnalysisFieldsVm(), typeof(NullReturningAnalysisFieldsVm)));
            StringAssert.Contains(ex.Message, "GetAnalysisFields");
            StringAssert.Contains(ex.Message, "null");
        }

        private class ThrowingAnalysisFieldsVm : BaseVM
        {
            public IEnumerable<AnalysisFieldMeta> GetAnalysisFields()
                => throw new InvalidOperationException("inner error from GetAnalysisFields");
        }

        /// <summary>GetAnalysisFields() 拋例外 → TargetInvocationException 被展開</summary>
        [TestMethod]
        public void GetAnalysisFields_unwraps_TargetInvocationException()
        {
            var ex = Assert.ThrowsException<InvalidOperationException>(() =>
                AnalysisVmInvoker.GetAnalysisFields(new ThrowingAnalysisFieldsVm(), typeof(ThrowingAnalysisFieldsVm)));
            StringAssert.Contains(ex.Message, "inner error from GetAnalysisFields");
        }

        private class ValidAnalysisFieldsVm : BaseVM
        {
            private readonly IEnumerable<AnalysisFieldMeta> _fields;
            public ValidAnalysisFieldsVm(IEnumerable<AnalysisFieldMeta> fields) { _fields = fields; }
            public IEnumerable<AnalysisFieldMeta> GetAnalysisFields() => _fields;
        }

        /// <summary>GetAnalysisFields() 回傳 IEnumerable (非 IList) → .ToList() 路徑</summary>
        [TestMethod]
        public void GetAnalysisFields_converts_IEnumerable_to_IList()
        {
            // 提供一個純 IEnumerable，不是 IList，確認走 .ToList() 路徑
            IEnumerable<AnalysisFieldMeta> enumerable = new AnalysisFieldMeta[]
            {
                new AnalysisFieldMeta
                {
                    FieldName   = "Amount",
                    DisplayName = "金額",
                    Kind        = AnalysisFieldKind.Measure,
                    ClrType     = typeof(decimal),
                    AllowedFuncs = AggregateFunc.Sum
                }
            }.Where(_ => true); // Where wraps in WhereEnumerable, not IList

            var vm = new ValidAnalysisFieldsVm(enumerable);
            var result = AnalysisVmInvoker.GetAnalysisFields(vm, typeof(ValidAnalysisFieldsVm));

            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.Count);
        }

        private class ListAnalysisFieldsVm : BaseVM
        {
            public IList<AnalysisFieldMeta> GetAnalysisFields() => new List<AnalysisFieldMeta>
            {
                new AnalysisFieldMeta
                {
                    FieldName   = "Amount",
                    DisplayName = "金額",
                    Kind        = AnalysisFieldKind.Measure,
                    ClrType     = typeof(decimal),
                    AllowedFuncs = AggregateFunc.Sum
                }
            };
        }

        /// <summary>GetAnalysisFields() 直接回傳 IList → 無需 .ToList() 轉換</summary>
        [TestMethod]
        public void GetAnalysisFields_uses_IList_directly_without_ToList()
        {
            var vm = new ListAnalysisFieldsVm();
            var result = AnalysisVmInvoker.GetAnalysisFields(vm, typeof(ListAnalysisFieldsVm));

            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("Amount", result[0].FieldName);
        }

        /// <summary>相同 vmType 第二次呼叫使用快取的 MethodInfo（邏輯等效，驗證不拋）</summary>
        [TestMethod]
        public void GetSearchQuery_second_call_uses_cached_method()
        {
            var data = new[] { "x" }.AsQueryable();
            var vm1 = new ValidSearchQueryVm(data);
            var vm2 = new ValidSearchQueryVm(data);

            // First call populates the cache
            var r1 = AnalysisVmInvoker.GetSearchQuery(vm1, typeof(ValidSearchQueryVm));
            // Second call should use the cache
            var r2 = AnalysisVmInvoker.GetSearchQuery(vm2, typeof(ValidSearchQueryVm));

            Assert.IsNotNull(r1);
            Assert.IsNotNull(r2);
        }
    }
}
