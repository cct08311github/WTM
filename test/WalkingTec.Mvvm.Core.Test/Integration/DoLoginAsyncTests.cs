using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Test.Fixtures;

namespace WalkingTec.Mvvm.Core.Test.Integration
{
    /// <summary>
    /// Integration tests for WTMContext.DoLoginAsync.
    /// Uses TestLoginUser (a concrete FrameworkUserBase subclass defined in Core.Test).
    ///
    /// Verifies:
    /// - BCrypt auth happy path
    /// - Wrong password rejection
    /// - MD5 → BCrypt migration (SuccessRehashNeeded → DB updated)
    /// - Disabled user rejection
    /// - Non-existent user rejection
    ///
    /// Uses SQLite shared in-memory (not EF InMemory) because LoadBasicInfoAsync has
    /// correlated collection subqueries that EF Core InMemory cannot translate.
    /// A single SqliteConnection is kept open throughout the test lifetime to keep
    /// the shared in-memory database alive across multiple context instances.
    /// </summary>
    [TestClass]
    public class DoLoginAsyncTests
    {
        private string _seed = null!;
        private SqliteConnection _keepAlive = null!;

        [TestInitialize]
        public void Initialize()
        {
            _seed = Guid.NewGuid().ToString();
            _keepAlive = new SqliteConnection($"DataSource={_seed}?mode=memory&cache=shared");
            _keepAlive.Open();
            using var db = CreateDb();
            ((DbContext)db).Database.EnsureCreated();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive?.Dispose();
        }

        private IDataContext CreateDb() => new LoginTestDataContext(_seed);

        // ─── BCrypt Happy Path ─────────────────────────────────────────────────

        [TestMethod]
        public async Task DoLoginAsync_ValidBCryptUser_ReturnsLoginUserInfo()
        {
            using var db = CreateDb();
            var user = WtmTestHelper.CreateUser("alice", "pass123");
            ((DbContext)db).Set<TestLoginUser>().Add(user);
            await ((DbContext)db).SaveChangesAsync();

            var wtm = WtmTestHelper.CreateLoginTestContext(CreateDb());
            var result = await wtm.DoLoginAsync("alice", "pass123", null);

            result.Should().NotBeNull();
            result!.ITCode.Should().Be("alice");
        }

        [TestMethod]
        public async Task DoLoginAsync_ValidBCryptUser_WrongPassword_ReturnsNull()
        {
            using var db = CreateDb();
            var user = WtmTestHelper.CreateUser("bob", "correct");
            ((DbContext)db).Set<TestLoginUser>().Add(user);
            await ((DbContext)db).SaveChangesAsync();

            var wtm = WtmTestHelper.CreateLoginTestContext(CreateDb());
            var result = await wtm.DoLoginAsync("bob", "wrong", null);

            result.Should().BeNull();
        }

        // ─── MD5 Migration ─────────────────────────────────────────────────────

        [TestMethod]
        public async Task DoLoginAsync_LegacyMD5User_CorrectPassword_ReturnsLoginUserInfo()
        {
            using var db = CreateDb();
            var user = WtmTestHelper.CreateLegacyMd5User("legacy_user", "000000");
            ((DbContext)db).Set<TestLoginUser>().Add(user);
            await ((DbContext)db).SaveChangesAsync();

            var wtm = WtmTestHelper.CreateLoginTestContext(CreateDb());
            var result = await wtm.DoLoginAsync("legacy_user", "000000", null);

            result.Should().NotBeNull();
            result!.ITCode.Should().Be("legacy_user");
        }

        [TestMethod]
        public async Task DoLoginAsync_LegacyMD5User_WrongPassword_ReturnsNull()
        {
            using var db = CreateDb();
            var user = WtmTestHelper.CreateLegacyMd5User("legacy2", "000000");
            ((DbContext)db).Set<TestLoginUser>().Add(user);
            await ((DbContext)db).SaveChangesAsync();

            var wtm = WtmTestHelper.CreateLoginTestContext(CreateDb());
            var result = await wtm.DoLoginAsync("legacy2", "wrong", null);

            result.Should().BeNull();
        }

