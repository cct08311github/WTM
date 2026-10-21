using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Security;

namespace WalkingTec.Mvvm.E2E.Tests.Security
{
    /// <summary>
    /// Permission matrix (#273) of the LayUI test host: every action of its route inventory is called with plain HTTP by an anonymous caller,
    /// an account without role, an account with a limited role and an administrator, and the outcome is compared with the checked-in table
    /// (Security/layui-e2ehost.permission-matrix.json). The requests are harmless (ids that do not exist, empty bodies) and only the outcome
    /// type is looked at.
    /// </summary>
    [TestClass]
    [TestCategory("permission-matrix")]
    public class LayUiPermissionMatrixTests
    {
        public TestContext TestContext { get; set; }

        public static IEnumerable<string> Identities => new[] { "anonymous", "norole", "limited", "admin" };

        [TestMethod]
        [DynamicData(nameof(Identities))]
        public async Task Outcomes_MatchTheTable(string identity)
        {
            MatrixAssertions.AssertIdentityMatches(await MatrixSuite.GetAsync(MatrixSetups.LayUi), identity, TestContext);
        }

        [TestMethod]
        public async Task Table_HasAnEntryForEveryAction() => MatrixAssertions.AssertEveryActionHasAnEntry(await MatrixSuite.GetAsync(MatrixSetups.LayUi));

        [TestMethod]
        public async Task Table_HasNoStaleEntries() => MatrixAssertions.AssertNoStaleEntries(await MatrixSuite.GetAsync(MatrixSetups.LayUi));

        [TestMethod]
        public async Task Table_FollowsTheDeclaredRules() => MatrixAssertions.AssertTableFollowsDeclaredRules(await MatrixSuite.GetAsync(MatrixSetups.LayUi));
    }

    /// <summary>The same for the Vue3Demo API (bearer token; the cookie of the administrator is a fifth identity) with the workflow feature off.</summary>
    [TestClass]
    [TestCategory("permission-matrix")]
    [TestCategory("vue3demo")]
    public class Vue3PermissionMatrixTests
    {
        public TestContext TestContext { get; set; }

        public static IEnumerable<string> Identities => new[] { "anonymous", "norole", "limited", "admin", "admin-cookie" };

        [TestMethod]
        [DynamicData(nameof(Identities))]
        public async Task Outcomes_MatchTheTable(string identity)
        {
            MatrixAssertions.AssertIdentityMatches(await MatrixSuite.GetAsync(MatrixSetups.Vue3), identity, TestContext);
        }

        [TestMethod]
        public async Task Table_HasAnEntryForEveryAction() => MatrixAssertions.AssertEveryActionHasAnEntry(await MatrixSuite.GetAsync(MatrixSetups.Vue3));

        [TestMethod]
        public async Task Table_HasNoStaleEntries() => MatrixAssertions.AssertNoStaleEntries(await MatrixSuite.GetAsync(MatrixSetups.Vue3));

        [TestMethod]
        public async Task Table_FollowsTheDeclaredRules() => MatrixAssertions.AssertTableFollowsDeclaredRules(await MatrixSuite.GetAsync(MatrixSetups.Vue3));
    }

    /// <summary>
    /// The Vue3Demo started with EnableWorkflow=true: the endpoints of the workflow engine appear, the stub that answers 404 for the
    /// workflow lookup routes disappears, and the outcomes of the workflow routes change. The table lists the differences to the default
    /// configuration (section "configurations").
    /// </summary>
    [TestClass]
    [TestCategory("permission-matrix")]
    [TestCategory("vue3demo")]
    public class Vue3WorkflowPermissionMatrixTests
    {
        public TestContext TestContext { get; set; }

        public static IEnumerable<string> Identities => new[] { "anonymous", "norole", "limited", "admin", "admin-cookie" };

        [TestMethod]
        [DynamicData(nameof(Identities))]
        public async Task Outcomes_MatchTheTable(string identity)
        {
            MatrixAssertions.AssertIdentityMatches(await MatrixSuite.GetAsync(MatrixSetups.Vue3WithWorkflow), identity, TestContext);
        }

        [TestMethod]
        public async Task Table_HasAnEntryForEveryAction() => MatrixAssertions.AssertEveryActionHasAnEntry(await MatrixSuite.GetAsync(MatrixSetups.Vue3WithWorkflow));

        [TestMethod]
        public async Task Table_HasNoStaleEntries() => MatrixAssertions.AssertNoStaleEntries(await MatrixSuite.GetAsync(MatrixSetups.Vue3WithWorkflow));

        [TestMethod]
        public async Task Table_FollowsTheDeclaredRules() => MatrixAssertions.AssertTableFollowsDeclaredRules(await MatrixSuite.GetAsync(MatrixSetups.Vue3WithWorkflow));
    }
}
