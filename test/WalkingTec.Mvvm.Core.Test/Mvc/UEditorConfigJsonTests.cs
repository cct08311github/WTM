#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc.UEditor;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Tests for UEditorConfigJson — an internal POCO used to deserialize
    /// UEditor's config.json response.  Verifies property read/write and that
    /// the class round-trips through JSON serialization correctly.
    /// </summary>
    [TestClass]
    public class UEditorConfigJsonTests
    {
        // ── Property round-trip via direct assignment ─────────────────────────

        [TestMethod]
        public void All_string_properties_can_be_assigned_and_read()
        {
            var cfg = new UEditorConfigJson
            {
                imageActionName      = "uploadimage",
                imageFieldName       = "upfile",
                imageMaxSize         = "2048000",
                imageCompressEnable  = "true",
                imageCompressBorder  = "1600",
                imageInsertAlign     = "none",
                imageUrlPrefix       = "",
                imagePathFormat      = "/upload/{yyyy}{mm}{dd}/{time}{rand:6}",
                scrawlActionName     = "uploadscrawl",
                scrawlFieldName      = "upfile",
                scrawlPathFormat     = "/upload/{yyyy}{mm}{dd}/{time}{rand:6}",
                scrawlMaxSize        = "2048000",
                scrawlUrlPrefix      = "",
                scrawlInsertAlign    = "none",
                snapscreenActionName = "uploadimage",
                snapscreenPathFormat = "/upload/{yyyy}{mm}{dd}/{time}{rand:6}",
                snapscreenUrlPrefix  = "",
                snapscreenInsertAlign= "none",
                catcherActionName    = "catchimage",
                catcherFieldName     = "source",
                catcherPathFormat    = "/upload/{yyyy}{mm}{dd}/{time}{rand:6}",
                catcherUrlPrefix     = "",
                catcherMaxSize       = "102400",
                videoActionName      = "uploadvideo",
                videoFieldName       = "upfile",
                videoPathFormat      = "/upload/{yyyy}{mm}{dd}/{time}{rand:6}",
                videoUrlPrefix       = "",
                videoMaxSize         = "102400000",
                fileActionName       = "uploadfile",
                fileFieldName        = "upfile",
                filePathFormat       = "/upload/{yyyy}{mm}{dd}/{time}{rand:6}",
                fileUrlPrefix        = "",
                fileMaxSize          = "204800000",
                imageManagerActionName  = "listimage",
                imageManagerListPath    = "/upload/",
                imageManagerListSize    = "20",
                imageManagerUrlPrefix   = "",
                imageManagerInsertAlign = "none",
                fileManagerActionName   = "listfile",
                fileManagerListPath     = "/upload/",
                fileManagerUrlPrefix    = "",
                fileManagerListSize     = "20",
            };

            Assert.AreEqual("uploadimage", cfg.imageActionName);
            Assert.AreEqual("upfile",      cfg.imageFieldName);
            Assert.AreEqual("2048000",     cfg.imageMaxSize);
            Assert.AreEqual("true",        cfg.imageCompressEnable);
            Assert.AreEqual("1600",        cfg.imageCompressBorder);
            Assert.AreEqual("none",        cfg.imageInsertAlign);
            Assert.AreEqual("",            cfg.imageUrlPrefix);
            Assert.AreEqual("/upload/{yyyy}{mm}{dd}/{time}{rand:6}", cfg.imagePathFormat);
            Assert.AreEqual("uploadscrawl",cfg.scrawlActionName);
            Assert.AreEqual("catchimage",  cfg.catcherActionName);
            Assert.AreEqual("uploadvideo", cfg.videoActionName);
            Assert.AreEqual("uploadfile",  cfg.fileActionName);
            Assert.AreEqual("listimage",   cfg.imageManagerActionName);
            Assert.AreEqual("listfile",    cfg.fileManagerActionName);
        }

        [TestMethod]
        public void All_list_properties_can_be_assigned_and_read()
        {
            var cfg = new UEditorConfigJson
            {
                imageAllowFiles          = new List<string> { ".png", ".jpg", ".gif" },
                catcherLocalDomain       = new List<string> { "localhost" },
                catcherAllowFiles        = new List<string> { ".png" },
                videoAllowFiles          = new List<string> { ".mp4", ".avi" },
                fileAllowFiles           = new List<string> { ".pdf", ".zip" },
                imageManagerAllowFiles   = new List<string> { ".png", ".jpg" },
                fileManagerAllowFiles    = new List<string> { ".pdf" }
            };

            CollectionAssert.AreEqual(new[] { ".png", ".jpg", ".gif" }, cfg.imageAllowFiles);
            CollectionAssert.AreEqual(new[] { "localhost" },            cfg.catcherLocalDomain);
            CollectionAssert.AreEqual(new[] { ".mp4", ".avi" },         cfg.videoAllowFiles);
            CollectionAssert.AreEqual(new[] { ".pdf", ".zip" },         cfg.fileAllowFiles);
        }

        // ── Default (null) property values ────────────────────────────────────

        [TestMethod]
        public void Default_instance_has_null_string_properties()
        {
            var cfg = new UEditorConfigJson();
            Assert.IsNull(cfg.imageActionName);
            Assert.IsNull(cfg.videoActionName);
            Assert.IsNull(cfg.fileActionName);
            Assert.IsNull(cfg.imageManagerActionName);
            Assert.IsNull(cfg.fileManagerActionName);
        }

        [TestMethod]
        public void Default_instance_has_null_list_properties()
        {
            var cfg = new UEditorConfigJson();
            Assert.IsNull(cfg.imageAllowFiles);
            Assert.IsNull(cfg.catcherLocalDomain);
            Assert.IsNull(cfg.videoAllowFiles);
            Assert.IsNull(cfg.fileAllowFiles);
        }

        // ── JSON serialization round-trip ─────────────────────────────────────

        [TestMethod]
        public void JSON_round_trip_preserves_string_values()
        {
            var cfg = new UEditorConfigJson
            {
                imageActionName = "uploadimage",
                imageMaxSize    = "2048000",
                imageFieldName  = "upfile",
            };
            var json   = JsonSerializer.Serialize(cfg);
            var parsed = JsonSerializer.Deserialize<UEditorConfigJson>(json);
            Assert.IsNotNull(parsed);
            Assert.AreEqual(cfg.imageActionName, parsed!.imageActionName);
            Assert.AreEqual(cfg.imageMaxSize,    parsed.imageMaxSize);
            Assert.AreEqual(cfg.imageFieldName,  parsed.imageFieldName);
        }

        [TestMethod]
        public void JSON_round_trip_preserves_list_values()
        {
            var cfg = new UEditorConfigJson
            {
                imageAllowFiles = new List<string> { ".png", ".jpg" },
                videoAllowFiles = new List<string> { ".mp4" },
            };
            var json   = JsonSerializer.Serialize(cfg);
            var parsed = JsonSerializer.Deserialize<UEditorConfigJson>(json);
            Assert.IsNotNull(parsed);
            CollectionAssert.AreEqual(cfg.imageAllowFiles, parsed!.imageAllowFiles);
            CollectionAssert.AreEqual(cfg.videoAllowFiles, parsed.videoAllowFiles);
        }

        [TestMethod]
        public void JSON_deserialize_partial_object_leaves_rest_null()
        {
            const string json = """{"imageActionName":"uploadimage"}""";
            var parsed = JsonSerializer.Deserialize<UEditorConfigJson>(json);
            Assert.IsNotNull(parsed);
            Assert.AreEqual("uploadimage", parsed!.imageActionName);
            Assert.IsNull(parsed.videoActionName);
            Assert.IsNull(parsed.imageAllowFiles);
        }
    }
}
