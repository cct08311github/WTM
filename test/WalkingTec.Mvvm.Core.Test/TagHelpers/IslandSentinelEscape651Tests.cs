#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.TagHelpers.LayUI;

namespace WalkingTec.Mvvm.Core.Test.TagHelpers;

/// <summary>
/// Issue #651 (stored XSS, dialog trust boundary): ff.OpenDialog2
/// (SelectorTagHelper.cs's &lt;wt:selector&gt; search-panel dialog opener)
/// rehydrates a wtm-dialog-init island's open/close tag ($$dialoginit$$ /
/// $$#dialoginit$$) and any bare &lt;script&gt; tag ($$script$$ / $$#script$$)
/// with GLOBAL regex replaces over the WHOLE composed search-panel template —
/// not scoped to the sentinel occurrences SelectorTagHelper itself placed.
/// _islandJsonOptions' default JavaScriptEncoder escapes '&lt;', '&gt;', '&amp;'
/// (safe against a raw &lt;/script&gt; breakout) but NOT '$' — so a stored value
/// (e.g. a selector field's selectVal, or a checkbox/radio field's default
/// selection) containing a literal sentinel sequence such as
/// "$$#dialoginit$$$$script$$window.evil=1$$#script$$" previously survived
/// JSON serialization intact and, once the composed template reached
/// ff.OpenDialog2, was tokenized/rehydrated exactly like a real sentinel —
/// breaking out of the island/attribute and injecting attacker-controlled
/// markup/script (stored XSS).
///
/// The fix (see ComboBoxTagHelper.cs's LayuiIslandJson.Serialize): after
/// JsonSerializer.Serialize, every '$' is replaced with its 6-character JSON
/// Unicode escape sequence. This is always valid/reversible for these
/// payloads because '$' can only ever appear inside JSON STRING VALUES here
/// (the structural JSON never contains '$') — JSON.parse on the client
/// already decodes the escape back to '$', so ff.LoadComboItems /
/// ff._readFieldDefaults see the exact original value.
///
/// These tests assert every wtm-dialog-init island emitter
/// (Combo/CheckBox/Radio/Transfer's loadComboItemsAction) and the
/// data-wtm-defaults attribute (CheckBox/Radio) are free of ANY literal '$'
/// character — hence free of every '$$dialoginit$$' / '$$#dialoginit$$' /
/// '$$script$$' / '$$#script$$' / '$$SearchPanel$$' sentinel — regardless of
/// what a stored value contains, and that the escaped text round-trips (via
/// JsonDocument.Parse, mirroring the client's JSON.parse) to the EXACT
/// original value.
/// </summary>
[TestClass]
public class IslandSentinelEscape651Tests
{
    private sealed class DummyModel
    {
        public string? StringField { get; set; }

        // Issue #651 (follow-up): lets a test drive the Items-based branches
        // (ComboBox's xmSelect `data:` at ~L359, Transfer's `data:` at ~L238)
        // with a ComboSelectListItem whose Value/Text carries a sentinel.
        public List<ComboSelectListItem>? ItemsField { get; set; }

        // Issue #651 (completeness): TreeTagHelper's Items branch consumes a
        // List<TreeSelectListItem>; a node Title carrying a sentinel exercises
        // its inline `data:` serialize (TreeTagHelper.cs ~L224).
        public List<TreeSelectListItem>? TreeItemsField { get; set; }
    }

    private static void SetupLocalizer()
    {
        var localizerMock = new Mock<Microsoft.Extensions.Localization.IStringLocalizer>();
        localizerMock.Setup(x => x[It.IsAny<string>()])
            .Returns((string s) => new Microsoft.Extensions.Localization.LocalizedString(s, s));
        localizerMock.Setup(x => x[It.IsAny<string>(), It.IsAny<object[]>()])
            .Returns((string s, object[] _) => new Microsoft.Extensions.Localization.LocalizedString(s, s));
        THProgram._localizer = localizerMock.Object;
        CoreProgram._localizer = localizerMock.Object;
    }

