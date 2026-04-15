#nullable enable
using System.Text.Json;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WalkingTec.Mvvm.Core
{
    public class CoreProgram
    {
        public static IStringLocalizer? _localizer {
            get;
            set;
        }

        // Issue #791: static utility classes (PropertyHelper, Utils, TypeExtension, CS,
        // JSON converters, QuartzHostService, ...) have no Wtm/ServiceProvider access
        // and previously silently swallowed exceptions. This factory is wired during
        // startup next to _localizer so those classes can emit real diagnostic logs.
        public static ILoggerFactory? _loggerFactory
        {
            get;
            set;
        }

        public static ILogger? GetLogger(string category) =>
            _loggerFactory?.CreateLogger(category);

        public static JsonSerializerOptions DefaultJsonOption
        {
            get;set;
        } = null!;

        public static JsonSerializerOptions DefaultPostJsonOption
        {
            get; set;
        } = null!;


        public static string[] Buildindll = new string[]
            {
                    "WalkingTec.Mvvm.Core",
                    "WalkingTec.Mvvm.Mvc",
                    "WalkingTec.Mvvm.Admin",
                    "WalkingTec.Mvvm.Taghelpers"
            };

    }
}
