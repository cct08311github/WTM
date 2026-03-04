# Analysis Mode Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 讓 WTM 的 DataTable 列表頁可切換「分析模式」，提供動態維度/度量選擇、GroupBy 聚合表格、ECharts 圖表及 Excel 匯出。

**Architecture:** 後端以 [Dimension]/[Measure] attribute 定義白名單欄位；AnalysisQueryEngine 用 EF Core Expression Tree 動態建 GroupBy query；_AnalysisController 提供 /meta、/query、/export 三個 API；前端 framework_analysis.js 提供維度/度量選擇器、聚合表格、ECharts 圖表；DataTableTagHelper 加 enable-analysis 屬性觸發整合。

**Tech Stack:** ASP.NET Core 8、EF Core 8、xUnit、SQLite in-memory（測試）、layui、ECharts、NPOI（匯出）

**設計文件：** docs/plans/2026-03-04-analysis-mode-design.md

---

## Task 1: Attribute 定義（AggregateFunc / DimensionAttribute / MeasureAttribute）

**Files:**
- Create: src/WalkingTec.Mvvm.Core/Analysis/AggregateFunc.cs
- Create: src/WalkingTec.Mvvm.Core/Analysis/DimensionAttribute.cs
- Create: src/WalkingTec.Mvvm.Core/Analysis/MeasureAttribute.cs
- Test:   test/WalkingTec.Mvvm.Core.Test/Analysis/AttributeTests.cs

**Step 1: 建立測試**

```csharp
// test/WalkingTec.Mvvm.Core.Test/Analysis/AttributeTests.cs
using WalkingTec.Mvvm.Core.Analysis;
using Xunit;

namespace WalkingTec.Mvvm.Core.Test.Analysis;

public class AttributeTests
{
    private class SampleModel
    {
        [Dimension(DisplayName = "地區")]
        public string Region { get; set; }

        [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count, DisplayName = "金額")]
        public decimal Amount { get; set; }

        public string NotAnnotated { get; set; }
    }

    [Fact]
    public void Dimension_attribute_applied_correctly()
    {
        var prop = typeof(SampleModel).GetProperty(nameof(SampleModel.Region));
        var attr = prop.GetCustomAttributes(typeof(DimensionAttribute), false)
                       .Cast<DimensionAttribute>().Single();
        Assert.Equal("地區", attr.DisplayName);
    }

    [Fact]
    public void Measure_allows_flag_combination()
    {
        var prop = typeof(SampleModel).GetProperty(nameof(SampleModel.Amount));
        var attr = prop.GetCustomAttributes(typeof(MeasureAttribute), false)
                       .Cast<MeasureAttribute>().Single();
        Assert.True(attr.AllowedFuncs.HasFlag(AggregateFunc.Sum));
        Assert.True(attr.AllowedFuncs.HasFlag(AggregateFunc.Count));
        Assert.False(attr.AllowedFuncs.HasFlag(AggregateFunc.Avg));
    }

    [Fact]
    public void Non_annotated_property_has_no_analysis_attributes()
    {
        var prop = typeof(SampleModel).GetProperty(nameof(SampleModel.NotAnnotated));
        Assert.Empty(prop.GetCustomAttributes(typeof(DimensionAttribute), false));
        Assert.Empty(prop.GetCustomAttributes(typeof(MeasureAttribute), false));
    }
}
```

**Step 2: Run test → expect FAIL**

```
dotnet test test/WalkingTec.Mvvm.Core.Test --filter "FullyQualifiedName~AttributeTests" -v minimal
```

**Step 3: 建立實作**

```csharp
// AggregateFunc.cs
namespace WalkingTec.Mvvm.Core.Analysis;

[Flags]
public enum AggregateFunc
{
    Count = 1, Sum = 2, Avg = 4, Max = 8, Min = 16
}
```

```csharp
// DimensionAttribute.cs
namespace WalkingTec.Mvvm.Core.Analysis;

[AttributeUsage(AttributeTargets.Property)]
public class DimensionAttribute : Attribute
{
    public string DisplayName { get; set; }
}
```

```csharp
// MeasureAttribute.cs
namespace WalkingTec.Mvvm.Core.Analysis;

[AttributeUsage(AttributeTargets.Property)]
public class MeasureAttribute : Attribute
{
    public AggregateFunc AllowedFuncs { get; set; }
    public string DisplayName { get; set; }
}
```

**Step 4: Run test → expect PASS (3 tests)**

**Step 5: Commit**
```
git add src/WalkingTec.Mvvm.Core/Analysis/ test/WalkingTec.Mvvm.Core.Test/Analysis/AttributeTests.cs
git commit -m "feat(analysis): add [Dimension] and [Measure] attributes with AggregateFunc flags"
```

---

## Task 2: AnalysisFieldMeta + AnalysisFieldScanner

**Files:**
- Create: src/WalkingTec.Mvvm.Core/Analysis/AnalysisFieldMeta.cs
- Create: src/WalkingTec.Mvvm.Core/Analysis/AnalysisFieldScanner.cs
- Test:   test/WalkingTec.Mvvm.Core.Test/Analysis/AnalysisFieldScannerTests.cs

**Step 1: 建立測試**

