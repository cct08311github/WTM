using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace WalkingTec.Mvvm.Mvc
{
    [Obsolete("Use WtmActionResult (via BaseController.FFResultJson()) instead. The FResult class emits a JavaScript response body that requires client-side script evaluation and blocks strict Content-Security-Policy. See issue #789 Phase 3C.", DiagnosticId = "WTM789")]
    public class FResult : ContentResult
    {
        public StringBuilder ContentBuilder { get; set; }
        public BaseController Controller { get; set; }

        public FResult() : this(string.Empty) { }

        public FResult(string content)
        {
            ContentBuilder = new StringBuilder(content);
        }

        public override Task ExecuteResultAsync(ActionContext context)
        {
            Content = ContentBuilder.ToString();
            return base.ExecuteResultAsync(context);
        }
    }
}
