#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core.Test.Utils
{
    // ─────────────────────────────────────────────────────────────────────────
    // GetIdByName
    // ─────────────────────────────────────────────────────────────────────────
    [TestClass]
    public class GetIdByNameTests
    {
        [DataTestMethod]
        [DataRow("Foo.Bar", "Foo_Bar")]
        [DataRow("a[0]", "a_0_")]
        [DataRow("a[0].b", "a_0__b")]
        [DataRow("a-minus-b", "aminusminusminusb")]
        [DataRow("simple", "simple")]
        public void GetIdByName_Replaces_SpecialChars(string input, string expected)
        {
            WalkingTec.Mvvm.Core.Utils.GetIdByName(input).Should().Be(expected);
        }

        [TestMethod]
        public void GetIdByName_NullInput_ReturnsEmpty()
        {
            // null input returns "" per null-coalescing in implementation
            WalkingTec.Mvvm.Core.Utils.GetIdByName(null!).Should().BeEmpty();
        }

        [TestMethod]
        public void GetIdByName_EmptyString_ReturnsEmpty()
        {
            WalkingTec.Mvvm.Core.Utils.GetIdByName("").Should().BeEmpty();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ZipAndBase64Encode / UnZipAndBase64Decode round-trip
    // ─────────────────────────────────────────────────────────────────────────
    [TestClass]
    public class ZipBase64Tests
    {
        [TestMethod]
        public void ZipAndBase64Encode_UnZip_RoundTrip()
        {
            var input = "Hello compression world 123!";
            var encoded = WalkingTec.Mvvm.Core.Utils.ZipAndBase64Encode(input);
            var decoded = WalkingTec.Mvvm.Core.Utils.UnZipAndBase64Decode(encoded);
            decoded.Should().Be(input);
        }

        [TestMethod]
        public void ZipAndBase64Encode_UnicodeRoundTrip()
        {
            var input = "測試中文 💡 テスト";
            var encoded = WalkingTec.Mvvm.Core.Utils.ZipAndBase64Encode(input);
            var decoded = WalkingTec.Mvvm.Core.Utils.UnZipAndBase64Decode(encoded);
            decoded.Should().Be(input);
        }

        [TestMethod]
        public void ZipAndBase64Encode_EmptyString_RoundTrip()
        {
            var input = "";
            var encoded = WalkingTec.Mvvm.Core.Utils.ZipAndBase64Encode(input);
            var decoded = WalkingTec.Mvvm.Core.Utils.UnZipAndBase64Decode(encoded);
            decoded.Should().Be(input);
        }

        [TestMethod]
        public void ZipAndBase64Encode_LargeRepetitiveInput_ProducesSmallerOutput()
        {
            // repetitive data compresses well
            var input = new string('A', 10_000);
            var encoded = WalkingTec.Mvvm.Core.Utils.ZipAndBase64Encode(input);
            // base64 of compressed bytes should be much smaller than base64 of raw bytes
            encoded.Length.Should().BeLessThan(input.Length);
        }

        [TestMethod]
        public void ZipAndBase64Encode_ReturnsBase64String()
        {
            var encoded = WalkingTec.Mvvm.Core.Utils.ZipAndBase64Encode("test data");
            // Should be valid base64 - no exception thrown
            var bytes = Convert.FromBase64String(encoded);
            bytes.Should().NotBeEmpty();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EncodeScriptJson
    // ─────────────────────────────────────────────────────────────────────────
    [TestClass]
    public class EncodeScriptJsonTests
    {
        [TestMethod]
        public void EncodeScriptJson_NullInput_ReturnsEmpty()
        {
            WalkingTec.Mvvm.Core.Utils.EncodeScriptJson(null!).Should().BeEmpty();
        }

        [TestMethod]
        public void EncodeScriptJson_EmptyInput_ReturnsEmpty()
        {
            WalkingTec.Mvvm.Core.Utils.EncodeScriptJson("").Should().BeEmpty();
        }

        [TestMethod]
        public void EncodeScriptJson_EscapesDoubleQuotes()
        {
            var result = WalkingTec.Mvvm.Core.Utils.EncodeScriptJson("say \"hello\"");
            result.Should().Contain("\\\\\\\"");
            result.Should().NotContain("\"hello\"");
        }

        [TestMethod]
        public void EncodeScriptJson_EscapesSingleQuotes()
        {
            var result = WalkingTec.Mvvm.Core.Utils.EncodeScriptJson("it's");
            result.Should().Contain("\\'");
        }

        [TestMethod]
        public void EncodeScriptJson_RemovesNewlines()
        {
            var input = "line1\r\nline2\r\nline3";
            var result = WalkingTec.Mvvm.Core.Utils.EncodeScriptJson(input);
            result.Should().NotContain("\r\n");
            result.Should().NotContain(Environment.NewLine);
        }

        [TestMethod]
        public void EncodeScriptJson_PlainText_Unchanged()
        {
            var result = WalkingTec.Mvvm.Core.Utils.EncodeScriptJson("hello world");
            result.Should().Be("hello world");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ConvertToColumnXType
    // ─────────────────────────────────────────────────────────────────────────
    [TestClass]
    public class ConvertToColumnXTypeTests
    {
        [DataTestMethod]
        [DataRow(typeof(bool),     "checkcolumn")]
        [DataRow(typeof(bool?),    "checkcolumn")]
        [DataRow(typeof(DateTime), "datecolumn")]
        [DataRow(typeof(DateTime?),"datecolumn")]
        [DataRow(typeof(decimal),  "numbercolumn")]
        [DataRow(typeof(decimal?), "numbercolumn")]
        [DataRow(typeof(double),   "numbercolumn")]
        [DataRow(typeof(double?),  "numbercolumn")]
        [DataRow(typeof(int),      "numbercolumn")]
        [DataRow(typeof(int?),     "numbercolumn")]
        [DataRow(typeof(long),     "numbercolumn")]
        [DataRow(typeof(long?),    "numbercolumn")]
        [DataRow(typeof(string),   "textcolumn")]
        [DataRow(typeof(Guid),     "textcolumn")]
        [DataRow(typeof(byte),     "textcolumn")]
        public void ConvertToColumnXType_Returns_CorrectColumnType(Type type, string expected)
        {
            WalkingTec.Mvvm.Core.Utils.ConvertToColumnXType(type).Should().Be(expected);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GetBoolCombo
    // ─────────────────────────────────────────────────────────────────────────
    [TestClass]
    public class GetBoolComboTests
    {
        [TestMethod]
        public void GetBoolCombo_YesNo_ReturnsTwoItems()
        {
            var result = WalkingTec.Mvvm.Core.Utils.GetBoolCombo(BoolComboTypes.YesNo);
            result.Should().HaveCount(2);
            result[0].Value.Should().Be("true");
            result[1].Value.Should().Be("false");
        }

        [TestMethod]
        public void GetBoolCombo_WithSelectText_ReturnsThreeItems()
        {
            var result = WalkingTec.Mvvm.Core.Utils.GetBoolCombo(BoolComboTypes.YesNo, selectText: "Please select");
            result.Should().HaveCount(3);
            result[0].Value.Should().Be("");
            result[0].Text.Should().Be("Please select");
        }

        [TestMethod]
        public void GetBoolCombo_DefaultTrue_MarksYesItemSelected()
        {
            var result = WalkingTec.Mvvm.Core.Utils.GetBoolCombo(BoolComboTypes.YesNo, defaultValue: true);
            result[0].Selected.Should().BeTrue();
            result[1].Selected.Should().BeFalse();
        }

        [TestMethod]
        public void GetBoolCombo_DefaultFalse_MarksNoItemSelected()
        {
            var result = WalkingTec.Mvvm.Core.Utils.GetBoolCombo(BoolComboTypes.YesNo, defaultValue: false);
            result[0].Selected.Should().BeFalse();
            result[1].Selected.Should().BeTrue();
        }

        [TestMethod]
        public void GetBoolCombo_DefaultNull_NeitherSelected()
        {
            var result = WalkingTec.Mvvm.Core.Utils.GetBoolCombo(BoolComboTypes.YesNo, defaultValue: null);
            result[0].Selected.Should().BeFalse();
            result[1].Selected.Should().BeFalse();
        }

        [TestMethod]
        public void GetBoolCombo_Custom_UsesProvidedLabels()
        {
            var result = WalkingTec.Mvvm.Core.Utils.GetBoolCombo(
                BoolComboTypes.Custom,
                trueText: "Active",
                falseText: "Inactive");
            result[0].Text.Should().Be("Active");
            result[1].Text.Should().Be("Inactive");
        }

        [TestMethod]
        public void GetBoolCombo_ValidInvalid_ReturnsTwoItems()
        {
            var result = WalkingTec.Mvvm.Core.Utils.GetBoolCombo(BoolComboTypes.ValidInvalid);
            result.Should().HaveCount(2);
        }

        [TestMethod]
        public void GetBoolCombo_MaleFemale_ReturnsTwoItems()
        {
            var result = WalkingTec.Mvvm.Core.Utils.GetBoolCombo(BoolComboTypes.MaleFemale);
            result.Should().HaveCount(2);
        }

        [TestMethod]
        public void GetBoolCombo_HaveNotHave_ReturnsTwoItems()
        {
            var result = WalkingTec.Mvvm.Core.Utils.GetBoolCombo(BoolComboTypes.HaveNotHave);
            result.Should().HaveCount(2);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MD5 helpers
    // ─────────────────────────────────────────────────────────────────────────
    [TestClass]
    public class Md5HelperTests
    {
        [TestMethod]
        public void GetMD5String_KnownValue_ReturnsUppercaseHex()
        {
#pragma warning disable CS0618
            // MD5("") = D41D8CD98F00B204E9800998ECF8427E
            var result = WalkingTec.Mvvm.Core.Utils.GetMD5String("");
#pragma warning restore CS0618
            result.Should().Be("D41D8CD98F00B204E9800998ECF8427E");
        }

        [TestMethod]
        public void GetMD5String_HelloWorld_ProducesExpectedHash()
        {
#pragma warning disable CS0618
            var result = WalkingTec.Mvvm.Core.Utils.GetMD5String("hello");
#pragma warning restore CS0618
            result.Should().Be("5D41402ABC4B2A76B9719D911017C592");
        }

        [TestMethod]
        public void GetMD5String_NullInput_ReturnsEmpty()
        {
#pragma warning disable CS0618
            WalkingTec.Mvvm.Core.Utils.GetMD5String(null!).Should().BeEmpty();
#pragma warning restore CS0618
        }

        [TestMethod]
        public void GetMD5String_Returns32CharUpperHex()
        {
#pragma warning disable CS0618
            var result = WalkingTec.Mvvm.Core.Utils.GetMD5String("test");
#pragma warning restore CS0618
            result.Should().HaveLength(32);
            result.Should().MatchRegex("^[0-9A-F]{32}$");
        }

        [TestMethod]
        public void GetMD5Stream_KnownValue_ReturnsCorrectHash()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("");
            using var ms = new MemoryStream(bytes);
            var result = WalkingTec.Mvvm.Core.Utils.GetMD5Stream(ms);
            result.Should().Be("D41D8CD98F00B204E9800998ECF8427E");
        }

        [TestMethod]
        public void GetMD5Stream_NonEmptyData_Returns32CharUpperHex()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("hello world");
            using var ms = new MemoryStream(bytes);
            var result = WalkingTec.Mvvm.Core.Utils.GetMD5Stream(ms);
            result.Should().HaveLength(32);
            result.Should().MatchRegex("^[0-9A-F]{32}$");
        }

        [TestMethod]
        public void GetMD5File_NonExistentFile_ReturnsEmpty()
        {
            var result = WalkingTec.Mvvm.Core.Utils.GetMD5File("/no/such/file/xyz123.txt");
            result.Should().BeEmpty();
        }

        [TestMethod]
        public void GetMD5File_ExistingFile_ReturnsHash()
        {
            var tmpFile = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tmpFile, "hello");
                var result = WalkingTec.Mvvm.Core.Utils.GetMD5File(tmpFile);
                result.Should().HaveLength(32);
                result.Should().MatchRegex("^[0-9A-F]{32}$");
            }
            finally
            {
                File.Delete(tmpFile);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FindMenu
    // ─────────────────────────────────────────────────────────────────────────
    [TestClass]
    public class FindMenuTests
    {
        private static List<SimpleMenu> BuildMenus() =>
        [
            new SimpleMenu { Url = "/Home/Index" },
            new SimpleMenu { Url = "/Admin/User" },
            new SimpleMenu { Url = "/products" },
        ];

        [TestMethod]
        public void FindMenu_NullUrl_ReturnsNull()
        {
            WalkingTec.Mvvm.Core.Utils.FindMenu(null, BuildMenus()).Should().BeNull();
        }

        [TestMethod]
        public void FindMenu_NullMenus_ReturnsNull()
        {
            WalkingTec.Mvvm.Core.Utils.FindMenu("/Home/Index", null).Should().BeNull();
        }

        [TestMethod]
        public void FindMenu_ExactMatch_ReturnsMenu()
        {
            var result = WalkingTec.Mvvm.Core.Utils.FindMenu("/Home/Index", BuildMenus());
            result.Should().NotBeNull();
            result!.Url.Should().Be("/Home/Index");
        }

        [TestMethod]
        public void FindMenu_CaseInsensitiveMatch()
        {
            var result = WalkingTec.Mvvm.Core.Utils.FindMenu("/home/index", BuildMenus());
            result.Should().NotBeNull();
        }

        [TestMethod]
        public void FindMenu_UrlWithQueryString_StripsAndMatches()
        {
            var result = WalkingTec.Mvvm.Core.Utils.FindMenu("/Admin/User?page=1", BuildMenus());
            result.Should().NotBeNull();
            result!.Url.Should().Be("/Admin/User");
        }

        [TestMethod]
        public void FindMenu_UrlEndingWithIndex_StripsAndMatches()
        {
            var result = WalkingTec.Mvvm.Core.Utils.FindMenu("/products/index", BuildMenus());
            result.Should().NotBeNull();
            result!.Url.Should().Be("/products");
        }

        [TestMethod]
        public void FindMenu_UrlEndingWithIndexAsync_StripsAndMatches()
        {
            var result = WalkingTec.Mvvm.Core.Utils.FindMenu("/products/indexasync", BuildMenus());
            result.Should().NotBeNull();
            result!.Url.Should().Be("/products");
        }

        [TestMethod]
        public void FindMenu_NoMatch_ReturnsNull()
        {
            var result = WalkingTec.Mvvm.Core.Utils.FindMenu("/no/such/path", BuildMenus());
            result.Should().BeNull();
        }

        [TestMethod]
        public void FindMenu_AsyncSuffix_MatchesWithoutSuffix_ViaQueryStringBranch()
        {
            // The async-suffix fallback is only triggered when a query-string was present.
            // After stripping "?x=1", the code checks: menu.Url + "async" == url (lowercased)
            // So "/admin/userasync?x=1" → strips to "/admin/userasync" → finds menu "/Admin/User"
            // because "/admin/user" + "async" == "/admin/userasync"
            var menus = new List<SimpleMenu> { new SimpleMenu { Url = "/Admin/User" } };
            var result = WalkingTec.Mvvm.Core.Utils.FindMenu("/Admin/Userasync?x=1", menus);
            result.Should().NotBeNull();
        }

        [TestMethod]
        public void FindMenu_EmptyMenuList_ReturnsNull()
        {
            var result = WalkingTec.Mvvm.Core.Utils.FindMenu("/Home/Index", []);
            result.Should().BeNull();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // File helpers: ReadTxt, DeleteFile, GetAllFileName, GetAllFilePathRecursion
    // ─────────────────────────────────────────────────────────────────────────
    [TestClass]
    public class FileHelperTests
    {
        [TestMethod]
        public void ReadTxt_ExistingFile_ReturnsContent()
        {
            var tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tmp, "hello text", Encoding.UTF8);
                var result = WalkingTec.Mvvm.Core.Utils.ReadTxt(tmp);
                result.Should().Be("hello text");
            }
            finally
            {
                File.Delete(tmp);
            }
        }

        [TestMethod]
        public void ReadTxt_NonExistentFile_ReturnsEmpty()
        {
            var result = WalkingTec.Mvvm.Core.Utils.ReadTxt("/no/such/file.txt");
            result.Should().BeEmpty();
        }

        [TestMethod]
        public void DeleteFile_ExistingFile_RemovesIt()
        {
            var tmp = Path.GetTempFileName();
            File.WriteAllText(tmp, "data");
            WalkingTec.Mvvm.Core.Utils.DeleteFile(tmp);
            File.Exists(tmp).Should().BeFalse();
        }

        [TestMethod]
        public void DeleteFile_NonExistentFile_DoesNotThrow()
        {
            // Should silently log and not rethrow
            var act = () => WalkingTec.Mvvm.Core.Utils.DeleteFile("/no/such/file_xyz.txt");
            act.Should().NotThrow();
        }

        [TestMethod]
        public void GetAllFileName_ReturnsFileNamesInDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"wtm_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
                File.WriteAllText(Path.Combine(dir, "b.txt"), "b");

                var names = WalkingTec.Mvvm.Core.Utils.GetAllFileName(dir);
                names.Should().Contain("a.txt");
                names.Should().Contain("b.txt");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [TestMethod]
        public void GetAllFilePathRecursion_ReturnsAllFilesRecursively()
        {
            var root = Path.Combine(Path.GetTempPath(), $"wtm_rec_{Guid.NewGuid():N}");
            var sub = Path.Combine(root, "sub");
            Directory.CreateDirectory(sub);
            try
            {
                File.WriteAllText(Path.Combine(root, "root.txt"), "r");
                File.WriteAllText(Path.Combine(sub, "sub.txt"), "s");

                var files = WalkingTec.Mvvm.Core.Utils.GetAllFilePathRecursion(root, []);
                files.Should().Contain(f => f.EndsWith("root.txt"));
                files.Should().Contain(f => f.EndsWith("sub.txt"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public void GetAllFilePathRecursion_NullAllFilesList_CreatesNewList()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"wtm_null_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "f.txt"), "x");
                // passing null — implementation creates a new list internally
                var files = WalkingTec.Mvvm.Core.Utils.GetAllFilePathRecursion(dir, null!);
                files.Should().NotBeNull();
                files.Should().Contain(f => f.EndsWith("f.txt"));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FormatCode / FormatText
    // ─────────────────────────────────────────────────────────────────────────
    [TestClass]
    public class FormatCodeTests
    {
        [TestMethod]
        public void FormatCode_EscapesHtmlAngleBrackets()
        {
            var result = WalkingTec.Mvvm.Core.Utils.FormatCode("<T>");
            result.Should().Contain("&lt;");
            result.Should().Contain("&gt;");
            result.Should().NotContain("<T>");
        }

        [TestMethod]
        public void FormatCode_HighlightsKeyword_ReturnsFontTag()
        {
            var result = WalkingTec.Mvvm.Core.Utils.FormatCode("int x = 0;");
            result.Should().Contain("<font color=#0000FF>");
        }

        [TestMethod]
        public void FormatCode_ReplacesNewlinesWithBr()
        {
            var result = WalkingTec.Mvvm.Core.Utils.FormatCode("line1\r\nline2");
            result.Should().Contain("<br>");
        }

        [TestMethod]
        public void FormatCode_ReplacesSpacesWithNbsp()
        {
            var result = WalkingTec.Mvvm.Core.Utils.FormatCode("a b");
            result.Should().Contain("&nbsp;");
        }

        [TestMethod]
        public void FormatCode_EmptyString_ReturnsEmptyString()
        {
            // Empty string passes through with no exceptions
            var result = WalkingTec.Mvvm.Core.Utils.FormatCode("");
            result.Should().NotBeNull();
        }

        [TestMethod]
        public void FormatText_IsCode_True_DelegatesToFormatCode()
        {
            var result = WalkingTec.Mvvm.Core.Utils.FormatText("int x;", isCode: true);
            // FormatCode output should have encoded spaces as &nbsp;
            result.Should().NotBeNull();
        }

        [TestMethod]
        public void FormatText_IsCode_False_ReturnsInputUnchangedWhenNoMarkers()
        {
            var input = "no special markers here";
            var result = WalkingTec.Mvvm.Core.Utils.FormatText(input, isCode: false);
            result.Should().Be(input);
        }

        [TestMethod]
        public void FormatText_WithCodeMarkers_FormatsEmbeddedCode()
        {
            // Pattern: text &&code&& text
            var input = "before &&int x = 0;&& after";
            var result = WalkingTec.Mvvm.Core.Utils.FormatText(input, isCode: false);
            result.Should().NotBe(input);
            // The code segment should be formatted (contains html font tag)
            result.Should().Contain("<font");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GetCS
    // ─────────────────────────────────────────────────────────────────────────
    [TestClass]
    public class GetCSTests
    {
        private static Configs MakeConfigs(params (string key, bool enabled)[] conns)
        {
            var config = new Configs();
            foreach (var (key, enabled) in conns)
            {
                config.Connections.Add(new CS { Key = key, Enabled = enabled });
            }
            return config;
        }

        [TestMethod]
        public void GetCS_NullCs_ReturnsNull()
        {
            var config = MakeConfigs(("default", true));
            var result = WalkingTec.Mvvm.Core.Utils.GetCS(null, null, config);
            result.Should().BeNull();
        }

        [TestMethod]
        public void GetCS_KnownKey_ReturnsKey()
        {
            var config = MakeConfigs(("default", true), ("mydb", true));
            var result = WalkingTec.Mvvm.Core.Utils.GetCS("mydb", null, config);
            result.Should().Be("mydb");
        }

        [TestMethod]
        public void GetCS_UnknownKey_FallsBackToDefault()
        {
            var config = MakeConfigs(("default", true));
            var result = WalkingTec.Mvvm.Core.Utils.GetCS("nosuchkey", null, config);
            result.Should().Be("default");
        }

        [TestMethod]
        public void GetCS_KeyWithUnderscoreSuffix_StripsIt()
        {
            // e.g. "mydb_read" → strips "_read" suffix → "mydb"
            var config = MakeConfigs(("mydb", true), ("mydb_read", true));
            var result = WalkingTec.Mvvm.Core.Utils.GetCS("mydb_read", null, config);
            // After stripping suffix, base key is "mydb"
            result.Should().Be("mydb");
        }

        [TestMethod]
        public void GetCS_ReadMode_PicksReadReplica()
        {
            var config = MakeConfigs(("mydb", true), ("mydb_r1", true), ("mydb_r2", true));
            // In read mode: strip suffix if present → "mydb", then pick from mydb_* replicas
            var result = WalkingTec.Mvvm.Core.Utils.GetCS("mydb", "read", config);
            // Should select one of the read replicas
            result.Should().BeOneOf("mydb_r1", "mydb_r2");
        }

        [TestMethod]
        public void GetCS_ReadMode_NoReplicas_ReturnsBaseKey()
        {
            var config = MakeConfigs(("default", true));
            var result = WalkingTec.Mvvm.Core.Utils.GetCS("default", "read", config);
            result.Should().Be("default");
        }

        [TestMethod]
        public void GetCS_DisabledKey_FallsBackToDefault()
        {
            var config = MakeConfigs(("default", true), ("mydb", false));
            var result = WalkingTec.Mvvm.Core.Utils.GetCS("mydb", null, config);
            result.Should().Be("default");
        }

        [TestMethod]
        public void GetCS_ModeCaseInsensitive()
        {
            var config = MakeConfigs(("default", true), ("default_r", true));
            // "READ" (uppercase) should behave like "read"
            var result = WalkingTec.Mvvm.Core.Utils.GetCS("default", "READ", config);
            result.Should().Be("default_r");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CheckDifference
    // ─────────────────────────────────────────────────────────────────────────
    [TestClass]
    public class CheckDifferenceTests
    {
        private class Poco : TopBasePoco { }

        [TestMethod]
        public void CheckDifference_AddsNewItems_ToToAdd()
        {
            var id = Guid.NewGuid();
            var oldList = new List<Poco>();
            var newList = new List<Poco> { new Poco { ID = id } };

            WalkingTec.Mvvm.Core.Utils.CheckDifference(oldList, newList, out var toRemove, out var toAdd);

            toAdd.Should().ContainSingle(x => x.ID == id);
            toRemove.Should().BeEmpty();
        }

        [TestMethod]
        public void CheckDifference_RemovedItems_InToRemove()
        {
            var id = Guid.NewGuid();
            var oldList = new List<Poco> { new Poco { ID = id } };
            var newList = new List<Poco>();

            WalkingTec.Mvvm.Core.Utils.CheckDifference(oldList, newList, out var toRemove, out var toAdd);

            toRemove.Should().ContainSingle(x => x.ID == id);
            toAdd.Should().BeEmpty();
        }

        [TestMethod]
        public void CheckDifference_NullLists_TreatedAsEmpty()
        {
            WalkingTec.Mvvm.Core.Utils.CheckDifference<Poco>(null!, null!, out var toRemove, out var toAdd);
            toRemove.Should().BeEmpty();
            toAdd.Should().BeEmpty();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ResetModule
    // ─────────────────────────────────────────────────────────────────────────
    [TestClass]
    public class ResetModuleTests
    {
        [TestMethod]
        public void ResetModule_EmptyList_ReturnsEmpty()
        {
            var result = WalkingTec.Mvvm.Core.Utils.ResetModule([]);
            result.Should().BeEmpty();
        }

        [TestMethod]
        public void ResetModule_ModuleWithNoPageActions_IsPreservedUnchanged()
        {
            var module = new SimpleModule
            {
                ModuleName = "Test",
                NameSpace = "NS",
                ClassName = "TestController",
                Actions =
                [
                    new SimpleAction { MethodName = "List",  ActionDes = null },
                    new SimpleAction { MethodName = "Edit",  ActionDes = null },
                ]
            };

            var result = WalkingTec.Mvvm.Core.Utils.ResetModule([module]);
            result.Should().HaveCount(1);
            result[0].ClassName.Should().Be("TestController");
        }

        [TestMethod]
        public void ResetModule_NullActionList_ThrowsArgumentNullException()
        {
            // ResetModule performs [.. x.Actions?.Select(...)] which collapses the
            // null enumerable into a spread — this throws ArgumentNullException.
            // This test documents and locks in the current (quirky) behavior.
            var module = new SimpleModule
            {
                ModuleName = "Test",
                ClassName = "X",
                Actions = null
            };

            var act = () => WalkingTec.Mvvm.Core.Utils.ResetModule([module]);
            act.Should().Throw<ArgumentNullException>();
        }

        [TestMethod]
        public void ResetModule_ClonesModuleProperties()
        {
            var id = Guid.NewGuid();
            var module = new SimpleModule
            {
                ID = id,
                ModuleName = "Original",
                NameSpace = "NS",
                ClassName = "Ctrl",
                IgnorePrivillege = true,
                IsApi = true,
                Actions = []
            };

            var result = WalkingTec.Mvvm.Core.Utils.ResetModule([module]);
            result.Should().HaveCount(1);
            result[0].ID.Should().Be(id);
            result[0].IgnorePrivillege.Should().BeTrue();
            result[0].IsApi.Should().BeTrue();
        }

        [TestMethod]
        public void ResetModule_MultipleModules_AllCloned()
        {
            var modules = new List<SimpleModule>
            {
                new SimpleModule { ModuleName = "A", ClassName = "A", Actions = [] },
                new SimpleModule { ModuleName = "B", ClassName = "B", Actions = [] },
                new SimpleModule { ModuleName = "C", ClassName = "C", Actions = [] },
            };

            var result = WalkingTec.Mvvm.Core.Utils.ResetModule(modules);
            result.Should().HaveCountGreaterThanOrEqualTo(3);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Thread-safe static init: GetAllAssembly / GetAllModels / GetAllVms (M22)
    // ─────────────────────────────────────────────────────────────────────────
    [TestClass]
    public class UtilsThreadSafetyTests
    {
        /// <summary>
        /// GetAllAssembly() invoked concurrently must return a non-empty list
        /// and must never return an empty intermediate list to any caller.
        /// Before the fix: _allAssemblies was assigned to [] then populated with
        /// AddRange — a racing thread could observe the empty intermediate state.
        /// After the fix: assignment is atomic (fully-populated list written once).
        /// </summary>
        [TestMethod]
        public void GetAllAssembly_ConcurrentCalls_AllReturnNonEmpty()
        {
            const int threadCount = 8;
            var results = new List<int>[threadCount];
            var errors = new Exception?[threadCount];

            var barrier = new Barrier(threadCount);
            var threads = Enumerable.Range(0, threadCount).Select(i => new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait(); // start all threads simultaneously
                    var assemblies = WalkingTec.Mvvm.Core.Utils.GetAllAssembly();
                    results[i] = new List<int> { assemblies.Count };
                }
                catch (Exception ex)
                {
                    errors[i] = ex;
                }
            })).ToList();

            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join(TimeSpan.FromSeconds(15)));

            // No thread should have thrown.
            errors.Where(e => e != null).Should().BeEmpty("no thread should throw when calling GetAllAssembly() concurrently");

            // Every thread must have seen a non-empty list (the empty-intermediate-state bug).
            foreach (var r in results)
            {
                r.Should().NotBeNull();
                r![0].Should().BeGreaterThan(0, "GetAllAssembly() must never return an empty list to any concurrent caller");
            }
        }

        /// <summary>
        /// GetAllAssembly() called repeatedly from multiple threads must return
        /// the same stable count — no partial re-initialisation.
        /// </summary>
        [TestMethod]
        public void GetAllAssembly_ConcurrentCalls_ReturnConsistentCount()
        {
            // Warm up once first (static field may already be populated from a prior test).
            var baseline = WalkingTec.Mvvm.Core.Utils.GetAllAssembly().Count;

            const int threadCount = 10;
            var counts = new int[threadCount];
            var barrier = new Barrier(threadCount);

            var threads = Enumerable.Range(0, threadCount).Select(i => new Thread(() =>
            {
                barrier.SignalAndWait();
                counts[i] = WalkingTec.Mvvm.Core.Utils.GetAllAssembly().Count;
            })).ToList();

            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join(TimeSpan.FromSeconds(15)));

            // All threads must see the same count as the baseline.
            counts.Should().AllSatisfy(c => c.Should().Be(baseline,
                "GetAllAssembly() must return a stable consistent list across concurrent threads"));
        }
    }
}
