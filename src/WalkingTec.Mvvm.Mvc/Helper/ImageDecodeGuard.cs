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
using SixLabors.ImageSharp.Processing;

namespace WalkingTec.Mvvm.Mvc.Imaging
{
    /// <summary>
    /// Limits applied before user supplied or stored bytes are handed to the ImageSharp decoder. Configuration section <c>ImageUploadLimits</c>
    /// (<c>MaxBytes</c>, <c>MaxWidth</c>, <c>MaxHeight</c>, <c>MaxPixels</c>, <c>MaxFrames</c>, <c>MaxResizeWidth</c>, <c>MaxResizeHeight</c>);
    /// a missing, zero or negative value keeps the default.
    /// </summary>
    public class ImageDecodeLimits
    {
        /// <summary>Default for <see cref="MaxBytes"/>: 10 MB.</summary>
        public const long DefaultMaxBytes = 10L * 1024 * 1024;
        /// <summary>Default for <see cref="MaxWidth"/>.</summary>
        public const int DefaultMaxWidth = 8192;
        /// <summary>Default for <see cref="MaxHeight"/>.</summary>
        public const int DefaultMaxHeight = 8192;
        /// <summary>Default for <see cref="MaxPixels"/>.</summary>
        public const long DefaultMaxPixels = 25_000_000;
        /// <summary>Default for <see cref="MaxFrames"/>.</summary>
        public const int DefaultMaxFrames = 1;
        /// <summary>Default for <see cref="MaxResizeWidth"/>.</summary>
        public const int DefaultMaxResizeWidth = 4096;
        /// <summary>Default for <see cref="MaxResizeHeight"/>.</summary>
        public const int DefaultMaxResizeHeight = 4096;

        /// <summary>Largest file that is buffered and decoded.</summary>
        public long MaxBytes { get; set; } = DefaultMaxBytes;
        /// <summary>Largest width of an image that is decoded.</summary>
        public int MaxWidth { get; set; } = DefaultMaxWidth;
        /// <summary>Largest height of an image that is decoded.</summary>
        public int MaxHeight { get; set; } = DefaultMaxHeight;
        /// <summary>Largest width x height of an image that is decoded.</summary>
        public long MaxPixels { get; set; } = DefaultMaxPixels;
        /// <summary>Frames decoded from multi-frame images (animated GIF/WebP). Thumbnails only need the first frame.</summary>
        public int MaxFrames { get; set; } = DefaultMaxFrames;
        /// <summary>Upper bound for the requested thumbnail width.</summary>
        public int MaxResizeWidth { get; set; } = DefaultMaxResizeWidth;
        /// <summary>Upper bound for the requested thumbnail height.</summary>
        public int MaxResizeHeight { get; set; } = DefaultMaxResizeHeight;

