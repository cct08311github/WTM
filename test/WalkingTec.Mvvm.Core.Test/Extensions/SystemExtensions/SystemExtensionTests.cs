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

        // ─── Entity with computed get-only property (IsBasePoco regression) ─────

        private class EntityWithComputedProp : TopBasePoco
        {
            public string? Name { get; set; }
            // Inherits computed get-only IsBasePoco from TopBasePoco — the source of the bug.
        }

        private class CrudVmWithComputedProp : BaseCRUDVM<EntityWithComputedProp> { }

        [TestMethod]
        public void GetCleanCrudVM_EntityWithComputedGetOnlyProperty_DoesNotThrow()
        {
            // Regression test for Issue #44:
            // GetCleanCrudVM threw ArgumentException ("Property set method not found") when
            // the entity type had a computed get-only property (e.g. TopBasePoco.IsBasePoco).
            // The fix adds `if (pro.CanWrite == false) continue;` at the top of the inner loop.
            //
            // This test FAILS (ArgumentException) against the unfixed code and PASSES after fix.
            var seed = Guid.NewGuid().ToString();
            var vm = new CrudVmWithComputedProp();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory), "user");
            vm.Entity = new EntityWithComputedProp { Name = "hello" };

            Action act = () => vm.GetCleanCrudVM();
            act.Should().NotThrow("computed get-only properties must be skipped, not set");
        }

        [TestMethod]
        public void GetCleanCrudVM_EntityWithComputedGetOnlyProperty_CopiesWritableProperties()
        {
            // Companion to the regression test: once the fix prevents the throw, the returned
            // VM's Entity must have the writable property copied across correctly.
            var seed = Guid.NewGuid().ToString();
            var vm = new CrudVmWithComputedProp();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory), "user");
            vm.Entity = new EntityWithComputedProp { Name = "copied-value" };

            var clean = vm.GetCleanCrudVM();

            clean.Should().NotBeNull();
            var cleanVm = clean as CrudVmWithComputedProp;
            cleanVm.Should().NotBeNull();
            cleanVm!.Entity.Name.Should().Be("copied-value");
        }

        [TestMethod]
        public void GetCleanCrudVM_CrudVm_ReturnsNewInstance()
        {
            // Smoke test: GetCleanCrudVM returns a non-null new instance for a valid CrudVM.
            var seed = Guid.NewGuid().ToString();
            var vm = new BaseCRUDVM<School>();
            vm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(seed, DBTypeEnum.Memory), "user");
            vm.Entity = new School { SchoolCode = "S001", SchoolName = "Test School" };

            var result = vm.GetCleanCrudVM();
            result.Should().NotBeNull();
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
