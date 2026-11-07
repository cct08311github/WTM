using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// Members marked CanNotEdit keep the stored value in every generic edit: a form (posted fields or the whole record, sync and async) and the grid edit
    /// (posted fields or all fields). The other members are still written.
    /// </summary>
    [TestClass]
    public class CanNotEditGenericEditTest
    {
        private string _seed;
        private Guid _id;

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString();
            using var dc = Dc();
            var album = new Album { ID = Guid.NewGuid(), Title = "stored", Owner = "alice" };
            dc.Set<Album>().Add(album);
            dc.SaveChanges();
            _id = album.ID;
        }

        private DataContext Dc() => new DataContext(_seed, DBTypeEnum.Memory);

        private (string Title, string Owner) Stored()
        {
            using var dc = Dc();
            return dc.Set<Album>().Where(x => x.ID == _id).AsEnumerable().Select(x => (x.Title, x.Owner)).Single();
        }

        private WTMContext Wtm() => MockWtmContext.CreateWtmContext(Dc(), "bob");

        private BaseCRUDVM<Album> FormVm(params string[] fields)
        {
            var vm = new BaseCRUDVM<Album> { Wtm = Wtm() };
            vm.Entity = new Album { ID = _id, Title = "edited", Owner = "mallory" };
            vm.FC = fields.ToDictionary(x => "Entity." + x, x => (object)"");
            return vm;
        }

        private BasePagedListVM<Album, BaseSearcher> ListVm(params string[] fields)
        {
            var vm = new BasePagedListVM<Album, BaseSearcher> { Wtm = Wtm() };
            vm.EntityList = new List<Album> { new Album { ID = _id, Title = "edited", Owner = "mallory" } };
            vm.FC = fields.ToDictionary(x => "EntityList[0]." + x, x => (object)"");
            return vm;
        }

        [TestMethod]
        public void FormEdit_AllFields_KeepsTheCanNotEditMember_AndWritesTheOthers()
        {
            var vm = FormVm();
            vm.DoEdit(true);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual(("edited", "alice"), Stored());
        }

        [TestMethod]
        public async Task FormEditAsync_AllFields_KeepsTheCanNotEditMember_AndWritesTheOthers()
        {
            var vm = FormVm();
            await vm.DoEditAsync(true);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual(("edited", "alice"), Stored());
        }

        [TestMethod]
        public void FormEdit_PostedFields_KeepsTheCanNotEditMember_EvenWhenNamed()
        {
            var vm = FormVm("Title", "Owner");
            vm.DoEdit(false);
            Assert.AreEqual(("edited", "alice"), Stored());
        }

        [TestMethod]
        public void ListEdit_AllFields_KeepsTheCanNotEditMember_AndWritesTheOthers()
        {
            var vm = ListVm("Title");
            vm.UpdateEntityList(true);
            Assert.IsTrue(vm.MSD.IsValid);
            Assert.AreEqual(("edited", "alice"), Stored());
        }

        [TestMethod]
        public void ListEdit_PostedFields_KeepsTheCanNotEditMember_EvenWhenNamed()
        {
            var vm = ListVm("Title", "Owner");
            vm.UpdateEntityList(false);
            Assert.AreEqual(("edited", "alice"), Stored());
        }
    }
}
