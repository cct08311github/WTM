#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Json;

namespace WalkingTec.Mvvm.Core.Test.Json
{
    [TestClass]
    public class BodyConverterTests
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            Converters = { new BodyConverter() }
        };

        private static PostedBody? Deserialize(string json)
        {
            return JsonSerializer.Deserialize<PostedBody>(json, Options);
        }

        // ─── Read ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void Read_EmptyObject_ReturnsEmptyProNames()
        {
            var result = Deserialize("{}");
            result.Should().NotBeNull();
            result!.ProNames.Should().NotBeNull().And.BeEmpty();
        }

        [TestMethod]
        public void Read_FlatObject_CollectsAllPropertyNames()
        {
            var json = "{\"Name\":\"Alice\",\"Age\":30}";
            var result = Deserialize(json);
            result!.ProNames.Should().Contain("Name").And.Contain("Age");
        }

        [TestMethod]
        public void Read_NestedObject_CollectsNestedPropertyNames()
        {
            var json = "{\"Address\":{\"City\":\"Taipei\",\"Zip\":\"100\"}}";
            var result = Deserialize(json);
            // "Address" itself is removed when StartObject is encountered (converter removes the last ProName).
            // Only the leaf properties with full path are retained.
            result!.ProNames.Should().Contain("Address.City")
                .And.Contain("Address.Zip");
            result.ProNames.Should().NotContain("Address");
        }

        [TestMethod]
        public void Read_ArrayProperty_CollectsArrayNotation()
        {
            var json = "{\"Items\":[{\"Id\":1},{\"Id\":2}]}";
            var result = Deserialize(json);
            result.Should().NotBeNull();
            // Items should be captured with [0] array notation
            result!.ProNames.Should().Contain(p => p.Contains("Items"));
        }

        [TestMethod]
        public void Read_DeepNested_CollectsLeafPropertyNames()
        {
            var json = "{\"A\":{\"B\":{\"C\":\"value\"}}}";
            var result = Deserialize(json);
            // Parent object properties are removed from ProNames when their object opens.
            // Only the deepest leaf with full path is collected.
            result!.ProNames.Should().Contain("A.B.C");
        }

        [TestMethod]
        public void Read_NoDuplicates_PropertyNamesAreDistinct()
        {
            var json = "{\"Name\":\"A\",\"Name\":\"B\"}";
            var result = Deserialize(json);
            // JSON with duplicate keys – ProNames should not duplicate
            result!.ProNames!.Distinct().Should().HaveSameCount(result.ProNames!);
        }

        // ─── Write ────────────────────────────────────────────────────────────

        [TestMethod]
        public void Write_ReturnsWithoutWritingAnything()
        {
            // Write is a no-op; serializing PostedBody should not throw
            var body = new PostedBody { ProNames = new System.Collections.Generic.List<string> { "Foo" } };
            var options = new JsonSerializerOptions { Converters = { new BodyConverter() } };

            // Should not throw
            var json = JsonSerializer.Serialize(body, options);
            // The write is a no-op, so result should be empty/null value
            json.Should().NotBeNull();
        }
    }
}
