using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>How GetFile presents a file: content type, and which files are never shown inline.</summary>
    [TestClass]
    public class FileServingTest
    {
        private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

        [TestMethod]
        public void TheTypesThatWereAlwaysSet_AreUnchanged()
        {
            Assert.AreEqual("application/pdf", FileServing.GetContentType("pdf"));
            Assert.AreEqual("image/png", FileServing.GetContentType("png"));
            Assert.AreEqual("image/jpg", FileServing.GetContentType("jpg"));
            Assert.AreEqual("image/jpeg", FileServing.GetContentType("jpeg"));
            Assert.AreEqual("image/tif", FileServing.GetContentType("tif"));
            Assert.AreEqual("video/mpeg4", FileServing.GetContentType("mp4"));
            Assert.AreEqual("application/octet-stream", FileServing.GetContentType("xyz"));
            Assert.AreEqual("application/octet-stream", FileServing.GetContentType(null));
        }

        [TestMethod]
        public void ActiveTypes_AreRecognisedByTheirExtension_InAnyCase()
        {
            foreach (var ext in new[] { "html", "HTM", "Svg", "xml", "xhtml", "js", ".js", "mjs", "xsl" })
            {
                Assert.IsTrue(FileServing.IsActiveContent(ext, Bytes("anything")), ext);
            }
            Assert.AreEqual("text/html", FileServing.GetContentType("HTML"));
            Assert.AreEqual("image/svg+xml", FileServing.GetContentType("svg"));
        }

        [TestMethod]
        public void PassiveTypes_AreNotActive_WhateverTheyContain()
        {
            foreach (var ext in new[] { "png", "jpg", "gif", "pdf", "mp4", "txt", "csv", "json", "webp", "mp3" })
            {
                Assert.IsFalse(FileServing.IsActiveContent(ext, Bytes("<html><script>1</script></html>")), ext);
            }
        }

        [TestMethod]
        public void AnUnknownType_IsActiveWhenItsContentLooksLikeMarkup()
        {
            Assert.IsTrue(FileServing.IsActiveContent("dat", Bytes("<!DOCTYPE html><title>x</title>")));
            Assert.IsTrue(FileServing.IsActiveContent("", Bytes("  \r\n<HTML>")));
            Assert.IsTrue(FileServing.IsActiveContent("bin", Bytes("﻿<script>1</script>")));
            Assert.IsTrue(FileServing.IsActiveContent("bin", Bytes("<svg/>")));
            Assert.IsFalse(FileServing.IsActiveContent("dat", Bytes("just some data < 5 and > 3")));
            Assert.IsFalse(FileServing.IsActiveContent("dat", new byte[] { 0x00, 0x01, 0x02 }));
            Assert.IsFalse(FileServing.IsActiveContent("dat", new byte[0]));
        }
    }
}
