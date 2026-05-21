#nullable enable
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WalkingTec.Mvvm.Core.Test.Json
{
    /// <summary>
    /// Unit tests for <see cref="JsonStringConverter"/> — Issue #36.
    /// Covers all branches of Read and Write.
    /// </summary>
    [TestClass]
    public class StringConverterTests
    {
        private static readonly JsonSerializerOptions _opts = new();
        private readonly JsonStringConverter _converter = new();

        // ─── Read ──────────────────────────────────────────────────────────────

        [TestMethod]
        public void Read_NullToken_ReturnsNull()
        {
            var bytes = Encoding.UTF8.GetBytes("null");
            var reader = new Utf8JsonReader(bytes);
            reader.Read(); // advance to Null token

            var result = _converter.Read(ref reader, typeof(string), _opts);

            result.Should().BeNull();
        }

        [TestMethod]
        public void Read_StringToken_ReturnsStringValue()
        {
            var bytes = Encoding.UTF8.GetBytes("\"hello\"");
            var reader = new Utf8JsonReader(bytes);
            reader.Read(); // advance to String token

            var result = _converter.Read(ref reader, typeof(string), _opts);

            result.Should().Be("hello");
        }

        [TestMethod]
        public void Read_OtherToken_ReturnsNull()
        {
            // Use a number token — neither Null nor String
            var bytes = Encoding.UTF8.GetBytes("42");
            var reader = new Utf8JsonReader(bytes);
            reader.Read(); // advance to Number token

            var result = _converter.Read(ref reader, typeof(string), _opts);

            result.Should().BeNull();
        }

        // ─── Write ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void Write_NullValue_WritesJsonNull()
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                _converter.Write(w, null!, _opts);
            }

            Encoding.UTF8.GetString(ms.ToArray()).Should().Be("null");
        }

        [TestMethod]
        public void Write_StringValue_WritesQuotedString()
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                _converter.Write(w, "hello", _opts);
            }

            Encoding.UTF8.GetString(ms.ToArray()).Should().Be("\"hello\"");
        }

        [TestMethod]
        public void Write_EmptyString_WritesEmptyQuotedString()
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                _converter.Write(w, "", _opts);
            }

            Encoding.UTF8.GetString(ms.ToArray()).Should().Be("\"\"");
        }
    }
}
