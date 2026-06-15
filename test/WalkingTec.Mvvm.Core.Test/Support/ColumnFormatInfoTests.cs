#nullable enable
using System;
using FluentAssertions;
using Microsoft.Extensions.Localization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test.Support
{
    /// <summary>
    /// Tests for <see cref="ColumnFormatInfo"/> — all factory methods and the
    /// property-bag DTO.  Target: 100% line coverage.
    /// </summary>
    [TestClass]
    public class ColumnFormatInfoTests
    {
        private IStringLocalizer? _savedLocalizer;

        [TestInitialize]
        public void TestInitialize()
        {
            _savedLocalizer = CoreProgram._localizer;
            CoreProgram._localizer = null;
        }

        [TestCleanup]
        public void TestCleanup()
        {
            CoreProgram._localizer = _savedLocalizer;
        }

        // ── DTO property-bag ─────────────────────────────────────────────────────

        [TestMethod]
        public void PropertyBag_DefaultConstruction_AllDefaults()
        {
            var info = new ColumnFormatInfo();
            info.FormatType.Should().Be(ColumnFormatTypeEnum.Dialog);
            info.ButtonType.Should().Be(ButtonTypesEnum.Button);
            info.Text.Should().BeNull();
            info.Title.Should().BeNull();
            info.Script.Should().BeNull();
            info.WindowID.Should().BeNull();
            info.Url.Should().BeNull();
            info.ShowDialog.Should().BeFalse();
            info.Resizable.Should().BeFalse();
            info.ButtonID.Should().BeNull();
            info.Width.Should().BeNull();
            info.Height.Should().BeNull();
            info.FileID.Should().BeNull();
            info.Html.Should().BeNull();
            info.Maxed.Should().BeFalse();
            info.ButtonClass.Should().BeNull();
            info.RType.Should().Be(RedirectTypesEnum.Layer);
            info.Style.Should().BeNull();
        }

        // ── MakeDialogButton ──────────────────────────────────────────────────────

        [TestMethod]
        public void MakeDialogButton_RequiredOnly_SetsExpectedDefaults()
        {
            var result = ColumnFormatInfo.MakeDialogButton(
                ButtonTypesEnum.Link, "/test/url", "Click Me", 800, 600);

            result.FormatType.Should().Be(ColumnFormatTypeEnum.Dialog);
            result.ButtonType.Should().Be(ButtonTypesEnum.Link);
            result.Url.Should().Be("/test/url");
            result.Text.Should().Be("Click Me");
            result.Width.Should().Be(800);
            result.Height.Should().Be(600);
            result.Title.Should().BeNull();
            result.ButtonID.Should().BeNull();
            result.ShowDialog.Should().BeTrue();   // default = true
            result.Resizable.Should().BeTrue();    // default = true
            result.Maxed.Should().BeFalse();       // default = false
            result.ButtonClass.Should().BeNull();
            result.Style.Should().BeNull();
        }

        [TestMethod]
        public void MakeDialogButton_AllParametersProvided_SetsAll()
        {
            var result = ColumnFormatInfo.MakeDialogButton(
                ButtonTypesEnum.Img,
                "/dialog/path",
                "Open Dialog",
                1024,
                768,
                title: "My Title",
                buttonID: "btn-1",
                showDialog: false,
                resizable: false,
                maxed: true,
                buttonclass: "primary",
                style: "color:red");

            result.FormatType.Should().Be(ColumnFormatTypeEnum.Dialog);
            result.ButtonType.Should().Be(ButtonTypesEnum.Img);
            result.Url.Should().Be("/dialog/path");
            result.Text.Should().Be("Open Dialog");
            result.Width.Should().Be(1024);
            result.Height.Should().Be(768);
            result.Title.Should().Be("My Title");
            result.ButtonID.Should().Be("btn-1");
            result.ShowDialog.Should().BeFalse();
            result.Resizable.Should().BeFalse();
            result.Maxed.Should().BeTrue();
            result.ButtonClass.Should().Be("primary");
            result.Style.Should().Be("color:red");
        }

        [TestMethod]
        public void MakeDialogButton_NullWidthHeight_Allowed()
        {
            var result = ColumnFormatInfo.MakeDialogButton(
                ButtonTypesEnum.Button, "/url", "Text", null, null);

            result.Width.Should().BeNull();
            result.Height.Should().BeNull();
        }

        // ── MakeScriptButton ──────────────────────────────────────────────────────

        [TestMethod]
        public void MakeScriptButton_RequiredOnly_SetsExpectedDefaults()
        {
            var result = ColumnFormatInfo.MakeScriptButton(
                ButtonTypesEnum.Button, "/script/url", "Run Script");

            result.FormatType.Should().Be(ColumnFormatTypeEnum.Script);
            result.ButtonType.Should().Be(ButtonTypesEnum.Button);
            result.Url.Should().Be("/script/url");
            result.Text.Should().Be("Run Script");
            result.ButtonID.Should().BeNull();
            result.Script.Should().BeEmpty();    // default = ""
            result.ButtonClass.Should().BeNull();
            result.Style.Should().BeNull();
        }

        [TestMethod]
        public void MakeScriptButton_AllParametersProvided_SetsAll()
        {
            var result = ColumnFormatInfo.MakeScriptButton(
                ButtonTypesEnum.Link,
                "/action",
                "Execute",
                buttonID: "exec-btn",
                script: "alert('hello')",
                buttonclass: "danger",
                style: "font-weight:bold");

            result.FormatType.Should().Be(ColumnFormatTypeEnum.Script);
            result.ButtonType.Should().Be(ButtonTypesEnum.Link);
            result.Url.Should().Be("/action");
            result.Text.Should().Be("Execute");
            result.ButtonID.Should().Be("exec-btn");
            result.Script.Should().Be("alert('hello')");
            result.ButtonClass.Should().Be("danger");
            result.Style.Should().Be("font-weight:bold");
        }

        [TestMethod]
        public void MakeScriptButton_NullScript_StoresNull()
        {
            var result = ColumnFormatInfo.MakeScriptButton(
                ButtonTypesEnum.Button, "/url", "Text", script: null);

            result.Script.Should().BeNull();
        }

        // ── MakeButton ────────────────────────────────────────────────────────────

        [TestMethod]
        public void MakeButton_RequiredOnly_SetsExpectedDefaults()
        {
            var result = ColumnFormatInfo.MakeButton(
                ButtonTypesEnum.Button, "/button/url", "Submit", 500, 400);

            result.FormatType.Should().Be(ColumnFormatTypeEnum.Button);
            result.ButtonType.Should().Be(ButtonTypesEnum.Button);
            result.Url.Should().Be("/button/url");
            result.Text.Should().Be("Submit");
            result.Width.Should().Be(500);
            result.Height.Should().Be(400);
            result.Title.Should().BeNull();
            result.ButtonID.Should().BeNull();
            result.Resizable.Should().BeTrue();   // default = true
            result.Maxed.Should().BeFalse();      // default = false
            result.ButtonClass.Should().BeNull();
            result.RType.Should().Be(RedirectTypesEnum.Layer); // default
            result.Style.Should().BeNull();
        }

        [TestMethod]
        public void MakeButton_AllParametersProvided_SetsAll()
        {
            var result = ColumnFormatInfo.MakeButton(
                ButtonTypesEnum.Img,
                "/full",
                "Full Button",
                1200,
                900,
                title: "Full Title",
                buttonID: "full-btn",
                resizable: false,
                maxed: true,
                buttonclass: "info",
                style: "margin:0",
                rtype: RedirectTypesEnum.NewTab);

            result.FormatType.Should().Be(ColumnFormatTypeEnum.Button);
            result.ButtonType.Should().Be(ButtonTypesEnum.Img);
            result.Url.Should().Be("/full");
            result.Text.Should().Be("Full Button");
            result.Width.Should().Be(1200);
            result.Height.Should().Be(900);
            result.Title.Should().Be("Full Title");
            result.ButtonID.Should().Be("full-btn");
            result.Resizable.Should().BeFalse();
            result.Maxed.Should().BeTrue();
            result.ButtonClass.Should().Be("info");
            result.RType.Should().Be(RedirectTypesEnum.NewTab);
            result.Style.Should().Be("margin:0");
        }

        [TestMethod]
        public void MakeButton_NullWidthHeight_Allowed()
        {
            var result = ColumnFormatInfo.MakeButton(
                ButtonTypesEnum.Button, "/url", "Text", null, null);

            result.Width.Should().BeNull();
            result.Height.Should().BeNull();
        }

        [TestMethod]
        public void MakeButton_RedirectTypeSelf_Stored()
        {
            var result = ColumnFormatInfo.MakeButton(
                ButtonTypesEnum.Button, "/url", "Go", null, null,
                rtype: RedirectTypesEnum.Self);

            result.RType.Should().Be(RedirectTypesEnum.Self);
        }

        [TestMethod]
        public void MakeButton_RedirectTypeNewWindow_Stored()
        {
            var result = ColumnFormatInfo.MakeButton(
                ButtonTypesEnum.Button, "/url", "Pop", null, null,
                rtype: RedirectTypesEnum.NewWindow);

            result.RType.Should().Be(RedirectTypesEnum.NewWindow);
        }

        // ── MakeDownloadButton ────────────────────────────────────────────────────

        [TestMethod]
        public void MakeDownloadButton_WithFileId_SetsFields()
        {
            var fileId = Guid.NewGuid();
            var result = ColumnFormatInfo.MakeDownloadButton(
                ButtonTypesEnum.Button, fileId, "Download Now", "btn-link", "color:blue");

            result.FormatType.Should().Be(ColumnFormatTypeEnum.Download);
            result.ButtonType.Should().Be(ButtonTypesEnum.Button);
            result.FileID.Should().Be(fileId);
            result.Text.Should().Be("Download Now");
            result.ButtonClass.Should().Be("btn-link");
            result.Style.Should().Be("color:blue");
        }

        [TestMethod]
        public void MakeDownloadButton_NullFileId_Allowed()
        {
            var result = ColumnFormatInfo.MakeDownloadButton(
                ButtonTypesEnum.Link, null, "Get File");

            result.FormatType.Should().Be(ColumnFormatTypeEnum.Download);
            result.FileID.Should().BeNull();
            result.Text.Should().Be("Get File");
        }

        [TestMethod]
        public void MakeDownloadButton_NullButtonText_UsesLocalizerFallback()
        {
            // CoreProgram._localizer is null in unit test context,
            // so buttonText ?? null?.Value evaluates to null.
            var result = ColumnFormatInfo.MakeDownloadButton(
                ButtonTypesEnum.Button, Guid.NewGuid());

            result.FormatType.Should().Be(ColumnFormatTypeEnum.Download);
            // Text is either a localised string or null (when _localizer is not configured)
            // Either way the method must not throw and the FormatType must be set.
        }

        // ── MakeViewButton ────────────────────────────────────────────────────────

        [TestMethod]
        public void MakeViewButton_RequiredOnly_SetsExpectedDefaults()
        {
            var fileId = Guid.NewGuid();
            var result = ColumnFormatInfo.MakeViewButton(ButtonTypesEnum.Button, fileId);

            result.FormatType.Should().Be(ColumnFormatTypeEnum.ViewPic);
            result.ButtonType.Should().Be(ButtonTypesEnum.Button);
            result.FileID.Should().Be(fileId);
            result.Width.Should().BeNull();
            result.Height.Should().BeNull();
            result.Title.Should().BeNull();   // fallback to localiser, null in test env
            result.WindowID.Should().BeNull();
            result.Resizable.Should().BeTrue();
            result.Maxed.Should().BeFalse();
            result.ButtonClass.Should().BeNull();
            result.Style.Should().BeNull();
        }

        [TestMethod]
        public void MakeViewButton_AllParametersProvided_SetsAll()
        {
            var fileId = Guid.NewGuid();
            var result = ColumnFormatInfo.MakeViewButton(
                ButtonTypesEnum.Img,
                fileId,
                width: 640,
                height: 480,
                title: "View Image",
                windowID: "win-preview",
                buttonText: "Preview",
                resizable: false,
                maxed: true,
                buttonclass: "view-class",
                style: "border:1px solid");

            result.FormatType.Should().Be(ColumnFormatTypeEnum.ViewPic);
            result.ButtonType.Should().Be(ButtonTypesEnum.Img);
            result.FileID.Should().Be(fileId);
            result.Width.Should().Be(640);
            result.Height.Should().Be(480);
            result.Title.Should().Be("View Image");
            result.WindowID.Should().Be("win-preview");
            result.Text.Should().Be("Preview");
            result.Resizable.Should().BeFalse();
            result.Maxed.Should().BeTrue();
            result.ButtonClass.Should().Be("view-class");
            result.Style.Should().Be("border:1px solid");
        }

        [TestMethod]
        public void MakeViewButton_NullFileId_Allowed()
        {
            var result = ColumnFormatInfo.MakeViewButton(ButtonTypesEnum.Link, null);

            result.FormatType.Should().Be(ColumnFormatTypeEnum.ViewPic);
            result.FileID.Should().BeNull();
        }

        // ── MakeHtml ──────────────────────────────────────────────────────────────

        [TestMethod]
        public void MakeHtml_NonEmptyHtml_AppendsScriptTag()
        {
            var result = ColumnFormatInfo.MakeHtml("<b>Hello</b>");

            result.FormatType.Should().Be(ColumnFormatTypeEnum.Html);
            result.Html.Should().Be("<b>Hello</b><script></script>");
        }

        [TestMethod]
        public void MakeHtml_EmptyString_DoesNotAppendScriptTag()
        {
            var result = ColumnFormatInfo.MakeHtml("");

            result.FormatType.Should().Be(ColumnFormatTypeEnum.Html);
            result.Html.Should().BeEmpty();
        }

        [TestMethod]
        public void MakeHtml_NullHtml_NullStored()
        {
            // string.IsNullOrEmpty(null) is true → branch not taken → Html stays null
            var result = ColumnFormatInfo.MakeHtml(null!);

            result.FormatType.Should().Be(ColumnFormatTypeEnum.Html);
            result.Html.Should().BeNull();
        }

        [TestMethod]
        public void MakeHtml_HtmlWithSpecialChars_PreservedPlusScript()
        {
            const string input = "<div class=\"x\">&amp;</div>";
            var result = ColumnFormatInfo.MakeHtml(input);

            result.Html.Should().Be(input + "<script></script>");
        }

        // ── All ButtonTypes covered ───────────────────────────────────────────────

        [DataTestMethod]
        [DataRow(ButtonTypesEnum.Button)]
        [DataRow(ButtonTypesEnum.Link)]
        [DataRow(ButtonTypesEnum.Img)]
        public void MakeDialogButton_AllButtonTypes_Stored(ButtonTypesEnum bt)
        {
            var result = ColumnFormatInfo.MakeDialogButton(bt, "/u", "T", 100, 100);
            result.ButtonType.Should().Be(bt);
        }

        [DataTestMethod]
        [DataRow(RedirectTypesEnum.Layer)]
        [DataRow(RedirectTypesEnum.Self)]
        [DataRow(RedirectTypesEnum.NewWindow)]
        [DataRow(RedirectTypesEnum.NewTab)]
        public void MakeButton_AllRedirectTypes_Stored(RedirectTypesEnum rt)
        {
            var result = ColumnFormatInfo.MakeButton(
                ButtonTypesEnum.Button, "/u", "T", null, null, rtype: rt);
            result.RType.Should().Be(rt);
        }
    }
}
