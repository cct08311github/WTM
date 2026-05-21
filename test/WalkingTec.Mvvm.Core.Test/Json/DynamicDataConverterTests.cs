#nullable enable
using System.Collections.Generic;
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
    public class DynamicDataConverterTests
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            Converters = { new DynamicDataConverter() }
        };

        private static DynamicData? Deserialize(string json)
        {
            return JsonSerializer.Deserialize<DynamicData>(json, Options);
        }

        private static string Serialize(DynamicData data)
        {
            return JsonSerializer.Serialize(data, Options);
        }

        // ─── Read ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void Read_EmptyObject_ReturnsEmptyFields()
        {
            var result = Deserialize("{}");
            result.Should().NotBeNull();
            result!.Fields.Should().BeEmpty();
        }

        [TestMethod]
        public void Read_StringProperty_CapturedCorrectly()
        {
            var result = Deserialize("{\"name\":\"Alice\"}");
            result!.Fields.Should().ContainKey("name");
        }

        [TestMethod]
        public void Read_NumberProperty_CapturedCorrectly()
        {
            var result = Deserialize("{\"age\":30}");
            result!.Fields.Should().ContainKey("age");
        }

        [TestMethod]
        public void Read_BooleanProperty_CapturedCorrectly()
        {
            var result = Deserialize("{\"active\":true}");
            result!.Fields.Should().ContainKey("active");
        }

        [TestMethod]
        public void Read_NullProperty_CapturedCorrectly()
        {
            var result = Deserialize("{\"value\":null}");
            result!.Fields.Should().ContainKey("value");
        }

        [TestMethod]
        public void Read_NestedObject_CapturedAsDynamicData()
        {
            var result = Deserialize("{\"inner\":{\"x\":1}}");
            result!.Fields.Should().ContainKey("inner");
            result.Fields["inner"].Should().BeOfType<DynamicData>();
        }

        [TestMethod]
        public void Read_ArrayOfPrimitives_CapturedAsList()
        {
            var result = Deserialize("{\"items\":[1,2,3]}");
            result!.Fields.Should().ContainKey("items");
            result.Fields["items"].Should().BeOfType<List<object>>();
        }

        [TestMethod]
        public void Read_ArrayOfObjects_CapturedAsList()
        {
            var result = Deserialize("{\"items\":[{\"id\":1},{\"id\":2}]}");
            result!.Fields.Should().ContainKey("items");
            var list = result.Fields["items"] as List<object>;
            list.Should().HaveCount(2);
        }

        [TestMethod]
        public void Read_FalseProperty_CapturedCorrectly()
        {
            var result = Deserialize("{\"flag\":false}");
            result!.Fields.Should().ContainKey("flag");
        }

        [TestMethod]
        public void Read_MultipleProperties_AllCaptured()
        {
            var result = Deserialize("{\"x\":1,\"y\":2,\"z\":3}");
            result.Should().NotBeNull();
            result!.Fields.Should().ContainKey("x")
                .And.ContainKey("y")
                .And.ContainKey("z");
        }

        // ─── Write ────────────────────────────────────────────────────────────

        [TestMethod]
        public void Write_EmptyFields_WritesEmptyObject()
        {
            var data = new DynamicData();
            data.Fields = new Dictionary<string, object>();
            var json = Serialize(data);
            json.Should().Be("{}");
        }

        [TestMethod]
        public void Write_NullDynamicData_WritesNullValue()
        {
            // null value object
            var data = new DynamicData();
            data.Fields = new Dictionary<string, object> { { "key", (object)null! } };

            // With default options (DefaultIgnoreCondition = Never), null should be written
            var opts = new JsonSerializerOptions
            {
                Converters = { new DynamicDataConverter() },
                DefaultIgnoreCondition = JsonIgnoreCondition.Never
            };
            var json = JsonSerializer.Serialize(data, opts);
            json.Should().Contain("key");
            json.Should().Contain("null");
        }

        [TestMethod]
        public void Write_NullValueWithWhenWritingNull_SkipsNullFields()
        {
            var data = new DynamicData();
            data.Fields = new Dictionary<string, object> { { "key", (object)null! } };

            var opts = new JsonSerializerOptions
            {
                Converters = { new DynamicDataConverter() },
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            var json = JsonSerializer.Serialize(data, opts);
            // null fields should be skipped (DefaultIgnoreCondition.Never is what triggers writing null)
            // With WhenWritingNull the null check skips writing, so key absent
            json.Should().Be("{}");
        }

        [TestMethod]
        public void Write_StringField_ProducesCorrectJson()
        {
            var data = new DynamicData();
            data.Fields = new Dictionary<string, object> { { "name", "test" } };
            var json = Serialize(data);
            json.Should().Contain("\"name\"").And.Contain("\"test\"");
        }

        [TestMethod]
        public void Write_NullDynamicDataObject_WritesNullLiteral()
        {
            // null object (not null Fields)
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            var converter = new DynamicDataConverter();
            var opts = new JsonSerializerOptions
            {
                Converters = { converter }
            };
            converter.Write(writer, null!, opts);
            writer.Flush();
            var result = Encoding.UTF8.GetString(ms.ToArray());
            result.Should().Be("null");
        }

        [TestMethod]
        public void Write_NullFields_WritesNullLiteral()
        {
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            var converter = new DynamicDataConverter();
            var data = new DynamicData();
            data.Fields = null!;
            converter.Write(writer, data, new JsonSerializerOptions());
            writer.Flush();
            var result = Encoding.UTF8.GetString(ms.ToArray());
            result.Should().Be("null");
        }
    }
}
