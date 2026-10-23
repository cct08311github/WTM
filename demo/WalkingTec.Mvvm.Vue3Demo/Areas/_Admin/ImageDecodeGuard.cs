using System.IO;
using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using WalkingTec.Mvvm.Mvc;
using Framework = WalkingTec.Mvvm.Mvc.Imaging;

namespace WalkingTec.Mvvm.Admin.Api
{
    /// <summary>
    /// Limits applied before user supplied bytes are handed to the ImageSharp decoder.
    /// Configurable through the "ImageUploadLimits" configuration section. The rules live in the framework
    /// (<see cref="Framework.ImageDecodeLimits"/>), which the framework's own image endpoints use as well; this type
    /// keeps the names and the API that existing copies of the template code rely on.
    /// </summary>
    public class ImageDecodeLimits : Framework.ImageDecodeLimits
    {
        public static new ImageDecodeLimits FromConfiguration(IConfiguration configuration)
        {
            var from = Framework.ImageDecodeLimits.FromConfiguration(configuration);
            return new ImageDecodeLimits
            {
                MaxBytes = from.MaxBytes,
                MaxWidth = from.MaxWidth,
                MaxHeight = from.MaxHeight,
                MaxPixels = from.MaxPixels,
                MaxFrames = from.MaxFrames,
                MaxResizeWidth = from.MaxResizeWidth,
                MaxResizeHeight = from.MaxResizeHeight
            };
        }
    }

    /// <summary>
    /// Mitigation for ImageSharp 3.1.x advisories (see issue #78): validates format, size and
    /// dimensions from the image header before any full decode of user supplied data.
    /// Allowed formats: JPEG, PNG, GIF, BMP, WebP. Forwards to <see cref="Framework.ImageDecodeGuard"/>.
    /// </summary>
    public static class ImageDecodeGuard
    {
        public static bool TryLoad(Stream input, Framework.ImageDecodeLimits limits, out Image image, out string error)
            => Framework.ImageDecodeGuard.TryLoad(input, limits, out image, out error);

        public static bool TryLoad(Stream input, Framework.ImageDecodeLimits limits, out Image image, out MemoryStream buffer, out string error)
            => Framework.ImageDecodeGuard.TryLoad(input, limits, out image, out buffer, out error);

        public static bool Validate(Stream seekable, Framework.ImageDecodeLimits limits, out string error)
            => Framework.ImageDecodeGuard.Validate(seekable, limits, out error);

        public static bool TryResolveSize(int? width, int? height, int sourceWidth, int sourceHeight, Framework.ImageDecodeLimits limits, out int targetWidth, out int targetHeight)
            => Framework.ImageDecodeGuard.TryResolveSize(width, height, sourceWidth, sourceHeight, limits, out targetWidth, out targetHeight);

        public static SpaImageResult Resize(Stream source, int? width, int? height, Framework.ImageDecodeLimits limits)
            => Framework.ImageDecodeGuard.Resize(source, width, height, limits);
    }
}
