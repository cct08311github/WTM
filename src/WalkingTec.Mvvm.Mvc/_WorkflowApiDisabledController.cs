using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Answers every request under /_WorkflowApi with 404 while workflow is disabled (#210). It is registered together with
    /// <see cref="WorkflowApiDisabledConvention"/> (EnableWorkflow=false) and removed by <see cref="WorkflowApiStubRemovalConvention"/>
    /// when workflow is enabled, so exactly one controller owns the _WorkflowApi route. Without it, hosts with an SPA wildcard
    /// (MapFallbackToFile) would answer /_WorkflowApi/* with index.html (200) instead of 404.
    /// Deliberately not an IBaseController: it never touches WTMContext, the data context or any query.
    /// Attribute routes take precedence over MapFallbackToFile (fallback order is int.MaxValue).
    /// </summary>
    [Public]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class WorkflowApiDisabledController : ControllerBase
    {
        [Route("_WorkflowApi")]
        [Route("_WorkflowApi/{**path}")]
        [AcceptVerbs("GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS")]
        [Public]
        [NoLog]
        public IActionResult NotAvailable(string path)
        {
            return NotFound();
        }
    }
}
