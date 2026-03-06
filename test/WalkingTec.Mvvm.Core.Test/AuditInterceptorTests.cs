#nullable disable
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;

namespace WalkingTec.Mvvm.Core.Test
{
    [TestClass]
    public class AuditInterceptorTests : IDisposable
    {
        private SqliteConnection _keepAlive;
        private TestAuditContext _dc;

        private class AuditTestModel : BasePoco
        {
            public string Name { get; set; }
        }

        private class TestAuditContext : EmptyContext
        {
            public DbSet<AuditTestModel> AuditTestModels { get; set; }

            public TestAuditContext(string cs, DBTypeEnum dbtype) : base(cs, dbtype) { }

            protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            {
                optionsBuilder.UseSqlite($"DataSource={CSName}?mode=memory&cache=shared");
            }
        }

        [TestInitialize]
        public void Setup()
        {
            var seed = $"AuditTest_{Guid.NewGuid():N}";
            _keepAlive = new SqliteConnection($"DataSource={seed}?mode=memory&cache=shared");
            _keepAlive.Open();
            _dc = new TestAuditContext(seed, DBTypeEnum.SQLite);
            _dc.Database.EnsureCreated();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _dc?.Dispose();
            _keepAlive?.Close();
            _keepAlive?.Dispose();
        }

        public void Dispose()
        {
            Cleanup();
        }

        [TestMethod]
        public void SaveChanges_fills_CreateBy_and_CreateTime_on_add()
        {
            _dc.CurrentUserCode = "admin";
            var entity = new AuditTestModel { Name = "Test" };
            _dc.Set<AuditTestModel>().Add(entity);
            _dc.SaveChanges();

            Assert.AreEqual("admin", entity.CreateBy);
            Assert.IsNotNull(entity.CreateTime);
        }

        [TestMethod]
        public void SaveChanges_does_not_overwrite_existing_CreateBy()
        {
            _dc.CurrentUserCode = "admin";
            var entity = new AuditTestModel
            {
                Name = "Test",
                CreateBy = "custom_user",
                CreateTime = new DateTime(2020, 1, 1)
            };
            _dc.Set<AuditTestModel>().Add(entity);
            _dc.SaveChanges();

            Assert.AreEqual("custom_user", entity.CreateBy,
                "Interceptor should not overwrite explicitly set CreateBy");
            Assert.AreEqual(new DateTime(2020, 1, 1), entity.CreateTime,
                "Interceptor should not overwrite explicitly set CreateTime");
        }

        [TestMethod]
        public void SaveChanges_fills_UpdateBy_and_UpdateTime_on_modify()
        {
            _dc.CurrentUserCode = "admin";
            var entity = new AuditTestModel { Name = "Original" };
            _dc.Set<AuditTestModel>().Add(entity);
            _dc.SaveChanges();

            // Clear update fields set during add
            entity.UpdateBy = null;
            entity.UpdateTime = null;

            // Modify
            entity.Name = "Modified";
            _dc.Entry(entity).State = EntityState.Modified;
            _dc.SaveChanges();

            Assert.AreEqual("admin", entity.UpdateBy);
            Assert.IsNotNull(entity.UpdateTime);
        }

        [TestMethod]
        public void SaveChanges_does_not_overwrite_existing_UpdateBy()
        {
            _dc.CurrentUserCode = "admin";
            var entity = new AuditTestModel { Name = "Test" };
            _dc.Set<AuditTestModel>().Add(entity);
            _dc.SaveChanges();

            // Set update fields manually (VM pattern)
            entity.UpdateBy = "vm_user";
            entity.UpdateTime = new DateTime(2025, 6, 15);
            entity.Name = "Updated";
            _dc.Entry(entity).State = EntityState.Modified;
            _dc.SaveChanges();

            Assert.AreEqual("vm_user", entity.UpdateBy,
                "Interceptor should not overwrite explicitly set UpdateBy");
            Assert.AreEqual(new DateTime(2025, 6, 15), entity.UpdateTime,
                "Interceptor should not overwrite explicitly set UpdateTime");
        }

        [TestMethod]
        public async Task SaveChangesAsync_fills_audit_fields()
        {
            _dc.CurrentUserCode = "async_user";
            var entity = new AuditTestModel { Name = "AsyncTest" };
            _dc.Set<AuditTestModel>().Add(entity);
            await _dc.SaveChangesAsync();

            Assert.AreEqual("async_user", entity.CreateBy);
            Assert.IsNotNull(entity.CreateTime);
        }

        [TestMethod]
        public void SaveChanges_works_without_CurrentUserCode()
        {
            // CurrentUserCode is null — should still set CreateTime but not CreateBy
            _dc.CurrentUserCode = null;
            var entity = new AuditTestModel { Name = "NoUser" };
            _dc.Set<AuditTestModel>().Add(entity);
            _dc.SaveChanges();

            Assert.IsNotNull(entity.CreateTime,
                "CreateTime should be set even without a current user");
            Assert.IsNull(entity.CreateBy,
                "CreateBy should remain null when CurrentUserCode is null");
        }
    }
}
