#nullable enable
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.Mvc
{
    /// <summary>
    /// Tests for RequestExtension.ToActionResult() — the pure, side-effect-free
    /// portion of RequestExtension.  The RedirectCall() method requires a full
    /// WTMContext + HTTP pipeline and is therefore not covered here.
    /// </summary>
    [TestClass]
    public class RequestExtensionTests
    {
        // ── ToActionResult ────────────────────────────────────────────────────

        [TestMethod]
        public void ToActionResult_null_returns_BadRequestResult()
        {
            ApiResult<string>? result = null;
            var actionResult = result.ToActionResult();
            Assert.IsInstanceOfType<BadRequestResult>(actionResult);
        }

        [TestMethod]
        public void ToActionResult_OK_returns_OkObjectResult_with_data()
        {
            var result = new ApiResult<string>
            {
                StatusCode = HttpStatusCode.OK,
                Data = "hello"
            };
            var actionResult = result.ToActionResult();
            var ok = actionResult as OkObjectResult;
            Assert.IsNotNull(ok, "Expected OkObjectResult");
            Assert.AreEqual("hello", ok.Value);
        }

        [TestMethod]
        public void ToActionResult_Unauthorized_returns_UnauthorizedResult()
        {
            var result = new ApiResult<string> { StatusCode = HttpStatusCode.Unauthorized };
            var actionResult = result.ToActionResult();
            Assert.IsInstanceOfType<UnauthorizedResult>(actionResult);
        }

        [TestMethod]
        public void ToActionResult_Forbidden_returns_ForbidResult()
        {
            var result = new ApiResult<string> { StatusCode = HttpStatusCode.Forbidden };
            var actionResult = result.ToActionResult();
            Assert.IsInstanceOfType<ForbidResult>(actionResult);
        }

        [TestMethod]
        public void ToActionResult_BadRequest_returns_BadRequestObjectResult_with_errors()
        {
            var errors = new ErrorObj();
            var result = new ApiResult<string>
            {
                StatusCode = HttpStatusCode.BadRequest,
                Errors = errors
            };
            var actionResult = result.ToActionResult();
            var br = actionResult as BadRequestObjectResult;
            Assert.IsNotNull(br, "Expected BadRequestObjectResult");
            Assert.AreSame(errors, br.Value);
        }

        [TestMethod]
        public void ToActionResult_unknown_status_code_returns_BadRequestResult()
        {
            var result = new ApiResult<string> { StatusCode = HttpStatusCode.InternalServerError };
            var actionResult = result.ToActionResult();
            Assert.IsInstanceOfType<BadRequestResult>(actionResult);
        }

        [TestMethod]
        public void ToActionResult_null_status_returns_BadRequestResult()
        {
            // StatusCode is null (not set)
            var result = new ApiResult<string> { StatusCode = null };
            var actionResult = result.ToActionResult();
            Assert.IsInstanceOfType<BadRequestResult>(actionResult);
        }
    }
}