    // Same (PropertyInfo, containerType) overload as LoadComboItemsIsland633Tests —
    // a plain string-typed field never touches CheckBox/RadioTagHelper's
    // list/bool-specific metadata branches, so this overload is safe for all
    // four widgets under test here (they all fall into the
    // "single Field.Model.ToString() value" branch).
    private static ModelExpression MakeField(string propertyName, object? modelValue = null)
    {
        var provider = new EmptyModelMetadataProvider();
        var propertyInfo = typeof(DummyModel).GetProperty(propertyName)!;
        var metadata = provider.GetMetadataForProperty(propertyInfo, typeof(DummyModel));
        var modelExplorer = new ModelExplorer(provider, metadata, modelValue);
        return new ModelExpression(propertyName, modelExplorer);
    }

    private static ModelExpression MakeItemsField(List<ComboSelectListItem> items)
    {
        var provider = new EmptyModelMetadataProvider();
        var propertyInfo = typeof(DummyModel).GetProperty("ItemsField")!;
        var metadata = provider.GetMetadataForProperty(propertyInfo, typeof(DummyModel));
        var modelExplorer = new ModelExplorer(provider, metadata, items);
        return new ModelExpression("ItemsField", modelExplorer);
    }

    private static ModelExpression MakeTreeItemsField(List<TreeSelectListItem> items)
    {
        var provider = new EmptyModelMetadataProvider();
        var propertyInfo = typeof(DummyModel).GetProperty("TreeItemsField")!;
        var metadata = provider.GetMetadataForProperty(propertyInfo, typeof(DummyModel));
        var modelExplorer = new ModelExplorer(provider, metadata, items);
        return new ModelExpression("TreeItemsField", modelExplorer);
    }

    private static TreeTagHelper CreateTreeHelper()
    {
        SetupLocalizer();
        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        return new TreeTagHelper(monitor.Object);
    }

    private static TagHelperContext MakeContext(string tagName)
        => new(tagName, new TagHelperAttributeList(),
               new Dictionary<object, object>(), "test-id");

