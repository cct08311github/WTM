#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Helper;

namespace WalkingTec.Mvvm.Benchmarks
{
    public class SampleEntity
    {
        [Display(Name = "编号")]
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public DateTime Created { get; set; }
    }

    public enum SampleColor
    {
        [Display(Name = "红色")]
        Red,
        [Display(Name = "绿色")]
        Green,
        Blue
    }

    [ShortRunJob]
    [MemoryDiagnoser]
    public class PropertyHelperBenchmarks
    {
        private PropertyInfo _idProp = null!;
        private PropertyInfo _nameProp = null!;

        [GlobalSetup]
        public void Setup()
        {
            _idProp = typeof(SampleEntity).GetProperty("Id")!;
            _nameProp = typeof(SampleEntity).GetProperty("Name")!;

            // Pre-warm the caches used by the Cached_* benchmarks
            PropertyHelper.GetPropertyExpression(typeof(SampleEntity), "Id");
            PropertyHelper.GetPropertyExpression(typeof(SampleEntity), "Name");
            _idProp.IsPropertyRequired();
            _nameProp.IsPropertyRequired();
            _idProp.GetPropertyDisplayName();
            PropertyHelper.GetEnumDisplayName(typeof(SampleColor), "Red");
        }

        // ---- GetPropertyExpression ----

        [Benchmark(Baseline = true)]
        public Func<object, object?> Uncached_GetPropertyExpression()
        {
            // Inline original logic (pre-cache)
            const string property = "Id";
            var type = typeof(SampleEntity);
            List<string> level = [];
            if (property.Contains('.'))
                level.AddRange(property.Split('.'));
            else
                level.Add(property);
            var pe = Expression.Parameter(type);
            var member = Expression.Property(pe, type.GetProperty(level[0])!);
            for (int i = 1; i < level.Count; i++)
                member = Expression.Property(member, member.Type.GetProperty(level[i])!);
            return Expression.Lambda<Func<object, object?>>(Expression.Convert(member, typeof(object)), pe).Compile();
        }

        [Benchmark]
        public Func<object, object?> Cached_GetPropertyExpression()
        {
            return PropertyHelper.GetPropertyExpression(typeof(SampleEntity), "Id");
        }

        // ---- IsPropertyRequired ----

        [Benchmark(Baseline = false)]
        public bool Uncached_IsPropertyRequired()
        {
            // Inline original logic (pre-cache)
            var pi = _idProp;
            Type? t = ((PropertyInfo)pi).PropertyType;
            if (t != null && (t.GetTypeInfo().IsPrimitive || t == typeof(decimal) || t.GetTypeInfo().IsEnum || t == typeof(Guid)))
                return true;
            if (pi.GetCustomAttributes(typeof(RequiredAttribute), false).FirstOrDefault() is RequiredAttribute req
                && req.AllowEmptyStrings == false)
                return true;
            if (pi.GetCustomAttributes(typeof(KeyAttribute), false).FirstOrDefault() != null)
                return true;
            return false;
        }

        [Benchmark]
        public bool Cached_IsPropertyRequired()
        {
            return _idProp.IsPropertyRequired();
        }

        // ---- GetPropertyDisplayName ----

        [Benchmark(Baseline = false)]
        public string Uncached_GetPropertyDisplayName()
        {
            // Inline original logic (pre-cache)
            var pi = _idProp;
            if (pi.GetCustomAttributes(typeof(DisplayAttribute), false).FirstOrDefault() is DisplayAttribute dis
                && !string.IsNullOrEmpty(dis.Name))
            {
                return dis.Name;
            }
            return pi.Name;
        }

        [Benchmark]
        public string Cached_GetPropertyDisplayName()
        {
            return _idProp.GetPropertyDisplayName();
        }

        // ---- GetEnumDisplayName (string overload) ----

        [Benchmark(Baseline = false)]
        public string Uncached_GetEnumDisplayName()
        {
            // Inline original logic (pre-cache)
            var enumType = typeof(SampleColor);
            const string value = "Red";
            FieldInfo? field = enumType.GetField(value);
            if (field != null)
            {
                List<Attribute> attribs = [.. field.GetCustomAttributes(typeof(DisplayAttribute), true).Cast<Attribute>()];
                if (attribs.Count > 0)
                    return ((DisplayAttribute)attribs[0]).GetName() ?? "";
                return value;
            }
            return "";
        }

        [Benchmark]
        public string Cached_GetEnumDisplayName()
        {
            return PropertyHelper.GetEnumDisplayName(typeof(SampleColor), "Red");
        }
    }
}
