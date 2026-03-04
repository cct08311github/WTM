using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Tests.Fixtures;
using Xunit;

namespace WalkingTec.Mvvm.Core.Tests.Integration
{
    /// <summary>
    /// Integration tests for WTMContext.DoLoginAsync.
    /// Uses TestLoginUser (a concrete FrameworkUserBase subclass defined in Core.Tests).
    ///
    /// Verifies:
    /// - PBKDF2 auth happy path
    /// - Wrong password rejection
    /// - MD5 → PBKDF2 migration (SuccessRehashNeeded → DB updated)
    /// - Disabled user rejection
    /// - Non-existent user rejection
    /// </summary>
    public class DoLoginAsyncTests
    {
        private readonly string _seed;

        public DoLoginAsyncTests()
        {
            _seed = Guid.NewGuid().ToString();
        }

        private IDataContext CreateDb() => new LoginTestDataContext(_seed, DBTypeEnum.Memory);

        // ─── PBKDF2 Happy Path ─────────────────────────────────────────────────

        [Fact]
        public async Task DoLoginAsync_ValidPBKDF2User_ReturnsLoginUserInfo()
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

        [Fact]
        public async Task DoLoginAsync_ValidPBKDF2User_WrongPassword_ReturnsNull()
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

        [Fact]
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

        [Fact]
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

        [Fact]
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

            // Assert: password in DB is now PBKDF2 (longer than 32 chars)
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

        // ─── User State Checks ─────────────────────────────────────────────────

        [Fact]
        public async Task DoLoginAsync_NonexistentUser_ReturnsNull()
        {
            var wtm = WtmTestHelper.CreateLoginTestContext(CreateDb());
            var result = await wtm.DoLoginAsync("nobody", "pass", null);
            result.Should().BeNull();
        }

        [Fact]
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

        [Fact]
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
    /// Uses FrameworkContext base so EF knows about FrameworkUserBase hierarchy.
    /// TestLoginUser is registered as a DbSet to map it to the InMemory store.
    /// </summary>
    internal class LoginTestDataContext : FrameworkContext
    {
        public LoginTestDataContext(string cs, DBTypeEnum dbtype)
            : base(cs, dbtype) { }

        public DbSet<TestLoginUser> TestLoginUsers { get; set; } = null!;
    }
}
