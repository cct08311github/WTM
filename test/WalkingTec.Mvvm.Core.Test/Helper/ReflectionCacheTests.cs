#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Localization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using WalkingTec.Mvvm.Core.Helper;

namespace WalkingTec.Mvvm.Core.Test.Helper
{
    [TestClass]
    public class ReflectionCacheTests
    {
        // --- Test model types ---

        private class SampleEntity
        {
            [Display(Name = "编号")]
            public int Id { get; set; }

            public string? Name { get; set; }

            [Required]
            public string RequiredName { get; set; } = string.Empty;

            public DateTime Created { get; set; }
        }

        private enum SampleStatus
        {
            [Display(Name = "待处理")]
            Pending,
            [Display(Name = "激活")]
            Active,
            Completed
        }

        private class EnumModel
        {
            [Dimension(DisplayName = "状态")]
            public SampleStatus Status { get; set; }

            [Measure(AllowedFuncs = AggregateFunc.Sum, DisplayName = "金额")]
            public decimal Amount { get; set; }
        }

        [TestInitialize]
        public void TestInit()
        {
            ReflectionCache.ClearAll();
            CoreProgram._localizer = null;
        }

        [TestCleanup]
        public void TestCleanup()
        {
            ReflectionCache.ClearAll();
            CoreProgram._localizer = null;
        }

        // --- GetPropertyExpression ---

        [TestMethod]
        public void GetPropertyExpression_returns_same_delegate_instance_on_second_call()
        {
            var del1 = PropertyHelper.GetPropertyExpression(typeof(SampleEntity), "Id");
            var del2 = PropertyHelper.GetPropertyExpression(typeof(SampleEntity), "Id");
            Assert.AreSame(del1, del2, "Second call should return the cached delegate instance");
        }

        [TestMethod]
        public void GetPropertyExpression_different_types_get_different_delegates()
        {
            // Two types both have a property called "Name"
            var del1 = PropertyHelper.GetPropertyExpression(typeof(SampleEntity), "Name");

            // Define a separate type with a Name property
            var del2 = PropertyHelper.GetPropertyExpression(typeof(SampleEntity), "Id");

            Assert.AreNotSame(del1, del2, "Different property paths should have different delegates");
        }

        [TestMethod]
        public void GetPropertyExpression_cached_delegate_produces_correct_value()
        {
            var entity = new SampleEntity { Id = 42 };
            var del = PropertyHelper.GetPropertyExpression(typeof(SampleEntity), "Id");
            // Call twice to ensure cached version still works
            var val1 = del(entity);
            var val2 = del(entity);
            Assert.AreEqual(42, val1);
            Assert.AreEqual(42, val2);
        }

        // --- IsPropertyRequired ---

        [TestMethod]
        public void IsPropertyRequired_cached_result_matches_uncached_for_int_primitive()
        {
            // int is a primitive → required
            var pi = typeof(SampleEntity).GetProperty("Id")!;
            ReflectionCache.ClearAll();
            var first = pi.IsPropertyRequired();
            Assert.IsTrue(first, "int property should be required");
            // Second call hits cache
            var second = pi.IsPropertyRequired();
            Assert.IsTrue(second, "Cached result must match first call");
        }

        [TestMethod]
        public void IsPropertyRequired_nullable_string_is_not_required()
        {
            var pi = typeof(SampleEntity).GetProperty("Name")!;
            Assert.IsFalse(pi.IsPropertyRequired(), "Nullable string should not be required");
        }

        [TestMethod]
        public void IsPropertyRequired_required_attribute_marks_required()
        {
            var pi = typeof(SampleEntity).GetProperty("RequiredName")!;
            Assert.IsTrue(pi.IsPropertyRequired(), "[Required] attribute should make field required");
        }

        [TestMethod]
        public void IsPropertyRequired_null_returns_false()
        {
            MemberInfo? pi = null;
            Assert.IsFalse(pi.IsPropertyRequired(), "null MemberInfo should return false");
        }

        [TestMethod]
        public void IsPropertyRequired_datetime_is_not_required()
        {
            // DateTime is a struct but not in the IsPrimitive / IsEnum / decimal / Guid list,
            // and has no [Required] or [Key] attribute — so the framework does not mark it required.
            var pi = typeof(SampleEntity).GetProperty("Created")!;
            Assert.IsFalse(pi.IsPropertyRequired(), "DateTime without attributes should not be required");
        }

        // --- GetPropertyDisplayName ---

        [TestMethod]
        public void GetPropertyDisplayName_null_returns_empty()
        {
            MemberInfo? pi = null;
            Assert.AreEqual("", pi.GetPropertyDisplayName());
        }

