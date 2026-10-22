using System;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Marks an action of a controller as a file endpoint of a single page application: it serves or stores files with the helper
    /// <see cref="SpaFileApi"/> (references, <c>FileEndpoints:Mode</c>, connections), which makes the decision itself.
    /// The marker is what the framework looks for; without it a controller routed at <c>api/_file</c> that exposes the file actions is reported at startup.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class FileEndpointAttribute : Attribute
    {
        /// <summary>
        /// When true and <c>IsFilePublic</c> is on, a caller who is not signed in is let through to the action (the privilege filter treats it like a
        /// public action); the endpoint still decides whether to serve the file. Default false. Does not by itself allow anonymous calls: that is
        /// <see cref="WalkingTec.Mvvm.Core.PublicAttribute"/>, as before.
        /// </summary>
        public bool PublicWhenFilesPublic { get; set; }
    }
}
