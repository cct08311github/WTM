using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    /// <summary>
    /// Tests for PropertyHelper.ConvertValue() — the core ETL type conversion
    /// engine used during Excel import entity assignment.
    /// </summary>
    [TestClass]
    public class ConvertValueTest
    {
        // ─── String Conversion ───────────────────────────────────────

        [TestMethod]
        public void ConvertValue_String_TrimsWhitespace()
        {
            var result = "  hello  ".ConvertValue(typeof(string));
            Assert.AreEqual("hello", result);
        }

        [TestMethod]
        public void ConvertValue_String_NullReturnsNull()
        {
            var result = ((object?)null).ConvertValue(typeof(string));
            Assert.IsNull(result);
        }

        // ─── Integer Conversion ──────────────────────────────────────

        [TestMethod]
        public void ConvertValue_StringToInt_ValidNumber()
        {
            var result = "42".ConvertValue(typeof(int));
            Assert.AreEqual(42, result);
        }

        [TestMethod]
        public void ConvertValue_StringToInt_InvalidReturnsNull()
        {
            // Convert.ChangeType throws, caught silently → returns null
            var result = "not_a_number".ConvertValue(typeof(int));
            Assert.IsNull(result);
        }

        [TestMethod]
        public void ConvertValue_StringToLong_ValidNumber()
        {
            var result = "9999999999".ConvertValue(typeof(long));
            Assert.AreEqual(9999999999L, result);
        }

        // ─── Decimal / Float Conversion ──────────────────────────────

        [TestMethod]
        public void ConvertValue_StringToDecimal_ValidDecimal()
        {
            var result = "19.99".ConvertValue(typeof(decimal));
            Assert.AreEqual(19.99m, result);
        }

        [TestMethod]
        public void ConvertValue_StringToDouble_ValidDouble()
        {
            var result = "3.14".ConvertValue(typeof(double));
            Assert.AreEqual(3.14, result);
        }

        [TestMethod]
        public void ConvertValue_StringToDecimal_InvalidReturnsNull()
        {
            var result = "abc".ConvertValue(typeof(decimal));
            Assert.IsNull(result);
        }

        // ─── Boolean Conversion ──────────────────────────────────────

        [TestMethod]
        public void ConvertValue_StringToBool_True()
        {
            var result = "True".ConvertValue(typeof(bool));
            Assert.AreEqual(true, result);
        }

        [TestMethod]
        public void ConvertValue_StringToBool_False()
        {
            var result = "False".ConvertValue(typeof(bool));
            Assert.AreEqual(false, result);
        }

        // ─── DateTime Conversion ─────────────────────────────────────

        [TestMethod]
        public void ConvertValue_StringToDateTime_ValidDate()
        {
            var result = "2026-03-12".ConvertValue(typeof(DateTime));
            Assert.IsInstanceOfType(result, typeof(DateTime));
            Assert.AreEqual(new DateTime(2026, 3, 12), result);
        }

        [TestMethod]
        public void ConvertValue_StringToDateTime_InvalidReturnsNull()
        {
            var result = "not-a-date".ConvertValue(typeof(DateTime));
            Assert.IsNull(result);
        }

        // ─── Guid Conversion ─────────────────────────────────────────

        [TestMethod]
        public void ConvertValue_StringToGuid_ValidGuid()
        {
            var guid = Guid.NewGuid();
            var result = guid.ToString().ConvertValue(typeof(Guid));
            Assert.AreEqual(guid, result);
        }

        [TestMethod]
        public void ConvertValue_StringToGuid_InvalidReturnsEmpty()
        {
            var result = "not-a-guid".ConvertValue(typeof(Guid));
            Assert.AreEqual(Guid.Empty, result);
        }

        [TestMethod]
        public void ConvertValue_NullToGuid_ReturnsEmpty()
        {
            var result = ((object?)null).ConvertValue(typeof(Guid));
            Assert.AreEqual(Guid.Empty, result);
        }

        // ─── Enum Conversion ─────────────────────────────────────────

        [TestMethod]
        public void ConvertValue_StringToEnum_ValidMemberName()
        {
            var result = "Male".ConvertValue(typeof(GenderEnum));
            Assert.AreEqual(GenderEnum.Male, result);
        }

        [TestMethod]
        public void ConvertValue_StringToEnum_AnotherMember()
        {
            var result = "Female".ConvertValue(typeof(GenderEnum));
            Assert.AreEqual(GenderEnum.Female, result);
        }

        [TestMethod]
        public void ConvertValue_StringToEnum_NullReturnsNull()
        {
            var result = ((object?)null).ConvertValue(typeof(GenderEnum));
            Assert.IsNull(result);
        }

        [TestMethod]
        public void ConvertValue_StringToEnum_EmptyReturnsNull()
        {
            var result = "".ConvertValue(typeof(GenderEnum));
            Assert.IsNull(result);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentException))]
        public void ConvertValue_StringToEnum_InvalidThrows()
        {
            // Enum.Parse throws ArgumentException for unknown names
            "InvalidValue".ConvertValue(typeof(GenderEnum));
        }

        // ─── Nullable<T> Conversion ──────────────────────────────────

        [TestMethod]
        public void ConvertValue_NullableInt_ValidNumber()
        {
            var result = "42".ConvertValue(typeof(int?));
            Assert.AreEqual(42, result);
        }

        [TestMethod]
        public void ConvertValue_NullableInt_NullReturnsNull()
        {
            var result = ((object?)null).ConvertValue(typeof(int?));
            Assert.IsNull(result);
        }

        [TestMethod]
        public void ConvertValue_NullableInt_EmptyStringReturnsNull()
        {
            var result = "".ConvertValue(typeof(int?));
            Assert.IsNull(result);
        }

        [TestMethod]
        public void ConvertValue_NullableDateTime_ValidDate()
        {
            var result = "2026-01-15".ConvertValue(typeof(DateTime?));
            Assert.IsNotNull(result);
            Assert.AreEqual(new DateTime(2026, 1, 15), result);
        }

        [TestMethod]
        public void ConvertValue_NullableDateTime_NullReturnsNull()
        {
            var result = ((object?)null).ConvertValue(typeof(DateTime?));
            Assert.IsNull(result);
        }

        [TestMethod]
        public void ConvertValue_NullableEnum_ValidMember()
        {
            var result = "Female".ConvertValue(typeof(GenderEnum?));
            Assert.AreEqual(GenderEnum.Female, result);
        }

        [TestMethod]
        public void ConvertValue_NullableEnum_EmptyReturnsNull()
        {
            var result = "".ConvertValue(typeof(GenderEnum?));
            Assert.IsNull(result);
        }

        // ─── DateRange Conversion ────────────────────────────────────

        [TestMethod]
        public void ConvertValue_StringToDateRange_ValidRange()
        {
            var result = "2026-01-01 ~ 2026-03-12".ConvertValue(typeof(DateRange));
            Assert.IsNotNull(result);
            Assert.IsInstanceOfType(result, typeof(DateRange));
            var range = (DateRange)result;
            Assert.AreEqual(new DateTime(2026, 1, 1), range.GetStartTime());
        }

        [TestMethod]
        public void ConvertValue_StringToDateRange_InvalidReturnsDefault()
        {
            var result = "invalid".ConvertValue(typeof(DateRange));
            Assert.IsNotNull(result);
            // Should return DateRange.Default (Today)
            Assert.IsInstanceOfType(result, typeof(DateRange));
        }

        [TestMethod]
        public void ConvertValue_NullToDateRange_ReturnsDefault()
        {
            var result = ((object?)null).ConvertValue(typeof(DateRange));
            Assert.IsNotNull(result);
            Assert.IsInstanceOfType(result, typeof(DateRange));
        }

        // ─── Backtick List Conversion ────────────────────────────────

        [TestMethod]
        public void ConvertValue_BacktickList_ParsesIntList()
        {
            var result = "`1,2,3`".ConvertValue(typeof(List<int>));
            Assert.IsNotNull(result);
            var list = result as List<int>;
            Assert.IsNotNull(list);
            Assert.AreEqual(3, list!.Count);
            Assert.AreEqual(1, list[0]);
            Assert.AreEqual(2, list[1]);
            Assert.AreEqual(3, list[2]);
        }

        [TestMethod]
        public void ConvertValue_BacktickList_ParsesStringList()
        {
            var result = "`a,b,c`".ConvertValue(typeof(List<string>));
            Assert.IsNotNull(result);
            var list = result as List<string>;
            Assert.IsNotNull(list);
            Assert.AreEqual(3, list!.Count);
            Assert.AreEqual("a", list[0]);
        }

        [TestMethod]
        public void ConvertValue_BacktickList_EmptyInnerReturnsNull()
        {
            var result = "``".ConvertValue(typeof(List<int>));
            Assert.IsNull(result);
        }

        // ─── Edge Cases ─────────────────────────────────────────────

        [TestMethod]
        public void ConvertValue_IntToInt_PassThrough()
        {
            var result = ((object)42).ConvertValue(typeof(int));
            Assert.AreEqual(42, result);
        }

        [TestMethod]
        public void ConvertValue_NullToNonNullableInt_ReturnsNull()
        {
            // Convert.ChangeType(null, int) throws, caught silently
            var result = ((object?)null).ConvertValue(typeof(int));
            Assert.IsNull(result);
        }
    }
}