```csharp
// test/WalkingTec.Mvvm.Core.Test/Analysis/AnalysisFieldScannerTests.cs
using WalkingTec.Mvvm.Core.Analysis;
using Xunit;

namespace WalkingTec.Mvvm.Core.Test.Analysis;

public class AnalysisFieldScannerTests
{
    private class OrderModel
    {
        [Dimension(DisplayName = "地區")] public string Region { get; set; }
        [Dimension(DisplayName = "業務員")] public string SalesRep { get; set; }
        [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count, DisplayName = "金額")]
        public decimal Amount { get; set; }
        public string Ignored { get; set; }
    }

    [Fact]
    public void Scan_returns_all_annotated_fields()
    {
        var fields = AnalysisFieldScanner.ScanModel(typeof(OrderModel)).ToList();
        Assert.Equal(3, fields.Count);
    }

    [Fact]
    public void Dimension_fields_have_correct_kind()
    {
        var dims = AnalysisFieldScanner.ScanModel(typeof(OrderModel))
                       .Where(f => f.Kind == AnalysisFieldKind.Dimension).ToList();
        Assert.Equal(2, dims.Count);
        Assert.Contains(dims, f => f.FieldName == "Region" && f.DisplayName == "地區");
    }

    [Fact]
    public void Measure_fields_have_allowed_funcs()
    {
        var m = AnalysisFieldScanner.ScanModel(typeof(OrderModel))
                    .Single(f => f.Kind == AnalysisFieldKind.Measure);
        Assert.Equal("Amount", m.FieldName);
        Assert.True(m.AllowedFuncs.HasFlag(AggregateFunc.Sum));
        Assert.False(m.AllowedFuncs.HasFlag(AggregateFunc.Avg));
    }

    [Fact]
    public void Non_annotated_properties_excluded()
    {
        var fields = AnalysisFieldScanner.ScanModel(typeof(OrderModel)).ToList();
        Assert.DoesNotContain(fields, f => f.FieldName == "Ignored");
    }
}
```

**Step 2: Run test → expect FAIL**

**Step 3: 建立實作**

```csharp
// AnalysisFieldMeta.cs
namespace WalkingTec.Mvvm.Core.Analysis;

public enum AnalysisFieldKind { Dimension, Measure }

public class AnalysisFieldMeta
{
    public string FieldName { get; set; }
    public string DisplayName { get; set; }
    public AnalysisFieldKind Kind { get; set; }
    public AggregateFunc AllowedFuncs { get; set; }
    public Type ClrType { get; set; }
}
```

```csharp
// AnalysisFieldScanner.cs
using System.Reflection;

namespace WalkingTec.Mvvm.Core.Analysis;

public static class AnalysisFieldScanner
{
    public static IEnumerable<AnalysisFieldMeta> ScanModel(Type modelType)
    {
        foreach (var prop in modelType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var dim = prop.GetCustomAttribute<DimensionAttribute>();
            if (dim != null)
            {
                yield return new AnalysisFieldMeta
                {
                    FieldName = prop.Name,
                    DisplayName = dim.DisplayName ?? prop.Name,
                    Kind = AnalysisFieldKind.Dimension,
                    ClrType = prop.PropertyType
                };
                continue;
            }

            var msr = prop.GetCustomAttribute<MeasureAttribute>();
            if (msr != null)
            {
                yield return new AnalysisFieldMeta
                {
                    FieldName = prop.Name,
                    DisplayName = msr.DisplayName ?? prop.Name,
                    Kind = AnalysisFieldKind.Measure,
                    AllowedFuncs = msr.AllowedFuncs,
                    ClrType = prop.PropertyType
                };
            }
        }
    }
}
```

**Step 4: Run test → expect PASS (4 tests)**

**Step 5: Commit**
```
git add src/WalkingTec.Mvvm.Core/Analysis/AnalysisFieldMeta.cs \
        src/WalkingTec.Mvvm.Core/Analysis/AnalysisFieldScanner.cs \
        test/WalkingTec.Mvvm.Core.Test/Analysis/AnalysisFieldScannerTests.cs
git commit -m "feat(analysis): add AnalysisFieldScanner to reflect [Dimension]/[Measure] attributes"
```

---

## Task 3: EnableAnalysisAttribute + AnalysisVmRegistry（VM 白名單）

**Files:**
- Create: src/WalkingTec.Mvvm.Core/Analysis/EnableAnalysisAttribute.cs
- Create: src/WalkingTec.Mvvm.Core/Analysis/AnalysisVmRegistry.cs
- Test:   test/WalkingTec.Mvvm.Core.Test/Analysis/AnalysisVmRegistryTests.cs

**Step 1: 建立測試**

