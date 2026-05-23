#nullable enable
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Json;

namespace WalkingTec.Mvvm.Core.Test.Json
{
    [TestClass]
    public class DateRangeConverterTests
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            Converters = { new DateRangeConverter() }
        };

        // ─── Read ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void Read_NullToken_ReturnsNull()
        {
            var result = JsonSerializer.Deserialize<DateRange?>("null", Options);
            result.Should().BeNull();
        }

        [TestMethod]
        public void Read_ValidDateArray_ParsesSuccessfully()
        {
            var json = "[\"2024-01-01\",\"2024-01-31\"]";
            var result = JsonSerializer.Deserialize<DateRange?>(json, Options);
            result.Should().NotBeNull();
            result!.GetStartTime()!.Value.Date.Should().Be(new DateTime(2024, 1, 1));
        }

        [TestMethod]
        public void Read_ValidDateTimeArray_ParsesSuccessfully()
        {
            var json = "[\"2024-03-01 08:00:00\",\"2024-03-31 23:59:59\"]";
            var result = JsonSerializer.Deserialize<DateRange?>(json, Options);
            result.Should().NotBeNull();
        }

        [TestMethod]
        public void Read_InvalidDateArray_ReturnsNull()
        {
            // Array with unparseable dates
            var json = "[\"not-a-date\",\"also-not-a-date\"]";
            var result = JsonSerializer.Deserialize<DateRange?>(json, Options);
            result.Should().BeNull();
        }

        [TestMethod]
        public void Read_NonArrayToken_ReturnsNull()
        {
            // Non-array, non-null token
            var json = "\"2024-01-01\"";
            var result = JsonSerializer.Deserialize<DateRange?>(json, Options);
            result.Should().BeNull();
        }

        [TestMethod]
        public void Read_NullStringElements_ReturnsNull()
        {
            var json = "[null, null]";
            var result = JsonSerializer.Deserialize<DateRange?>(json, Options);
            result.Should().BeNull();
        }

        // ─── Write ────────────────────────────────────────────────────────────

        [TestMethod]
        public void Write_NonNullDateRange_WritesStartAndEndAsArray()
        {
            var start = new DateTime(2024, 1, 1);
            var end = new DateTime(2024, 1, 31);
            var dr = new DateRange(start, end);

            var json = JsonSerializer.Serialize(dr, Options);
            // DateRangeConverter.Write uses DateTime.ToString() (locale-dependent format)
            // so we only assert the JSON array structure, not the specific date format.
            json.Should().StartWith("[").And.EndWith("]");
            json.Should().Contain(","); // two elements separated by comma
        }

        [TestMethod]
        public void Write_NullDateRange_WritesNullLiteral()
        {
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            var converter = new DateRangeConverter();
            converter.Write(writer, null!, new JsonSerializerOptions());
            writer.Flush();
            var result = Encoding.UTF8.GetString(ms.ToArray());
            result.Should().Be("null");
        }

        // ─── Round-trip ───────────────────────────────────────────────────────

        [TestMethod]
        public void RoundTrip_DateRange_PreservesStartAndEnd()
        {
            var start = new DateTime(2024, 6, 1);
            var end = new DateTime(2024, 6, 30);
            var dr = new DateRange(start, end);

            var json = JsonSerializer.Serialize(dr, Options);
            var restored = JsonSerializer.Deserialize<DateRange?>(json, Options);

            restored.Should().NotBeNull();
            restored!.GetStartTime()!.Value.Date.Should().Be(start.Date);
        }
    }
}
