#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core.Test.Extensions
{
    /// <summary>
    /// Unit tests for <see cref="DictionaryExtension.GetValue{T}"/> — Issue #36.
    /// Covers: key present with direct value, key present as JsonElement,
    /// key absent (default returned), null/unconvertible values.
    /// </summary>
    [TestClass]
    public class DictionaryExtensionTests
    {
        // ─── Key present — direct value ────────────────────────────────────────

        [TestMethod]
        public void GetValue_KeyExistsWithStringValue_ReturnsString()
        {
            var dict = new Dictionary<string, object> { ["name"] = "Alice" };

            var result = dict.GetValue<string>("name");

            result.Should().Be("Alice");
        }

        [TestMethod]
        public void GetValue_KeyExistsWithIntValue_ReturnsInt()
        {
            var dict = new Dictionary<string, object> { ["age"] = "30" };

            var result = dict.GetValue<int>("age");

            result.Should().Be(30);
        }

        // ─── Key present — JsonElement ─────────────────────────────────────────

        [TestMethod]
        public void GetValue_KeyExistsAsJsonElement_ReturnsConvertedValue()
        {
            // Parse a JSON document to obtain a real JsonElement
            using var doc = JsonDocument.Parse("{\"score\":99}");
            var element = doc.RootElement.GetProperty("score");

            var dict = new Dictionary<string, object> { ["score"] = element };

            // JsonElement.ToString() on a number yields "99" which ConvertValue turns to int
            var result = dict.GetValue<int>("score");

            result.Should().Be(99);
        }

        [TestMethod]
        public void GetValue_KeyExistsAsJsonStringElement_ReturnsString()
        {
            using var doc = JsonDocument.Parse("{\"city\":\"Taipei\"}");
            var element = doc.RootElement.GetProperty("city");

            var dict = new Dictionary<string, object> { ["city"] = element };

            var result = dict.GetValue<string>("city");

            result.Should().Be("Taipei");
        }

        // ─── Key absent ────────────────────────────────────────────────────────

        [TestMethod]
        public void GetValue_KeyAbsent_ReturnsDefaultInt()
        {
            var dict = new Dictionary<string, object>();

            var result = dict.GetValue<int>("missing");

            result.Should().Be(0);
        }

        [TestMethod]
        public void GetValue_KeyAbsent_ReturnsDefaultString()
        {
            var dict = new Dictionary<string, object>();

            var result = dict.GetValue<string>("missing");

            result.Should().BeNull();
        }

        // ─── Unconvertible / null coercion ─────────────────────────────────────

        [TestMethod]
        public void GetValue_KeyExistsButValueUnconvertible_ReturnsDefault()
        {
            // "abc" cannot be parsed as int → ConvertValue returns null → default(int) = 0
            var dict = new Dictionary<string, object> { ["x"] = "abc" };

            var result = dict.GetValue<int>("x");

            result.Should().Be(0);
        }
    }
}