```csharp
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using Xunit;

namespace WalkingTec.Mvvm.Core.Test.Analysis;

public class AnalysisVmRegistryTests
{
    private class TestSearcher : BaseSearcher { }
    private class TestModel : TopBasePoco
    {
        [Dimension] public string Name { get; set; }
    }

    [EnableAnalysis]
    private class AnnotatedListVM : BasePagedListVM<TestModel, TestSearcher>
    {
        public override IOrderedQueryable<TestModel> GetSearchQuery() => throw new NotImplementedException();
        public override void InitGridHeader() { }
    }

    private class NotAnnotatedListVM : BasePagedListVM<TestModel, TestSearcher>
    {
        public override IOrderedQueryable<TestModel> GetSearchQuery() => throw new NotImplementedException();
        public override void InitGridHeader() { }
    }

    [Fact]
    public void Resolves_annotated_vm()
    {
        var registry = new AnalysisVmRegistry();
        registry.Build(new[] { typeof(AnnotatedListVM).Assembly });
        var type = registry.Resolve(typeof(AnnotatedListVM).FullName!);
        Assert.Equal(typeof(AnnotatedListVM), type);
    }

    [Fact]
    public void Throws_for_unannotated_vm()
    {
        var registry = new AnalysisVmRegistry();
        registry.Build(new[] { typeof(NotAnnotatedListVM).Assembly });
        Assert.Throws<InvalidOperationException>(
            () => registry.Resolve(typeof(NotAnnotatedListVM).FullName!));
    }

    [Fact]
    public void Throws_for_unknown_type()
    {
        var registry = new AnalysisVmRegistry();
        registry.Build(new[] { typeof(AnnotatedListVM).Assembly });
        Assert.Throws<InvalidOperationException>(() => registry.Resolve("Some.Unknown.Type"));
    }
}
```

**Step 2: Run test → expect FAIL**

**Step 3: 建立實作**

```csharp
// EnableAnalysisAttribute.cs
namespace WalkingTec.Mvvm.Core.Analysis;

[AttributeUsage(AttributeTargets.Class)]
public class EnableAnalysisAttribute : Attribute { }
```

```csharp
// AnalysisVmRegistry.cs
using System.Reflection;

namespace WalkingTec.Mvvm.Core.Analysis;

public class AnalysisVmRegistry
{
    private readonly Dictionary<string, Type> _whitelist = new();

    public void Build(IEnumerable<Assembly> assemblies)
    {
        _whitelist.Clear();
        foreach (var asm in assemblies)
        foreach (var type in asm.GetTypes())
        {
            if (!type.IsAbstract
                && IsBasePagedListVm(type)
                && type.GetCustomAttribute<EnableAnalysisAttribute>() != null)
            {
                _whitelist[type.FullName!] = type;
            }
        }
    }

    public Type Resolve(string fullName)
        => _whitelist.TryGetValue(fullName, out var t) ? t
           : throw new InvalidOperationException($"VM type not registered for analysis: {fullName}");

    private static bool IsBasePagedListVm(Type t)
    {
        var bt = t.BaseType;
        while (bt != null)
        {
            if (bt.IsGenericType && bt.GetGenericTypeDefinition() == typeof(BasePagedListVM<,>))
                return true;
            bt = bt.BaseType;
        }
        return false;
    }
}
```

**Step 4: Run test → expect PASS (3 tests)**

**Step 5: Commit**
```
git add src/WalkingTec.Mvvm.Core/Analysis/EnableAnalysisAttribute.cs \
        src/WalkingTec.Mvvm.Core/Analysis/AnalysisVmRegistry.cs \
        test/WalkingTec.Mvvm.Core.Test/Analysis/AnalysisVmRegistryTests.cs
git commit -m "feat(analysis): add AnalysisVmRegistry with [EnableAnalysis] whitelist"
```

---

## Task 4: AnalysisQueryRequest / Response DTOs

**Files:**
- Create: src/WalkingTec.Mvvm.Core/Analysis/AnalysisQueryRequest.cs
- Create: src/WalkingTec.Mvvm.Core/Analysis/AnalysisQueryResponse.cs

（純 DTO，無邏輯，直接實作）

```csharp
// AnalysisQueryRequest.cs
namespace WalkingTec.Mvvm.Core.Analysis;

public class AnalysisQueryRequest
{
    public string ListVmType { get; set; }
    public string SearcherFormData { get; set; }
    public List<string> Dimensions { get; set; } = new();
    public List<MeasureRequest> Measures { get; set; } = new();
    public List<FilterCondition> Filters { get; set; } = new();
}

public class MeasureRequest
{
    public string Field { get; set; }
    public AggregateFunc Func { get; set; }
}

public class FilterCondition
{
    public string Field { get; set; }
    public FilterOperator Operator { get; set; }
    public string Value { get; set; }
}

public enum FilterOperator { Eq, Gt, Gte, Lt, Lte, Contains, In }
```

```csharp
// AnalysisQueryResponse.cs
namespace WalkingTec.Mvvm.Core.Analysis;

public class AnalysisQueryResponse
{
    public List<string> Columns { get; set; } = new();
    public List<Dictionary<string, object>> Rows { get; set; } = new();
    public int TotalCount { get; set; }
    public bool Truncated { get; set; }
    public string QueryHash { get; set; }  // Phase 2 快取預留
}
```

**Commit**
```
git add src/WalkingTec.Mvvm.Core/Analysis/AnalysisQueryRequest.cs \
        src/WalkingTec.Mvvm.Core/Analysis/AnalysisQueryResponse.cs
git commit -m "feat(analysis): add AnalysisQueryRequest/Response DTOs with FilterCondition"
```

---

## Task 5: AnalysisQueryEngine（白名單驗證 + Filter + GroupBy）

**Files:**
- Create: src/WalkingTec.Mvvm.Core/Analysis/AnalysisQueryEngine.cs
- Test:   test/WalkingTec.Mvvm.Core.Test/Analysis/AnalysisQueryEngineTests.cs

**Step 1: 建立測試**

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;
using Xunit;

namespace WalkingTec.Mvvm.Core.Test.Analysis;

