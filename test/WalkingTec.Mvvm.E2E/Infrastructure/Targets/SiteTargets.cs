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
            CollectJsCoverage = true,
            ConfigureEnvironment = LayUiSetup.ConfigureEnvironment
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

        // ---- instances for the permission matrix (#273) ------------------------------------------------------------------------------
        // The matrix calls every action of a site with harmless but real requests (an administrator's empty create request creates an empty
        // row, a logout ends a session, one test fixture action switches a host option). It therefore runs against sites of its own: the
        // browser tests keep their data, the route coverage baseline stays what the tests of the feature reach, and the matrix instances
        // are started only when a matrix test runs.

        /// <summary>
        /// The LayUI test host with a request culture that has a decimal comma (Languages=de-DE), for the tests of values that are
        /// written into pages and posted back whatever the regional number format of the server is (#322). The browser context stays en-US.
        /// </summary>
        public static readonly SiteTarget LayUIDecimalComma = new SiteTarget
        {
            Name = "layui-e2ehost-decimal-comma",
            ProjectDir = LayUI.ProjectDir,
            AssemblyName = LayUI.AssemblyName,
            RouteExclusionFile = LayUI.RouteExclusionFile,
            ReadyPath = LayUI.ReadyPath,
            ReportRouteCoverage = false,
            ConfigureEnvironment = (site, env) => { LayUiSetup.ConfigureEnvironment(site, env); env["Languages"] = "de-DE"; }
        };

        /// <summary>A second instance of the LayUI test host for the permission matrix.</summary>
        public static readonly SiteTarget LayUIMatrix = new SiteTarget
        {
            Name = "layui-e2ehost-matrix",
            ProjectDir = LayUI.ProjectDir,
            AssemblyName = LayUI.AssemblyName,
            RouteExclusionFile = LayUI.RouteExclusionFile,
            ReadyPath = LayUI.ReadyPath,
            ReportRouteCoverage = false,
            // the table states the outcomes of the default mode of the file endpoints, whatever mode the whole run was started in
            ConfigureEnvironment = (site, env) => { LayUiSetup.ConfigureEnvironment(site, env); env["FileEndpoints__Mode"] = "Audit"; }
        };

        /// <summary>The LayUI test host with FileEndpoints:Mode=Enforce for the permission matrix: the table lists the differences to the default.</summary>
        public static readonly SiteTarget LayUIMatrixFileEnforce = new SiteTarget
        {
            Name = "layui-e2ehost-matrix-file-enforce",
            ProjectDir = LayUI.ProjectDir,
            AssemblyName = LayUI.AssemblyName,
            RouteExclusionFile = LayUI.RouteExclusionFile,
            ReadyPath = LayUI.ReadyPath,
            ReportRouteCoverage = false,
            ConfigureEnvironment = (site, env) => { LayUiSetup.ConfigureEnvironment(site, env); env["FileEndpoints__Mode"] = "Enforce"; }
        };

        /// <summary>A second instance of the Vue3Demo for the permission matrix (workflow feature off, the default).</summary>
        public static readonly SiteTarget Vue3DemoMatrix = new SiteTarget
        {
            Name = "vue3demo-matrix",
            ProjectDir = Vue3Demo.ProjectDir,
            AssemblyName = Vue3Demo.AssemblyName,
            RouteExclusionFile = Vue3Demo.RouteExclusionFile,
            ReadyPath = Vue3Demo.ReadyPath,
            AdminAccount = Vue3Demo.AdminAccount,
            ReportRouteCoverage = false,
            ConfigureEnvironment = (site, env) => Vue3DemoSetup.ConfigureEnvironment(Vue3DemoMatrix, site, env),
            AfterStartAsync = Vue3DemoSetup.InstallAdminPasswordAsync
        };

        /// <summary>The Vue3Demo with FileEndpoints:Mode=Enforce for the permission matrix: the table lists the differences to the default.</summary>
        public static readonly SiteTarget Vue3DemoMatrixFileEnforce = new SiteTarget
        {
            Name = "vue3demo-matrix-file-enforce",
            ProjectDir = Vue3Demo.ProjectDir,
            AssemblyName = Vue3Demo.AssemblyName,
            RouteExclusionFile = Vue3Demo.RouteExclusionFile,
            ReadyPath = Vue3Demo.ReadyPath,
            AdminAccount = Vue3Demo.AdminAccount,
            ReportRouteCoverage = false,
            ConfigureEnvironment = (site, env) =>
            {
                Vue3DemoSetup.ConfigureEnvironment(Vue3DemoMatrixFileEnforce, site, env);
                env["FileEndpoints__Mode"] = "Enforce";
            },
            AfterStartAsync = Vue3DemoSetup.InstallAdminPasswordAsync
        };

        /// <summary>
        /// The Vue3Demo started with EnableWorkflow=true (the demo registers the Elsa workflow engine when the setting is on), for the
        /// permission matrix: which endpoints appear, disappear or change their outcome with the feature on.
        /// </summary>
        public static readonly SiteTarget Vue3DemoMatrixWithWorkflow = new SiteTarget
        {
            Name = "vue3demo-matrix-workflow",
            ProjectDir = Vue3Demo.ProjectDir,
            AssemblyName = Vue3Demo.AssemblyName,
            RouteExclusionFile = Vue3Demo.RouteExclusionFile,
            ReadyPath = Vue3Demo.ReadyPath,
            AdminAccount = Vue3Demo.AdminAccount,
            ReportRouteCoverage = false,
            ConfigureEnvironment = (site, env) =>
            {
                Vue3DemoSetup.ConfigureEnvironment(Vue3DemoMatrixWithWorkflow, site, env);
                env["EnableWorkflow"] = "true";
            },
            AfterStartAsync = Vue3DemoSetup.InstallAdminPasswordAsync
        };
    }
}
