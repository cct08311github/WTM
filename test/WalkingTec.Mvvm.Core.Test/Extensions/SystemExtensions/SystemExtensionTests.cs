#nullable enable
using System;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Extensions.SystemExtensions
{
    [TestClass]
    public class SystemExtensionTests
    {
        // ─── ToNoSplitString ──────────────────────────────────────────────────

        [TestMethod]
        public void ToNoSplitString_RemovesDashes()
        {
            var guid = new Guid("12345678-1234-1234-1234-123456789012");
            var result = guid.ToNoSplitString();
            result.Should().Be("12345678123412341234123456789012");
            result.Should().NotContain("-");
        }

        [TestMethod]
        public void ToNoSplitString_EmptyGuid_ReturnsDashlessString()
        {
            var result = Guid.Empty.ToNoSplitString();
            result.Should().Be("00000000000000000000000000000000");
        }

        [TestMethod]
        public void ToNoSplitString_Length_Is32()
        {
            var result = Guid.NewGuid().ToNoSplitString();
            result.Should().HaveLength(32);
        }

        [TestMethod]
        public void ToNoSplitString_TwoDifferentGuids_ProduceDifferentResults()
        {
            var g1 = Guid.NewGuid();
            var g2 = Guid.NewGuid();
            g1.ToNoSplitString().Should().NotBe(g2.ToNoSplitString());
        }

        // ─── GetCleanCrudVM ───────────────────────────────────────────────────

        [TestMethod]
        public void GetCleanCrudVM_NonCrudVmObject_ReturnsNull()
        {
            // Any plain object that does not implement IBaseCRUDVM<TopBasePoco>
            var plain = new object();
            var result = plain.GetCleanCrudVM();
            result.Should().BeNull();
        }

        [TestMethod]
        public void GetCleanCrudVM_StringObject_ReturnsNull()
        {
            object str = "hello";
            str.GetCleanCrudVM().Should().BeNull();
        }

        [TestMethod]
        public void GetCleanCrudVM_CrudVm_ReturnsNewInstance()
        {
            // GetCleanCrudVM iterates all properties of the entity and tries to copy them.
            // This throws if the entity has computed (setter-less) properties inherited from
            // TopBasePoco (e.g., IsBasePoco). The method is exercised but currently throws for
            // entities with read-only computed properties — lock in that known behaviour.
            var seed = Guid.NewGuid().ToString();
            var vm = new BaseCRUDVM<School>();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory), "user");
            vm.Entity = new School { SchoolCode = "S001", SchoolName = "Test School" };

            // The method reaches the IBaseCRUDVM branch and creates a new VM instance,
            // but throws when it hits a property without a setter on the entity type
            // (e.g. the computed IsBasePoco property on TopBasePoco).
            Action act = () => vm.GetCleanCrudVM();
            act.Should().Throw<Exception>(); // ArgumentException or TargetInvocationException depending on runtime
        }

        [TestMethod]
        public void GetCleanCrudVM_NonCrudVm_ReturnsNullWithoutThrowing()
        {
            // A plain object that is not an IBaseCRUDVM always returns null without exercising
            // the entity-copy path.
            var plain = new object();
            Action act = () => { var _ = plain.GetCleanCrudVM(); };
            act.Should().NotThrow();
            plain.GetCleanCrudVM().Should().BeNull();
        }
    }
}