        [TestMethod]
        public void GetPropertyDisplayName_uses_display_attribute_name()
        {
            // Id has [Display(Name = "编号")]
            var pi = typeof(SampleEntity).GetProperty("Id")!;
            // No localizer — should return raw display name
            var result = pi.GetPropertyDisplayName();
            Assert.AreEqual("编号", result);
        }

        [TestMethod]
        public void GetPropertyDisplayName_falls_back_to_member_name()
        {
            var pi = typeof(SampleEntity).GetProperty("Name")!;
            var result = pi.GetPropertyDisplayName();
            Assert.AreEqual("Name", result);
        }

        [TestMethod]
        public void GetPropertyDisplayName_respects_changing_localizer()
        {
            var pi = typeof(SampleEntity).GetProperty("Id")!; // [Display(Name="编号")]

            var mock1 = new Mock<IStringLocalizer>();
            mock1.Setup(l => l[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k + "-v1"));
            CoreProgram._localizer = mock1.Object;

            var result1 = pi.GetPropertyDisplayName();
            Assert.AreEqual("编号-v1", result1, "First call should use mock1 localizer");

            var mock2 = new Mock<IStringLocalizer>();
            mock2.Setup(l => l[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k + "-v2"));
            CoreProgram._localizer = mock2.Object;

            var result2 = pi.GetPropertyDisplayName();
            Assert.AreEqual("编号-v2", result2, "Second call should reflect new localizer (only raw name is cached)");
        }

        [TestMethod]
        public void GetPropertyDisplayName_explicit_local_overrides_CoreProgram_localizer()
        {
            var pi = typeof(SampleEntity).GetProperty("Id")!;

            var globalMock = new Mock<IStringLocalizer>();
            globalMock.Setup(l => l[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, "global"));
            CoreProgram._localizer = globalMock.Object;

            var explicitMock = new Mock<IStringLocalizer>();
            explicitMock.Setup(l => l[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, "explicit"));

            var result = pi.GetPropertyDisplayName(explicitMock.Object);
            Assert.AreEqual("explicit", result, "Explicit localizer should take precedence");
        }

        // --- GetEnumDisplayName (string overload) ---

        [TestMethod]
        public void GetEnumDisplayName_string_overload_returns_display_attribute_name()
        {
            var result = PropertyHelper.GetEnumDisplayName(typeof(SampleStatus), "Pending");
            Assert.AreEqual("待处理", result);
        }

        [TestMethod]
        public void GetEnumDisplayName_string_overload_falls_back_to_value_name()
        {
            // Completed has no [Display]
            var result = PropertyHelper.GetEnumDisplayName(typeof(SampleStatus), "Completed");
            Assert.AreEqual("Completed", result);
        }

        [TestMethod]
        public void GetEnumDisplayName_string_overload_respects_changing_localizer()
        {
            var mock1 = new Mock<IStringLocalizer>();
            mock1.Setup(l => l[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k + "-loc1"));
            CoreProgram._localizer = mock1.Object;

            var result1 = PropertyHelper.GetEnumDisplayName(typeof(SampleStatus), "Pending");
            Assert.AreEqual("待处理-loc1", result1);

            var mock2 = new Mock<IStringLocalizer>();
            mock2.Setup(l => l[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k + "-loc2"));
            CoreProgram._localizer = mock2.Object;

            var result2 = PropertyHelper.GetEnumDisplayName(typeof(SampleStatus), "Pending");
            Assert.AreEqual("待处理-loc2", result2, "Localizer swap must be reflected on second call");
        }

        [TestMethod]
        public void GetEnumDisplayName_string_overload_null_type_returns_empty()
        {
            Assert.AreEqual("", PropertyHelper.GetEnumDisplayName(null, "Pending"));
        }

        [TestMethod]
        public void GetEnumDisplayName_string_overload_null_value_returns_empty()
        {
            Assert.AreEqual("", PropertyHelper.GetEnumDisplayName(typeof(SampleStatus), null));
        }

        // --- GetEnumDisplayName (int overload) ---

        [TestMethod]
        public void GetEnumDisplayName_int_overload_returns_same_result_as_uncached()
        {
            // First call — populates cache
            ReflectionCache.ClearAll();
            var first = PropertyHelper.GetEnumDisplayName(typeof(SampleStatus), 0);  // Pending
            // Second call — hits cache
            var second = PropertyHelper.GetEnumDisplayName(typeof(SampleStatus), 0);
            Assert.AreEqual(first, second);
            Assert.AreEqual("待处理", first);
        }

