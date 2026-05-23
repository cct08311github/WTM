using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.VM
{
    [TestClass]
    public class BaseCRUDVMAsyncTest
    {
        private BaseCRUDVM<School> _schoolvm = new BaseCRUDVM<School>();
        private BaseCRUDVM<Major> _majorvm = new BaseCRUDVM<Major>();
        private BaseCRUDVM<Student> _studentvm = new BaseCRUDVM<Student>();
        private BaseCRUDVM<GoodsSpecification> _goodsvm = new BaseCRUDVM<GoodsSpecification>();
        private BaseCRUDVM<GoodsCatalog> _goodsCatalogvm = new BaseCRUDVM<GoodsCatalog>();
        private string _seed = null!;

        [TestInitialize]
        public void Initialize()
        {
            _seed = Guid.NewGuid().ToString();

            _schoolvm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory), "schooluser");
            _majorvm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory), "majoruser");
            _studentvm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory), "studentuser");
            _goodsvm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory), "goodsuser");
            _goodsCatalogvm.Wtm = MockWtmContext.CreateWtmContext(new DataContext(_seed, DBTypeEnum.Memory), "goodcatalogsuser");
        }

        // ─── Single Table Add ─────────────────────────────────────────────────

        [TestMethod]
        [DataTestMethod]
        [DataRow("111", "test1", SchoolTypeEnum.PRI, "remark1")]
        [DataRow("222", "test2", SchoolTypeEnum.PUB, "remark2")]
        public async Task SingleTableDoAddAsync(string code, string name, SchoolTypeEnum schooltype, string remark)
        {
            School s = new School
            {
                SchoolCode = code,
                SchoolName = name,
                SchoolType = schooltype,
                Remark = remark
            };
            _schoolvm.Entity = s;
            await _schoolvm.DoAddAsync();

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var rv = context.Set<School>().ToList();
                Assert.AreEqual(1, rv.Count);
                Assert.AreEqual(code, rv[0].SchoolCode);
                Assert.AreEqual(name, rv[0].SchoolName);
                Assert.AreEqual(schooltype, rv[0].SchoolType);
                Assert.AreEqual(remark, rv[0].Remark);
                Assert.AreEqual("schooluser", rv[0].CreateBy);
                Assert.IsTrue(DateTime.Now.Subtract(rv[0].CreateTime!.Value).TotalSeconds < 10);
            }
            Assert.IsTrue(_schoolvm.MSD.Count == 0);
        }

        // ─── Single Table Edit (all fields) ───────────────────────────────────

        [TestMethod]
        [DataTestMethod]
        [DataRow("111", "test1", SchoolTypeEnum.PRI, "remark1")]
        [DataRow("222", "test2", SchoolTypeEnum.PUB, "remark2")]
        public async Task SingleTableDoEditAsync(string code, string name, SchoolTypeEnum schooltype, string remark)
        {
            School s = new School
            {
                SchoolCode = "000",
                SchoolName = "default",
                SchoolType = SchoolTypeEnum.PRI,
                Remark = "default"
            };
            _schoolvm.Entity = s;
            await _schoolvm.DoAddAsync();

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                School s2 = new School
                {
                    SchoolCode = code,
                    SchoolName = name,
                    SchoolType = schooltype,
                    Remark = remark,
                    ID = s.ID
                };
                _schoolvm.DC = context;
                _schoolvm.Entity = s2;
                await _schoolvm.DoEditAsync(true);
            }

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var rv = context.Set<School>().ToList();
                Assert.AreEqual(1, rv.Count);
                Assert.AreEqual(code, rv[0].SchoolCode);
                Assert.AreEqual(name, rv[0].SchoolName);
                Assert.AreEqual(schooltype, rv[0].SchoolType);
                Assert.AreEqual(remark, rv[0].Remark);
                Assert.AreEqual("schooluser", rv[0].UpdateBy);
                Assert.IsTrue(DateTime.Now.Subtract(rv[0].UpdateTime!.Value).TotalSeconds < 10);
            }
        }

        // ─── Single Table Edit (specific fields) ─────────────────────────────

        [TestMethod]
        [DataTestMethod]
        [DataRow("111", "test1", SchoolTypeEnum.PRI, "remark1")]
        [DataRow("222", "test2", SchoolTypeEnum.PUB, "remark2")]
        public async Task SingleTableDoEditFieldsAsync(string code, string name, SchoolTypeEnum schooltype, string remark)
        {
            School s = new School
            {
                SchoolCode = "000",
                SchoolName = "default",
                SchoolType = SchoolTypeEnum.PRI,
                Remark = "default"
            };
            _schoolvm.Entity = s;
            await _schoolvm.DoAddAsync();

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                School s2 = new School
                {
                    SchoolCode = code,
                    SchoolName = name,
                    SchoolType = schooltype,
                    Remark = remark,
                    ID = s.ID
                };
                _schoolvm.DC = context;
                _schoolvm.Entity = s2;
                _schoolvm.FC.Add("Entity.SchoolName", name);
                await _schoolvm.DoEditAsync();
            }

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var rv = context.Set<School>().ToList();
                Assert.AreEqual(1, rv.Count);
                Assert.AreEqual("000", rv[0].SchoolCode);
                Assert.AreEqual(name, rv[0].SchoolName);
                Assert.AreEqual(SchoolTypeEnum.PRI, rv[0].SchoolType);
                Assert.AreEqual("default", rv[0].Remark);
                Assert.AreEqual("schooluser", rv[0].UpdateBy);
                Assert.IsTrue(DateTime.Now.Subtract(rv[0].UpdateTime!.Value).TotalSeconds < 10);
            }
        }

        // ─── Single Table Delete ──────────────────────────────────────────────

        [TestMethod]
        public async Task SingleTableDeleteAsync()
        {
            School s = new School
            {
                SchoolCode = "000",
                SchoolName = "default",
                SchoolType = SchoolTypeEnum.PUB,
                Remark = "default"
            };
            _schoolvm.Entity = s;
            await _schoolvm.DoAddAsync();

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(1, context.Set<School>().Count());
                _schoolvm.DC = context;
                _schoolvm.Entity = new School { ID = s.ID };
                await _schoolvm.DoDeleteAsync();
            }

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(0, context.Set<School>().Count());
            }
        }

        // ─── Persist (soft) Delete ────────────────────────────────────────────

        [TestMethod]
        public async Task SingleTablePersistDeleteAsync()
        {
            Student s = new Student
            {
                LoginName = "a",
                Password = "b",
                Name = "ab"
            };
            _studentvm.Entity = s;
            await _studentvm.DoAddAsync();

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(1, context.Set<Student>().Count());
                _studentvm.DC = context;
                _studentvm.Entity = context.Set<Student>().Where(x => x.ID == s.ID).FirstOrDefault()!;
                await _studentvm.DoDeleteAsync();
            }

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(1, context.Set<Student>().IgnoreQueryFilters().Count());
                var rv = context.Set<Student>().IgnoreQueryFilters().ToList()[0];
                Assert.AreEqual(false, rv.IsValid);
                Assert.AreEqual("studentuser", rv.UpdateBy);
                Assert.IsTrue(DateTime.Now.Subtract(rv.UpdateTime!.Value).TotalSeconds < 10);
            }
        }

        // ─── Physical Delete (DoRealDeleteAsync) ─────────────────────────────

        [TestMethod]
        public async Task SingleTablePersistRealDeleteAsync()
        {
            Student s = new Student
            {
                LoginName = "a",
                Password = "b",
                Name = "ab"
            };
            _studentvm.Entity = s;
            await _studentvm.DoAddAsync();

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(1, context.Set<Student>().Count());
                _studentvm.DC = context;
                _studentvm.Entity = context.Set<Student>().Include(x => x.StudentMajor).Where(x => x.ID == s.ID).FirstOrDefault()!;
                await _studentvm.DoRealDeleteAsync();
            }

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(0, context.Set<Student>().Count());
            }
        }

        // ─── One-to-Many Add ──────────────────────────────────────────────────

        [TestMethod]
        public async Task One2ManyDoAddAsync()
        {
            School s = new School
            {
                SchoolCode = "000",
                SchoolName = "school",
                SchoolType = SchoolTypeEnum.PRI,
                Remark = "",
                Majors = new List<Major>()
            };
            s.Majors.Add(new Major
            {
                MajorCode = "111",
                MajorName = "major1",
                MajorType = MajorTypeEnum.Optional,
                Remark = ""
            });
            s.Majors.Add(new Major
            {
                MajorCode = "222",
                MajorName = "major2",
                MajorType = MajorTypeEnum.Required,
                Remark = ""
            });
            _schoolvm.Entity = s;
            await _schoolvm.DoAddAsync();

            using var context = new DataContext(_seed, DBTypeEnum.Memory);
            Assert.AreEqual(1, context.Set<School>().Count());
            Assert.AreEqual(2, context.Set<Major>().Count());
            var rv = context.Set<Major>().ToList();
            Assert.AreEqual("111", rv[0].MajorCode);
            Assert.AreEqual("major1", rv[0].MajorName);
            Assert.AreEqual(MajorTypeEnum.Optional, rv[0].MajorType);
            Assert.AreEqual("222", rv[1].MajorCode);
            Assert.AreEqual("major2", rv[1].MajorName);
            Assert.AreEqual(MajorTypeEnum.Required, rv[1].MajorType);

            Assert.AreEqual("schooluser", context.Set<School>().First().CreateBy);
            Assert.IsTrue(DateTime.Now.Subtract(context.Set<School>().First().CreateTime!.Value).TotalSeconds < 10);
        }

        // ─── One-to-Many Edit ─────────────────────────────────────────────────

        [TestMethod]
        public async Task One2ManyDoEditAsync()
        {
            await One2ManyDoAddAsync();

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var id = context.Set<School>().Select(x => x.ID).First();
                var mid = context.Set<Major>().Select(x => x.ID).First();
                School s = new School { ID = id };
                s.Majors = new List<Major>
                {
                    new Major
                    {
                        MajorCode = "333",
                        MajorName = "major3",
                        MajorType = MajorTypeEnum.Optional
                    },
                    new Major { ID = mid, MajorCode = "111update" }
                };
                _schoolvm.Entity = s;
                _schoolvm.DC = context;
                await _schoolvm.DoEditAsync(true);
            }

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var id = context.Set<School>().Select(x => x.ID).First();
                Assert.AreEqual(1, context.Set<School>().Count());
                Assert.AreEqual(2, context.Set<Major>().Where(x => x.SchoolId == id).Count());
                var rv1 = context.Set<Major>().Where(x => x.MajorCode == "111update").SingleOrDefault();
                Assert.AreEqual("111update", rv1!.MajorCode);
                var rv2 = context.Set<Major>().Where(x => x.MajorCode == "333").SingleOrDefault();
                Assert.AreEqual("333", rv2!.MajorCode);
                Assert.AreEqual("major3", rv2.MajorName);
            }
        }

        // ─── One-to-Many Delete (parent) ─────────────────────────────────────

        [TestMethod]
        public async Task One2ManyTableDeleteAsync()
        {
            await One2ManyDoAddAsync();

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var id = context.Set<School>().AsNoTracking().First().ID;
                _schoolvm.DC = context;
                _schoolvm.Entity = context.Set<School>().Include(x => x.Majors).Where(x => x.ID == id).FirstOrDefault()!;
                await _schoolvm.DoDeleteAsync();
            }

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(0, context.Set<School>().Count());
            }
        }

        // ─── Many-to-Many Add ─────────────────────────────────────────────────

        [TestMethod]
        public async Task Many2ManyDoAddAsync()
        {
            Major m1 = new Major
            {
                MajorCode = "111",
                MajorName = "major1",
                MajorType = MajorTypeEnum.Optional
            };
            Major m2 = new Major
            {
                MajorCode = "222",
                MajorName = "major2",
                MajorType = MajorTypeEnum.Required
            };
            Student s1 = new Student
            {
                LoginName = "s1",
                Password = "aaa",
                Name = "student1"
            };
            Student s2 = new Student
            {
                LoginName = "s2",
                Password = "bbb",
                Name = "student2"
            };
            _majorvm.Entity = m1;
            await _majorvm.DoAddAsync();
            _majorvm.Entity = m2;
            await _majorvm.DoAddAsync();

            s1.StudentMajor = new List<StudentMajor>
            {
                new StudentMajor { MajorId = m1.ID }
            };
            s2.StudentMajor = new List<StudentMajor>
            {
                new StudentMajor { MajorId = m2.ID }
            };
            _studentvm.Entity = s1;
            await _studentvm.DoAddAsync();
            _studentvm.Entity = s2;
            await _studentvm.DoAddAsync();

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(2, context.Set<Major>().Count());
                Assert.AreEqual(2, context.Set<Student>().Count());
                Assert.AreEqual(2, context.Set<StudentMajor>().Count());
                var rv = context.Set<StudentMajor>().ToList();
                Assert.AreEqual(s1.ID, rv[0].StudentId);
                Assert.AreEqual(m1.ID, rv[0].MajorId);
                Assert.AreEqual(s2.ID, rv[1].StudentId);
                Assert.AreEqual(m2.ID, rv[1].MajorId);
            }
        }

        // ─── Many-to-Many Delete ──────────────────────────────────────────────

        [TestMethod]
        public async Task Many2ManyTableDeleteAsync()
        {
            await Many2ManyDoAddAsync();

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var id = context.Set<Student>().AsNoTracking().First().ID;
                _studentvm.DC = context;
                _studentvm.Entity = context.Set<Student>().Include(x => x.StudentMajor).Where(x => x.ID == id).FirstOrDefault()!;
                await _studentvm.DoRealDeleteAsync();
            }

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(1, context.Set<Student>().Count());
            }
        }

        // ─── Persist FK Delete (child soft-delete, parent unchanged) ──────────

        [TestMethod]
        public async Task One2ManyTablePersistDeleteAsync()
        {
            GoodsCatalog gc = new GoodsCatalog
            {
                IsValid = true,
                Name = "c1",
                OrderNum = 2
            };
            _goodsvm.DC.AddEntity(gc);
            _goodsvm.DC.SaveChanges();

            GoodsSpecification g = new GoodsSpecification
            {
                Name = "g1",
                OrderNum = 1,
                IsValid = true,
                CatalogId = gc.ID
            };
            _goodsvm.Entity = g;
            await _goodsvm.DoAddAsync();

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(1, context.Set<GoodsSpecification>().IgnoreQueryFilters().Count());
                _goodsvm.DC = context;
                _goodsvm.Entity = context.Set<GoodsSpecification>().IgnoreQueryFilters().Include(x => x.Catalog).Where(x => x.ID == g.ID).FirstOrDefault()!;
                await _goodsvm.DoDeleteAsync();
            }

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(1, context.Set<GoodsSpecification>().IgnoreQueryFilters().Count());
                var rv = context.Set<GoodsSpecification>().IgnoreQueryFilters().ToList()[0];
                Assert.AreEqual(false, rv.IsValid);
                Assert.AreEqual("goodsuser", rv.UpdateBy);

                var rv2 = context.Set<GoodsCatalog>().IgnoreQueryFilters().ToList()[0];
                Assert.AreEqual(true, rv2.IsValid);

                Assert.IsTrue(DateTime.Now.Subtract(rv.UpdateTime!.Value).TotalSeconds < 10);
            }
        }

        // ─── Persist FK Delete (parent soft-delete cascades to child) ─────────

        [TestMethod]
        public async Task One2ManyTablePersistDelete2Async()
        {
            GoodsCatalog gc = new GoodsCatalog
            {
                IsValid = true,
                Name = "c1",
                OrderNum = 2
            };
            _goodsvm.DC.AddEntity(gc);
            _goodsvm.DC.SaveChanges();

            GoodsSpecification g = new GoodsSpecification
            {
                Name = "g1",
                OrderNum = 1,
                IsValid = true,
                CatalogId = gc.ID
            };
            _goodsvm.Entity = g;
            await _goodsvm.DoAddAsync();

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                Assert.AreEqual(1, context.Set<GoodsCatalog>().Count());
                _goodsCatalogvm.DC = context;
                _goodsCatalogvm.Entity = context.Set<GoodsCatalog>().Where(x => x.ID == g.ID).FirstOrDefault()!;
                await _goodsCatalogvm.DoDeleteAsync();
            }

            using (var context = new DataContext(_seed, DBTypeEnum.Memory))
            {
                var rv2 = context.Set<GoodsCatalog>().IgnoreQueryFilters().ToList()[0];
                Assert.AreEqual(false, rv2.IsValid);
                Assert.AreEqual("goodcatalogsuser", rv2.UpdateBy);

                Assert.AreEqual(1, context.Set<GoodsSpecification>().IgnoreQueryFilters().Count());
                var rv = context.Set<GoodsSpecification>().IgnoreQueryFilters().ToList()[0];
                Assert.AreEqual(false, rv.IsValid);
                Assert.AreEqual("goodcatalogsuser", rv.UpdateBy);

                Assert.IsTrue(DateTime.Now.Subtract(rv.UpdateTime!.Value).TotalSeconds < 10);
            }
        }
    }
}
