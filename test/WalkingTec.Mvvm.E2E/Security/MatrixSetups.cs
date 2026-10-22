using System.Collections.Generic;
using WalkingTec.Mvvm.E2E.Infrastructure;

namespace WalkingTec.Mvvm.E2E.Security
{
    /// <summary>The sites and configurations the permission matrix runs against.</summary>
    public static class MatrixSetups
    {
        /// <summary>LayUI test host: server rendered pages, session cookie.</summary>
        public static readonly MatrixSetup LayUi = new MatrixSetup
        {
            Site = SiteTargets.LayUIMatrix,
            TableFile = "layui-e2ehost.permission-matrix.json",
            OpenIdentities = MatrixAccounts.LayUiAsync,
            LoginPaths = new[] { "/Login/Login" },
            Kinds = new Dictionary<string, IdentityKind>
            {
                ["anonymous"] = IdentityKind.Anonymous,
                ["norole"] = IdentityKind.Cookie,
                ["limited"] = IdentityKind.Cookie,
                ["admin"] = IdentityKind.Cookie,
            },
            // without its argument this test fixture action switches the HTML encoding option of the whole host off
            QueryOverrides = new Dictionary<string, string> { ["lab/htmlencoding GET"] = "on=true" },
            SessionActions = new[] { "login/logout GET" },
            // the list page and the read-only actions the host gives the role of the limited account (E2ESeed.LimitedRoleActions)
            LimitedGrants = new[] { "gadget/index", "gadget/search", "gadget/details", "gadget/exportexcel" },
        };

        /// <summary>The LayUI test host with FileEndpoints:Mode=Enforce; the table lists the differences to the default (section "configurations").</summary>
        public static readonly MatrixSetup LayUiFileEnforce = new MatrixSetup
        {
            Site = SiteTargets.LayUIMatrixFileEnforce,
            Configuration = "file-enforce",
            TableFile = LayUi.TableFile,
            OpenIdentities = LayUi.OpenIdentities,
            LoginPaths = LayUi.LoginPaths,
            Kinds = LayUi.Kinds,
            QueryOverrides = LayUi.QueryOverrides,
            SessionActions = LayUi.SessionActions,
            LimitedGrants = LayUi.LimitedGrants,
        };

        private static readonly IReadOnlyDictionary<string, IdentityKind> Vue3Kinds = new Dictionary<string, IdentityKind>
        {
            ["anonymous"] = IdentityKind.Anonymous,
            ["norole"] = IdentityKind.Bearer,
            ["limited"] = IdentityKind.Bearer,
            ["admin"] = IdentityKind.Bearer,
            ["admin-cookie"] = IdentityKind.Cookie,
        };

        // the actions MatrixAccounts.LimitedRoleUrls give the role of the limited account
        private static readonly string[] Vue3LimitedGrants =
        {
            "schooldata/city/search", "schooldata/city/get", "school/search", "frameworkrole/search", "frameworkrole/get",
        };

        private static readonly string[] Vue3SessionActions = { "account/logout GET", "account/refreshtoken POST" };

        /// <summary>Vue3Demo with the workflow feature off (the default).</summary>
        public static readonly MatrixSetup Vue3 = new MatrixSetup
        {
            Site = SiteTargets.Vue3DemoMatrix,
            TableFile = "vue3demo.permission-matrix.json",
            OpenIdentities = MatrixAccounts.Vue3Async,
            LoginPaths = new[] { "/_Framework/Redirect401", "/Login/Login" },
            Kinds = Vue3Kinds,
            SessionActions = Vue3SessionActions,
            LimitedGrants = Vue3LimitedGrants,
        };

        /// <summary>Vue3Demo with EnableWorkflow=true; the table of this configuration lists the differences to the default.</summary>
        public static readonly MatrixSetup Vue3WithWorkflow = new MatrixSetup
        {
            Site = SiteTargets.Vue3DemoMatrixWithWorkflow,
            Configuration = "workflow-enabled",
            TableFile = "vue3demo.permission-matrix.json",
            OpenIdentities = MatrixAccounts.Vue3Async,
            LoginPaths = new[] { "/_Framework/Redirect401", "/Login/Login" },
            Kinds = Vue3Kinds,
            SessionActions = Vue3SessionActions,
            LimitedGrants = Vue3LimitedGrants,
        };
    }
}