public class SaleRecord : TopBasePoco
{
    [Dimension(DisplayName = "地區")] public string Region { get; set; }
    [Dimension(DisplayName = "類別")] public string Category { get; set; }
    [Measure(AllowedFuncs = AggregateFunc.Sum | AggregateFunc.Count | AggregateFunc.Avg,
             DisplayName = "金額")]
    public decimal Amount { get; set; }
}

public class AnalysisTestContext : DbContext
{
    public AnalysisTestContext(DbContextOptions opts) : base(opts) { }
    public DbSet<SaleRecord> SaleRecords { get; set; }
}

public class AnalysisQueryEngineTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly AnalysisTestContext _ctx;
    private readonly IEnumerable<AnalysisFieldMeta> _whitelist;

    public AnalysisQueryEngineTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var opts = new DbContextOptionsBuilder<AnalysisTestContext>()
            .UseSqlite(_conn).Options;
        _ctx = new AnalysisTestContext(opts);
        _ctx.Database.EnsureCreated();
        _ctx.SaleRecords.AddRange(
            new SaleRecord { Region = "華東", Category = "A", Amount = 100 },
            new SaleRecord { Region = "華東", Category = "B", Amount = 200 },
            new SaleRecord { Region = "華南", Category = "A", Amount = 300 }
        );
        _ctx.SaveChanges();
        _whitelist = AnalysisFieldScanner.ScanModel(typeof(SaleRecord));
    }

    public void Dispose() { _ctx.Dispose(); _conn.Dispose(); }

    [Fact]
    public void Throws_when_dimension_not_in_whitelist()
    {
        var engine = new AnalysisQueryEngine();
        var req = new AnalysisQueryRequest { Dimensions = new() { "Nonexistent" }, Measures = new() };
        Assert.Throws<InvalidOperationException>(
            () => engine.Execute(_ctx.SaleRecords, req, _whitelist));
    }

    [Fact]
    public void Throws_when_measure_func_not_allowed()
    {
        var engine = new AnalysisQueryEngine();
        var req = new AnalysisQueryRequest
        {
            Dimensions = new() { "Region" },
            Measures = new() { new() { Field = "Amount", Func = AggregateFunc.Max } }
        };
        Assert.Throws<InvalidOperationException>(
            () => engine.Execute(_ctx.SaleRecords, req, _whitelist));
    }

    [Fact]
    public void Filter_Eq_reduces_rows()
    {
        var engine = new AnalysisQueryEngine();
        var req = new AnalysisQueryRequest
        {
            Dimensions = new() { "Region" },
            Measures = new() { new() { Field = "Amount", Func = AggregateFunc.Sum } },
            Filters = new() { new() { Field = "Region", Operator = FilterOperator.Eq, Value = "華東" } }
        };
        var result = engine.Execute(_ctx.SaleRecords, req, _whitelist);
        Assert.Single(result.Rows);
        Assert.Equal("華東", result.Rows[0]["Region"].ToString());
    }

    [Fact]
    public void GroupBy_single_dimension_sum()
    {
        var engine = new AnalysisQueryEngine();
        var req = new AnalysisQueryRequest
        {
            Dimensions = new() { "Region" },
            Measures = new() { new() { Field = "Amount", Func = AggregateFunc.Sum } }
        };
        var result = engine.Execute(_ctx.SaleRecords, req, _whitelist);
        Assert.Equal(2, result.Rows.Count);
        var east = result.Rows.Single(r => r["Region"].ToString() == "華東");
        Assert.Equal(300m, Convert.ToDecimal(east["Amount_Sum"]));
    }

    [Fact]
    public void Result_truncated_at_10000_rows()
    {
        _ctx.SaleRecords.AddRange(
            Enumerable.Range(1, 10_001)
                      .Select(i => new SaleRecord { Region = $"R{i}", Category = "X", Amount = i }));
        _ctx.SaveChanges();

        var engine = new AnalysisQueryEngine();
        var req = new AnalysisQueryRequest
        {
            Dimensions = new() { "Region" },
            Measures = new() { new() { Field = "Amount", Func = AggregateFunc.Count } }
        };
        var result = engine.Execute(_ctx.SaleRecords, req, _whitelist);
        Assert.Equal(10_000, result.Rows.Count);
        Assert.True(result.Truncated);
    }
}
```

**Step 2: Run test → expect FAIL**

**Step 3: 建立實作**

```csharp
// AnalysisQueryEngine.cs
using System.Linq.Expressions;
using System.Reflection;

namespace WalkingTec.Mvvm.Core.Analysis;

public class AnalysisQueryEngine
{
    private const int MaxRows = 10_000;

    public AnalysisQueryResponse Execute<TModel>(
        IQueryable<TModel> baseQuery,
        AnalysisQueryRequest req,
        IEnumerable<AnalysisFieldMeta> whitelist)
    {
        var wl = whitelist.ToDictionary(f => f.FieldName);
        ValidateFields(req, wl);

        var filtered = ApplyFilters(baseQuery, req.Filters, wl);
        var rows = ExecuteGroupBy(filtered, req, wl);
        bool truncated = rows.Count > MaxRows;
        if (truncated) rows = rows.Take(MaxRows).ToList();

        var columns = req.Dimensions
            .Concat(req.Measures.Select(m => $"{m.Field}_{m.Func}"))
            .ToList();

        return new AnalysisQueryResponse
        {
            Columns = columns,
            Rows = rows,
            TotalCount = rows.Count,
            Truncated = truncated,
            QueryHash = ComputeHash(req)
        };
    }

