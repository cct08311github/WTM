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

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString("N");
            _keepAlive = new SqliteConnection($"Data Source=file:{_seed}?mode=memory&cache=shared");
            _keepAlive.Open();
            using (var dc = new DataContext($"Data Source=file:{_seed}?mode=memory&cache=shared", DBTypeEnum.SQLite))
            {
                dc.Database.EnsureCreated();
            }
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive?.Dispose();
        }

        private void AddUser(string itcode, string storedPassword, bool isValid = true, string tenant = null)
        {
            using (var dc = new DataContext($"Data Source=file:{_seed}?mode=memory&cache=shared", DBTypeEnum.SQLite))
            {
                dc.Set<FrameworkUser>().Add(new FrameworkUser { ITCode = itcode, Name = itcode, Password = storedPassword, IsValid = isValid, TenantCode = tenant });
                dc.SaveChanges();
            }
        }

        private string StoredPassword(string itcode)
        {
            using (var dc = new DataContext($"Data Source=file:{_seed}?mode=memory&cache=shared", DBTypeEnum.SQLite))
            {
                return dc.Set<FrameworkUser>().Where(x => x.ITCode == itcode).Select(x => x.Password).Single();
            }
        }

        private WTMContext CreateContext(bool authenticated = false, PasswordHasher hasher = null)
        {
            var wtm = MockWtmContext.CreateWtmContext(new DataContext($"Data Source=file:{_seed}?mode=memory&cache=shared", DBTypeEnum.SQLite), "admin");
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
            wtm.ConfigInfo.PasswordOptions = new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = 1000 };
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
            var hasher = new PasswordHasher(new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = 1000 });
            Assert.IsNotNull(CreateContext(hasher: hasher).DoLogin("admin", "000000", null));
            var stored = StoredPassword("admin");
            StringAssert.StartsWith(stored, "$pbkdf2-sha256$1000$");
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
            var hasher = new PasswordHasher(new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = 1000 });
            Assert.IsNull(CreateContext(hasher: hasher).DoLogin("admin", "wrong", null));
            Assert.AreEqual(stored, StoredPassword("admin"));
        }

        [TestMethod]
        public void Pbkdf2StoredPasswordLogsIn()
        {
            var hasher = new PasswordHasher(new PasswordOption { Algorithm = PasswordAlgorithm.Pbkdf2Sha256, Pbkdf2Iterations = 1000 });
            AddUser("admin", hasher.Hash("000000"));
            Assert.IsNotNull(CreateContext().DoLogin("admin", "000000", null), "verified even though the default options are MD5");
            Assert.IsNull(CreateContext().DoLogin("admin", "111111", null));
            Assert.IsNull(CreateContext().DoLogin("admin", null, null));
        }
    }
}
