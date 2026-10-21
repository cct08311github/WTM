namespace WalkingTec.Mvvm.E2E.Infrastructure
{
    public static class SiteTargets
    {
        /// <summary>
        /// test/WalkingTec.Mvvm.LayUI.E2EHost: server rendered LayUI site (TagHelpers.LayUI + framework_layui.js).
        /// </summary>
        public static readonly SiteTarget LayUI = new SiteTarget
        {
            Name = "layui-e2ehost",
            ProjectDir = "test/WalkingTec.Mvvm.LayUI.E2EHost",
            AssemblyName = "WalkingTec.Mvvm.LayUI.E2EHost",
            RouteExclusionFile = "layui-e2ehost.route-exclusions.json",
            ReadyPath = "/Login/Login"
        };

        // Future: public static readonly SiteTarget Vue3Demo = ... (needs the demo to expose the same E2E:* settings,
        // or a hosting startup assembly that adds the instrumentation without touching demo/**).
    }
}
