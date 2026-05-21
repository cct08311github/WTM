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
    /// <summary>
    /// Unit tests for <see cref="WalkingTec.Mvvm.Core.Json.TypeConverter"/> — Issue #36.
    /// Both Read and Write are trivial stubs (always-null / always-write-null).
    /// </summary>
    [TestClass]
    public class TypeConverterTests
    {
        private static readonly JsonSerializerOptions _opts = new();
        private readonly TypeConverter _converter = new();

        // ─── Read ──────────────────────────────────────────────────────────────

        [TestMethod]
        public void Read_AnyToken_AlwaysReturnsNull()
        {
            var bytes = Encoding.UTF8.GetBytes("\"System.String\"");
            var reader = new Utf8JsonReader(bytes);
            reader.Read();

            var result = _converter.Read(ref reader, typeof(Type), _opts);

            result.Should().BeNull();
        }

        // ─── Write ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void Write_AnyType_WritesJsonNull()
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                _converter.Write(w, typeof(string), _opts);
            }

            Encoding.UTF8.GetString(ms.ToArray()).Should().Be("null");
        }
    }
}
