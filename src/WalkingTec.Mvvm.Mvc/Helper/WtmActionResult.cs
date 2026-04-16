#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// A CSP-safe <see cref="ActionResult"/> that serializes a list of
    /// <see cref="WtmAction"/>s as JSON and signals the client via the
    /// <c>X-WTM-Action: application/json</c> response header.
    /// </summary>
    /// <remarks>
    /// Introduced in issue #789 Phase 3C as the successor to
    /// <see cref="FResult"/>. The client-side <c>ff.DispatchAction</c>
    /// handler in <c>framework_layui.js</c> reads the header, parses the
    /// JSON body, and dispatches each action through a whitelist of
    /// declarative handlers - no eval(), no dynamic code execution.
    /// </remarks>
    public class WtmActionResult : ActionResult
    {
        public List<WtmAction> Actions { get; } = new();

        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Issue #806: WtmActionType serializes as camelCase strings
            // ("alert", "closeDialog", "refreshGrid", ...) matching the
            // literals in framework_layui.js's dispatcher switch.
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        public override async Task ExecuteResultAsync(ActionContext context)
        {
            var response = context.HttpContext.Response;
            response.Headers["X-WTM-Action"] = "application/json";
            response.ContentType = "application/json; charset=utf-8";
            response.StatusCode = StatusCodes.Status200OK;

            var payload = new { actions = Actions };
            var json = JsonSerializer.Serialize(payload, s_jsonOptions);
            await response.WriteAsync(json);
        }
    }
}