    // Non-generic entry point for Controller (receives IQueryable without type param)
    public AnalysisQueryResponse ExecuteDynamic(
        IQueryable baseQuery,
        AnalysisQueryRequest req,
        IEnumerable<AnalysisFieldMeta> whitelist)
    {
        // Use reflection to call generic Execute<TModel>
        var elementType = baseQuery.ElementType;
        var method = typeof(AnalysisQueryEngine)
            .GetMethod(nameof(Execute))!
            .MakeGenericMethod(elementType);
        return (AnalysisQueryResponse)method.Invoke(this, new object[] { baseQuery, req, whitelist })!;
    }

    private static void ValidateFields(AnalysisQueryRequest req, Dictionary<string, AnalysisFieldMeta> wl)
    {
        foreach (var dim in req.Dimensions)
            if (!wl.TryGetValue(dim, out var m) || m.Kind != AnalysisFieldKind.Dimension)
                throw new InvalidOperationException($"Field '{dim}' is not a valid Dimension.");

        foreach (var mr in req.Measures)
        {
            if (!wl.TryGetValue(mr.Field, out var m) || m.Kind != AnalysisFieldKind.Measure)
                throw new InvalidOperationException($"Field '{mr.Field}' is not a valid Measure.");
            if (!m.AllowedFuncs.HasFlag(mr.Func))
                throw new InvalidOperationException(
                    $"AggregateFunc '{mr.Func}' is not allowed for '{mr.Field}'.");
        }

        foreach (var f in req.Filters)
            if (!wl.ContainsKey(f.Field))
                throw new InvalidOperationException($"Filter field '{f.Field}' is not in whitelist.");
    }

    private static IQueryable<TModel> ApplyFilters<TModel>(
        IQueryable<TModel> query,
        List<FilterCondition> filters,
        Dictionary<string, AnalysisFieldMeta> wl)
    {
        foreach (var filter in filters)
        {
            var param = Expression.Parameter(typeof(TModel), "x");
            var prop = Expression.Property(param, filter.Field);
            var meta = wl[filter.Field];
            var converted = ConvertValue(filter.Value, meta.ClrType);
            var constant = Expression.Constant(converted, meta.ClrType);

            Expression body = filter.Operator switch
            {
                FilterOperator.Eq       => Expression.Equal(prop, constant),
                FilterOperator.Gt       => Expression.GreaterThan(prop, constant),
                FilterOperator.Gte      => Expression.GreaterThanOrEqual(prop, constant),
                FilterOperator.Lt       => Expression.LessThan(prop, constant),
                FilterOperator.Lte      => Expression.LessThanOrEqual(prop, constant),
                FilterOperator.Contains => Expression.Call(prop,
                    typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })!,
                    constant),
                _ => throw new NotSupportedException($"Operator {filter.Operator} not supported.")
            };

            query = query.Where(Expression.Lambda<Func<TModel, bool>>(body, param));
        }
        return query;
    }

    private static object ConvertValue(string value, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        try { return Convert.ChangeType(value, underlying); }
        catch { throw new InvalidOperationException($"Cannot convert '{value}' to {underlying.Name}."); }
    }

    private static List<Dictionary<string, object>> ExecuteGroupBy<TModel>(
        IQueryable<TModel> query,
        AnalysisQueryRequest req,
        Dictionary<string, AnalysisFieldMeta> wl)
    {
        // Phase 1: materialise then group in-process (SQLite + InMemory safe)
        // Production note: verify actual SQL with ToQueryString() to ensure server-side execution
        var items = query.ToList();

        return items
            .GroupBy(row => BuildGroupKey(row, req.Dimensions))
            .Take(MaxRows + 1)
            .Select(g =>
            {
                var dict = new Dictionary<string, object>();
                var keyParts = g.Key.Split('\0');
                for (int i = 0; i < req.Dimensions.Count; i++)
                    dict[req.Dimensions[i]] = keyParts[i];

                foreach (var m in req.Measures)
                {
                    var prop = typeof(TModel).GetProperty(m.Field)!;
                    var values = g.Select(row => Convert.ToDecimal(prop.GetValue(row))).ToList();
                    dict[$"{m.Field}_{m.Func}"] = m.Func switch
                    {
                        AggregateFunc.Sum   => values.Sum(),
                        AggregateFunc.Count => (decimal)values.Count,
                        AggregateFunc.Avg   => values.Average(),
                        AggregateFunc.Max   => values.Max(),
                        AggregateFunc.Min   => values.Min(),
                        _ => throw new NotSupportedException()
                    };
                }
                return dict;
            })
            .ToList();
    }

    private static string BuildGroupKey<TModel>(TModel row, List<string> dimensions)
        => string.Join('\0', dimensions.Select(d =>
               typeof(TModel).GetProperty(d)!.GetValue(row)?.ToString() ?? ""));

    private static string ComputeHash(AnalysisQueryRequest req)
    {
        var raw = System.Text.Json.JsonSerializer.Serialize(req);
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..16];
    }
}
```

**Step 4: Run test → expect PASS (5 tests)**

**Step 5: Commit**
```
git add src/WalkingTec.Mvvm.Core/Analysis/AnalysisQueryEngine.cs \
        test/WalkingTec.Mvvm.Core.Test/Analysis/AnalysisQueryEngineTests.cs
