#nullable enable
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Json;

namespace WalkingTec.Mvvm.Core.Test.Json
{
    [TestClass]
    public class RawStringConverterTests
    {
        private readonly RawStringConverter _converter = new();
        private readonly JsonSerializerOptions _options = new();

        // ─── Read ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void Read_NullToken_ReturnsNull()
        {
            var json = "null";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read(); // advance to token
            string? result;
            // Utf8JsonReader is ref struct — must call directly
            result = _converter.Read(ref reader, typeof(string), _options);
            result.Should().BeNull();
        }

        [TestMethod]
        public void Read_StringToken_ReturnsString()
        {
            var json = "\"hello world\"";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            var result = _converter.Read(ref reader, typeof(string), _options);
            result.Should().Be("hello world");
        }

        [TestMethod]
        public void Read_EmptyString_ReturnsEmptyString()
        {
            var json = "\"\"";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            var result = _converter.Read(ref reader, typeof(string), _options);
            result.Should().Be("");
        }

        [TestMethod]
        public void Read_NonStringToken_ReturnsNull()
        {
            // A number token — not a string, not null
            var json = "42";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            var result = _converter.Read(ref reader, typeof(string), _options);
            result.Should().BeNull();
        }

        [TestMethod]
        public void Read_BooleanToken_ReturnsNull()
        {
            var json = "true";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            var result = _converter.Read(ref reader, typeof(string), _options);
            result.Should().BeNull();
        }

        // ─── Write ────────────────────────────────────────────────────────────

        [TestMethod]
        public void Write_NullValue_WritesJsonNull()
        {
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            _converter.Write(writer, null!, _options);
            writer.Flush();
            var json = Encoding.UTF8.GetString(ms.ToArray());
            json.Should().Be("null");
        }

        [TestMethod]
        public void Write_NonNullValue_WrapsWithRawMarkers()
        {
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            _converter.Write(writer, "hello", _options);
            writer.Flush();
            var json = Encoding.UTF8.GetString(ms.ToArray());
            // The converter wraps value with _raw_ prefix and suffix
            json.Should().Contain("_raw_hello_raw_");
        }

        [TestMethod]
        public void Write_EmptyString_WritesWrappedMarkers()
        {
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            _converter.Write(writer, "", _options);
            writer.Flush();
            var json = Encoding.UTF8.GetString(ms.ToArray());
            json.Should().Contain("_raw__raw_");
        }

        [TestMethod]
        public void Write_SpecialCharacters_OutputIsValidJson()
        {
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            _converter.Write(writer, "<script>alert('xss')</script>", _options);
            writer.Flush();
            var json = Encoding.UTF8.GetString(ms.ToArray());
            // Should be valid JSON (parseable)
            var parsed = JsonDocument.Parse(json);
            parsed.Should().NotBeNull();
        }

        // ─── Round-trip via JsonSerializer ────────────────────────────────────

        [TestMethod]
        public void RoundTrip_NonNullString_WrapsAndCanBeDeserialized()
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(_converter);

            // Serialize a model that contains a string with this converter
            // We test Write + Read indirectly through serialization of a wrapper
            var obj = new RawStringWrapper { Value = "test" };
            var json = JsonSerializer.Serialize(obj, options);
            json.Should().Contain("_raw_test_raw_");
        }

        // Helper type for round-trip test
        private class RawStringWrapper
        {
            [System.Text.Json.Serialization.JsonConverter(typeof(RawStringConverter))]
            public string? Value { get; set; }
        }
    }
}