        [TestMethod]
        public void GetEnumDisplayName_int_overload_respects_changing_localizer()
        {
            var mock1 = new Mock<IStringLocalizer>();
            mock1.Setup(l => l[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k + "-intloc1"));
            CoreProgram._localizer = mock1.Object;

            var result1 = PropertyHelper.GetEnumDisplayName(typeof(SampleStatus), 0);
            Assert.AreEqual("待处理-intloc1", result1);

            var mock2 = new Mock<IStringLocalizer>();
            mock2.Setup(l => l[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k + "-intloc2"));
            CoreProgram._localizer = mock2.Object;

            var result2 = PropertyHelper.GetEnumDisplayName(typeof(SampleStatus), 0);
            Assert.AreEqual("待处理-intloc2", result2, "Localizer swap must be reflected for int overload");
        }

        [TestMethod]
        public void GetEnumDisplayName_int_overload_null_type_returns_empty()
        {
            Assert.AreEqual("", PropertyHelper.GetEnumDisplayName(null, 0));
        }

        // --- ScanModel caching ---

        [TestMethod]
        public void ScanModel_returns_cached_template_but_live_AllowedValues()
        {
            // First scan: no localizer — AllowedValues should use raw names
            var fields1 = AnalysisFieldScanner.ScanModel(typeof(EnumModel)).ToList();
            var statusField1 = fields1.Single(f => f.FieldName == "Status");
            Assert.IsNotNull(statusField1.AllowedValues);
            // Raw names (no localizer)
            Assert.IsTrue(statusField1.AllowedValues!.Contains("待处理"), "Pending should have Display name");

            // Now wire a localizer that appends suffix
            var mock = new Mock<IStringLocalizer>();
            mock.Setup(l => l[It.IsAny<string>()]).Returns<string>(k => new LocalizedString(k, k + "-localized"));
            CoreProgram._localizer = mock.Object;

            // Second scan: template is cached but AllowedValues must be regenerated via localizer
            var fields2 = AnalysisFieldScanner.ScanModel(typeof(EnumModel)).ToList();
            var statusField2 = fields2.Single(f => f.FieldName == "Status");
            Assert.IsNotNull(statusField2.AllowedValues);
            Assert.IsTrue(
                statusField2.AllowedValues!.Contains("待处理-localized"),
                "AllowedValues should reflect current localizer, not cached localizer output");

            // The non-localized fields (structural) must be stable
            Assert.AreEqual("Status", statusField1.FieldName);
            Assert.AreEqual("Status", statusField2.FieldName);
        }

        [TestMethod]
        public void ScanModel_template_is_cached_across_calls()
        {
            // First call populates cache
            AnalysisFieldScanner.ScanModel(typeof(EnumModel)).ToList();
            Assert.IsTrue(
                ReflectionCache.AnalysisFieldTemplates.ContainsKey(typeof(EnumModel)),
                "Template cache should be populated after first ScanModel call");
        }

        // --- Concurrent access ---

        [TestMethod]
        public void Concurrent_access_smoke_test()
        {
            // 16 threads, each calling each helper 100 times with mixed inputs.
            const int threadCount = 16;
            const int iterationsPerThread = 100;

            var pi_id = typeof(SampleEntity).GetProperty("Id")!;
            var pi_name = typeof(SampleEntity).GetProperty("Name")!;
            var pi_required = typeof(SampleEntity).GetProperty("RequiredName")!;

            var exceptions = new List<Exception>();
            var barrier = new Barrier(threadCount);
            var tasks = new Task[threadCount];

            for (int t = 0; t < threadCount; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        for (int i = 0; i < iterationsPerThread; i++)
                        {
                            PropertyHelper.GetPropertyExpression(typeof(SampleEntity), "Id");
                            PropertyHelper.GetPropertyExpression(typeof(SampleEntity), "Name");
                            pi_id.IsPropertyRequired();
                            pi_name.IsPropertyRequired();
                            pi_required.IsPropertyRequired();
                            pi_id.GetPropertyDisplayName();
                            pi_name.GetPropertyDisplayName();
                            PropertyHelper.GetEnumDisplayName(typeof(SampleStatus), "Pending");
                            PropertyHelper.GetEnumDisplayName(typeof(SampleStatus), "Completed");
                            PropertyHelper.GetEnumDisplayName(typeof(SampleStatus), 0);
                            PropertyHelper.GetEnumDisplayName(typeof(SampleStatus), 2);
                            AnalysisFieldScanner.ScanModel(typeof(EnumModel)).ToList();
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions)
                        {
                            exceptions.Add(ex);
                        }
                    }
                });
            }

            Task.WaitAll(tasks);

            Assert.AreEqual(0, exceptions.Count,
                $"Expected no exceptions but got {exceptions.Count}: " +
                string.Join("; ", exceptions.Select(e => e.Message)));
        }
    }
}
