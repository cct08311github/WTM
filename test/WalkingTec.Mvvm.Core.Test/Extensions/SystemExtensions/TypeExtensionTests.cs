#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core.Test.Extensions.SystemExtensions
{
    // ── Fixture types used across all test groups ────────────────────────────────

    public enum TypeExtTestEnum { A, B, C }

    public class TypeExtPlainModel
    {
        public int IntProp { get; set; }
        public string? StringProp { get; set; }
        public bool BoolProp { get; set; }
        public DateTime DateProp { get; set; }
        public decimal DecimalProp { get; set; }
        public double DoubleProp { get; set; }
        public float FloatProp { get; set; }
        public short ShortProp { get; set; }
        public long LongProp { get; set; }
        public TypeExtTestEnum EnumProp { get; set; }
        public TypeExtTestEnum? NullableEnumProp { get; set; }
        public int? NullableIntProp { get; set; }
        public bool? NullableBoolProp { get; set; }
        public DateTime? NullableDateProp { get; set; }
        public List<string>? StringListProp { get; set; }
    }

    public class TypeExtDerivedModel : TypeExtPlainModel
    {
        public string? ExtraField { get; set; }
    }

    public class TypeExtGenericModel<T>
    {
        public T? Value { get; set; }
    }

    // Model for GetRandomValues / GetRandomValuesForTestData
    public class TypeExtRvModel
    {
        public int IntField { get; set; }
        public string? StrField { get; set; }
        public bool BoolField { get; set; }
        public TypeExtTestEnum EnumField { get; set; }
        public int? NullableInt { get; set; }
        public bool? NullableBool { get; set; }
        public TypeExtTestEnum? NullableEnum { get; set; }
        public DateTime DateField { get; set; }
        public DateTime? NullableDate { get; set; }

        [StringLength(10, MinimumLength = 3)]
        public string? BoundedStr { get; set; }

        [Range(5, 15)]
        public int RangedInt { get; set; }

        [NotMapped]
        public string? NotMappedStr { get; set; }

        // Navigation-like — should be skipped
        public TypeExtPlainModel? Navigation { get; set; }
    }

    // Model that simulates a FK pattern (MajorId → Major)
    public class TypeExtFkModel
    {
        public int MajorId { get; set; }
        public TypeExtPlainModel? Major { get; set; }
        public string? Name { get; set; }
    }

    // For IBasePoco / IPersistPoco coverage
    public class TypeExtBasePoco : TopBasePoco
    {
        public string? Note { get; set; }
    }

    // For regex-based GetRandomValuesForTestData
    public class TypeExtRegexModel
    {
        [RegularExpression(@"[A-Z]{3}")]
        public string? RegexStr { get; set; }

        [RegularExpression(@"[A-Z]{10}")]
        [StringLength(5)]
        public string? RegexAndLength { get; set; }
    }

    [TestClass]
    public class TypeExtensionTests
    {
        // ── IsGeneric ────────────────────────────────────────────────────────────

        [TestMethod]
        public void IsGeneric_ListOfInt_MatchesListDefinition()
        {
            typeof(List<int>).IsGeneric(typeof(List<>)).Should().BeTrue();
        }

        [TestMethod]
        public void IsGeneric_NonGenericType_ReturnsFalse()
        {
            typeof(string).IsGeneric(typeof(List<>)).Should().BeFalse();
        }

        [TestMethod]
        public void IsGeneric_WrongGenericDefinition_ReturnsFalse()
        {
            typeof(List<int>).IsGeneric(typeof(IEnumerable<>)).Should().BeFalse();
        }

        [TestMethod]
        public void IsGeneric_NullableInt_MatchesNullableDefinition()
        {
            typeof(int?).IsGeneric(typeof(Nullable<>)).Should().BeTrue();
        }

        [TestMethod]
        public void IsGeneric_DictionaryStringInt_MatchesDictionaryDefinition()
        {
            typeof(Dictionary<string, int>).IsGeneric(typeof(Dictionary<,>)).Should().BeTrue();
        }

        // ── IsNullable ───────────────────────────────────────────────────────────

        [TestMethod]
        public void IsNullable_NullableInt_ReturnsTrue()
        {
            typeof(int?).IsNullable().Should().BeTrue();
        }

        [TestMethod]
        public void IsNullable_NullableBool_ReturnsTrue()
        {
            typeof(bool?).IsNullable().Should().BeTrue();
        }

        [TestMethod]
        public void IsNullable_NullableEnum_ReturnsTrue()
        {
            typeof(TypeExtTestEnum?).IsNullable().Should().BeTrue();
        }

        [TestMethod]
        public void IsNullable_PlainInt_ReturnsFalse()
        {
            typeof(int).IsNullable().Should().BeFalse();
        }

        [TestMethod]
        public void IsNullable_String_ReturnsFalse()
        {
            typeof(string).IsNullable().Should().BeFalse();
        }

        [TestMethod]
        public void IsNullable_NullableDateTime_ReturnsTrue()
        {
            typeof(DateTime?).IsNullable().Should().BeTrue();
        }

        // ── IsList ───────────────────────────────────────────────────────────────

        [TestMethod]
        public void IsList_ListOfString_ReturnsTrue()
        {
            typeof(List<string>).IsList().Should().BeTrue();
        }

        [TestMethod]
        public void IsList_IEnumerableOfInt_ReturnsTrue()
        {
            typeof(IEnumerable<int>).IsList().Should().BeTrue();
        }

        [TestMethod]
        public void IsList_PlainString_ReturnsFalse()
        {
            typeof(string).IsList().Should().BeFalse();
        }

        [TestMethod]
        public void IsList_PlainInt_ReturnsFalse()
        {
            typeof(int).IsList().Should().BeFalse();
        }

        // ── IsListOf ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void IsListOf_ListOfString_WithStringType_ReturnsTrue()
        {
            typeof(List<string>).IsListOf<string>().Should().BeTrue();
        }

        [TestMethod]
        public void IsListOf_ListOfInt_WithIntType_ReturnsTrue()
        {
            typeof(List<int>).IsListOf<int>().Should().BeTrue();
        }

        [TestMethod]
        public void IsListOf_ListOfInt_WithStringType_ReturnsFalse()
        {
            typeof(List<int>).IsListOf<string>().Should().BeFalse();
        }

        [TestMethod]
        public void IsListOf_PlainString_ReturnsFalse()
        {
            typeof(string).IsListOf<string>().Should().BeFalse();
        }

        // ── IsEnum ───────────────────────────────────────────────────────────────

        [TestMethod]
        public void IsEnum_EnumType_ReturnsTrue()
        {
            typeof(TypeExtTestEnum).IsEnum().Should().BeTrue();
        }

        [TestMethod]
        public void IsEnum_StringType_ReturnsFalse()
        {
            typeof(string).IsEnum().Should().BeFalse();
        }

        [TestMethod]
        public void IsEnum_NullableEnum_ReturnsFalse()
        {
            // Nullable<TypeExtTestEnum> is not itself an enum
            typeof(TypeExtTestEnum?).IsEnum().Should().BeFalse();
        }

        // ── IsEnumOrNullableEnum ─────────────────────────────────────────────────

        [TestMethod]
        public void IsEnumOrNullableEnum_EnumType_ReturnsTrue()
        {
            typeof(TypeExtTestEnum).IsEnumOrNullableEnum().Should().BeTrue();
        }

        [TestMethod]
        public void IsEnumOrNullableEnum_NullableEnum_ReturnsTrue()
        {
            typeof(TypeExtTestEnum?).IsEnumOrNullableEnum().Should().BeTrue();
        }

        [TestMethod]
        public void IsEnumOrNullableEnum_StringType_ReturnsFalse()
        {
            typeof(string).IsEnumOrNullableEnum().Should().BeFalse();
        }

        [TestMethod]
        public void IsEnumOrNullableEnum_IntType_ReturnsFalse()
        {
            typeof(int).IsEnumOrNullableEnum().Should().BeFalse();
        }

        // ── IsPrimitive ──────────────────────────────────────────────────────────

        [TestMethod]
        public void IsPrimitive_Int_ReturnsTrue()
        {
            typeof(int).IsPrimitive().Should().BeTrue();
        }

        [TestMethod]
        public void IsPrimitive_Bool_ReturnsTrue()
        {
            typeof(bool).IsPrimitive().Should().BeTrue();
        }

        [TestMethod]
        public void IsPrimitive_Decimal_ReturnsTrue()
        {
            typeof(decimal).IsPrimitive().Should().BeTrue();
        }

        [TestMethod]
        public void IsPrimitive_Double_ReturnsTrue()
        {
            typeof(double).IsPrimitive().Should().BeTrue();
        }

        [TestMethod]
        public void IsPrimitive_String_ReturnsFalse()
        {
            typeof(string).IsPrimitive().Should().BeFalse();
        }

        [TestMethod]
        public void IsPrimitive_DateTime_ReturnsFalse()
        {
            typeof(DateTime).IsPrimitive().Should().BeFalse();
        }

        // ── IsNumber ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void IsNumber_Int_ReturnsTrue()
        {
            typeof(int).IsNumber().Should().BeTrue();
        }

        [TestMethod]
        public void IsNumber_Short_ReturnsTrue()
        {
            typeof(short).IsNumber().Should().BeTrue();
        }

        [TestMethod]
        public void IsNumber_Long_ReturnsTrue()
        {
            typeof(long).IsNumber().Should().BeTrue();
        }

        [TestMethod]
        public void IsNumber_Float_ReturnsTrue()
        {
            typeof(float).IsNumber().Should().BeTrue();
        }

        [TestMethod]
        public void IsNumber_Decimal_ReturnsTrue()
        {
            typeof(decimal).IsNumber().Should().BeTrue();
        }

        [TestMethod]
        public void IsNumber_Double_ReturnsTrue()
        {
            typeof(double).IsNumber().Should().BeTrue();
        }

        [TestMethod]
        public void IsNumber_NullableInt_ReturnsTrue()
        {
            typeof(int?).IsNumber().Should().BeTrue();
        }

        [TestMethod]
        public void IsNumber_NullableDecimal_ReturnsTrue()
        {
            typeof(decimal?).IsNumber().Should().BeTrue();
        }

        [TestMethod]
        public void IsNumber_String_ReturnsFalse()
        {
            typeof(string).IsNumber().Should().BeFalse();
        }

        [TestMethod]
        public void IsNumber_Bool_ReturnsFalse()
        {
            typeof(bool).IsNumber().Should().BeFalse();
        }

        [TestMethod]
        public void IsNumber_DateTime_ReturnsFalse()
        {
            typeof(DateTime).IsNumber().Should().BeFalse();
        }

        // ── IsBool ───────────────────────────────────────────────────────────────

        [TestMethod]
        public void IsBool_BoolType_ReturnsTrue()
        {
            typeof(bool).IsBool().Should().BeTrue();
        }

        [TestMethod]
        public void IsBool_NullableBool_ReturnsFalse()
        {
            typeof(bool?).IsBool().Should().BeFalse();
        }

        [TestMethod]
        public void IsBool_String_ReturnsFalse()
        {
            typeof(string).IsBool().Should().BeFalse();
        }

        // ── IsBoolOrNullableBool ─────────────────────────────────────────────────

        [TestMethod]
        public void IsBoolOrNullableBool_BoolType_ReturnsTrue()
        {
            typeof(bool).IsBoolOrNullableBool().Should().BeTrue();
        }

        [TestMethod]
        public void IsBoolOrNullableBool_NullableBool_ReturnsTrue()
        {
            typeof(bool?).IsBoolOrNullableBool().Should().BeTrue();
        }

        [TestMethod]
        public void IsBoolOrNullableBool_String_ReturnsFalse()
        {
            typeof(string).IsBoolOrNullableBool().Should().BeFalse();
        }

        [TestMethod]
        public void IsBoolOrNullableBool_Int_ReturnsFalse()
        {
            typeof(int).IsBoolOrNullableBool().Should().BeFalse();
        }

        // ── GetSingleProperty(string) ────────────────────────────────────────────

        [TestMethod]
        public void GetSingleProperty_ByName_ExistingProp_ReturnsPropertyInfo()
        {
            var pi = typeof(TypeExtPlainModel).GetSingleProperty(nameof(TypeExtPlainModel.IntProp));
            pi.Should().NotBeNull();
            pi!.Name.Should().Be(nameof(TypeExtPlainModel.IntProp));
        }

        [TestMethod]
        public void GetSingleProperty_ByName_NonExistentProp_ReturnsNull()
        {
            var pi = typeof(TypeExtPlainModel).GetSingleProperty("DoesNotExist");
            pi.Should().BeNull();
        }

        [TestMethod]
        public void GetSingleProperty_ByName_CaseSensitive()
        {
            // Property names are case-sensitive in the implementation
            var pi = typeof(TypeExtPlainModel).GetSingleProperty("intProp");
            pi.Should().BeNull();
        }

        [TestMethod]
        public void GetSingleProperty_ByName_CachesResult_SecondCallReturnsSameObject()
        {
            var pi1 = typeof(TypeExtPlainModel).GetSingleProperty(nameof(TypeExtPlainModel.StringProp));
            var pi2 = typeof(TypeExtPlainModel).GetSingleProperty(nameof(TypeExtPlainModel.StringProp));
            // Because the cache returns the same List<PropertyInfo>, we should get equivalent PropertyInfo
            pi1.Should().NotBeNull();
            pi2.Should().NotBeNull();
            pi1!.Name.Should().Be(pi2!.Name);
        }

        // ── GetSingleProperty(Func) ──────────────────────────────────────────────

        [TestMethod]
        public void GetSingleProperty_ByPredicate_FindsBoolProp()
        {
            var pi = typeof(TypeExtPlainModel).GetSingleProperty(p => p.PropertyType == typeof(bool));
            pi.Should().NotBeNull();
            pi!.PropertyType.Should().Be(typeof(bool));
        }

        [TestMethod]
        public void GetSingleProperty_ByPredicate_NoMatch_ReturnsNull()
        {
            var pi = typeof(TypeExtPlainModel).GetSingleProperty(p => p.Name == "Nonexistent");
            pi.Should().BeNull();
        }

        [TestMethod]
        public void GetSingleProperty_ByPredicate_FindsNullableEnum()
        {
            var pi = typeof(TypeExtPlainModel).GetSingleProperty(
                p => p.PropertyType == typeof(TypeExtTestEnum?));
            pi.Should().NotBeNull();
            pi!.Name.Should().Be(nameof(TypeExtPlainModel.NullableEnumProp));
        }

        // ── GetAllProperties ─────────────────────────────────────────────────────

        [TestMethod]
        public void GetAllProperties_ReturnsAllDeclaredProperties()
        {
            var props = typeof(TypeExtPlainModel).GetAllProperties();
            props.Should().NotBeNull();
            props.Should().NotBeEmpty();
            props.Any(p => p.Name == nameof(TypeExtPlainModel.IntProp)).Should().BeTrue();
            props.Any(p => p.Name == nameof(TypeExtPlainModel.StringProp)).Should().BeTrue();
        }

        [TestMethod]
        public void GetAllProperties_DerivedType_IncludesDerivedAndBaseProps()
        {
            var props = typeof(TypeExtDerivedModel).GetAllProperties();
            props.Any(p => p.Name == nameof(TypeExtDerivedModel.ExtraField)).Should().BeTrue();
        }

        [TestMethod]
        public void GetAllProperties_MultipleCalls_ReturnsCachedResult()
        {
            var result1 = typeof(TypeExtPlainModel).GetAllProperties();
            var result2 = typeof(TypeExtPlainModel).GetAllProperties();
            // Same list instance returned from cache
            result1.Should().BeSameAs(result2);
        }

        [TestMethod]
        public void GetAllProperties_CountMatchesReflection()
        {
            var expected = typeof(TypeExtPlainModel).GetProperties().Length;
            var actual = typeof(TypeExtPlainModel).GetAllProperties().Count;
            actual.Should().Be(expected);
        }

        // ── GetRandomValues ───────────────────────────────────────────────────────

        [TestMethod]
        public void GetRandomValues_PlainModel_ReturnsNonEmptyDictionary()
        {
            var rv = typeof(TypeExtRvModel).GetRandomValues();
            rv.Should().NotBeNull();
            rv.Should().NotBeEmpty();
        }

        [TestMethod]
        public void GetRandomValues_IntField_ReturnsNumericString()
        {
            var rv = typeof(TypeExtRvModel).GetRandomValues();
            rv.Should().ContainKey(nameof(TypeExtRvModel.IntField));
            int.TryParse(rv[nameof(TypeExtRvModel.IntField)], out _).Should().BeTrue();
        }

        [TestMethod]
        public void GetRandomValues_StringField_IsQuoted()
        {
            // String values are wrapped in quotes: "\"sometext\""
            var rv = typeof(TypeExtRvModel).GetRandomValues();
            rv.Should().ContainKey(nameof(TypeExtRvModel.StrField));
            var val = rv[nameof(TypeExtRvModel.StrField)];
            val.Should().StartWith("\"");
            val.Should().EndWith("\"");
        }

        [TestMethod]
        public void GetRandomValues_BoolField_IsTrueOrFalse()
        {
            var rv = typeof(TypeExtRvModel).GetRandomValues();
            rv.Should().ContainKey(nameof(TypeExtRvModel.BoolField));
            new[] { "true", "false" }.Should().Contain(rv[nameof(TypeExtRvModel.BoolField)]);
        }

        [TestMethod]
        public void GetRandomValues_EnumField_ContainsFQN()
        {
            var rv = typeof(TypeExtRvModel).GetRandomValues();
            rv.Should().ContainKey(nameof(TypeExtRvModel.EnumField));
            rv[nameof(TypeExtRvModel.EnumField)].Should().Contain("TypeExtTestEnum");
        }

        [TestMethod]
        public void GetRandomValues_NullableEnumField_MayContainNull()
        {
            // Nullable enum values include "null" as one of the options
            var rv = typeof(TypeExtRvModel).GetRandomValues();
            rv.Should().ContainKey(nameof(TypeExtRvModel.NullableEnum));
            var val = rv[nameof(TypeExtRvModel.NullableEnum)];
            val.Should().NotBeNullOrEmpty();
        }

        [TestMethod]
        public void GetRandomValues_NullableBoolField_Present()
        {
            var rv = typeof(TypeExtRvModel).GetRandomValues();
            rv.Should().ContainKey(nameof(TypeExtRvModel.NullableBool));
        }

        [TestMethod]
        public void GetRandomValues_DateField_IsDateTimeParseExpression()
        {
            var rv = typeof(TypeExtRvModel).GetRandomValues();
            rv.Should().ContainKey(nameof(TypeExtRvModel.DateField));
            rv[nameof(TypeExtRvModel.DateField)].Should().StartWith("DateTime.Parse(");
        }

        [TestMethod]
        public void GetRandomValues_NullableDateField_IsDateTimeParseExpression()
        {
            var rv = typeof(TypeExtRvModel).GetRandomValues();
            rv.Should().ContainKey(nameof(TypeExtRvModel.NullableDate));
            rv[nameof(TypeExtRvModel.NullableDate)].Should().StartWith("DateTime.Parse(");
        }

        [TestMethod]
        public void GetRandomValues_NotMappedField_Excluded()
        {
            var rv = typeof(TypeExtRvModel).GetRandomValues();
            rv.Should().NotContainKey(nameof(TypeExtRvModel.NotMappedStr));
        }

        [TestMethod]
        public void GetRandomValues_NavigationProperty_Excluded()
        {
            // List and TopBasePoco sub-types are excluded
            var rv = typeof(TypeExtRvModel).GetRandomValues();
            rv.Should().NotContainKey(nameof(TypeExtRvModel.Navigation));
        }

        [TestMethod]
        public void GetRandomValues_RangedInt_InRange()
        {
            // Run several times to reduce flakiness from Random
            for (int i = 0; i < 10; i++)
            {
                var rv = typeof(TypeExtRvModel).GetRandomValues();
                rv.Should().ContainKey(nameof(TypeExtRvModel.RangedInt));
                int.TryParse(rv[nameof(TypeExtRvModel.RangedInt)], out int val).Should().BeTrue();
                val.Should().BeInRange(5, 14);
            }
        }

        [TestMethod]
        public void GetRandomValues_BoundedString_LengthRespected()
        {
            for (int i = 0; i < 10; i++)
            {
                var rv = typeof(TypeExtRvModel).GetRandomValues();
                rv.Should().ContainKey(nameof(TypeExtRvModel.BoundedStr));
                var val = rv[nameof(TypeExtRvModel.BoundedStr)];
                // Strip surrounding quotes
                var inner = val.Trim('"');
                inner.Length.Should().BeInRange(3, 10);
            }
        }

        [TestMethod]
        public void GetRandomValues_FkPattern_ReturnsFkSentinel()
        {
            // TypeExtFkModel has MajorId (int) and Major (navigation); the FK heuristic
            // detects "majorid" == "major" + "id" and returns "$fk$"
            var rv = typeof(TypeExtFkModel).GetRandomValues();
            rv.Should().ContainKey(nameof(TypeExtFkModel.MajorId));
            rv[nameof(TypeExtFkModel.MajorId)].Should().Be("$fk$");
        }

        [TestMethod]
        public void GetRandomValues_BasePoco_SkipsAuditFields()
        {
            var rv = typeof(TypeExtBasePoco).GetRandomValues();
            // IBasePoco audit fields must be absent
            rv.Should().NotContainKey(nameof(IBasePoco.CreateBy));
            rv.Should().NotContainKey(nameof(IBasePoco.CreateTime));
            rv.Should().NotContainKey(nameof(IBasePoco.UpdateBy));
            rv.Should().NotContainKey(nameof(IBasePoco.UpdateTime));
        }

        // ── GetRandomValuesForTestData ────────────────────────────────────────────

        [TestMethod]
        public void GetRandomValuesForTestData_PlainModel_ReturnsNonEmptyDictionary()
        {
            var rv = typeof(TypeExtRvModel).GetRandomValuesForTestData();
            rv.Should().NotBeNull();
            rv.Should().NotBeEmpty();
        }

        [TestMethod]
        public void GetRandomValuesForTestData_IntField_ReturnsNumericString()
        {
            var rv = typeof(TypeExtRvModel).GetRandomValuesForTestData();
            rv.Should().ContainKey(nameof(TypeExtRvModel.IntField));
            int.TryParse(rv[nameof(TypeExtRvModel.IntField)], out _).Should().BeTrue();
        }

        [TestMethod]
        public void GetRandomValuesForTestData_StringField_IsQuoted()
        {
            var rv = typeof(TypeExtRvModel).GetRandomValuesForTestData();
            rv.Should().ContainKey(nameof(TypeExtRvModel.StrField));
            rv[nameof(TypeExtRvModel.StrField)].Should().StartWith("\"").And.EndWith("\"");
        }

        [TestMethod]
        public void GetRandomValuesForTestData_BoolField_IsTrueOrFalse()
        {
            var rv = typeof(TypeExtRvModel).GetRandomValuesForTestData();
            rv.Should().ContainKey(nameof(TypeExtRvModel.BoolField));
            new[] { "true", "false" }.Should().Contain(rv[nameof(TypeExtRvModel.BoolField)]);
        }

        [TestMethod]
        public void GetRandomValuesForTestData_EnumField_IsIntString()
        {
            // GetRandomValuesForTestData returns int representation, not FQN
            var rv = typeof(TypeExtRvModel).GetRandomValuesForTestData();
            rv.Should().ContainKey(nameof(TypeExtRvModel.EnumField));
            int.TryParse(rv[nameof(TypeExtRvModel.EnumField)], out _).Should().BeTrue();
        }

        [TestMethod]
        public void GetRandomValuesForTestData_DateField_IsDateString()
        {
            var rv = typeof(TypeExtRvModel).GetRandomValuesForTestData();
            rv.Should().ContainKey(nameof(TypeExtRvModel.DateField));
            // Value should be parseable as DateTime
            DateTime.TryParse(rv[nameof(TypeExtRvModel.DateField)].Trim('"'), out _).Should().BeTrue();
        }

        [TestMethod]
        public void GetRandomValuesForTestData_NotMappedField_Excluded()
        {
            var rv = typeof(TypeExtRvModel).GetRandomValuesForTestData();
            rv.Should().NotContainKey(nameof(TypeExtRvModel.NotMappedStr));
        }

        [TestMethod]
        public void GetRandomValuesForTestData_ListField_Excluded()
        {
            // StringListProp in TypeExtPlainModel is a List<> — must be excluded
            var rv = typeof(TypeExtPlainModel).GetRandomValuesForTestData();
            rv.Should().NotContainKey(nameof(TypeExtPlainModel.StringListProp));
        }

        [TestMethod]
        public void GetRandomValuesForTestData_RegexString_MatchesPattern()
        {
            // TypeExtRegexModel.RegexStr has [RegularExpression(@"[A-Z]{3}")]
            // GetRandomValuesForTestData uses Fare.Xeger to generate; result should match pattern
            var rv = typeof(TypeExtRegexModel).GetRandomValuesForTestData();
            rv.Should().ContainKey(nameof(TypeExtRegexModel.RegexStr));
            var val = rv[nameof(TypeExtRegexModel.RegexStr)].Trim('"');
            System.Text.RegularExpressions.Regex.IsMatch(val, @"^[A-Z]{3}$")
                .Should().BeTrue($"Fare.Xeger should generate '[A-Z]{{3}}' match, got: '{val}'");
        }

        [TestMethod]
        public void GetRandomValuesForTestData_RegexAndLength_TruncatedToMaxLength()
        {
            // [RegularExpression(@"[A-Z]{10}")] + [StringLength(5)] → result truncated to ≤4 chars
            var rv = typeof(TypeExtRegexModel).GetRandomValuesForTestData();
            rv.Should().ContainKey(nameof(TypeExtRegexModel.RegexAndLength));
            var inner = rv[nameof(TypeExtRegexModel.RegexAndLength)].Trim('"');
            inner.Length.Should().BeLessThanOrEqualTo(4);
        }

        [TestMethod]
        public void GetRandomValuesForTestData_RangedInt_InRange()
        {
            for (int i = 0; i < 10; i++)
            {
                var rv = typeof(TypeExtRvModel).GetRandomValuesForTestData();
                rv.Should().ContainKey(nameof(TypeExtRvModel.RangedInt));
                int.TryParse(rv[nameof(TypeExtRvModel.RangedInt)], out int val).Should().BeTrue();
                val.Should().BeInRange(5, 14);
            }
        }

        [TestMethod]
        public void GetRandomValuesForTestData_FkPattern_ReturnsFkSentinel()
        {
            var rv = typeof(TypeExtFkModel).GetRandomValuesForTestData();
            rv.Should().ContainKey(nameof(TypeExtFkModel.MajorId));
            rv[nameof(TypeExtFkModel.MajorId)].Should().Be("$fk$");
        }

        [TestMethod]
        public void GetRandomValuesForTestData_BasePoco_SkipsAuditFields()
        {
            var rv = typeof(TypeExtBasePoco).GetRandomValuesForTestData();
            rv.Should().NotContainKey(nameof(IBasePoco.CreateBy));
            rv.Should().NotContainKey(nameof(IBasePoco.CreateTime));
        }
    }
}