git commit -m "feat(analysis): add AnalysisQueryEngine with whitelist validation, filter, GroupBy, truncation"
```

---

## Task 6: BasePagedListVM — GetAnalysisFields() 虛方法

**Files:**
- Modify: src/WalkingTec.Mvvm.Core/BasePagedListVM.cs

**Step 1: 找插入位置**
```
grep -n "public abstract void InitGridHeader" src/WalkingTec.Mvvm.Core/BasePagedListVM.cs
```

**Step 2: 在 InitGridHeader 下方加入**
```csharp
// 加到檔案 using 區塊
using WalkingTec.Mvvm.Core.Analysis;

// 加到 InitGridHeader 下方
public virtual IEnumerable<AnalysisFieldMeta> GetAnalysisFields()
    => AnalysisFieldScanner.ScanModel(typeof(TModel));
```

**Step 3: Run all tests → expect PASS (向下相容)**
```
dotnet test WalkingTec.Mvvm.sln -c Release --verbosity minimal
```

**Step 4: Commit**
```
git add src/WalkingTec.Mvvm.Core/BasePagedListVM.cs
git commit -m "feat(analysis): add GetAnalysisFields() virtual method to BasePagedListVM"
```

---

## Task 7: _AnalysisController

**Files:**
- Create: src/WalkingTec.Mvvm.Mvc/_AnalysisController.cs
- Modify: src/WalkingTec.Mvvm.Mvc/Helper/FrameworkServiceExtension.cs

**Step 1: 建立 Controller**

```csharp
// src/WalkingTec.Mvvm.Mvc/_AnalysisController.cs
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Analysis;

namespace WalkingTec.Mvvm.Mvc;

[ApiController]
[Route("/_analysis")]
[AllRights]
public class _AnalysisController : BaseController
{
    private readonly AnalysisVmRegistry _registry;

    public _AnalysisController(AnalysisVmRegistry registry)
        => _registry = registry;

    [HttpGet("meta")]
    public IActionResult GetMeta([FromQuery] string listVmType)
    {
        Type vmType;
        try { vmType = _registry.Resolve(listVmType); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

        var vm = (IBasePagedListVM)Activator.CreateInstance(vmType)!;
        vm.DC = DC;
        var fields = vm.GetAnalysisFields();

        return Ok(fields.Select(f => new
        {
            f.FieldName,
            f.DisplayName,
            Kind = f.Kind.ToString(),
            AllowedFuncs = f.Kind == AnalysisFieldKind.Measure
                ? GetAllowedFuncNames(f.AllowedFuncs)
                : Array.Empty<string>()
        }));
    }

    [HttpPost("query")]
    public IActionResult Query([FromBody] AnalysisQueryRequest req)
    {
        if (req.Dimensions.Count > 3) return BadRequest("最多選取 3 個維度。");
        if (req.Measures.Count > 3)   return BadRequest("最多選取 3 個度量。");

        Type vmType;
        try { vmType = _registry.Resolve(req.ListVmType); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

        var vm = CreateAndBindVm(vmType, req.SearcherFormData);
        var whitelist = vm.GetAnalysisFields();
        var baseQuery = GetBaseQuery(vm, vmType);
        if (baseQuery == null) return BadRequest("無法取得查詢來源。");

        try
        {
            var result = new AnalysisQueryEngine().ExecuteDynamic(baseQuery, req, whitelist);
            return Ok(result);
        }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }

    [HttpPost("export")]
    public IActionResult Export([FromBody] AnalysisQueryRequest req,
                                [FromQuery] string format = "xlsx")
    {
        Type vmType;
        try { vmType = _registry.Resolve(req.ListVmType); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }

        var vm = CreateAndBindVm(vmType, req.SearcherFormData);
        var result = new AnalysisQueryEngine()
            .ExecuteDynamic(GetBaseQuery(vm, vmType)!, req, vm.GetAnalysisFields());

        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            var csv = BuildCsv(result);
            return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "analysis.csv");
        }

        var xlsx = AnalysisExcelExporter.Export(result);
        return File(xlsx,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "analysis.xlsx");
    }

