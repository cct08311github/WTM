#nullable enable
using System.Linq;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.RateLimiting;

namespace WalkingTec.Mvvm.Mvc
{
    /// <summary>
    /// Adapts <see cref="WtmRateLimitAttribute"/> (WTM-flavored, friendly
    /// ctor args) into ASP.NET Core's built-in
    /// <see cref="EnableRateLimitingAttribute"/> (sealed, string-based
    /// policy name) by augmenting action metadata at model-build time.
    /// Introduced by issue #828.
    /// </summary>
    /// <remarks>
    /// For each action that carries (directly or via controller) a
    /// <see cref="WtmRateLimitAttribute"/>, this convention injects an
    /// <see cref="EnableRateLimitingAttribute"/> with a matching policy
    /// name into the action's <see cref="ActionModel.Selectors"/>
    /// endpoint metadata. The ASP.NET Core rate-limiter middleware then
    /// discovers the policy and applies it at runtime.
    /// <para>
    /// Action-level <see cref="WtmRateLimitAttribute"/> overrides a
    /// controller-level one — ASP.NET Core metadata resolution
    /// naturally favours the closest scope.
    /// </para>
    /// </remarks>
    public sealed class WtmRateLimitConvention : IActionModelConvention
    {
        public void Apply(ActionModel action)
        {
            // Action-level attribute wins over controller-level — check
            // action first, fall back to controller if absent.
            var attr = action.Attributes.OfType<WtmRateLimitAttribute>().FirstOrDefault()
                       ?? action.Controller.Attributes.OfType<WtmRateLimitAttribute>().FirstOrDefault();

            if (attr == null) { return; }

            var enable = new EnableRateLimitingAttribute(attr.PolicyName);
            foreach (var selector in action.Selectors)
            {
                selector.EndpointMetadata.Add(enable);
            }
        }
    }
}