        [TestMethod]
        public async Task DoLoginAsync_MD5Migration_PersistsNewHashToDb()
        {
            // Arrange: user has legacy MD5 password
            var userId = Guid.NewGuid();
            using (var db = CreateDb())
            {
                var user = WtmTestHelper.CreateLegacyMd5User("migrating", "000000");
                user.ID = userId;
                ((DbContext)db).Set<TestLoginUser>().Add(user);
                await ((DbContext)db).SaveChangesAsync();
            }

            // Verify starting state: MD5 hash (32 chars uppercase hex)
            using (var db = CreateDb())
            {
                var before = await ((DbContext)db).Set<TestLoginUser>().FindAsync(userId);
                before!.Password.Should().HaveLength(32,
                    "Legacy MD5 hash is 32 hex characters");
                PasswordHashHelper.IsLegacyMD5Hash(before.Password).Should().BeTrue();
            }

            // Act: login triggers migration
            var wtm = WtmTestHelper.CreateLoginTestContext(CreateDb());
            var result = await wtm.DoLoginAsync("migrating", "000000", null);
            result.Should().NotBeNull("Login should succeed with correct MD5 password");

            // Assert: password in DB is now BCrypt (longer than 32 chars)
            using (var db = CreateDb())
            {
                var after = await ((DbContext)db).Set<TestLoginUser>().FindAsync(userId);
                after!.Password.Length.Should().BeGreaterThan(32,
                    "Password should be upgraded from MD5 to BCrypt");
                PasswordHashHelper.IsLegacyMD5Hash(after.Password)
                    .Should().BeFalse("Upgraded hash is no longer MD5");
                PasswordHashHelper.VerifyPassword(after.Password, "000000")
                    .Should().Be(PasswordVerifyResult.Success,
                        "New BCrypt hash verifies without rehash needed");
            }
        }

        // ─── User State Checks ─────────────────────────────────────────────────

        [TestMethod]
        public async Task DoLoginAsync_NonexistentUser_ReturnsNull()
        {
            var wtm = WtmTestHelper.CreateLoginTestContext(CreateDb());
            var result = await wtm.DoLoginAsync("nobody", "pass", null);
            result.Should().BeNull();
        }

        [TestMethod]
        public async Task DoLoginAsync_DisabledUser_ReturnsNull()
        {
            using var db = CreateDb();
            var user = WtmTestHelper.CreateUser("disabled_user", "pass", isValid: false);
            ((DbContext)db).Set<TestLoginUser>().Add(user);
            await ((DbContext)db).SaveChangesAsync();

            var wtm = WtmTestHelper.CreateLoginTestContext(CreateDb());
            var result = await wtm.DoLoginAsync("disabled_user", "pass", null);

            result.Should().BeNull("Disabled users should be rejected");
        }

        [TestMethod]
        public async Task DoLoginAsync_TenantMismatch_ReturnsNull()
        {
            // User exists but belongs to "tenant_a"; login request targets "tenant_b"
            using var db = CreateDb();
            var user = WtmTestHelper.CreateUser("tenant_user", "pass", tenantCode: "tenant_a");
            ((DbContext)db).Set<TestLoginUser>().Add(user);
            await ((DbContext)db).SaveChangesAsync();

            var wtm = WtmTestHelper.CreateLoginTestContext(CreateDb());
            var result = await wtm.DoLoginAsync("tenant_user", "pass", "tenant_b");

            result.Should().BeNull("Tenant mismatch should reject login");
        }
    }

    /// <summary>
    /// Minimal DataContext for DoLoginAsync tests.
    /// Inherits FrameworkContext so all related entity types (FrameworkUserRole, etc.)
    /// are in the model — required by LoadBasicInfoAsync's correlated LINQ queries.
    /// Uses SQLite (not EF InMemory) because the correlated subqueries in LoadBasicInfoAsync
    /// cannot be translated by the EF Core InMemory provider.
    /// Overrides OnConfiguring to prevent the base SqlServer/InMemory config from running
    /// (the connection is configured by the DoLoginAsyncTests ctor via EnsureCreated).
    /// </summary>
    /// <summary>
    /// Minimal DataContext for DoLoginAsync tests.
    /// Inherits FrameworkContext to get the framework entity DbSets.
    /// Overrides OnModelCreating to SKIP the global Utils.GetAllModels() scan,
    /// which would discover conflicting entities (StudentMajor/StudentMajorTop)
    /// from the test project's DataContext and cause SQLite schema errors.
    /// </summary>
    internal class LoginTestDataContext : FrameworkContext
    {
        public LoginTestDataContext(string seed)
            : base($"DataSource={seed}?mode=memory&cache=shared", DBTypeEnum.SQLite) { }

        public DbSet<TestLoginUser> TestLoginUsers { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Intentionally do NOT call base.OnModelCreating() to avoid
            // Utils.GetAllModels() scanning the entire test assembly.
            // The framework entities are discovered via DbSet properties
            // on FrameworkContext, which is sufficient for EnsureCreated().
        }
    }
}
