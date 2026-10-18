using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;

namespace WalkingTec.Mvvm.Admin.Api
{
    /// <summary>
    /// Limits applied before user supplied bytes are handed to the ImageSharp decoder.
    /// Configurable through the "ImageUploadLimits" configuration section.
    /// </summary>
    public class ImageDecodeLimits
    {
        public const long DefaultMaxBytes = 10L * 1024 * 1024;
        public const int DefaultMaxWidth = 8192;
        public const int DefaultMaxHeight = 8192;
        public const long DefaultMaxPixels = 40_000_000;

        public long MaxBytes { get; set; } = DefaultMaxBytes;
        public int MaxWidth { get; set; } = DefaultMaxWidth;
        public int MaxHeight { get; set; } = DefaultMaxHeight;
        public long MaxPixels { get; set; } = DefaultMaxPixels;

        public static ImageDecodeLimits FromConfiguration(IConfiguration configuration)
        {
            var rv = new ImageDecodeLimits();
            var section = configuration?.GetSection("ImageUploadLimits");
            if (section == null)
            {
                return rv;
            }
            if (long.TryParse(section["MaxBytes"], out var b) && b > 0) rv.MaxBytes = b;
            if (int.TryParse(section["MaxWidth"], out var w) && w > 0) rv.MaxWidth = w;
            if (int.TryParse(section["MaxHeight"], out var h) && h > 0) rv.MaxHeight = h;
            if (long.TryParse(section["MaxPixels"], out var p) && p > 0) rv.MaxPixels = p;
            return rv;
        }
    }

    /// <summary>
    /// Mitigation for ImageSharp 3.1.x advisories (see issue #78): validates format, size and
    /// dimensions from the image header before any full decode of user supplied data.
    /// Allowed formats: JPEG, PNG, GIF, BMP, WebP.
    /// </summary>
    public static class ImageDecodeGuard
    {
        /// <summary>
        /// Validates the stream and, if it passes, fully decodes it. Returns false (image == null) otherwise.
        /// The input stream is buffered into memory (bounded by MaxBytes), so it need not be seekable.
        /// </summary>
        public static bool TryLoad(Stream input, ImageDecodeLimits limits, out Image image, out string error)
        {
            image = null;
            if (!TryBuffer(input, limits, out var ms, out error))
            {
                return false;
            }
            using (ms)
            {
                if (!Validate(ms, limits, out error))
                {
                    return false;
                }
                ms.Position = 0;
                try
                {
                    image = Image.Load(ms);
                    return true;
                }
                catch (Exception)
                {
                    error = "Invalid image";
                    return false;
                }
            }
        }

        /// <summary>Header-only validation (format allowlist, byte size, pixel dimensions).</summary>
        public static bool Validate(Stream seekable, ImageDecodeLimits limits, out string error)
        {
            error = null;
            limits ??= new ImageDecodeLimits();
            if (seekable.CanSeek && seekable.Length > limits.MaxBytes)
            {
                error = "Image file too large";
                return false;
            }
            try
            {
                seekable.Position = 0;
                var info = Image.Identify(seekable);
                seekable.Position = 0;
                if (info == null)
                {
                    error = "Invalid image";
                    return false;
                }
                var format = info.Metadata?.DecodedImageFormat;
                if (!IsAllowed(format))
                {
                    error = "Unsupported image format";
                    return false;
                }
                if (info.Width <= 0 || info.Height <= 0
                    || info.Width > limits.MaxWidth
                    || info.Height > limits.MaxHeight
                    || (long)info.Width * info.Height > limits.MaxPixels)
                {
                    error = "Image dimensions too large";
                    return false;
                }
                return true;
            }
            catch (Exception)
            {
                // UnknownImageFormatException, InvalidImageContentException, ...
                error = "Invalid image";
                return false;
            }
        }

        private static bool IsAllowed(IImageFormat format)
        {
            return format is JpegFormat || format is PngFormat || format is GifFormat
                || format is BmpFormat || format is WebpFormat;
        }

        private static bool TryBuffer(Stream input, ImageDecodeLimits limits, out MemoryStream ms, out string error)
        {
            error = null;
            ms = null;
            limits ??= new ImageDecodeLimits();
            if (input == null)
            {
                error = "Invalid image";
                return false;
            }
            if (input.CanSeek && input.Length > limits.MaxBytes)
            {
                error = "Image file too large";
                return false;
            }
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > limits.MaxBytes)
                {
                    buffer.Dispose();
                    error = "Image file too large";
                    return false;
                }
                buffer.Write(chunk, 0, read);
            }
            buffer.Position = 0;
            ms = buffer;
            return true;
        }
    }
}