    private IBasePagedListVM CreateAndBindVm(Type vmType, string searcherFormData)
    {
        var vm = (IBasePagedListVM)Activator.CreateInstance(vmType)!;
        vm.DC = DC;
        vm.LoginUserInfo = LoginUserInfo;

        if (!string.IsNullOrEmpty(searcherFormData))
        {
            var searcherProp = vmType.GetProperty("Searcher");
            if (searcherProp != null)
            {
                var searcher = JsonSerializer.Deserialize(
                    searcherFormData, searcherProp.PropertyType,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                searcherProp.SetValue(vm, searcher);
            }
        }
        return vm;
    }

    private static IQueryable? GetBaseQuery(IBasePagedListVM vm, Type vmType)
        => vmType.GetMethod("GetSearchQuery")?.Invoke(vm, null) as IQueryable;

    private static string[] GetAllowedFuncNames(AggregateFunc funcs)
        => Enum.GetValues<AggregateFunc>()
               .Where(f => funcs.HasFlag(f))
               .Select(f => f.ToString())
               .ToArray();

    private static string BuildCsv(AnalysisQueryResponse result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(",", result.Columns));
        foreach (var row in result.Rows)
            sb.AppendLine(string.Join(",", result.Columns.Select(c => row.GetValueOrDefault(c))));
        return sb.ToString();
    }
}
```

**Step 2: 注冊 AnalysisVmRegistry**

在 `FrameworkServiceExtension.cs` 的 `AddFrameworkService` 結尾加入：

```csharp
var registry = new AnalysisVmRegistry();
registry.Build(AppDomain.CurrentDomain.GetAssemblies());
services.AddSingleton(registry);
```

**Step 3: Build 確認**
```
dotnet build WalkingTec.Mvvm.sln -c Release --verbosity minimal
dotnet test WalkingTec.Mvvm.sln -c Release --verbosity minimal
```

**Step 4: Commit**
```
git add src/WalkingTec.Mvvm.Mvc/_AnalysisController.cs \
        src/WalkingTec.Mvvm.Mvc/Helper/FrameworkServiceExtension.cs
git commit -m "feat(analysis): add _AnalysisController with meta/query/export endpoints"
```

---

## Task 8: AnalysisExcelExporter

**Files:**
- Create: src/WalkingTec.Mvvm.Core/Analysis/AnalysisExcelExporter.cs

```csharp
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace WalkingTec.Mvvm.Core.Analysis;

public static class AnalysisExcelExporter
{
    public static byte[] Export(AnalysisQueryResponse result)
    {
        var workbook = new XSSFWorkbook();
        var sheet = workbook.CreateSheet("Analysis");

        var header = sheet.CreateRow(0);
        for (int i = 0; i < result.Columns.Count; i++)
            header.CreateCell(i).SetCellValue(result.Columns[i]);

        for (int r = 0; r < result.Rows.Count; r++)
        {
            var row = sheet.CreateRow(r + 1);
            for (int c = 0; c < result.Columns.Count; c++)
            {
                var val = result.Rows[r].GetValueOrDefault(result.Columns[c]);
                var cell = row.CreateCell(c);
                if (val is decimal d) cell.SetCellValue((double)d);
                else cell.SetCellValue(val?.ToString() ?? "");
            }
        }

        using var ms = new MemoryStream();
        workbook.Write(ms, leaveOpen: true);
        return ms.ToArray();
    }
}
```

**Commit**
```
git add src/WalkingTec.Mvvm.Core/Analysis/AnalysisExcelExporter.cs
git commit -m "feat(analysis): add AnalysisExcelExporter using NPOI"
```

---

## Task 9: DataTableTagHelper — enable-analysis 屬性

**Files:**
- Modify: src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs

**Step 1: 找 toolbar 按鈕注入位置**
```
grep -n "NeedShowFilter\|NeedShowPrint\|toolBarBtnStrBuilder" \
  src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs | head -15
```

**Step 2: 加入屬性（在類別內）**
```csharp
public bool EnableAnalysis { get; set; }
```

**Step 3: 在 toolbar 按鈕區段加入切換鍵（NeedShowPrint 附近）**
```csharp
if (EnableAnalysis)
{
    var vmTypeName = Vm?.ModelExplorer?.ModelType?.FullName ?? "";
    toolBarBtnStrBuilder.Append(
        $@"<button type=""button"" class=""layui-btn layui-btn-sm"" " +
        $@"onclick=""wtmAnalysis.toggle('{Id}','{vmTypeName}')"">&#xe67e; 分析模式</button>");
}
```

**Step 4: 在 output 後插入 analysis-panel 容器（grid 容器結尾後）**
```csharp
if (EnableAnalysis)
{
    output.PostElement.AppendHtml(
        $@"<div id=""analysis-panel-{Id}"" style=""display:none;""></div>");
    output.PostElement.AppendHtml(
        @"<script src=""/_framework/analysis.js""></script>");
}
```

**Step 5: Run tests**
```
dotnet test WalkingTec.Mvvm.sln -c Release --verbosity minimal
```

**Step 6: Commit**
```
git add src/WalkingTec.Mvvm.TagHelpers.LayUI/DataTableTagHelper.cs
git commit -m "feat(analysis): add enable-analysis attribute to DataTableTagHelper"
```

---

## Task 10: framework_analysis.js + JS Tests

**Files:**
- Create: src/WalkingTec.Mvvm.Mvc/framework_analysis.js
- Test:   test/WalkingTec.Mvvm.Js.Tests/__tests__/framework_analysis.test.js

**XSS 安全注意事項：**
framework_analysis.js 在建立 DOM 元素時，所有來自伺服器的欄位名稱和值都必須使用 textContent 設值，或透過 document.createElement + appendChild 建立，禁止直接將伺服器資料拼入 HTML 字串。靜態框架 HTML（空容器、按鈕）可使用 DOM 方法建立。

實作要點：
1. `toggle(gridId, listVmType)` — 切換 grid/panel 顯示，呼叫 loadMeta
2. `loadMeta(gridId, listVmType, panelEl)` — GET /_analysis/meta，結果傳入 renderPanel
3. `renderPanel(gridId, fields, panelEl)` — 用 document.createElement 建立 checkbox + select（所有文字用 textContent 設值，防 XSS）
4. `query(gridId)` — 收集選取的維度/度量/SearcherFormData，POST /_analysis/query
5. `renderTable(gridId, result)` — 用 DOM 方法建立 table（td.textContent = val）
6. `renderChart(gridId, result, req)` — 呼叫 echarts.init + setOption
7. `detectChartType(dims, msrs)` — 純函式，可單元測試
8. `validateSelection(dims, measures)` — 純函式，可單元測試
9. `exportData(gridId, format)` — 建立隱藏 form 送 POST

**Step 1: 先寫 JS 純函式測試**

```javascript
// test/WalkingTec.Mvvm.Js.Tests/__tests__/framework_analysis.test.js
function detectChartType(dims, msrs) {
    if (dims.length === 0) return 'card';
    if (dims.some(d => d.isDate)) return 'line';
    if (dims.length === 2) return 'bar-stacked';
    return 'bar';
}

