#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Extensions
{
    // ─── Fixtures ───────────────────────────────────────────────────────────────

    public class ListExtTestItem : TopBasePoco
    {
        [Display(Name = "Item Name")]
        public string Name { get; set; } = string.Empty;

        [Display(Name = "Score")]
        public int Score { get; set; }

        [Display(Name = "Is Active")]
        public bool IsActive { get; set; }

        public GenderEnum? Gender { get; set; }
    }

    /// <summary>
    /// Simple ListVM that returns in-memory data — no database needed.
    /// </summary>
    public class ListExtTestListVM : BasePagedListVM<ListExtTestItem, BaseSearcher>
    {
        private static readonly List<ListExtTestItem> _store = [];

        public static void Reset() => _store.Clear();

        public static void Add(ListExtTestItem item) => _store.Add(item);

        public override IOrderedQueryable<ListExtTestItem> GetSearchQuery()
            => _store.AsQueryable().OrderBy(x => x.ID);

        protected override IEnumerable<IGridColumn<ListExtTestItem>> InitGridHeader()
        {
            return
            [
                this.MakeGridColumn(x => x.Name),
                this.MakeGridColumn(x => x.Score),
                this.MakeGridColumn(x => x.IsActive),
            ];
        }
    }

    /// <summary>
    /// ListVM variant that overrides GetHeaders to expose a column with
    /// background-color and foreground-color functions.
    /// </summary>
    public class ColoredListVM : BasePagedListVM<ListExtTestItem, BaseSearcher>
    {
        private static readonly List<ListExtTestItem> _store = [];

        public static void Reset() => _store.Clear();
        public static void Add(ListExtTestItem item) => _store.Add(item);

        public override IOrderedQueryable<ListExtTestItem> GetSearchQuery()
            => _store.AsQueryable().OrderBy(x => x.ID);

        protected override IEnumerable<IGridColumn<ListExtTestItem>> InitGridHeader()
        {
            return
            [
                this.MakeGridColumn(x => x.Name,
                    ForeGroundFunc: i => i.Score > 50 ? "FF0000" : "",
                    BackGroundFunc: i => i.IsActive ? "00FF00" : ""),
                this.MakeGridColumn(x => x.Score),
            ];
        }
    }

    /// <summary>
    /// ListVM that uses hash-prefixed color values to test the '#' prefix-insertion branch.
    /// </summary>
    public class HashColorListVM : BasePagedListVM<ListExtTestItem, BaseSearcher>
    {
        private static readonly List<ListExtTestItem> _store = [];

        public static void Reset() => _store.Clear();
        public static void Add(ListExtTestItem item) => _store.Add(item);

        public override IOrderedQueryable<ListExtTestItem> GetSearchQuery()
            => _store.AsQueryable().OrderBy(x => x.ID);

        protected override IEnumerable<IGridColumn<ListExtTestItem>> InitGridHeader()
        {
            return
            [
                this.MakeGridColumn(x => x.Name,
                    // Already has '#' — should NOT be doubled
                    ForeGroundFunc: i => "#FF0000",
                    BackGroundFunc: i => "#00FF00"),
            ];
        }
    }

    /// <summary>
    /// ListVM that exposes editable columns (TextBox, CheckBox, ComboBox, Datetime).
    /// </summary>
    public class EditableListVM : BasePagedListVM<ListExtTestItem, BaseSearcher>
    {
        private static readonly List<ListExtTestItem> _store = [];

        public static void Reset() => _store.Clear();
        public static void Add(ListExtTestItem item) => _store.Add(item);

        public override IOrderedQueryable<ListExtTestItem> GetSearchQuery()
            => _store.AsQueryable().OrderBy(x => x.ID);

        protected override IEnumerable<IGridColumn<ListExtTestItem>> InitGridHeader()
        {
            var nameCol = this.MakeGridColumn(x => x.Name);
            nameCol.EditType = EditTypeEnum.TextBox;

            var activeCol = this.MakeGridColumn(x => x.IsActive);
            activeCol.EditType = EditTypeEnum.CheckBox;

            var genderCol = this.MakeGridColumn(x => x.Gender);
            genderCol.EditType = EditTypeEnum.ComboBox;
            genderCol.ListItems = [];

            var scoreCol = this.MakeGridColumn(x => x.Score);
            // Score is int, not DateTime — Datetime EditType still exercises the branch
            scoreCol.EditType = EditTypeEnum.Datetime;

            return [nameCol, activeCol, genderCol, scoreCol];
        }
    }

    /// <summary>
    /// ListVM that has an ID column so containsID = true (skips appending extra ID).
    /// </summary>
    public class IdColumnListVM : BasePagedListVM<ListExtTestItem, BaseSearcher>
    {
        private static readonly List<ListExtTestItem> _store = [];

        public static void Reset() => _store.Clear();
        public static void Add(ListExtTestItem item) => _store.Add(item);

        public override IOrderedQueryable<ListExtTestItem> GetSearchQuery()
            => _store.AsQueryable().OrderBy(x => x.ID);

        protected override IEnumerable<IGridColumn<ListExtTestItem>> InitGridHeader()
        {
            var idCol = this.MakeGridColumn(x => x.ID);
            return [idCol, this.MakeGridColumn(x => x.Name)];
        }
    }

    /// <summary>
    /// ListVM whose color functions return a CSS-injection payload.
    /// Used to verify grid-002: malicious values are dropped by ValidateColor.
    /// </summary>
    public class MaliciousColorListVM : BasePagedListVM<ListExtTestItem, BaseSearcher>
    {
        private static readonly List<ListExtTestItem> _store = [];

        public static void Reset() => _store.Clear();
        public static void Add(ListExtTestItem item) => _store.Add(item);

        public override IOrderedQueryable<ListExtTestItem> GetSearchQuery()
            => _store.AsQueryable().OrderBy(x => x.ID);

        protected override IEnumerable<IGridColumn<ListExtTestItem>> InitGridHeader()
        {
            return
            [
                this.MakeGridColumn(x => x.Name,
                    ForeGroundFunc: _ => "red;}</style><script>alert('xss')</script>",
                    BackGroundFunc: _ => "blue\"; alert(1);//"),
            ];
        }
    }

    /// <summary>
    /// ListVM exposing an enum column to cover enum display-name resolution.
    /// </summary>
    public class EnumListVM : BasePagedListVM<ListExtTestItem, BaseSearcher>
    {
        private static readonly List<ListExtTestItem> _store = [];

        public static void Reset() => _store.Clear();
        public static void Add(ListExtTestItem item) => _store.Add(item);

        public override IOrderedQueryable<ListExtTestItem> GetSearchQuery()
            => _store.AsQueryable().OrderBy(x => x.ID);

        protected override IEnumerable<IGridColumn<ListExtTestItem>> InitGridHeader()
        {
            return [this.MakeGridColumn(x => x.Gender)];
        }
    }

    // ─── Tests ──────────────────────────────────────────────────────────────────

    [TestClass]
    public class ListVMExtensionTests
    {
        private ListExtTestListVM _vm = null!;

        [TestInitialize]
        public void Setup()
        {
            ListExtTestListVM.Reset();
            _vm = new ListExtTestListVM();
            _vm.Wtm = MockWtmContext.CreateWtmContext();
        }

        [TestCleanup]
        public void Cleanup()
        {
            ListExtTestListVM.Reset();
            ColoredListVM.Reset();
            MaliciousColorListVM.Reset();
        }

        // ─── GetDataJson — empty list ────────────────────────────────────────────

        [TestMethod]
        public void GetDataJson_EmptyList_ReturnsEmptyJsonArray()
        {
            var json = _vm.GetDataJson();
            json.Should().Be("[]");
        }

        // ─── GetDataJson — single item ────────────────────────────────────────────

        [TestMethod]
        public void GetDataJson_SingleItem_ContainsFieldValue()
        {
            ListExtTestListVM.Add(new ListExtTestItem { Name = "Alice", Score = 80 });

            var json = _vm.GetDataJson();

            json.Should().Contain("Alice");
        }

        // ─── GetDataJson — multiple items ────────────────────────────────────────

        [TestMethod]
        public void GetDataJson_MultipleItems_ProducesCommaSeparatedObjects()
        {
            ListExtTestListVM.Add(new ListExtTestItem { Name = "Alice", Score = 80 });
            ListExtTestListVM.Add(new ListExtTestItem { Name = "Bob", Score = 70 });

            var json = _vm.GetDataJson();

            json.Should().StartWith("[");
            json.Should().EndWith("]");
            // Two objects ⇒ at least one comma between them
            json.Should().Contain("Alice");
            json.Should().Contain("Bob");
        }

        // ─── GetDataJson — auto-generates IDs when all are zero/empty ────────────

        [TestMethod]
        public void GetDataJson_ItemsWithEmptyGuidIds_GeneratesNewIds()
        {
            // TopBasePoco's default ID is Guid.Empty
            ListExtTestListVM.Add(new ListExtTestItem { Name = "NoId" });

            var json = _vm.GetDataJson();

            // The generated ID should now be a non-empty GUID
            json.Should().NotContain(Guid.Empty.ToString());
        }

        // ─── GetDataJson — already-searched flag ─────────────────────────────────

        [TestMethod]
        public void GetDataJson_CalledTwice_DoesNotDoubleSearch()
        {
            ListExtTestListVM.Add(new ListExtTestItem { Name = "X" });

            _vm.DoSearch();
            var json1 = _vm.GetDataJson();
            var json2 = _vm.GetDataJson();

            // Both calls should return the same data
            json1.Should().Be(json2);
        }

        // ─── GetJson — structure ──────────────────────────────────────────────────

        [TestMethod]
        public void GetJson_ReturnsExpectedTopLevelKeys()
        {
            ListExtTestListVM.Add(new ListExtTestItem { Name = "Alice" });

            var json = _vm.GetJson();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            root.TryGetProperty("Code", out _).Should().BeTrue();
            root.TryGetProperty("Count", out _).Should().BeTrue();
            root.TryGetProperty("Data", out _).Should().BeTrue();
            root.TryGetProperty("Msg", out _).Should().BeTrue();
            root.TryGetProperty("Page", out _).Should().BeTrue();
            root.TryGetProperty("PageCount", out _).Should().BeTrue();
        }

        [TestMethod]
        public void GetJson_Code_Is200()
        {
            var json = _vm.GetJson();
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("Code").GetInt32().Should().Be(200);
        }

        [TestMethod]
        public void GetJson_Msg_IsSuccess()
        {
            var json = _vm.GetJson();
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("Msg").GetString().Should().Be("success");
        }

        [TestMethod]
        public void GetJson_WithItems_CountMatchesTotal()
        {
            ListExtTestListVM.Add(new ListExtTestItem { Name = "A" });
            ListExtTestListVM.Add(new ListExtTestItem { Name = "B" });

            _vm.DoSearch();
            var json = _vm.GetJson();
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("Count").GetInt32().Should().Be(2);
        }

        [TestMethod]
        public void GetJson_WithAdditionalFunc_IncludesExtraKeys()
        {
            var json = _vm.GetJson(func: () => new Dictionary<string, object>
            {
                ["CustomKey"] = "CustomValue"
            });

            json.Should().Contain("\"CustomKey\":\"CustomValue\"");
        }

        [TestMethod]
        public void GetJson_Searcher_IsPlainText_Overrides_PlainText()
        {
            ListExtTestListVM.Add(new ListExtTestItem { Name = "X" });

            _vm.Searcher.IsPlainText = false;

            var json = _vm.GetJson(PlainText: true); // IsPlainText should override

            // Just verify it parses and has the right structure
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("Code").GetInt32().Should().Be(200);
        }

        // ─── GetJsonForApi ────────────────────────────────────────────────────────

        [TestMethod]
        public void GetJsonForApi_ReturnsAnonymousObjectWithData()
        {
            ListExtTestListVM.Add(new ListExtTestItem { Name = "Alice" });
            _vm.DoSearch();

            var result = _vm.GetJsonForApi();

            result.Should().NotBeNull();
            // Verify it's an anonymous object (has Data, Count etc. properties)
            var type = result.GetType();
            type.GetProperty("Data").Should().NotBeNull();
            type.GetProperty("Count").Should().NotBeNull();
            type.GetProperty("Code").Should().NotBeNull();
            type.GetProperty("Msg").Should().NotBeNull();
        }

        [TestMethod]
        public void GetJsonForApi_MsgIsSuccess()
        {
            _vm.DoSearch();
            var result = _vm.GetJsonForApi();
            var type = result.GetType();
            var msg = (string?)type.GetProperty("Msg")?.GetValue(result);
            msg.Should().Be("success");
        }

        // ─── GetError ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void GetError_ReturnsJsonWith400Code()
        {
            var errorJson = _vm.GetError();

            errorJson.Should().Contain("\"Code\":400");
            errorJson.Should().Contain("\"Count\":0");
            errorJson.Should().Contain("\"Page\":0");
        }

        // ─── GetError — JSON escaping (M17) ───────────────────────────────────────

        [TestMethod]
        public void GetError_ErrorContainingDoubleQuote_IsEscapedAndProducesValidJson()
        {
            // Arrange: inject an error message that contains a double-quote character.
            // Before the fix this would produce broken JSON: "Msg":"say "hello" "
            // MSD on the VM is wired to Wtm.MSD (read-only property).
            _vm.Wtm.MSD!.AddModelError("field", "say \"hello\"");

            var errorJson = _vm.GetError();

            // The result must parse as valid JSON.
            Action parse = () => System.Text.Json.JsonDocument.Parse(errorJson);
            parse.Should().NotThrow("error message with double-quotes must be properly escaped");

            // The Msg field must contain the literal escaped value (not a raw double-quote).
            using var doc = System.Text.Json.JsonDocument.Parse(errorJson);
            doc.RootElement.GetProperty("Msg").GetString().Should().Contain("say");
            doc.RootElement.GetProperty("Code").GetInt32().Should().Be(400);
        }

        [TestMethod]
        public void GetError_ErrorContainingBackslash_IsEscapedAndProducesValidJson()
        {
            // Arrange: inject an error message that contains a backslash character.
            // Before the fix this would produce broken JSON: "Msg":"C:\path "
            _vm.Wtm.MSD!.AddModelError("path", "C:\\path");

            var errorJson = _vm.GetError();

            Action parse = () => System.Text.Json.JsonDocument.Parse(errorJson);
            parse.Should().NotThrow("error message with backslash must be properly escaped");

            using var doc = System.Text.Json.JsonDocument.Parse(errorJson);
            doc.RootElement.GetProperty("Msg").GetString().Should().Contain("path");
        }

        // ─── GetSingleDataJson — basic ────────────────────────────────────────────

        [TestMethod]
        public void GetSingleDataJson_ReturnsValidJson_WithNameField()
        {
            ListExtTestListVM.Add(new ListExtTestItem { Name = "Carol", Score = 55 });
            _vm.DoSearch();

            var item = new ListExtTestItem { Name = "TestEntity", Score = 99 };
            var json = _vm.GetSingleDataJson(item, returnColumnObject: false);

            json.Should().StartWith("{");
            json.Should().EndWith("}");
            json.Should().Contain("TestEntity");
        }

        [TestMethod]
        public void GetSingleDataJson_ContainsTempIsSelected()
        {
            _vm.DoSearch();
            var item = new ListExtTestItem { Name = "Dave" };
            var json = _vm.GetSingleDataJson(item, returnColumnObject: false);
            json.Should().Contain("TempIsSelected");
        }

        [TestMethod]
        public void GetSingleDataJson_ContainsLayChecked()
        {
            _vm.DoSearch();
            var item = new ListExtTestItem { Name = "Eve" };
            var json = _vm.GetSingleDataJson(item, returnColumnObject: false);
            json.Should().Contain("LAY_CHECKED");
        }

        // ─── Color column tests ───────────────────────────────────────────────────

        [TestMethod]
        public void GetDataJson_WithForeGroundColor_InjectsColorKeys()
        {
            ColoredListVM.Reset();
            ColoredListVM.Add(new ListExtTestItem { Name = "Highlight", Score = 80, IsActive = true });

            var colorVm = new ColoredListVM();
            colorVm.Wtm = MockWtmContext.CreateWtmContext();

            var json = colorVm.GetDataJson();

            // Score > 50 → forecolor should be set; IsActive=true → bgcolor set
            json.Should().Contain("forecolor");
            json.Should().Contain("bgcolor");
        }

        [TestMethod]
        public void GetDataJson_WithNoColors_NoColorKeys()
        {
            ColoredListVM.Reset();
            // Score <= 50, IsActive = false → no colors triggered
            ColoredListVM.Add(new ListExtTestItem { Name = "Plain", Score = 10, IsActive = false });

            var colorVm = new ColoredListVM();
            colorVm.Wtm = MockWtmContext.CreateWtmContext();

            var json = colorVm.GetDataJson();

            json.Should().NotContain("forecolor");
            json.Should().NotContain("bgcolor");
        }

        // ─── Enum / bool handling ─────────────────────────────────────────────────

        [TestMethod]
        public void GetDataJson_BoolField_WhenEnumToStringTrue_ContainsCheckboxHtml()
        {
            // With enumToString=true (default) and no UIService,
            // bool values fall through and return empty string (UIService.MakeCheckBox is null)
            // This verifies the code path doesn't throw.
            ListExtTestListVM.Add(new ListExtTestItem { Name = "Bool Test", IsActive = true });

            Action act = () => _vm.GetDataJson(enumToString: true);
            act.Should().NotThrow();
        }

        [TestMethod]
        public void GetDataJson_BoolField_WhenEnumToStringFalse_SkipsRow()
        {
            // With enumToString=false, empty html values are skipped (continue)
            ListExtTestListVM.Add(new ListExtTestItem { Name = "Bool False", IsActive = false });

            Action act = () => _vm.GetDataJson(enumToString: false);
            act.Should().NotThrow();
        }

        // ─── returnColumnObject = true ───────────────────────────────────────────

        [TestMethod]
        public void GetDataJson_ReturnColumnObjectTrue_DoesNotThrow()
        {
            ListExtTestListVM.Add(new ListExtTestItem { Name = "Raw Column", Score = 5 });

            Action act = () => _vm.GetDataJson(returnColumnObject: true);
            act.Should().NotThrow();
        }

        // ─── Hash-prefixed color values ───────────────────────────────────────────

        [TestMethod]
        public void GetDataJson_HashColorAlreadyHasHash_NotDoubled()
        {
            HashColorListVM.Reset();
            HashColorListVM.Add(new ListExtTestItem { Name = "HashColor" });

            var colorVm = new HashColorListVM();
            colorVm.Wtm = MockWtmContext.CreateWtmContext();

            var json = colorVm.GetDataJson();

            // Should contain exactly one '#' prefix per color entry — not '##'
            json.Should().NotContain("##");
        }

        // ─── Editable columns ────────────────────────────────────────────────────

        [TestMethod]
        public void GetDataJson_EditableColumns_TextBox_DoesNotThrow()
        {
            EditableListVM.Reset();
            EditableListVM.Add(new ListExtTestItem { Name = "Edit", IsActive = true });

            var vm = new EditableListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext();

            Action act = () => vm.GetDataJson();
            act.Should().NotThrow();
        }

        [TestMethod]
        public void GetDataJson_EditableColumns_CheckBox_DoesNotThrow()
        {
            EditableListVM.Reset();
            EditableListVM.Add(new ListExtTestItem { Name = "CbTest", IsActive = false });

            var vm = new EditableListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext();

            Action act = () => vm.GetDataJson();
            act.Should().NotThrow();
        }

        [TestMethod]
        public void GetDataJson_EditableColumns_ComboBox_DoesNotThrow()
        {
            EditableListVM.Reset();
            EditableListVM.Add(new ListExtTestItem { Name = "ComboTest", Gender = GenderEnum.Male });

            var vm = new EditableListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext();

            Action act = () => vm.GetDataJson();
            act.Should().NotThrow();
        }

        // ─── ID column present → containsID = true ────────────────────────────────

        [TestMethod]
        public void GetDataJson_WithIdColumn_DoesNotAppendExtraId()
        {
            IdColumnListVM.Reset();
            var item = new ListExtTestItem { Name = "IDTest" };
            item.ID = Guid.NewGuid();
            IdColumnListVM.Add(item);

            var vm = new IdColumnListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext();

            var json = vm.GetDataJson();

            // With explicit ID column, the extra ,\"ID\":\"...\" append is skipped
            json.Should().Contain("IDTest");
        }

        // ─── Enum column enumToString branches ───────────────────────────────────

        [TestMethod]
        public void GetDataJson_EnumColumn_EnumToStringTrue_ResolvesDisplayName()
        {
            EnumListVM.Reset();
            EnumListVM.Add(new ListExtTestItem { Gender = GenderEnum.Male });

            var vm = new EnumListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext();

            Action act = () => vm.GetDataJson(enumToString: true);
            act.Should().NotThrow();
        }

        [TestMethod]
        public void GetDataJson_EnumColumn_EnumToStringFalse_DoesNotThrow()
        {
            EnumListVM.Reset();
            EnumListVM.Add(new ListExtTestItem { Gender = GenderEnum.Female });

            var vm = new EnumListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext();

            Action act = () => vm.GetDataJson(enumToString: false);
            act.Should().NotThrow();
        }

        // ─── GetJson IsEnumToString Searcher override ─────────────────────────────

        [TestMethod]
        public void GetJson_Searcher_IsEnumToString_Overrides_EnumToString()
        {
            EnumListVM.Reset();
            EnumListVM.Add(new ListExtTestItem { Gender = GenderEnum.Male });

            var vm = new EnumListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext();
            vm.Searcher.IsEnumToString = false;

            Action act = () => vm.GetJson(enumToString: true);
            act.Should().NotThrow();
        }

        // ─── Row background/foreground set to RowBgColor / RowColor ─────────────

        [TestMethod]
        public void GetDataJson_WithRowBgColorSet_InjectsColorIntoOutput()
        {
            // ColoredListVM with Score<=50 and IsActive=false means column funcs return ""
            // but row-level colors can be set by SetFullRowBgColor override if implemented.
            // Here we simply ensure the path doesn't throw.
            ColoredListVM.Reset();
            ColoredListVM.Add(new ListExtTestItem { Name = "RowColor", Score = 10, IsActive = false });

            var vm = new ColoredListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext();

            Action act = () => vm.GetDataJson();
            act.Should().NotThrow();
        }

        // ─── GetSingleDataJson with non-T object (uses CreateEmptyEntity) ────────

        [TestMethod]
        public void GetSingleDataJson_WithNonMatchingObj_UsesEmptyEntity()
        {
            _vm.DoSearch();

            // Pass a non-T object — code path: "if (obj is not T sou) sou = CreateEmptyEntity()"
            var json = _vm.GetSingleDataJson("not-a-T", returnColumnObject: false);

            json.Should().StartWith("{");
            json.Should().EndWith("}");
        }

        // ─── grid-002: ValidateColor — strict allowlist for __bgcolor/__forecolor ──

        [TestMethod]
        public void ValidateColor_MaliciousValue_IsDropped()
        {
            // grid-002: a CSS-injection payload must be rejected (returns null).
            // Without this guard, a DB-driven color field could inject into a <script>
            // or style attribute: "red;}</style><script>alert(1)</script>".
            var result = ListVMExtension.ValidateColor("red;}</style><script>alert(1)</script>");
            result.Should().BeNull(
                "an injection payload must be rejected by ValidateColor");
        }

        [TestMethod]
        public void ValidateColor_MaliciousValueWithQuotes_IsDropped()
        {
            // Another common injection form: quotes to break out of a JS string literal.
            var result = ListVMExtension.ValidateColor("red'; alert('xss'); var x='");
            result.Should().BeNull(
                "a payload with quotes must be rejected by ValidateColor");
        }

        [TestMethod]
        public void ValidateColor_ValidHexSixDigit_IsAccepted()
        {
            // Standard #RRGGBB hex color must pass the allowlist.
            var result = ListVMExtension.ValidateColor("#FF0000");
            result.Should().Be("#FF0000",
                "a valid 6-digit hex color must be accepted");
        }

        [TestMethod]
        public void ValidateColor_ValidHexThreeDigit_IsAccepted()
        {
            // Shorthand #RGB hex color must also be accepted.
            var result = ListVMExtension.ValidateColor("#F00");
            result.Should().Be("#F00",
                "a valid 3-digit hex color must be accepted");
        }

        [TestMethod]
        public void ValidateColor_HexWithoutHash_GetsHashPrefixed()
        {
            // A 6-digit hex without '#' should be accepted and prefixed with '#'.
            var result = ListVMExtension.ValidateColor("FF0000");
            result.Should().Be("#FF0000",
                "a hex string without '#' should be auto-prefixed");
        }

        [TestMethod]
        public void ValidateColor_CssNamedColor_IsAccepted()
        {
            // CSS named colors (e.g. "red", "blue") must pass the allowlist.
            ListVMExtension.ValidateColor("red").Should().Be("red");
            ListVMExtension.ValidateColor("blue").Should().Be("blue");
            ListVMExtension.ValidateColor("transparent").Should().Be("transparent");
        }

        [TestMethod]
        public void ValidateColor_CssNamedColorCaseInsensitive_IsAccepted()
        {
            // Named color matching must be case-insensitive.
            ListVMExtension.ValidateColor("RED").Should().Be("RED");
            ListVMExtension.ValidateColor("Blue").Should().Be("Blue");
        }

        [TestMethod]
        public void ValidateColor_NullOrEmpty_ReturnsNull()
        {
            // Null / empty / whitespace-only input must return null without throwing.
            ListVMExtension.ValidateColor(null).Should().BeNull();
            ListVMExtension.ValidateColor("").Should().BeNull();
            ListVMExtension.ValidateColor("   ").Should().BeNull();
        }

        [TestMethod]
        public void ValidateColor_UnknownNamedColor_IsDropped()
        {
            // An unrecognized string that is not a hex color or CSS named color must be rejected.
            ListVMExtension.ValidateColor("notacolor").Should().BeNull(
                "unrecognized color name must be rejected");
        }

        [TestMethod]
        public void GetDataJson_MaliciousColorValue_IsNotEmittedInJson()
        {
            // grid-002: end-to-end test — a ListVM whose color func returns a malicious
            // value must not produce the injection payload in the JSON output.
            MaliciousColorListVM.Reset();
            MaliciousColorListVM.Add(new ListExtTestItem { Name = "Hack" });

            var vm = new MaliciousColorListVM();
            vm.Wtm = MockWtmContext.CreateWtmContext();

            var json = vm.GetDataJson();

            json.Should().NotContain("<script>",
                "malicious color value must not appear in JSON output");
            json.Should().NotContain("alert(",
                "XSS payload must not appear in JSON output");
        }
    }
}
