using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// What /_Framework/UpdateModelProperty never writes whatever the reference lists, and how the text of the request becomes the value
    /// of a property.
    /// </summary>
    [TestClass]
    public class ModelPropertyUpdateRulesTest
    {
        public enum Kind { A, B }

        [Flags]
        public enum Bits { None = 0, One = 1, Two = 2 }

        public class Plain : BasePoco
        {
            public string Name { get; set; }
            public int Count { get; set; }
            public int? Limit { get; set; }
            public decimal Price { get; set; }
            public double Ratio { get; set; }
            public bool Flag { get; set; }
            public DateTime When { get; set; }
            public DateTimeOffset? At { get; set; }
            public Guid Token { get; set; }
            public Kind Kind { get; set; }
            public Bits Bits { get; set; }
            public Plain Parent { get; set; }
            public List<Plain> Children { get; set; }
            public string[] Tags { get; set; }
            public string ReadOnly { get; }
            public string PrivateSet { get; private set; }
            public static string Shared { get; set; }
            [NotMapped]
            public string Computed { get; set; }
            [CanNotEdit]
            public string Fixed { get; set; }
            public string this[int i] { get => null; set { } }
        }

        public class Tenant : PersistPoco, ITenant
        {
            public string TenantCode { get; set; }
            public string Name { get; set; }
        }

        public class NotATenant : TopBasePoco
        {
            public string TenantCode { get; set; }
            public bool IsValid { get; set; }
        }

        [TestMethod]
        [DataRow("Name")]
        [DataRow("Count")]
        [DataRow("Limit")]
        [DataRow("Price")]
        [DataRow("Ratio")]
        [DataRow("Flag")]
        [DataRow("When")]
        [DataRow("At")]
        [DataRow("Token")]
        [DataRow("Kind")]
        [DataRow("Bits")]
        public void FindWritableProperty_AcceptsScalarProperties(string name)
        {
            Assert.IsNotNull(ModelPropertyUpdateRules.FindWritableProperty(typeof(Plain), name));
        }

        [TestMethod]
        [DataRow("ID")]
        [DataRow("CreateTime")]
        [DataRow("CreateBy")]
        [DataRow("UpdateTime")]
        [DataRow("UpdateBy")]
        [DataRow("Parent")]
        [DataRow("Children")]
        [DataRow("Tags")]
        [DataRow("ReadOnly")]
        [DataRow("PrivateSet")]
        [DataRow("Shared")]
        [DataRow("Computed")]
        [DataRow("Fixed")]
        [DataRow("Item")]
        [DataRow("name")]
        [DataRow("NAME")]
        [DataRow("Nothing")]
        [DataRow("")]
        [DataRow(null)]
        public void FindWritableProperty_RefusesKeyStampsAndMembersThatAreNotEditableScalars(string name)
        {
            Assert.IsNull(ModelPropertyUpdateRules.FindWritableProperty(typeof(Plain), name));
        }

        [TestMethod]
        public void FindWritableProperty_RefusesTheTenantCodeAndTheValidityFlagOfTheRecordsThatHaveThem()
        {
            Assert.IsNull(ModelPropertyUpdateRules.FindWritableProperty(typeof(Tenant), "TenantCode"));
            Assert.IsNull(ModelPropertyUpdateRules.FindWritableProperty(typeof(Tenant), "IsValid"));
            Assert.IsNotNull(ModelPropertyUpdateRules.FindWritableProperty(typeof(Tenant), "Name"));
            // a record that does not implement the interfaces: the names are ordinary there
            Assert.IsNotNull(ModelPropertyUpdateRules.FindWritableProperty(typeof(NotATenant), "TenantCode"));
            Assert.IsNotNull(ModelPropertyUpdateRules.FindWritableProperty(typeof(NotATenant), "IsValid"));
        }

        [TestMethod]
        [DataRow(typeof(int), "42", 42)]
        [DataRow(typeof(int), " -7 ", -7)]
        [DataRow(typeof(int?), "5", 5)]
        [DataRow(typeof(long), "9000000000", 9000000000L)]
        [DataRow(typeof(bool), "true", true)]
        [DataRow(typeof(bool?), "False", false)]
        [DataRow(typeof(Kind), "B", Kind.B)]
        [DataRow(typeof(Kind), "b", Kind.B)]
        [DataRow(typeof(Kind), "1", Kind.B)]
        [DataRow(typeof(Bits), "3", Bits.One | Bits.Two)]
        [DataRow(typeof(Bits), "One, Two", Bits.One | Bits.Two)]
        public void TryConvert_ParsesValuesOfTheType(Type type, string text, object expected)
        {
            Assert.IsTrue(ModelPropertyUpdateRules.TryConvert(type, text, out var value));
            Assert.AreEqual(expected, value);
        }

        [TestMethod]
        public void TryConvert_ParsesNumbersWithTheInvariantCulture_AndDatesInIsoFormat()
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.IsTrue(ModelPropertyUpdateRules.TryConvert(typeof(decimal), "1234.5", out var price));
                Assert.AreEqual(1234.5m, price);
                Assert.IsFalse(ModelPropertyUpdateRules.TryConvert(typeof(decimal), "1.234,5", out _));
                Assert.IsFalse(ModelPropertyUpdateRules.TryConvert(typeof(decimal), "1,234.5", out _), "No thousands separators.");
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }
            Assert.IsTrue(ModelPropertyUpdateRules.TryConvert(typeof(double), "1.5e3", out var ratio));
            Assert.AreEqual(1500d, ratio);
            Assert.IsTrue(ModelPropertyUpdateRules.TryConvert(typeof(DateTime), "2026-03-04T05:06:07", out var when));
            Assert.AreEqual(new DateTime(2026, 3, 4, 5, 6, 7), when);
            Assert.IsTrue(ModelPropertyUpdateRules.TryConvert(typeof(DateTime), "2026-03-04", out var day));
            Assert.AreEqual(new DateTime(2026, 3, 4), day);
            Assert.IsTrue(ModelPropertyUpdateRules.TryConvert(typeof(DateTimeOffset?), "2026-03-04T05:06:07+08:00", out var at));
            Assert.AreEqual(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(8)), at);
            var id = Guid.NewGuid();
            Assert.IsTrue(ModelPropertyUpdateRules.TryConvert(typeof(Guid), id.ToString(), out var guid));
            Assert.AreEqual(id, guid);
        }

        [TestMethod]
        [DataRow(typeof(int), "abc")]
        [DataRow(typeof(int), "1.5")]
        [DataRow(typeof(int), "99999999999")]
        [DataRow(typeof(int), "")]
        [DataRow(typeof(int?), "x")]
        [DataRow(typeof(byte), "256")]
        [DataRow(typeof(decimal), "1e3")]
        [DataRow(typeof(double), "NaN")]
        [DataRow(typeof(double), "Infinity")]
        [DataRow(typeof(float), "abc")]
        [DataRow(typeof(bool), "maybe")]
        [DataRow(typeof(bool), "")]
        [DataRow(typeof(DateTime), "not a date")]
        [DataRow(typeof(DateTime), "")]
        [DataRow(typeof(DateTimeOffset), "13/45/2026")]
        [DataRow(typeof(Guid), "not-a-guid")]
        [DataRow(typeof(Guid), "")]
        [DataRow(typeof(Kind), "C")]
        [DataRow(typeof(Kind), "7")]
        [DataRow(typeof(Kind), "")]
        [DataRow(typeof(object), "x")]
        public void TryConvert_RefusesTextThatIsNotAValueOfTheType_InsteadOfUsingADefault(Type type, string text)
        {
            Assert.IsFalse(ModelPropertyUpdateRules.TryConvert(type, text, out var value));
            Assert.IsNull(value);
        }

        [TestMethod]
        public void TryConvert_EmptyTextIsNullForAStringOrANullableType()
        {
            Assert.IsTrue(ModelPropertyUpdateRules.TryConvert(typeof(string), "", out var text));
            Assert.IsNull(text);
            Assert.IsTrue(ModelPropertyUpdateRules.TryConvert(typeof(int?), "", out var number));
            Assert.IsNull(number);
            Assert.IsTrue(ModelPropertyUpdateRules.TryConvert(typeof(DateTime?), null, out var date));
            Assert.IsNull(date);
        }

        [TestMethod]
        public void TryConvert_StringKeepsItsTextExceptThatBackslashesBecomeSlashes()
        {
            Assert.IsTrue(ModelPropertyUpdateRules.TryConvert(typeof(string), "  a\\b  ", out var text));
            Assert.AreEqual("  a/b  ", text);
        }
    }
}
