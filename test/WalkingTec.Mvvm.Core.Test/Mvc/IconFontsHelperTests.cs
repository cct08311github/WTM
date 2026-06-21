#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Tests for IconFontsHelper.GenerateIconFont + ResolveIconfont parsing logic.
    /// Because the static fields are process-wide, each test reinitialises them.
    /// </summary>
    [TestClass]
    public class IconFontsHelperTests
    {
        private string _tempDir = null!;

        [TestInitialize]
        public void Init()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"wtm-icon-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        // ── Helper to write a CSS file with a given content ─────────────────

        private string WriteCss(string filename, string content)
        {
            var path = Path.Combine(_tempDir, filename);
            File.WriteAllText(path, content);
            return path;
        }

        // ── Basic parsing ─────────────────────────────────────────────────────

        [TestMethod]
        public void GenerateIconFont_single_valid_css_populates_IconFontItems()
        {
            var css = """
                @font-face { font-family: "layui-icon"; }
                .layui-icon-home:before { content: '\e600'; }
                .layui-icon-search:before { content: '\e601'; }
                """;
            WriteCss("icon.css", css);

            IconFontsHelper.GenerateIconFont(_tempDir);

            Assert.IsNotNull(IconFontsHelper.IconFontItems);
            Assert.IsTrue(IconFontsHelper.IconFontItems.Any(i => i.Value == "layui-icon"),
                "layui-icon family should appear in IconFontItems");
        }

        [TestMethod]
        public void GenerateIconFont_valid_css_populates_IconFontDicItems()
        {
            var css = """
                @font-face { font-family: 'myfont'; }
                .myfont-arrow:before { content: '\e700'; }
                .myfont-bell:before { content: '\e701'; }
                """;
            WriteCss("my.css", css);

            IconFontsHelper.GenerateIconFont(_tempDir);

            Assert.IsTrue(IconFontsHelper.IconFontDicItems.ContainsKey("myfont"),
                "myfont should be a key in IconFontDicItems");
            var items = IconFontsHelper.IconFontDicItems["myfont"];
            Assert.IsTrue(items.Any(m => m.Value == "myfont-arrow"));
            Assert.IsTrue(items.Any(m => m.Value == "myfont-bell"));
        }

        [TestMethod]
        public void GenerateIconFont_css_without_font_face_produces_no_entries()
        {
            var css = "body { color: red; } .icon:before { content: '\\e999'; }";
            WriteCss("no-fontface.css", css);

            IconFontsHelper.GenerateIconFont(_tempDir);

            // No @font-face → no font family parsed → should not contain any matching key
            Assert.IsNotNull(IconFontsHelper.IconFontItems);
            Assert.IsFalse(IconFontsHelper.IconFontItems.Any(i => i.Icon == null));
        }

        [TestMethod]
        public void GenerateIconFont_empty_directory_produces_empty_lists()
        {
            IconFontsHelper.GenerateIconFont(_tempDir);

            Assert.IsNotNull(IconFontsHelper.IconFontItems);
            Assert.AreEqual(0, IconFontsHelper.IconFontItems.Count);
            Assert.IsNotNull(IconFontsHelper.IconFontDicItems);
            Assert.AreEqual(0, IconFontsHelper.IconFontDicItems.Count);
        }

        [TestMethod]
        public void GenerateIconFont_non_existent_dirs_do_not_throw()
        {
            // Pass a non-existent directory — should silently ignore
            var nonExistent = Path.Combine(Path.GetTempPath(), $"wtm-ghost-{Guid.NewGuid():N}");
            IconFontsHelper.GenerateIconFont(nonExistent);

            Assert.IsNotNull(IconFontsHelper.IconFontItems);
        }

        [TestMethod]
        public void GenerateIconFont_recursive_subdirectory_is_processed()
        {
            var subDir = Path.Combine(_tempDir, "sub");
            Directory.CreateDirectory(subDir);
            var css = """
                @font-face { font-family: subicons; }
                .subicons-tick:before { content: '\e800'; }
                """;
            File.WriteAllText(Path.Combine(subDir, "sub.css"), css);

            IconFontsHelper.GenerateIconFont(_tempDir);

            Assert.IsTrue(IconFontsHelper.IconFontDicItems.ContainsKey("subicons"),
                "subicons from subdirectory should be found via recursion");
        }

        // ── IconFontItems getter resets Selected flags ────────────────────────

        [TestMethod]
        public void IconFontItems_getter_clears_selected_flag()
        {
            var css = """
                @font-face { font-family: cleartest; }
                .cleartest-x:before { content: '\e900'; }
                """;
            WriteCss("clear.css", css);
            IconFontsHelper.GenerateIconFont(_tempDir);

            // Manually set Selected on first item
            var first = IconFontsHelper.IconFontItems.First(i => i.Value == "cleartest");
            first.Selected = true;

            // Next access should clear it
            var items = IconFontsHelper.IconFontItems;
            Assert.IsFalse(items.Any(i => i.Selected),
                "IconFontItems getter must reset all Selected flags");
        }

        [TestMethod]
        public void IconFontDicItems_Icon_is_family_plus_classname()
        {
            var css = """
                @font-face { font-family: iconfont; }
                .iconfont-star:before { content: '\e100'; }
                """;
            WriteCss("ic.css", css);
            IconFontsHelper.GenerateIconFont(_tempDir);

            var items = IconFontsHelper.IconFontDicItems["iconfont"];
            var star = items.FirstOrDefault(m => m.Value == "iconfont-star");
            Assert.IsNotNull(star, "iconfont-star should be present");
            Assert.AreEqual("iconfont iconfont-star", star.Icon,
                "Icon should be 'family classname'");
        }

        // ── #464 regression: null-before-init guards ─────────────────────────
        //
        // These tests reset the private static backing fields to null via reflection,
        // simulating the state when GenerateIconFont has never been called (e.g. the
        // app boots but defers icon-font generation), then verify the getters return
        // non-null empty collections instead of throwing ArgumentNullException.

        /// <summary>
        /// Resets the two static backing fields to null and returns the previous values so
        /// the calling test can restore them in a finally block.
        /// </summary>
        private static (List<ComboSelectListItem>? prevItems, Dictionary<string, List<MenuItem>>? prevDic)
            ResetBackingFieldsToNull()
        {
            var type = typeof(IconFontsHelper);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;

            var itemsField = type.GetField("_iconFontItems", flags)
                             ?? throw new InvalidOperationException("_iconFontItems field not found");
            var dicField   = type.GetField("_iconFontDicItems", flags)
                             ?? throw new InvalidOperationException("_iconFontDicItems field not found");

            var prevItems = (List<ComboSelectListItem>?)itemsField.GetValue(null);
            var prevDic   = (Dictionary<string, List<MenuItem>>?)dicField.GetValue(null);

            itemsField.SetValue(null, null);
            dicField.SetValue(null, null);

            return (prevItems, prevDic);
        }

        private static void RestoreBackingFields(
            List<ComboSelectListItem>? items,
            Dictionary<string, List<MenuItem>>? dic)
        {
            var type = typeof(IconFontsHelper);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;

            type.GetField("_iconFontItems",    flags)!.SetValue(null, items);
            type.GetField("_iconFontDicItems", flags)!.SetValue(null, dic);
        }

        [TestMethod]
        public void IconFontItems_before_GenerateIconFont_returns_empty_list_not_null()
        {
            // Arrange — force both backing fields to null
            var (prevItems, prevDic) = ResetBackingFieldsToNull();
            try
            {
                // Act — must not throw when backing field is null (#464)
                List<ComboSelectListItem> result;
                try
                {
                    result = IconFontsHelper.IconFontItems;
                }
                catch (Exception ex)
                {
                    Assert.Fail(
                        $"IconFontItems getter must not throw when backing field is null; got: {ex.GetType().Name}: {ex.Message}");
                    return; // unreachable, but satisfies definite-assignment
                }

                Assert.IsNotNull(result,
                    "IconFontItems must return a non-null list even before GenerateIconFont runs");
                Assert.AreEqual(0, result.Count,
                    "IconFontItems must return an empty list when no icons have been generated");
            }
            finally
            {
                RestoreBackingFields(prevItems, prevDic);
            }
        }

        [TestMethod]
        public void IconFontDicItems_before_GenerateIconFont_returns_empty_dict_not_null()
        {
            // Arrange — force both backing fields to null
            var (prevItems, prevDic) = ResetBackingFieldsToNull();
            try
            {
                // Act — must not throw when backing field is null (#464)
                Dictionary<string, List<MenuItem>> result;
                try
                {
                    result = IconFontsHelper.IconFontDicItems;
                }
                catch (Exception ex)
                {
                    Assert.Fail(
                        $"IconFontDicItems getter must not throw when backing field is null; got: {ex.GetType().Name}: {ex.Message}");
                    return; // unreachable, but satisfies definite-assignment
                }

                Assert.IsNotNull(result,
                    "IconFontDicItems must return a non-null dict even before GenerateIconFont runs");
                Assert.AreEqual(0, result.Count,
                    "IconFontDicItems must return an empty dict when no icons have been generated");
            }
            finally
            {
                RestoreBackingFields(prevItems, prevDic);
            }
        }

        [TestMethod]
        public void GenerateIconFont_after_null_reset_overwrites_defaults_correctly()
        {
            // Arrange — reset to null first, access the getters (triggering lazy init), then
            // call GenerateIconFont with actual CSS. The assign-in-GenerateIconFont path must
            // still overwrite the lazily-created empty collections.
            var (prevItems, prevDic) = ResetBackingFieldsToNull();
            try
            {
                // Touch getters to trigger the ??= lazy init
                _ = IconFontsHelper.IconFontItems;
                _ = IconFontsHelper.IconFontDicItems;

                var css = """
                    @font-face { font-family: overwrite; }
                    .overwrite-tick:before { content: '\eA00'; }
                    """;
                WriteCss("ow.css", css);

                IconFontsHelper.GenerateIconFont(_tempDir);

                Assert.IsTrue(IconFontsHelper.IconFontDicItems.ContainsKey("overwrite"),
                    "GenerateIconFont must overwrite the lazily-initialised empty collections");
                Assert.IsTrue(IconFontsHelper.IconFontItems.Any(i => i.Value == "overwrite"),
                    "IconFontItems must reflect the newly generated data after GenerateIconFont");
            }
            finally
            {
                RestoreBackingFields(prevItems, prevDic);
            }
        }
    }
}
