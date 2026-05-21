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
    /// Unit tests for <see cref="DateTimeConverter"/> — Issue #36.
    /// Covers string-token parse, native-date-token fallback, and Write formatting.
    /// </summary>
    [TestClass]
    public class DateTimeConverterTests
    {
        private static readonly JsonSerializerOptions _opts = new();
        private readonly DateTimeConverter _converter = new();

        // ─── Read ──────────────────────────────────────────────────────────────

        [TestMethod]
        public void Read_StringToken_ParsesDateFromString()
        {
            var bytes = Encoding.UTF8.GetBytes("\"2026-05-20\"");
            var reader = new Utf8JsonReader(bytes);
            reader.Read();

            var result = _converter.Read(ref reader, typeof(DateTime), _opts);

            result.Should().Be(new DateTime(2026, 5, 20));
        }

        [TestMethod]
        public void Read_StringToken_ParsesDateTimeFromString()
        {
            var bytes = Encoding.UTF8.GetBytes("\"2026-05-20T13:45:00\"");
            var reader = new Utf8JsonReader(bytes);
            reader.Read();

            var result = _converter.Read(ref reader, typeof(DateTime), _opts);

            result.Should().Be(new DateTime(2026, 5, 20, 13, 45, 0));
        }

        [TestMethod]
        public void Read_UnparsableStringToken_ThrowsFormatException()
        {
            // TryParse("not-a-date") fails → falls through to reader.GetDateTime()
            // which throws FormatException ("The JSON value is not in a supported
            // DateTime format") for a String token that is not a valid ISO-8601 date.
            // ref locals cannot be captured in lambdas, so we call directly and catch.
            var bytes = Encoding.UTF8.GetBytes("\"not-a-date\"");
            var reader = new Utf8JsonReader(bytes);
            reader.Read();

            bool threw = false;
            try
            {
                _converter.Read(ref reader, typeof(DateTime), _opts);
            }
            catch (FormatException)
            {
                threw = true;
            }

            threw.Should().BeTrue("reader.GetDateTime() throws FormatException on a non-ISO String token");
        }

        // ─── Write ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void Write_DateOnly_FormatsAsDateWithMidnight()
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                _converter.Write(w, new DateTime(2026, 5, 20), _opts);
            }

            Encoding.UTF8.GetString(ms.ToArray()).Should().Be("\"2026-05-20 00:00:00\"");
        }

        [TestMethod]
        public void Write_DateTimeWithTime_FormatsWithTime()
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                _converter.Write(w, new DateTime(2026, 5, 20, 13, 45, 7), _opts);
            }

            Encoding.UTF8.GetString(ms.ToArray()).Should().Be("\"2026-05-20 13:45:07\"");
        }
    }
}
