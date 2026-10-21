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
            ReadyPath = "/Login/Login",
            CollectJsCoverage = true
        };

        /// <summary>
        /// demo/WalkingTec.Mvvm.Vue3Demo: Element Plus SPA (ClientApp built with Vite) plus the ASP.NET Core API. The demo is not
        /// changed; see <see cref="Vue3DemoSetup"/> for how it is configured and instrumented.
        /// </summary>
        public static readonly SiteTarget Vue3Demo = new SiteTarget
        {
            Name = "vue3demo",
            ProjectDir = "demo/WalkingTec.Mvvm.Vue3Demo",
            AssemblyName = "WalkingTec.Mvvm.Vue3Demo",
            RouteExclusionFile = "vue3demo.route-exclusions.json",
            ReadyPath = "/",
            AdminAccount = "admin",
            ConfigureEnvironment = (site, env) => Vue3DemoSetup.ConfigureEnvironment(Vue3Demo, site, env),
            AfterStartAsync = Vue3DemoSetup.InstallAdminPasswordAsync
        };
    }
}
