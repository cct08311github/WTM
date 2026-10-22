using System;
using System.Collections.Generic;
using System.Text;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// How <c>/_Framework/GetFile</c> presents a file: its content type, and whether it may be shown inline in the page. Files that a browser would
    /// run as part of the site (HTML, SVG, XML, script) are always sent as attachments; images, PDF, video, audio and plain text keep being shown inline.
    /// </summary>
    public static class FileServing
    {
        // types the framework has always set; kept as they are (including their spelling, e.g. image/jpg)
        private static readonly Dictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["pdf"] = "application/pdf",
            ["png"] = "image/png",
            ["bmp"] = "image/bmp",
            ["gif"] = "image/gif",
            ["tif"] = "image/tif",
            ["jpg"] = "image/jpg",
            ["jpeg"] = "image/jpeg",
            ["mp4"] = "video/mpeg4",
            // passive types that used to be shown inline by sniffing
            ["txt"] = "text/plain",
            ["csv"] = "text/csv",
            ["json"] = "application/json",
            ["webp"] = "image/webp",
            ["ico"] = "image/x-icon",
            ["mp3"] = "audio/mpeg",
            ["wav"] = "audio/wav",
            ["ogg"] = "audio/ogg",
            ["webm"] = "video/webm",
        };

        // types a browser runs or renders as a document of the site
        private static readonly Dictionary<string, string> Active = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["html"] = "text/html",
            ["htm"] = "text/html",
            ["xhtml"] = "application/xhtml+xml",
            ["svg"] = "image/svg+xml",
            ["svgz"] = "image/svg+xml",
            ["xml"] = "application/xml",
            ["xsl"] = "application/xml",
            ["xslt"] = "application/xml",
            ["js"] = "text/javascript",
            ["mjs"] = "text/javascript",
        };

        /// <summary>The content type for a file extension (without the dot); <c>application/octet-stream</c> when it is not known.</summary>
        public static string GetContentType(string ext)
        {
            ext = (ext ?? string.Empty).TrimStart('.');
            if (Known.TryGetValue(ext, out var known))
            {
                return known;
            }
            if (Active.TryGetValue(ext, out var active))
            {
                return active;
            }
            return "application/octet-stream";
        }

        /// <summary>
        /// True when a browser could run the file as part of the site: the extension is an active type, or the extension is not a known one and
        /// the start of the content looks like HTML, SVG or script (what a browser would detect as <c>text/html</c>).
        /// </summary>
        /// <param name="head">the first bytes of the file (a few hundred are enough)</param>
        public static bool IsActiveContent(string ext, ReadOnlySpan<byte> head)
        {
            ext = (ext ?? string.Empty).TrimStart('.');
            if (Active.ContainsKey(ext))
            {
                return true;
            }
            if (Known.ContainsKey(ext))
            {
                return false;
            }
            return LooksLikeMarkup(head);
        }

        private static bool LooksLikeMarkup(ReadOnlySpan<byte> head)
        {
            if (head.Length > 512)
            {
                head = head.Slice(0, 512);
            }
            var text = Encoding.UTF8.GetString(head).TrimStart('﻿', ' ', '\t', '\r', '\n', '\f').ToLowerInvariant();
            foreach (var start in new[] { "<!doctype html", "<html", "<head", "<body", "<script", "<svg", "<iframe", "<img", "<a ", "<style", "<?xml", "<!--", "<form", "<table", "<h1", "<div", "<p", "<b>", "<font", "<meta", "<title", "<link", "<object", "<embed" })
            {
                if (text.StartsWith(start, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