function validateSelection(dims, measures) {
    var errors = [];
    if (dims.length > 3) errors.push('維度最多選 3 個');
    if (measures.length > 3) errors.push('度量最多選 3 個');
    if (dims.length === 0 && measures.length === 0) errors.push('請至少選擇一個維度或度量');
    return errors;
}

describe('detectChartType', () => {
    test('no dimensions → card', () => {
        expect(detectChartType([], [{ field: 'Amount' }])).toBe('card');
    });
    test('date dimension → line', () => {
        expect(detectChartType([{ isDate: true }], [{}])).toBe('line');
    });
    test('1 dim + 1 measure → bar', () => {
        expect(detectChartType([{ isDate: false }], [{}])).toBe('bar');
    });
    test('2 dims → bar-stacked', () => {
        expect(detectChartType([{}, {}], [{}])).toBe('bar-stacked');
    });
});

describe('validateSelection', () => {
    test('empty → error', () => {
        expect(validateSelection([], [])).toHaveLength(1);
    });
    test('4 dims → error', () => {
        expect(validateSelection([1,2,3,4], [1])).toContain('維度最多選 3 個');
    });
    test('valid selection → no errors', () => {
        expect(validateSelection([1,2], [1])).toHaveLength(0);
    });
});
```

**Step 2: Run JS tests → PASS**
```
cd test/WalkingTec.Mvvm.Js.Tests && npm test -- framework_analysis
```

**Step 3: 建立 framework_analysis.js**

見設計文件 docs/plans/2026-03-04-analysis-mode-design.md §7。
實作時依上述 XSS 安全注意事項，所有動態內容使用 textContent 或 DOM 方法，不使用字串拼接 HTML。

**Step 4: Run all JS tests**
```
cd test/WalkingTec.Mvvm.Js.Tests && npm test
```
預期：原 25 + 新增 7 = 32 tests passed

**Step 5: Commit**
```
git add src/WalkingTec.Mvvm.Mvc/framework_analysis.js \
        test/WalkingTec.Mvvm.Js.Tests/__tests__/framework_analysis.test.js
git commit -m "feat(analysis): add framework_analysis.js with dimension/measure UI, table, ECharts"
```

---

## Task 11: 靜態資源路由（framework_analysis.js as embedded resource）

**Files:**
- Modify: src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj
- Modify: src/WalkingTec.Mvvm.Mvc/_FrameworkController.cs

**Step 1: 設定 Embedded Resource**

在 .csproj 加入：
```xml
<ItemGroup>
  <EmbeddedResource Include="framework_analysis.js" />
</ItemGroup>
```

**Step 2: 加入路由 Action**

找到 framework_layui.js 的服務 Action，仿照加入：
```csharp
[HttpGet("/_framework/analysis.js")]
[AllRights]
public IActionResult GetAnalysisScript()
{
    var stream = typeof(_FrameworkController).Assembly
        .GetManifestResourceStream("WalkingTec.Mvvm.Mvc.framework_analysis.js");
    if (stream == null) return NotFound();
    return File(stream, "application/javascript");
}
```

**Step 3: Build**
```
dotnet build WalkingTec.Mvvm.sln -c Release --verbosity minimal
```

**Step 4: Commit**
```
git add src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj \
        src/WalkingTec.Mvvm.Mvc/_FrameworkController.cs
git commit -m "feat(analysis): serve framework_analysis.js as embedded resource at /_framework/analysis.js"
```

---

## Task 12: 全套驗收

**Step 1: Run 所有測試**
```
dotnet test WalkingTec.Mvvm.sln -c Release --verbosity minimal
cd test/WalkingTec.Mvvm.Js.Tests && npm test
```

**Step 2: Push**
```
git push origin dotnet8
```

**Step 3: 手動驗收 checklist**

- [ ] ListVM 加 [EnableAnalysis]，Model 加 [Dimension]/[Measure]
- [ ] View 的 <wt:grid> 加 enable-analysis="true"
- [ ] 開啟頁面，確認 toolbar 有「分析模式」按鈕
- [ ] 切換後維度/度量 checkbox 出現（呼叫 /meta 成功）
- [ ] 勾選維度 + 度量 → 查詢 → 聚合表格與圖表正確
- [ ] 切換圖表 tab → ECharts 正確渲染
- [ ] 匯出 Excel → 可正常開啟，資料正確
- [ ] 未標記 [EnableAnalysis] 的 VM → /meta 回傳 400
- [ ] 超過 10,000 筆 → 顯示截斷警告

