using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.LayUI.E2EHost.Fixtures
{
    /// <summary>
    /// Test only: the default upload rule (<see cref="ConfiguredUploadFileRule"/>, with the list of <c>FileEndpoints:UploadAllowList</c> from the
    /// settings of this host), in force only for requests that carry the header <c>X-E2E-Upload-AllowList</c>. The rest of the test run
    /// therefore uploads as it does without a rule, and the tests that exercise the allow-list switch it on per request.
    /// </summary>
    public class E2EUploadFileRule : IUploadFileRule
    {
        public const string Header = "X-E2E-Upload-AllowList";

        private readonly ConfiguredUploadFileRule _inner = new ConfiguredUploadFileRule();

        public bool IsActive(WTMContext wtm)
        {
            return string.IsNullOrEmpty(wtm?.HttpContext?.Request?.Headers[Header].ToString()) == false && _inner.IsActive(wtm);
        }

        public bool IsAllowed(UploadFileRequest request) => _inner.IsAllowed(request);
    }
}