        /// <summary>Reads the <c>ImageUploadLimits</c> section; every value that is missing or not a positive number keeps its default.</summary>
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
            if (int.TryParse(section["MaxFrames"], out var f) && f > 0) rv.MaxFrames = f;
            if (int.TryParse(section["MaxResizeWidth"], out var rw) && rw > 0) rv.MaxResizeWidth = rw;
            if (int.TryParse(section["MaxResizeHeight"], out var rh) && rh > 0) rv.MaxResizeHeight = rh;
            return rv;
        }
    }

    /// <summary>
    /// Validates format, size and dimensions from the image header before any full decode, and makes thumbnails within <see cref="ImageDecodeLimits"/>.
    /// Allowed formats: JPEG, PNG, GIF, BMP, WebP. The framework's image endpoints (<c>/_Framework/UploadImage</c>, <c>/_Framework/GetFile</c> with a
    /// size) and the template file API use it; an application that decodes pictures itself should go through it too.
    /// </summary>
    public static class ImageDecodeGuard
    {
        // Only the allowed formats are registered, so that Identify/Load never even parse TIFF, TGA, PBM, QOI...
        // (the default Configuration registers all of them).
        // Note: allowed decoders can carry ICC profiles; never read IccProfile.Entries on untrusted images.
        private static readonly Configuration RestrictedConfiguration = new Configuration(
            new JpegConfigurationModule(), new PngConfigurationModule(), new GifConfigurationModule(),
            new BmpConfigurationModule(), new WebpConfigurationModule());

        private static DecoderOptions Options(ImageDecodeLimits limits) => new DecoderOptions
        {
            Configuration = RestrictedConfiguration,
            MaxFrames = (uint)Math.Max(1, limits?.MaxFrames ?? ImageDecodeLimits.DefaultMaxFrames)
        };

        /// <summary>
        /// Validates the stream and, if it passes, fully decodes it. Returns false (image == null) otherwise.
        /// The input stream is buffered into memory (bounded by MaxBytes), so it need not be seekable.
        /// </summary>
        public static bool TryLoad(Stream input, ImageDecodeLimits limits, out Image image, out string error)
        {
            var ok = TryLoad(input, limits, out image, out var buffer, out error);
            buffer?.Dispose();
            return ok;
        }

        /// <summary>
        /// Same as the other overload, but hands the buffered copy of the input back to the caller (also when the
        /// image is rejected) so a non-seekable source that has been consumed can still be served. The caller
        /// owns and must dispose <paramref name="buffer"/>; it is null when the input was too large to buffer.
        /// </summary>
        public static bool TryLoad(Stream input, ImageDecodeLimits limits, out Image image, out MemoryStream buffer, out string error)
        {
            image = null;
            limits ??= new ImageDecodeLimits();
            if (!TryBuffer(input, limits, out buffer, out error))
            {
                return false;
            }
            if (!Validate(buffer, limits, out error))
            {
                buffer.Position = 0;
                return false;
            }
            buffer.Position = 0;
            try
            {
                image = Image.Load(Options(limits), buffer);
                buffer.Position = 0;
                return true;
            }
            catch (Exception)
            {
                buffer.Position = 0;
                error = "Invalid image";
                return false;
            }
        }

        /// <summary>Header-only validation (format allowlist, byte size, pixel dimensions). The stream must be seekable.</summary>
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
                var info = Image.Identify(Options(limits), seekable);
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

        /// <summary>
        /// Resolves the thumbnail size from the requested width/height (either may be null, not both) keeping the
        /// aspect ratio. Returns false when a value is out of range (&lt;= 0 or above the configured maximum).
        /// </summary>
        public static bool TryResolveSize(int? width, int? height, int sourceWidth, int sourceHeight, ImageDecodeLimits limits, out int targetWidth, out int targetHeight)
        {
            targetWidth = 0;
            targetHeight = 0;
            limits ??= new ImageDecodeLimits();
            if ((width == null && height == null) || sourceWidth <= 0 || sourceHeight <= 0)
            {
                return false;
            }
            if ((width != null && (width <= 0 || width > limits.MaxResizeWidth))
                || (height != null && (height <= 0 || height > limits.MaxResizeHeight)))
            {
                return false;
            }
            long w = width ?? Math.Max(1L, (long)height.Value * sourceWidth / sourceHeight);
            long h = height ?? Math.Max(1L, (long)width.Value * sourceHeight / sourceWidth);
            if (w > limits.MaxResizeWidth || h > limits.MaxResizeHeight)
            {
                return false;
            }
            targetWidth = (int)w;
            targetHeight = (int)h;
            return true;
        }

        /// <summary>
        /// Makes a JPEG thumbnail. The image is identified first and decoded only when it is within <paramref name="limits"/> and the requested size is in
        /// range. On success <see cref="SpaImageResult.Resized"/> is set; otherwise <see cref="SpaImageResult.Error"/> says why (a fixed text, never file
        /// content) and <see cref="SpaImageResult.Buffered"/> holds a seekable copy of the input (null when it was too large to buffer) so the caller can
        /// still serve the original. Nothing is thrown for a bad image.
        /// </summary>
        public static SpaImageResult Resize(Stream source, int? width, int? height, ImageDecodeLimits limits)
        {
            limits ??= new ImageDecodeLimits();
            if (!TryLoad(source, limits, out var image, out MemoryStream buffer, out var error) || image == null)
            {
                return new SpaImageResult { Buffered = buffer, Error = error };
            }
            using (image)
            {
                if (!TryResolveSize(width, height, image.Width, image.Height, limits, out var targetWidth, out var targetHeight))
                {
                    return new SpaImageResult { Buffered = buffer, Error = "Requested size out of range" };
                }
                var resized = new MemoryStream();
                try
                {
                    image.Mutate(x => x.Resize(targetWidth, targetHeight));
                    image.SaveAsJpeg(resized);
                }
                catch (Exception)
                {
                    resized.Dispose();
                    return new SpaImageResult { Buffered = buffer, Error = "Invalid image" };
                }
                resized.Position = 0;
                buffer?.Dispose();
                return new SpaImageResult { Resized = resized };
            }
        }

        /// <summary>A <see cref="SpaImageResizer"/> that uses <see cref="Resize"/> with the limits that <paramref name="limits"/> returns for each call.</summary>
        public static SpaImageResizer CreateResizer(Func<ImageDecodeLimits> limits)
        {
            return (source, width, height) => Resize(source, width, height, limits?.Invoke());
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
