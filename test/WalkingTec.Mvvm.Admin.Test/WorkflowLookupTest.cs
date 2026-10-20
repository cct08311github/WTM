using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.WorkFlow;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>
    /// #13: roles/groups/users for the approve activity come straight from the database, not from an anonymous HTTP self-call.
    /// The mock context has no service provider, so the old HTTP path could only ever return an empty list.
    /// </summary>
    [TestClass]
    public class WorkflowLookupTest
    {
        private readonly string _seed = Guid.NewGuid().ToString();

        private WTMContext CreateWtm()
        {
            var wtm = MockWtmContext.CreateWtmContext(new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory), "user");
            wtm.GlobaInfo.CustomUserType = typeof(FrameworkUser);
            return wtm;
        }

        private void Seed()
        {
            using var dc = new WalkingTec.Mvvm.Vue3Demo.DataContext(_seed, DBTypeEnum.Memory);
            dc.Set<FrameworkRole>().Add(new FrameworkRole { ID = Guid.NewGuid(), RoleCode = "r2", RoleName = "B-role" });
            dc.Set<FrameworkRole>().Add(new FrameworkRole { ID = Guid.NewGuid(), RoleCode = "r1", RoleName = "A-role" });
            dc.Set<FrameworkGroup>().Add(new FrameworkGroup { ID = Guid.NewGuid(), GroupCode = "g2", GroupName = "B-group", Manager = "mgr2" });
            dc.Set<FrameworkGroup>().Add(new FrameworkGroup { ID = Guid.NewGuid(), GroupCode = "g1", GroupName = "A-group", Manager = "mgr1" });
            dc.Set<FrameworkUserGroup>().Add(new FrameworkUserGroup { UserCode = "u1", GroupCode = "g1" });
            dc.Set<FrameworkUser>().Add(new FrameworkUser { ITCode = "u2", Name = "Two", Password = "p" });
            dc.Set<FrameworkUser>().Add(new FrameworkUser { ITCode = "u1", Name = "One", Password = "p" });
            dc.SaveChanges();
        }

        private static object GetOptions(WTMContext wtm, string property)
        {
            return new WtmApproveActivity(wtm).GetOptions(typeof(WtmApproveActivity).GetProperty(property));
        }

        [TestMethod]
        public void ApproveRoles_OptionsComeFromDatabase_SortedByName()
        {
            Seed();
            var rv = (List<ComboSelectListItem>)GetOptions(CreateWtm(), nameof(WtmApproveActivity.ApproveRoles));
            CollectionAssert.AreEqual(new[] { "A-role", "B-role" }, rv.Select(x => x.Text).ToArray());
            Assert.IsTrue(rv.All(x => Guid.TryParse(x.Value.ToString(), out _)));
        }

        [TestMethod]
        public void ApproveGroups_OptionsComeFromDatabase_SortedByName()
        {
            Seed();
            var rv = (List<ComboSelectListItem>)GetOptions(CreateWtm(), nameof(WtmApproveActivity.ApproveGroups));
            CollectionAssert.AreEqual(new[] { "A-group", "B-group" }, rv.Select(x => x.Text).ToArray());
        }

        [TestMethod]
        public void ApproveManagers_OptionsAreGroupsWithSuffix()
        {
            Seed();
            var rv = (List<ComboSelectListItem>)GetOptions(CreateWtm(), nameof(WtmApproveActivity.ApproveManagers));
            CollectionAssert.AreEqual(new[] { "A-group主管", "B-group主管" }, rv.Select(x => x.Text).ToArray());
        }

        [TestMethod]
        public void Options_WithEmptyDatabase_AreEmpty()
        {
            var rv = (List<ComboSelectListItem>)GetOptions(CreateWtm(), nameof(WtmApproveActivity.ApproveRoles));
            Assert.AreEqual(0, rv.Count);
        }

        [TestMethod]
        public void Lookup_FiltersByIdsAndItcodes()
        {
            Seed();
            var wtm = CreateWtm();
            var groupId = wtm.DC.Set<FrameworkGroup>().Where(x => x.GroupName == "B-group").Select(x => x.ID).First();
            var groups = WorkflowLookup.GetGroups(wtm, new[] { groupId.ToString() });
            Assert.AreEqual(1, groups.Count);
            Assert.AreEqual("B-group", groups[0].Text);
            Assert.AreEqual("mgr2", WorkflowLookup.GetGroupManagers(wtm, new[] { groupId.ToString() })[0].Text);

            var users = WorkflowLookup.GetUsers(wtm, new[] { "u1" });
            Assert.AreEqual(1, users.Count);
            Assert.AreEqual("One", users[0].Text);
            Assert.AreEqual("u1", users[0].Value);
            Assert.AreEqual(2, WorkflowLookup.GetUsers(wtm, null).Count);

            Assert.AreEqual("mgr1", WorkflowLookup.GetMyGroupManagers(wtm, "u1")[0].Text);
            Assert.AreEqual(0, WorkflowLookup.GetMyGroupManagers(wtm, "nobody").Count);
        }
    }
}
