using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Test.Fixtures;

namespace WalkingTec.Mvvm.Core.Test.Security
{
    /// <summary>
    /// Integration tests for MD5 → PBKDF2 password migration via DoLoginAsync.
    ///
    /// Verifies:
    /// - MD5 login triggers SuccessRehashNeeded and upgrades stored hash to PBKDF2
    /// - PBKDF2 login works and does NOT re-hash
    /// - Wrong password on MD5 hash rejects without migration
    /// - Disabled and non-existent users are rejected
    /// - Tenant mismatch is rejected
    ///
    /// Uses SQLite shared in-memory because LoadBasicInfoAsync has correlated
    /// subqueries that EF Core InMemory cannot translate.
    ///
    /// KEY FIX: LoginTestDataContext overrides OnModelCreating to skip the global
    /// Utils.GetAllModels() scan, which would discover conflicting test entities
    /// (StudentMajor/StudentMajorTop) and cause 'duplicate column name: MajorId'.
    /// </summary>
    [TestClass]
    public class PasswordMigrationIntegrationTests
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
        public void Cleanup() => _keepAlive.Dispose();

        private IDataContext CreateDb() => new LoginTestDataContext(_seed);

        // ─── MD5 Migration Core Tests ─────────────────────────────────────────

        [TestMethod]
        public async Task DoLoginAsync_LegacyMD5_CorrectPassword_LoginSucceeds()
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
        public async Task DoLoginAsync_LegacyMD5_CorrectPassword_UpgradesHashToPBKDF2()
        {
            var userId = Guid.NewGuid();
            using (var db = CreateDb())
            {
                var user = WtmTestHelper.CreateLegacyMd5User("migrating", "000000");
                user.ID = userId;
                ((DbContext)db).Set<TestLoginUser>().Add(user);
                await ((DbContext)db).SaveChangesAsync();
            }

            // Verify starting state: MD5 hash
            using (var db = CreateDb())
            {
                var before = await ((DbContext)db).Set<TestLoginUser>().FindAsync(userId);
                before!.Password.Should().HaveLength(32, "Legacy MD5 hash is 32 hex characters");
                PasswordHashHelper.IsLegacyMD5Hash(before.Password).Should().BeTrue();
            }

            // Act: login triggers migration
            var wtm = WtmTestHelper.CreateLoginTestContext(CreateDb());
            var result = await wtm.DoLoginAsync("migrating", "000000", null);
            result.Should().NotBeNull("Login should succeed with correct MD5 password");

            // Assert: password in DB is now PBKDF2
            using (var db = CreateDb())
            {
                var after = await ((DbContext)db).Set<TestLoginUser>().FindAsync(userId);
                after!.Password.Length.Should().BeGreaterThan(32,
                    "Password should be upgraded from MD5 to PBKDF2");
                PasswordHashHelper.IsLegacyMD5Hash(after.Password)
                    .Should().BeFalse("Upgraded hash is no longer MD5");
                PasswordHashHelper.VerifyPassword(after.Password, "000000")
                    .Should().Be(PasswordVerifyResult.Success,
                        "New PBKDF2 hash verifies without rehash needed");
            }
        }

        [TestMethod]
        public async Task DoLoginAsync_LegacyMD5_WrongPassword_RejectsWithoutMigration()
        {
            var userId = Guid.NewGuid();
            using (var db = CreateDb())
            {
                var user = WtmTestHelper.CreateLegacyMd5User("no_migrate", "000000");
                user.ID = userId;
                ((DbContext)db).Set<TestLoginUser>().Add(user);
                await ((DbContext)db).SaveChangesAsync();
            }

            var wtm = WtmTestHelper.CreateLoginTestContext(CreateDb());
            var result = await wtm.DoLoginAsync("no_migrate", "wrong", null);

            result.Should().BeNull("Wrong password should reject login");

            // Hash must remain MD5 — no migration on failed login
            using (var db = CreateDb())
            {
                var after = await ((DbContext)db).Set<TestLoginUser>().FindAsync(userId);
                after!.Password.Should().HaveLength(32, "Hash must remain MD5 after failed login");
                PasswordHashHelper.IsLegacyMD5Hash(after.Password).Should().BeTrue();
            }
        }

        // ─── PBKDF2 Happy Path ─────────────────────────────────────────────────

        [TestMethod]
        public async Task DoLoginAsync_PBKDF2User_CorrectPassword_LoginSucceeds()
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
        public async Task DoLoginAsync_PBKDF2User_WrongPassword_ReturnsNull()
        {
            using var db = CreateDb();
            var user = WtmTestHelper.CreateUser("bob", "correct");
            ((DbContext)db).Set<TestLoginUser>().Add(user);
            await ((DbContext)db).SaveChangesAsync();

            var wtm = WtmTestHelper.CreateLoginTestContext(CreateDb());
            var result = await wtm.DoLoginAsync("bob", "wrong", null);

            result.Should().BeNull();
        }

        [TestMethod]
        public async Task DoLoginAsync_PBKDF2User_DoesNotRehash()
        {
            var userId = Guid.NewGuid();
            string originalHash;
            using (var db = CreateDb())
            {
                var user = WtmTestHelper.CreateUser("stable", "mypass");
                user.ID = userId;
                originalHash = user.Password;
                ((DbContext)db).Set<TestLoginUser>().Add(user);
                await ((DbContext)db).SaveChangesAsync();
            }

            var wtm = WtmTestHelper.CreateLoginTestContext(CreateDb());
            await wtm.DoLoginAsync("stable", "mypass", null);

            using (var db = CreateDb())
            {
                var after = await ((DbContext)db).Set<TestLoginUser>().FindAsync(userId);
                after!.Password.Should().Be(originalHash,
                    "PBKDF2 password should not be rehashed on successful login");
            }
        }

        // ─── Edge Cases ─────────────────────────────────────────────────────────

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
    /// DataContext for DoLoginAsync tests.
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
