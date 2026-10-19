using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Vue3Demo;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>
    /// DoLogin against the demo model on SQLite (shared in-memory mode): LoginUserInfo.LoadBasicInfoAsync uses queries
    /// that the EF InMemory provider cannot translate. The database lives as long as one connection to the same name stays open.
    /// </summary>
    [TestClass]
    public class DoLoginPasswordTest
    {
        private string _seed;
        private SqliteConnection _keepAlive;
        // true: EF InMemory (no set based updates, so the rehash takes its fallback path); LoadBasicInfoAsync cannot run there, see TryLogin
        private bool _memory;

        private string SqliteCs => $"Data Source=file:{_seed}?mode=memory&cache=shared";

        private DataContext NewDc()
        {
            return _memory
                ? new DataContext(new CS { Key = "default", Value = _seed, DbType = DBTypeEnum.Memory })
                : new DataContext(SqliteCs, DBTypeEnum.SQLite);
        }

        private void Execute(string sql)
        {
            using (var cmd = _keepAlive.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        private long ScalarLong(string sql)
        {
            using (var cmd = _keepAlive.CreateCommand())
            {
                cmd.CommandText = sql;
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }

        // after the password check DoLogin loads the user's roles, which EF InMemory cannot translate; the rehash has already happened by then
        private LoginUserInfo TryLogin(WTMContext wtm, string user, string password, string tenant = null)
        {
            try
            {
                return wtm.DoLogin(user, password, tenant);
            }
            catch (AggregateException) when (_memory)
            {
                return null;
            }
        }

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString("N");
            _keepAlive = new SqliteConnection($"Data Source=file:{_seed}?mode=memory&cache=shared");
            _keepAlive.Open();
            using (var dc = NewDc())
            {
                dc.Database.EnsureCreated();
            }
            // counts every UPDATE statement that reaches the user table
            Execute("CREATE TABLE WriteLog (N INTEGER)");
            Execute("CREATE TRIGGER LogUserUpdates AFTER UPDATE ON FrameworkUsers BEGIN INSERT INTO WriteLog VALUES (1); END");
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive?.Dispose();
        }

        private void AddUser(string itcode, string storedPassword, bool isValid = true, string tenant = null)
        {
            using (var dc = NewDc())
            {
                dc.Set<FrameworkUser>().Add(new FrameworkUser { ITCode = itcode, Name = itcode, Password = storedPassword, IsValid = isValid, TenantCode = tenant });
                dc.SaveChanges();
            }
        }

        private string StoredPassword(string itcode)
        {
            using (var dc = NewDc())
            {
                return dc.Set<FrameworkUser>().Where(x => x.ITCode == itcode).Select(x => x.Password).Single();
            }
        }

        private WTMContext CreateContext(bool authenticated = false, PasswordHasher hasher = null)
        {
            var wtm = MockWtmContext.CreateWtmContext(NewDc(), "admin");
            wtm.GlobaInfo.CustomUserType = typeof(FrameworkUser);
            wtm.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant>());
            var identity = authenticated ? new ClaimsIdentity(new[] { new Claim(AuthConstants.JwtClaimTypes.Subject, "authuser") }, "test") : new ClaimsIdentity();
            Mock.Get(wtm.HttpContext).Setup(x => x.User).Returns(new ClaimsPrincipal(identity));
            var tokenService = new Mock<ITokenService>();
            tokenService.Setup(x => x.IssueTokenAsync(It.IsAny<LoginUserInfo>())).Returns(Task.FromResult(new Token { AccessToken = "token" }));
            Mock.Get(wtm.HttpContext.RequestServices).Setup(x => x.GetService(typeof(ITokenService))).Returns(tokenService.Object);
            if (hasher != null)
            {
                wtm.PasswordHasher = hasher;
            }
            return wtm;
        }

        [TestMethod]
        public void CorrectPasswordLogsIn()
        {
            AddUser("admin", Utils.GetMD5String("000000"));
            var user = CreateContext().DoLogin("admin", "000000", null);
            Assert.IsNotNull(user);
            Assert.AreEqual("admin", user.ITCode);
            Assert.AreEqual("token", user.RemoteToken);
        }

        [TestMethod]
        public void LowerCaseStoredHashLogsIn()
        {
            AddUser("admin", Utils.GetMD5String("000000").ToLowerInvariant());
            Assert.IsNotNull(CreateContext().DoLogin("admin", "000000", null));
        }

        [TestMethod]
        public void WrongPasswordIsRejected()
        {
            AddUser("admin", Utils.GetMD5String("000000"));
            Assert.IsNull(CreateContext().DoLogin("admin", "111111", null));
            Assert.IsNull(CreateContext().DoLogin("admin", "", null));
        }

        [TestMethod]
        public void UnknownUserIsRejected()
        {
            AddUser("admin", Utils.GetMD5String("000000"));
            Assert.IsNull(CreateContext().DoLogin("nobody", "000000", null));
        }

        [TestMethod]
        public void InvalidUserIsRejected()
        {
            AddUser("admin", Utils.GetMD5String("000000"), isValid: false);
            Assert.IsNull(CreateContext().DoLogin("admin", "000000", null));
        }

        [TestMethod]
        public void StoredPasswordItselfIsNotAccepted()
        {
            var stored = Utils.GetMD5String("000000");
            AddUser("admin", stored);
            Assert.IsNull(CreateContext().DoLogin("admin", stored, null));
        }

        [TestMethod]
        public void NullPasswordDoesNotLogInUsersWithHashedPassword()
        {
            AddUser("admin", Utils.GetMD5String("000000"));
            Assert.IsNull(CreateContext().DoLogin("admin", null, null));
        }

        [TestMethod]
        public void NullPasswordOnlyMatchesEmptyStoredPassword()
        {
            // unchanged: GetMD5String(null) is the empty string, so only an account whose stored password is empty matches
            AddUser("legacy", "");
            Assert.IsNotNull(CreateContext().DoLogin("legacy", null, null));
            Assert.IsNull(CreateContext().DoLogin("legacy", "", null));
            Assert.IsNull(CreateContext().DoLogin("legacy", "anything", null));
        }

        [TestMethod]
        public void TenantMustMatch()
        {
            AddUser("admin", Utils.GetMD5String("000000"), tenant: "t1");
            Assert.IsNull(CreateContext().DoLogin("admin", "000000", null));
        }

        [TestMethod]
        public void AuthenticatedRequestSkipsPasswordCheck()
        {
            AddUser("authuser", Utils.GetMD5String("000000"));
            var user = CreateContext(authenticated: true).DoLogin("ignored", null, null);
            Assert.IsNotNull(user);
            Assert.AreEqual("authuser", user.ITCode);
            AddUser("authinvalid", "x", isValid: false);
            var invalid = CreateContext(authenticated: true);
            Mock.Get(invalid.HttpContext).Setup(x => x.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(AuthConstants.JwtClaimTypes.Subject, "authinvalid") }, "test")));
            Assert.IsNull(invalid.DoLogin("ignored", null, null), "the account must still exist and be valid");
        }

        [TestMethod]
        public void DefaultOptionsKeepMd5AndNeverRehash()
        {
            var stored = Utils.GetMD5String("000000");
            AddUser("admin", stored);
            var wtm = CreateContext();
            Assert.AreEqual(PasswordAlgorithm.Md5, wtm.PasswordHasher.Algorithm);
            Assert.IsNotNull(wtm.DoLogin("admin", "000000", null));
            Assert.AreEqual(stored, StoredPassword("admin"));
            Assert.AreEqual(Utils.GetMD5String("abc"), wtm.HashPassword("abc"));
            Assert.AreEqual(PasswordVerifyResult.Success, wtm.VerifyPassword(stored, "000000"));
        }

        [TestMethod]
        public void Pbkdf2ConfiguredButColumnTooShortFallsBackToMd5()
        {
            var stored = Utils.GetMD5String("000000");
            AddUser("admin", stored);
            var wtm = CreateContext();
            wtm.ConfigInfo.PasswordOptions = new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = PasswordHasher.MinIterations };
            Assert.AreEqual(PasswordAlgorithm.Md5, wtm.PasswordHasher.Algorithm, "the model column holds 32 characters");
            Assert.IsTrue(wtm.PasswordHasher.FellBackToMd5);
            Assert.IsNotNull(wtm.DoLogin("admin", "000000", null));
            Assert.AreEqual(stored, StoredPassword("admin"), "no rehash into a column that is too short");
            Assert.AreEqual(Utils.GetMD5String("abc"), wtm.HashPassword("abc"));
        }

        [TestMethod]
        public void SuccessfulLoginRehashesWhenHasherAsksForIt()
        {
            AddUser("admin", Utils.GetMD5String("000000"));
            var hasher = new PasswordHasher(new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = PasswordHasher.MinIterations });
            Assert.IsNotNull(CreateContext(hasher: hasher).DoLogin("admin", "000000", null));
            var stored = StoredPassword("admin");
            StringAssert.StartsWith(stored, "$pbkdf2-sha256$100000$");
            // and the upgraded hash keeps working
            Assert.IsNotNull(CreateContext(hasher: hasher).DoLogin("admin", "000000", null));
            Assert.IsNull(CreateContext(hasher: hasher).DoLogin("admin", "111111", null));
            Assert.AreEqual(stored, StoredPassword("admin"), "a current hash is not rewritten");
        }

        [TestMethod]
        public void FailedLoginDoesNotRehash()
        {
            var stored = Utils.GetMD5String("000000");
            AddUser("admin", stored);
            var hasher = new PasswordHasher(new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = PasswordHasher.MinIterations });
            Assert.IsNull(CreateContext(hasher: hasher).DoLogin("admin", "wrong", null));
            Assert.AreEqual(stored, StoredPassword("admin"));
        }

        [TestMethod]
        public void Pbkdf2StoredPasswordLogsIn()
        {
            var hasher = new PasswordHasher(new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = PasswordHasher.MinIterations });
            AddUser("admin", hasher.Hash("000000"));
            Assert.IsNotNull(CreateContext().DoLogin("admin", "000000", null), "verified even though the default options are MD5");
            Assert.IsNull(CreateContext().DoLogin("admin", "111111", null));
            Assert.IsNull(CreateContext().DoLogin("admin", null, null));
        }

        private static CountingPasswordHasher Pbkdf2Counting(int? maxStoredLength = null)
        {
            return new CountingPasswordHasher(new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = PasswordHasher.MinIterations }, maxStoredLength);
        }

        private void ChangePasswordBehindTheBack(string itcode, string newStored)
        {
            using (var dc = NewDc())
            {
                dc.Set<FrameworkUser>().Single(x => x.ITCode == itcode).Password = newStored;
                dc.SaveChanges();
            }
        }

        [TestMethod]
        public void RehashDoesNotRevertAConcurrentPasswordChange_SetBasedUpdate()
        {
            AddUser("admin", Utils.GetMD5String("000000"));
            var changed = Utils.GetMD5String("changed meanwhile");
            var hasher = Pbkdf2Counting();
            // the first derivation happens while the stored hash is being checked, before the rehash is written
            hasher.OnDerive = n => { if (n == 1) { ChangePasswordBehindTheBack("admin", changed); } };
            Assert.IsNotNull(CreateContext(hasher: hasher).DoLogin("admin", "000000", null), "the password was correct when it was checked");
            Assert.AreEqual(changed, StoredPassword("admin"), "the newer password is kept");
        }

        [TestMethod]
        public void RehashDoesNotRevertAConcurrentPasswordChange_FallbackUpdate()
        {
            _memory = true;
            AddUser("admin", Utils.GetMD5String("000000"));
            var changed = Utils.GetMD5String("changed meanwhile");
            var hasher = Pbkdf2Counting();
            hasher.OnDerive = n => { if (n == 1) { ChangePasswordBehindTheBack("admin", changed); } };
            TryLogin(CreateContext(hasher: hasher), "admin", "000000");
            Assert.AreEqual(changed, StoredPassword("admin"), "the newer password is kept");
        }

        [TestMethod]
        public void RehashWritesWhenNothingChanged_FallbackUpdate()
        {
            _memory = true;
            AddUser("admin", Utils.GetMD5String("000000"));
            TryLogin(CreateContext(hasher: Pbkdf2Counting()), "admin", "000000");
            StringAssert.StartsWith(StoredPassword("admin"), "$pbkdf2-sha256$100000$");
        }

        [TestMethod]
        public void RehashWriteIsNarrow_SetBasedUpdate()
        {
            AddUser("admin", Utils.GetMD5String("000000"));
            var wtm = CreateContext(hasher: Pbkdf2Counting());
            wtm.DC.Set<FrameworkRole>().Add(new FrameworkRole { RoleCode = "pending", RoleName = "pending" });
            Assert.IsNotNull(wtm.DoLogin("admin", "000000", null));
            StringAssert.StartsWith(StoredPassword("admin"), "$pbkdf2-sha256$100000$");
            using (var dc = NewDc())
            {
                Assert.AreEqual(0, dc.Set<FrameworkRole>().Count(), "unrelated pending changes of the request context are not flushed");
            }
            Assert.IsTrue(((DbContext)wtm.DC).ChangeTracker.HasChanges(), "and they are still pending");
        }

        [TestMethod]
        public void RehashWriteIsNarrow_FallbackUpdate()
        {
            _memory = true;
            AddUser("admin", Utils.GetMD5String("000000"));
            var wtm = CreateContext(hasher: Pbkdf2Counting());
            wtm.DC.Set<FrameworkRole>().Add(new FrameworkRole { RoleCode = "pending", RoleName = "pending" });
            TryLogin(wtm, "admin", "000000");
            StringAssert.StartsWith(StoredPassword("admin"), "$pbkdf2-sha256$100000$");
            using (var dc = NewDc())
            {
                Assert.AreEqual(0, dc.Set<FrameworkRole>().Count(), "unrelated pending changes of the request context are not flushed");
            }
        }

        [TestMethod]
        public void RehashFailureLeavesTheContextCleanAndTheLoginWorking()
        {
            var stored = Utils.GetMD5String("000000");
            AddUser("admin", stored);
            var hasher = Pbkdf2Counting();
            // derivation 1 is the timing equalization inside Verify, derivation 2 is the new hash for the rehash
            hasher.OnDerive = n => { if (n == 2) { throw new InvalidOperationException("simulated failure"); } };
            var wtm = CreateContext(hasher: hasher);
            Assert.IsNotNull(wtm.DoLogin("admin", "000000", null), "a failed upgrade must not block the login");
            Assert.AreEqual(stored, StoredPassword("admin"));
            Assert.IsFalse(((DbContext)wtm.DC).ChangeTracker.HasChanges());
        }

        [TestMethod]
        public void NullPasswordDoesNoHashingWork()
        {
            AddUser("legacy", "");
            AddUser("admin", Utils.GetMD5String("000000"));
            var hasher = Pbkdf2Counting();
            Assert.IsNotNull(CreateContext(hasher: hasher).DoLogin("legacy", null, null));
            Assert.IsNull(CreateContext(hasher: hasher).DoLogin("admin", null, null));
            Assert.IsNull(CreateContext(hasher: hasher).DoLogin("nobody", null, null));
            Assert.AreEqual(0, hasher.Derivations);
        }

        [TestMethod]
        public void UnknownAccountAndLegacyHashCostTheSameDerivations()
        {
            AddUser("admin", Utils.GetMD5String("000000"));
            var unknown = Pbkdf2Counting();
            Assert.IsNull(CreateContext(hasher: unknown).DoLogin("nobody", "000000", null));
            var legacyWrong = Pbkdf2Counting();
            Assert.IsNull(CreateContext(hasher: legacyWrong).DoLogin("admin", "wrong", null));
            Assert.AreEqual(1, unknown.Derivations);
            Assert.AreEqual(unknown.Derivations, legacyWrong.Derivations);
        }

        [TestMethod]
        public void SameAccountInTwoTenantsLogsInWithTheRightPassword()
        {
            AddUser("admin", Utils.GetMD5String("main"), tenant: null);
            AddUser("admin", Utils.GetMD5String("other"), tenant: "t1");
            Assert.IsNotNull(CreateContext().DoLogin("admin", "main", null));
            Assert.IsNull(CreateContext().DoLogin("admin", "other", null), "the password of the other tenant's account does not work here");

            var wtm = CreateContext();
            wtm.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant> { new FrameworkTenant { TCode = "t1", TName = "t1", TDb = SqliteCs, TDbType = DBTypeEnum.SQLite, DbContext = "DataContext" } });
            var user = wtm.DoLogin("admin", "other", "t1");
            Assert.IsNotNull(user);
            Assert.AreEqual("t1", user.TenantCode);
            Assert.IsNull(CreateContext().DoLogin("admin", "main", "t1"));
            var wrong = CreateContext();
            wrong.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant> { new FrameworkTenant { TCode = "t1", TName = "t1", TDb = SqliteCs, TDbType = DBTypeEnum.SQLite, DbContext = "DataContext" } });
            Assert.IsNull(wrong.DoLogin("admin", "main", "t1"), "the main account's password does not work for the tenant account");
        }

        [TestMethod]
        public void ExternalAuthAndReloadPaths_NeverWritePassword()
        {
            // the column is considered wide enough here, so PBKDF2 is really in effect and a rehash would be possible
            var hasher = Pbkdf2Counting(maxStoredLength: null);
            Assert.AreEqual(PasswordAlgorithm.Pbkdf2Sha256, hasher.Algorithm);
            var md5 = Utils.GetMD5String("000000");
            AddUser("authuser", md5);
            AddUser("admin", md5);
            AddUser("legacy", "");

            // (a) authenticated request, e.g. after external sign-in: no password is involved
            var authenticated = CreateContext(authenticated: true, hasher: hasher);
            var user = authenticated.DoLogin("ignored", null, null);
            Assert.IsNotNull(user);
            Assert.AreEqual("authuser", user.ITCode);
            Assert.AreEqual(md5, StoredPassword("authuser"));
            Assert.IsFalse(((DbContext)authenticated.DC).ChangeTracker.HasChanges());

            // (b) ReloadUser: the password is null
            var reload = CreateContext(hasher: hasher);
            Assert.IsNull(reload.ReloadUser("admin"), "an MD5 hashed account cannot be reloaded without its password, as before");
            Assert.AreEqual(md5, StoredPassword("admin"));
            var reloadLegacy = CreateContext(hasher: hasher);
            Assert.IsNotNull(reloadLegacy.ReloadUser("legacy"));
            Assert.AreEqual("", StoredPassword("legacy"));

            Assert.AreEqual(0, hasher.Derivations, "no hashing work on these paths");
            Assert.AreEqual(0, ScalarLong("SELECT COUNT(*) FROM WriteLog"), "no UPDATE statement reached the user table");
        }
    }
}