    private static TagHelperOutput MakeOutput()
        => new("div", new TagHelperAttributeList(),
               (_, __) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));

    // Same island-JSON extraction helper as LoadComboItemsIsland633Tests /
    // SliderTagHelperTests (#552) — the loadComboItems island is a bare
    // (non-{actions:[...]}-wrapped) payload.
    private static string? ExtractJsonFromIsland(string html)
    {
        const string open = "\"wtm-dialog-init\">";
        const string close = "</script>";
        var start = html.IndexOf(open);
        if (start < 0) return null;
        start += open.Length;
        var end = html.IndexOf(close, start);
        if (end < 0) return null;
        return html[start..end];
    }

    private static ComboBoxTagHelper CreateComboBoxHelper()
    {
        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        // WTMContext is stored but never read inside ComboBoxTagHelper.Process().
        return new ComboBoxTagHelper(monitor.Object, null!);
    }

    private static DateTimeTagHelper CreateDateTimeHelper()
    {
        SetupLocalizer();
        var configs = new Configs();
        var monitor = new Mock<IOptionsMonitor<Configs>>();
        monitor.Setup(m => m.CurrentValue).Returns(configs);
        return new DateTimeTagHelper(monitor.Object) { Field = MakeField("StringField", null) };
    }

    // A realistic composite payload matching the #651 report's reproduction:
    // the sentinel appears TWICE (open/close-shaped), bracketing an
    // attacker-controlled statement, plus surrounding text so the assertions
    // also prove partial/embedded occurrences are neutralized, not just a
    // payload that is ENTIRELY the sentinel.
    private static string MakeCompositePayload(string sentinel) =>
        "prefix-" + sentinel + "-window.__pwned=1-" + sentinel + "-suffix";

    // Every '$' must be gone from the serialized text, and therefore every
    // possible '$$...$$' sentinel shape (dialoginit/script/SearchPanel alike)
    // can never survive — this is the actual invariant the fix provides,
    // stronger than merely checking the ONE sentinel under test.
    private static void AssertNoDollarSurvives(string serializedJson, string sentinel)
    {
        Assert.IsFalse(serializedJson.Contains('$'),
            "No literal '$' may survive in the serialized island/attribute payload — " +
            "every '$' must have been replaced with its \\u0024 escape");
        Assert.IsFalse(serializedJson.Contains(sentinel),
            $"Literal sentinel '{sentinel}' must not survive serialization");
        Assert.IsFalse(serializedJson.Contains("$$"),
            "No literal '$$' pair may survive — this is what OpenDialog2's global " +
            "regex replaces key off of");
    }

    // The five sentinel tokens SelectorTagHelper.cs places and framework_layui.js
    // OpenDialog2 globally rehydrates. $$SearchPanel$$ is the template splice
    // placeholder; the rest are the script/island open/close pairs.
    private static readonly string[] AllSentinels =
    {
        "$$dialoginit$$", "$$#dialoginit$$", "$$script$$", "$$#script$$", "$$SearchPanel$$"
    };

    // A payload embedding EVERY sentinel at once — if the fix misses any one
    // site or any one sentinel, the corresponding assertion below fires.
    private static string AllSentinelsPayload() =>
        "x" + string.Join("y", AllSentinels) + "z";

    // For INLINE <script> markup (not pure JSON): the emitted script legitimately
    // contains '$' (jQuery '$(...)', '$.get', …), so — unlike AssertNoDollarSurvives
    // for the JSON-only island/attribute — we cannot assert "no '$'". The
    // security-relevant invariant is narrower and exact: none of the five literal
    // sentinel tokens may appear, because those (and only those) are what
    // OpenDialog2's global replaces act on. With every model-derived '$' escaped
    // to $, a sentinel embedded in the model data can no longer form.
    private static void AssertNoSentinelInMarkup(string markup)
    {
        foreach (var s in AllSentinels)
        {
            Assert.IsFalse(markup.Contains(s),
                $"Emitted markup must not contain the literal sentinel '{s}' — a model " +
                "value carrying it must have every '$' escaped to \\u0024 before emission");
        }
    }

    // ── ComboBoxTagHelper: loadComboItems island ────────────────────────────

    [DataTestMethod]
    [DataRow("$$dialoginit$$")]
    [DataRow("$$#dialoginit$$")]
    [DataRow("$$script$$")]
    [DataRow("$$#script$$")]
    [DataRow("$$SearchPanel$$")]
    public void ComboBox_LoadComboItemsIsland_SentinelInSelectVal_NoLiteralSentinelSurvivesAndRoundTrips(string sentinel)
    {
        SetupLocalizer();
        var payload = MakeCompositePayload(sentinel);
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", payload);
        helper.Id = "combo_651";
        helper.ItemUrl = "/Home/GetComboItems";
        var output = MakeOutput();
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        AssertNoDollarSurvives(json!, sentinel);

        using var doc = JsonDocument.Parse(json!);
        var selectVal = doc.RootElement.GetProperty("selectVal");
        Assert.AreEqual(JsonValueKind.Array, selectVal.ValueKind);
        Assert.AreEqual(payload, selectVal[0].GetString(),
            "Round-trip (JSON.parse-equivalent) must yield the exact original payload");
    }

    // ── CheckBoxTagHelper: loadComboItems island AND data-wtm-defaults ──────
    // CheckBoxTagHelper computes `values` ONCE and feeds BOTH the island
    // (ItemUrl branch) and the unconditional data-wtm-defaults attribute from
    // it — one Process() call exercises both #651 call sites at once.

    [DataTestMethod]
    [DataRow("$$dialoginit$$")]
    [DataRow("$$#dialoginit$$")]
    [DataRow("$$script$$")]
    [DataRow("$$#script$$")]
    [DataRow("$$SearchPanel$$")]
    public void CheckBox_IslandAndDataWtmDefaults_SentinelInValue_NoLiteralSentinelSurvivesAndRoundTrips(string sentinel)
    {
        SetupLocalizer();
        var payload = MakeCompositePayload(sentinel);
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("StringField", payload),
            Id = "checkbox_651",
            ItemUrl = "/Home/GetCheckboxItems"
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:checkbox"), output);
        var postHtml = output.PostElement.GetContent();

        // Island (ItemUrl branch).
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        AssertNoDollarSurvives(json!, sentinel);
        using (var doc = JsonDocument.Parse(json!))
        {
            var selectVal = doc.RootElement.GetProperty("selectVal");
            Assert.AreEqual(payload, selectVal[0].GetString(),
                "Island round-trip must yield the exact original payload");
        }

        // data-wtm-defaults attribute (unconditional).
        Assert.IsTrue(output.Attributes.ContainsName("data-wtm-defaults"));
        var attrValue = output.Attributes["data-wtm-defaults"].Value?.ToString();
        Assert.IsNotNull(attrValue);
        AssertNoDollarSurvives(attrValue!, sentinel);
        using (var attrDoc = JsonDocument.Parse(attrValue!))
        {
            Assert.AreEqual(payload, attrDoc.RootElement[0].GetString(),
                "Attribute round-trip must yield the exact original payload");
        }
    }

    // ── RadioTagHelper: loadComboItems island AND data-wtm-defaults ─────────
    // RadioTagHelper likewise computes `values` once and feeds both the
    // unconditional data-wtm-defaults attribute and (if ItemUrl set) the
    // island from it.

    [DataTestMethod]
    [DataRow("$$dialoginit$$")]
    [DataRow("$$#dialoginit$$")]
    [DataRow("$$script$$")]
    [DataRow("$$#script$$")]
    [DataRow("$$SearchPanel$$")]
    public void Radio_IslandAndDataWtmDefaults_SentinelInValue_NoLiteralSentinelSurvivesAndRoundTrips(string sentinel)
    {
        SetupLocalizer();
        var payload = MakeCompositePayload(sentinel);
        var helper = new RadioTagHelper
        {
            Field = MakeField("StringField", payload),
            Id = "radio_651",
            ItemUrl = "/Home/GetRadioItems"
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:radio"), output);
        var postHtml = output.PostElement.GetContent();

        // Island (ItemUrl branch).
        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        AssertNoDollarSurvives(json!, sentinel);
        using (var doc = JsonDocument.Parse(json!))
        {
            var selectVal = doc.RootElement.GetProperty("selectVal");
            Assert.AreEqual(payload, selectVal[0].GetString(),
                "Island round-trip must yield the exact original payload");
        }

        // data-wtm-defaults attribute (unconditional, added before the
        // ItemUrl branch even runs).
        Assert.IsTrue(output.Attributes.ContainsName("data-wtm-defaults"));
        var attrValue = output.Attributes["data-wtm-defaults"].Value?.ToString();
        Assert.IsNotNull(attrValue);
        AssertNoDollarSurvives(attrValue!, sentinel);
        using (var attrDoc = JsonDocument.Parse(attrValue!))
        {
            Assert.AreEqual(payload, attrDoc.RootElement[0].GetString(),
                "Attribute round-trip must yield the exact original payload");
        }
    }

    // ── TransferTagHelper: loadComboItems island ────────────────────────────

    [DataTestMethod]
    [DataRow("$$dialoginit$$")]
    [DataRow("$$#dialoginit$$")]
    [DataRow("$$script$$")]
    [DataRow("$$#script$$")]
    [DataRow("$$SearchPanel$$")]
    public void Transfer_LoadComboItemsIsland_SentinelInSelectVal_NoLiteralSentinelSurvivesAndRoundTrips(string sentinel)
    {
        SetupLocalizer();
        var payload = MakeCompositePayload(sentinel);
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", payload),
            Id = "transfer_651",
            ItemUrl = "/Home/GetTransferItems"
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:transfer"), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "Island must contain parseable JSON");
        AssertNoDollarSurvives(json!, sentinel);

        using var doc = JsonDocument.Parse(json!);
        var selectVal = doc.RootElement.GetProperty("selectVal");
        Assert.AreEqual(payload, selectVal[0].GetString(),
            "Round-trip (JSON.parse-equivalent) must yield the exact original payload");
    }

    // ════════════════════════════════════════════════════════════════════════
    // Issue #651 follow-up: INLINE <script> writes (not islands / attributes)
    // that also land inside the tokenized selector-panel template and carry the
    // SAME sentinel-collision risk. Each test drives ONE Process() path that
    // emits the relevant inline write and asserts no sentinel token survives in
    // the emitted markup. A single AllSentinelsPayload() embeds every sentinel,
    // so a miss at any site OR for any sentinel fails.
    // ════════════════════════════════════════════════════════════════════════

    // ComboBox inline: {Id}defaultvalues (~L361), xmSelect data: (~L359), and the
    // linked-field setTimeout {Id}data (~L367) — all three fire in one render
    // (Items supplies the data: item; LinkId forces the setTimeout block).
    [TestMethod]
    public void ComboBox_InlineScripts_SentinelInValueAndItems_NoSentinelSurvives()
    {
        SetupLocalizer();
        var payload = AllSentinelsPayload();
        var helper = CreateComboBoxHelper();
        helper.Field = MakeField("StringField", payload);
        helper.Id = "combo_inline_651";
        helper.LinkId = "someLinkTarget"; // forces the {Id}data setTimeout block
        helper.Items = MakeItemsField(new List<ComboSelectListItem>
        {
            new() { Value = payload, Text = payload } // forces the data: GetLayuiTree branch
        });
        var output = MakeOutput();
        helper.Process(MakeContext("wt:combobox"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "defaultvalues =",
            "the inline defaultvalues write must still be emitted (behaviour preserved)");
        StringAssert.Contains(postHtml, "xmSelect.render(",
            "the xmSelect render script (carrying data:) must still be emitted");
        AssertNoSentinelInMarkup(postHtml);
    }

    // CheckBox inline: {Id}defaultvalues (~L252) plus the data-wtm-defaults
    // attribute — both fed from the same `values`.
    [TestMethod]
    public void CheckBox_InlineDefaultvaluesScript_SentinelInValue_NoSentinelSurvives()
    {
        SetupLocalizer();
        var payload = AllSentinelsPayload();
        var helper = new CheckBoxTagHelper
        {
            Field = MakeField("StringField", payload),
            Id = "checkbox_inline_651"
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:checkbox"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "defaultvalues =",
            "the inline defaultvalues write must still be emitted (behaviour preserved)");
        AssertNoSentinelInMarkup(postHtml);
        AssertNoSentinelInMarkup(output.Attributes["data-wtm-defaults"].Value?.ToString()!);
    }

    // Radio inline: {Id}defaultvalues (~L212) plus the data-wtm-defaults attribute.
    [TestMethod]
    public void Radio_InlineDefaultvaluesScript_SentinelInValue_NoSentinelSurvives()
    {
        SetupLocalizer();
        var payload = AllSentinelsPayload();
        var helper = new RadioTagHelper
        {
            Field = MakeField("StringField", payload),
            Id = "radio_inline_651"
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:radio"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "defaultvalues =",
            "the inline defaultvalues write must still be emitted (behaviour preserved)");
        AssertNoSentinelInMarkup(postHtml);
        AssertNoSentinelInMarkup(output.Attributes["data-wtm-defaults"].Value?.ToString()!);
    }

    // Transfer inline: DefaultValue built from selectVal (~L164) → rendered as
    // `var defaultVal = [...]` in the emitted layui.transfer <script>.
    [TestMethod]
    public void Transfer_InlineDefaultVal_SentinelInSelectVal_NoSentinelSurvives()
    {
        SetupLocalizer();
        var payload = AllSentinelsPayload();
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", payload),
            Id = "transfer_inline_651"
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:transfer"), output);

        AssertNoSentinelInMarkup(output.PostElement.GetContent());
    }

    // Transfer inline: DefaultValue built from the DEVELOPER-set DefaultValue
    // string (~L170) — the else branch (no Field.Model), still rendered into the
    // emitted <script>.
    [TestMethod]
    public void Transfer_InlineDefaultValueString_SentinelInDefaultValue_NoSentinelSurvives()
    {
        SetupLocalizer();
        var payload = AllSentinelsPayload();
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", null),
            Id = "transfer_dv_651",
            DefaultValue = payload
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:transfer"), output);

        AssertNoSentinelInMarkup(output.PostElement.GetContent());
    }

    // Transfer inline: data: built from Items (~L238) — a ComboSelectListItem
    // whose Value/Text carries the sentinel.
    [TestMethod]
    public void Transfer_InlineData_SentinelInItems_NoSentinelSurvives()
    {
        SetupLocalizer();
        var payload = AllSentinelsPayload();
        var helper = new TransferTagHelper
        {
            Field = MakeField("StringField", null),
            Id = "transfer_data_651",
            Items = MakeItemsField(new List<ComboSelectListItem>
            {
                new() { Value = payload, Text = payload }
            })
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:transfer"), output);

        AssertNoSentinelInMarkup(output.PostElement.GetContent());
    }

    // ════════════════════════════════════════════════════════════════════════
    // Issue #651 completeness: the OTHER wtm-dialog-init island emitters (#635
    // makes them all dispatch on the selector path). Guarded wholesale by the
    // framework_layui_651_island_serialize_guard.test.js source-sweep; these two
    // concrete cases drive real Process() output for an island emitter (TagInput)
    // and a newly-routed inline site (DateTime mark:).
    // ════════════════════════════════════════════════════════════════════════

    // TagInput island: the widget's model/config-derived opts (placeholder from
    // EmptyText, separator from Delimiter) travel in the wtm-dialog-init island.
    // (The TAG VALUES themselves go to an HtmlEncoded hidden-input value
    // attribute — a plaintext, non-JSON channel — which is the #652 variant, out
    // of scope here.)
    [DataTestMethod]
    [DataRow("$$dialoginit$$")]
    [DataRow("$$#dialoginit$$")]
    [DataRow("$$script$$")]
    [DataRow("$$#script$$")]
    [DataRow("$$SearchPanel$$")]
    public void TagInput_Island_SentinelInPlaceholderAndSeparator_NoLiteralSentinelSurvivesAndRoundTrips(string sentinel)
    {
        SetupLocalizer();
        var payload = MakeCompositePayload(sentinel);
        var helper = new TagInputTagHelper
        {
            Field = MakeField("StringField", null),
            Id = "taginput_651",
            EmptyText = payload,
            Delimiter = payload
        };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:taginput"), output);
        var postHtml = output.PostElement.GetContent();

        var json = ExtractJsonFromIsland(postHtml);
        Assert.IsNotNull(json, "TagInput must emit a parseable wtm-dialog-init island");
        AssertNoDollarSurvives(json!, sentinel);

        using var doc = JsonDocument.Parse(json!);
        var opts = doc.RootElement.GetProperty("opts");
        Assert.AreEqual(payload, opts.GetProperty("placeholder").GetString(),
            "placeholder must round-trip to the exact original payload");
        Assert.AreEqual(payload, opts.GetProperty("separator").GetString(),
            "separator must round-trip to the exact original payload");
    }

    // DateTime mark: — a newly-routed INLINE <script> serialization (the laydate
    // render script's `,mark: {...}`), fed from the model-derived Mark dictionary.
    // hasCallback (DoneFunc) forces the inline branch that carries mark:.
    [DataTestMethod]
    [DataRow("$$dialoginit$$")]
    [DataRow("$$#dialoginit$$")]
    [DataRow("$$script$$")]
    [DataRow("$$#script$$")]
    [DataRow("$$SearchPanel$$")]
    public void DateTime_InlineMark_SentinelInMarkValue_NoSentinelSurvives(string sentinel)
    {
        var payload = MakeCompositePayload(sentinel);
        var helper = CreateDateTimeHelper();
        helper.Id = "dt_651";
        helper.DoneFunc = "someDoneFunc"; // forces the inline (non-island) branch that emits mark:
        helper.Mark = new Dictionary<string, string> { ["01"] = payload };
        var output = MakeOutput();
        helper.Process(MakeContext("wt:datetime"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "mark:",
            "the inline laydate render script must still emit mark: (behaviour preserved)");
        AssertNoSentinelInMarkup(postHtml);
    }

    // Tree inline data: — a NON-island, DEFAULT-overload inline serialize
    // (TreeTagHelper.cs ~L224) that framework_layui_651_island_serialize_guard's
    // Guard 3 catch-all protects. The sentinel travels in a tree node's Title,
    // which reaches ONLY the escaped `data:` JSON. (The field's own model value
    // travels separately to an HtmlEncoded hidden-input `value` attribute — the
    // plaintext #652 channel — so this test deliberately leaves Field.Model null
    // to isolate the #651 island-JSON class.)
    [DataTestMethod]
    [DataRow("$$dialoginit$$")]
    [DataRow("$$#dialoginit$$")]
    [DataRow("$$script$$")]
    [DataRow("$$#script$$")]
    [DataRow("$$SearchPanel$$")]
    public void Tree_InlineData_SentinelInNodeTitle_NoSentinelSurvivesAndRoundTrips(string sentinel)
    {
        var payload = MakeCompositePayload(sentinel);
        var helper = CreateTreeHelper();
        helper.Id = "tree_651";
        helper.Field = MakeField("StringField", null);
        helper.Items = MakeTreeItemsField(new List<TreeSelectListItem>
        {
            new() { Value = "v1", Text = payload } // node Title → the inline data: serialize (L224)
        });
        var output = MakeOutput();
        helper.Process(MakeContext("wt:tree"), output);
        var postHtml = output.PostElement.GetContent();

        StringAssert.Contains(postHtml, "xmSelect.render(",
            "the inline tree render script (carrying data:) must still be emitted");
        AssertNoSentinelInMarkup(postHtml);
        // Round-trip: the node title appears $-escaped ($), i.e. exactly the
        // reversible form JS/JSON.parse decodes back to the original value.
        StringAssert.Contains(postHtml, payload.Replace("$", "\\u0024"),
            "the node title must be emitted with every '$' escaped to \\u0024 (reversible round-trip)");
    }

    // Note: LayuiIslandJson (the shared helper in ComboBoxTagHelper.cs) is
    // `internal` — same visibility as the pre-existing LoadComboItemsIslandAction
    // DTO it sits next to — and this test assembly has no InternalsVisibleTo
    // grant onto WalkingTec.Mvvm.TagHelpers.LayUI (matching every other
    // *Island*Tests.cs file's convention in this project). Its contract is
    // therefore exercised exclusively through the public TagHelper.Process()
    // entry points above, never by calling it directly.

    [TestCleanup]
    public void Cleanup()
    {
        THProgram._localizer = null!;
        CoreProgram._localizer = null!;
    }
}
