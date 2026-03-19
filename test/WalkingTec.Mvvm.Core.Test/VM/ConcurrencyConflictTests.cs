#nullable enable
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM;

/// <summary>
/// Guards issue #620 — optimistic concurrency handling in BaseCRUDVM.
/// DoEdit / DoEditAsync must catch DbUpdateConcurrencyException, set
/// IsConcurrencyConflict = true, and add a model error instead of throwing.
/// </summary>
[TestClass]
public class ConcurrencyConflictTests
{
    // A DataContext subclass whose SaveChanges always throws DbUpdateConcurrencyException.
    private class ThrowConcurrencyContext : DataContext
    {
        public ThrowConcurrencyContext(string seed) : base(seed, DBTypeEnum.Memory) { }

        private static DbUpdateConcurrencyException MakeException()
            => new("simulated", Array.Empty<IUpdateEntry>());

        public override int SaveChanges() => throw MakeException();
        public override int SaveChanges(bool acceptAllChangesOnSuccess) => throw MakeException();
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => throw MakeException();
        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
            => throw MakeException();
    }

    private BaseCRUDVM<School> CreateVm(string seed)
    {
        var vm = new BaseCRUDVM<School>();
        vm.Wtm = MockWtmContext.CreateWtmContext(new ThrowConcurrencyContext(seed), "testuser");
        return vm;
    }

    [TestMethod]
    public void DoEdit_sets_IsConcurrencyConflict_and_adds_model_error()
    {
        var seed = Guid.NewGuid().ToString();
        var vm = CreateVm(seed);
        // Attach a stub entity so DoEditPrepare doesn't fail resolving relationships.
        vm.Entity = new School
        {
            ID = Guid.NewGuid(),
            SchoolCode = "001",
            SchoolName = "Test",
            SchoolType = SchoolTypeEnum.PUB,
            Remark = "r"
        };

        vm.DoEdit(updateAllFields: true);

        Assert.IsTrue(vm.IsConcurrencyConflict, "IsConcurrencyConflict should be true after concurrency exception");
        Assert.IsTrue(vm.MSD!.Count > 0, "A model error should have been added");
    }

    [TestMethod]
    public async Task DoEditAsync_sets_IsConcurrencyConflict_and_adds_model_error()
    {
        var seed = Guid.NewGuid().ToString();
        var vm = CreateVm(seed);
        vm.Entity = new School
        {
            ID = Guid.NewGuid(),
            SchoolCode = "002",
            SchoolName = "Test2",
            SchoolType = SchoolTypeEnum.PRI,
            Remark = "r"
        };

        await vm.DoEditAsync(updateAllFields: true);

        Assert.IsTrue(vm.IsConcurrencyConflict, "IsConcurrencyConflict should be true after async concurrency exception");
        Assert.IsTrue(vm.MSD!.Count > 0, "A model error should have been added");
    }

    [TestMethod]
    public void IsConcurrencyConflict_is_false_before_edit()
    {
        var seed = Guid.NewGuid().ToString();
        var vm = CreateVm(seed);
        Assert.IsFalse(vm.IsConcurrencyConflict);
    }

    [TestMethod]
    public void IsConcurrencyConflict_on_interface_is_readonly()
    {
        var prop = typeof(IBaseCRUDVM<>).GetProperty("IsConcurrencyConflict");
        Assert.IsNotNull(prop, "IsConcurrencyConflict must exist on IBaseCRUDVM");
        Assert.IsTrue(prop!.CanRead);
        Assert.IsFalse(prop.CanWrite, "IBaseCRUDVM.IsConcurrencyConflict must be read-only");
    }
}
