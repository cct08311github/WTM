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
    public class BoolStringConverterTests
    {
        private readonly BoolStringConverter _converter = new();
        private readonly JsonSerializerOptions _options = new();

        // ─── Read: String tokens ──────────────────────────────────────────────

        [TestMethod]
        public void Read_StringTrue_ReturnsTrue()
        {
            var json = "\"true\"";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            var result = _converter.Read(ref reader, typeof(bool), _options);
            result.Should().BeTrue();
        }

        [TestMethod]
        public void Read_StringTrueUppercase_ReturnsTrue()
        {
            var json = "\"TRUE\"";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            var result = _converter.Read(ref reader, typeof(bool), _options);
            result.Should().BeTrue();
        }

        [TestMethod]
        public void Read_StringTrueMixedCase_ReturnsTrue()
        {
            var json = "\"True\"";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            var result = _converter.Read(ref reader, typeof(bool), _options);
            result.Should().BeTrue();
        }

        [TestMethod]
        public void Read_StringFalse_ReturnsFalse()
        {
            var json = "\"false\"";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            var result = _converter.Read(ref reader, typeof(bool), _options);
            result.Should().BeFalse();
        }

        [TestMethod]
        public void Read_StringFalseUppercase_ReturnsFalse()
        {
            var json = "\"FALSE\"";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            var result = _converter.Read(ref reader, typeof(bool), _options);
            result.Should().BeFalse();
        }

        [TestMethod]
        public void Read_UnrecognizedString_ReturnsFalse()
        {
            var json = "\"yes\"";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            var result = _converter.Read(ref reader, typeof(bool), _options);
            result.Should().BeFalse();
        }

        [TestMethod]
        public void Read_EmptyString_ReturnsFalse()
        {
            var json = "\"\"";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            var result = _converter.Read(ref reader, typeof(bool), _options);
            result.Should().BeFalse();
        }

        // ─── Read: Boolean tokens ─────────────────────────────────────────────

        [TestMethod]
        public void Read_JsonTrue_ReturnsTrue()
        {
            var json = "true";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            var result = _converter.Read(ref reader, typeof(bool), _options);
            result.Should().BeTrue();
        }

        [TestMethod]
        public void Read_JsonFalse_ReturnsFalse()
        {
            var json = "false";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            var result = _converter.Read(ref reader, typeof(bool), _options);
            result.Should().BeFalse();
        }

        // ─── Read: Other token types ──────────────────────────────────────────

        [TestMethod]
        public void Read_NumberToken_ReturnsFalse()
        {
            var json = "1";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            var result = _converter.Read(ref reader, typeof(bool), _options);
            result.Should().BeFalse();
        }

        [TestMethod]
        public void Read_NullToken_ReturnsFalse()
        {
            var json = "null";
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes);
            reader.Read();
            var result = _converter.Read(ref reader, typeof(bool), _options);
            result.Should().BeFalse();
        }

        // ─── Write ────────────────────────────────────────────────────────────

        [TestMethod]
        public void Write_True_WritesJsonTrue()
        {
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            _converter.Write(writer, true, _options);
            writer.Flush();
            var json = Encoding.UTF8.GetString(ms.ToArray());
            json.Should().Be("true");
        }

        [TestMethod]
        public void Write_False_WritesJsonFalse()
        {
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            _converter.Write(writer, false, _options);
            writer.Flush();
            var json = Encoding.UTF8.GetString(ms.ToArray());
            json.Should().Be("false");
        }

        // ─── Round-trip via JsonSerializer ────────────────────────────────────

        [TestMethod]
        public void RoundTrip_StringTrue_DeserializesToTrue()
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(_converter);
            var json = "{\"Flag\":\"true\"}";
            var result = JsonSerializer.Deserialize<BoolWrapper>(json, options);
            result!.Flag.Should().BeTrue();
        }

        [TestMethod]
        public void RoundTrip_StringFalse_DeserializesToFalse()
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(_converter);
            var json = "{\"Flag\":\"false\"}";
            var result = JsonSerializer.Deserialize<BoolWrapper>(json, options);
            result!.Flag.Should().BeFalse();
        }

        [TestMethod]
        public void RoundTrip_BoolTrue_SerializesToJsonTrue()
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(_converter);
            var obj = new BoolWrapper { Flag = true };
            var json = JsonSerializer.Serialize(obj, options);
            json.Should().Contain("true");
        }

        // Helper
        private class BoolWrapper
        {
            [System.Text.Json.Serialization.JsonConverter(typeof(BoolStringConverter))]
            public bool Flag { get; set; }
        }
    }
}
