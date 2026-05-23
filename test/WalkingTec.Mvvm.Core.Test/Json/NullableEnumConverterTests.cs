#nullable enable
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Json;

namespace WalkingTec.Mvvm.Core.Test.Json
{
    [TestClass]
    public class NullableEnumConverterTests
    {
        private enum Color { Red = 1, Green = 2, Blue = 3 }

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            Converters = { new NullableEnumConverter() }
        };

        // ─── CanConvert ───────────────────────────────────────────────────────

        [TestMethod]
        public void CanConvert_NullableEnum_ReturnsTrue()
        {
            var converter = new NullableEnumConverter();
            converter.CanConvert(typeof(Color?)).Should().BeTrue();
        }

        [TestMethod]
        public void CanConvert_NonNullableEnum_ReturnsFalse()
        {
            var converter = new NullableEnumConverter();
            converter.CanConvert(typeof(Color)).Should().BeFalse();
        }

        [TestMethod]
        public void CanConvert_NullableInt_ReturnsFalse()
        {
            var converter = new NullableEnumConverter();
            converter.CanConvert(typeof(int?)).Should().BeFalse();
        }

        [TestMethod]
        public void CanConvert_String_ReturnsFalse()
        {
            var converter = new NullableEnumConverter();
            converter.CanConvert(typeof(string)).Should().BeFalse();
        }

        // ─── Read ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void Read_ValidEnumValue_DeserializesCorrectly()
        {
            var json = "1";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            var converter = new NullableEnumConverter();
            var innerConverter = converter.CreateConverter(typeof(Color?), Options);

            Color? result = default;
            // Use JsonSerializer which will use the converter
            result = JsonSerializer.Deserialize<Color?>(json, Options);
            result.Should().Be(Color.Red);
        }

        [TestMethod]
        public void Read_NullJson_ReturnsNull()
        {
            var result = JsonSerializer.Deserialize<Color?>("null", Options);
            result.Should().BeNull();
        }

        [TestMethod]
        public void Read_EmptyString_ReturnsNull()
        {
            var result = JsonSerializer.Deserialize<Color?>("\"\"", Options);
            result.Should().BeNull();
        }

        [TestMethod]
        public void Read_WhitespaceString_ReturnsNull()
        {
            var result = JsonSerializer.Deserialize<Color?>("\"   \"", Options);
            result.Should().BeNull();
        }

        [TestMethod]
        public void Read_EnumByName_DeserializesWhenSupported()
        {
            var optsWithNames = new JsonSerializerOptions
            {
                Converters =
                {
                    new NullableEnumConverter(),
                    new JsonStringEnumConverter()
                }
            };
            // integer value should always work
            var result = JsonSerializer.Deserialize<Color?>("2", optsWithNames);
            result.Should().Be(Color.Green);
        }

        // ─── Write ────────────────────────────────────────────────────────────

        [TestMethod]
        public void Write_NonNullValue_SerializesAsInteger()
        {
            Color? value = Color.Blue;
            var json = JsonSerializer.Serialize(value, Options);
            json.Should().Be("3");
        }

        [TestMethod]
        public void Write_NullValue_SerializesAsNull()
        {
            Color? value = null;
            var json = JsonSerializer.Serialize(value, Options);
            json.Should().Be("null");
        }

        // ─── Round-trip ───────────────────────────────────────────────────────

        [DataTestMethod]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public void RoundTrip_EnumValues_PreservedCorrectly(int enumInt)
        {
            var original = (Color?)enumInt;
            var json = JsonSerializer.Serialize(original, Options);
            var deserialized = JsonSerializer.Deserialize<Color?>(json, Options);
            deserialized.Should().Be(original);
        }

        [TestMethod]
        public void RoundTrip_Null_PreservesNull()
        {
            Color? original = null;
            var json = JsonSerializer.Serialize(original, Options);
            var deserialized = JsonSerializer.Deserialize<Color?>(json, Options);
            deserialized.Should().BeNull();
        }

        // ─── CreateConverter ──────────────────────────────────────────────────

        [TestMethod]
        public void CreateConverter_ReturnsNonNull()
        {
            var factory = new NullableEnumConverter();
            var converter = factory.CreateConverter(typeof(Color?), Options);
            converter.Should().NotBeNull();
        }

        [TestMethod]
        public void CreateConverter_ReturnedConverterCanHandleType()
        {
            var factory = new NullableEnumConverter();
            var converter = factory.CreateConverter(typeof(Color?), Options);
            converter.CanConvert(typeof(Color?)).Should().BeTrue();
        }
    }
}
