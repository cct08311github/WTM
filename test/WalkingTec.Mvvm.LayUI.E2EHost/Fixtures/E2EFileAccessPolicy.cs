using System;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.LayUI.E2EHost.Fixtures
{
    /// <summary>
    /// Test only: an application rule for the file endpoints (<see cref="IFileAccessPolicy"/>) that the tests switch per request with the header
    /// <c>X-E2E-File-Policy</c>: <c>deny</c> refuses, <c>throw</c> fails, anything else allows.
    /// </summary>
    public class E2EFileAccessPolicy : IFileAccessPolicy
    {
        public bool IsAllowed(FileAccessRequest request)
        {
            var mode = request.Wtm?.HttpContext?.Request?.Headers["X-E2E-File-Policy"].ToString();
            if (string.Equals(mode, "throw", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("E2E policy failure");
            }
            return string.Equals(mode, "deny", StringComparison.OrdinalIgnoreCase) == false;
        }
    }
}
