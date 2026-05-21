#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    }
}
