using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Vue3Demo;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Admin.Test
{
    /// <summary>
    /// EnsureLoginUserInfoAsync / ReloadUserAsync against the demo model on SQLite (shared in-memory mode), compared with what the
    /// synchronous property loads: same user data, same cache key, same token. The database lives as long as one connection to the same name stays open.
    /// </summary>
    [TestClass]
    public class LoginUserInfoAsyncDbTest
    {
        private string _seed;
        private SqliteConnection _keepAlive;
        private Mock<ITokenService> _tokens;

        private string SqliteCs => $"Data Source=file:{_seed}?mode=memory&cache=shared";

        private DataContext NewDc()
        {
            return new DataContext(SqliteCs, DBTypeEnum.SQLite);
        }

        [TestInitialize]
        public void Init()
        {
            _seed = Guid.NewGuid().ToString("N");
            _keepAlive = new SqliteConnection(SqliteCs);
            _keepAlive.Open();
            using (var dc = NewDc())
            {
                dc.Database.EnsureCreated();
            }
        }

        [TestCleanup]
        public void Cleanup()
        {
            _keepAlive?.Dispose();
        }

        private void AddUser(string itcode, bool isValid = true, string tenant = null, string name = null)
        {
            using (var dc = NewDc())
            {
                dc.Set<FrameworkUser>().Add(new FrameworkUser { ITCode = itcode, Name = name ?? itcode, Password = Utils.GetMD5String("000000"), IsValid = isValid, TenantCode = tenant });
                dc.SaveChanges();
            }
        }

        private static string Key(string itcode, string tenant = null)
        {
            return $"{GlobalConstants.CacheKey.UserInfo}:{itcode + "$`$" + tenant}";
        }

        // a signed-in request whose user is not loaded yet
        private WTMContext CreateContext(string subject, string tenantClaim = null, Task<Token> token = null)
        {
            var wtm = MockWtmContext.CreateWtmContext(NewDc(), "template");
            wtm.GlobaInfo.CustomUserType = typeof(FrameworkUser);
            wtm.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant>());
            wtm.LoginUserInfo = null;
            var claims = new List<Claim> { new Claim(AuthConstants.JwtClaimTypes.Subject, subject) };
            if (tenantClaim != null)
            {
                claims.Add(new Claim(AuthConstants.JwtClaimTypes.TenantCode, tenantClaim));
                wtm.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant> { new FrameworkTenant { TCode = tenantClaim, TName = tenantClaim, TDb = SqliteCs, TDbType = DBTypeEnum.SQLite, DbContext = "DataContext" } });
            }
            Mock.Get(wtm.HttpContext).Setup(x => x.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
            var tokenService = new Mock<ITokenService>();
            tokenService.Setup(x => x.IssueTokenAsync(It.IsAny<LoginUserInfo>())).Returns(token ?? Task.FromResult(new Token { AccessToken = "token" }));
            Mock.Get(wtm.HttpContext.RequestServices).Setup(x => x.GetService(typeof(ITokenService))).Returns(tokenService.Object);
            _tokens = tokenService;
            return wtm;
        }

        private static void AssertSameUser(LoginUserInfo expected, LoginUserInfo actual)
        {
            Assert.IsNotNull(expected);
            Assert.IsNotNull(actual);
            Assert.AreEqual(expected.ITCode, actual.ITCode);
            Assert.AreEqual(expected.UserId, actual.UserId);
            Assert.AreEqual(expected.Name, actual.Name);
            Assert.AreEqual(expected.TenantCode, actual.TenantCode);
            Assert.AreEqual(expected.RemoteToken, actual.RemoteToken);
            Assert.AreEqual(expected.Roles?.Count, actual.Roles?.Count);
            Assert.AreEqual(expected.Groups?.Count, actual.Groups?.Count);
            Assert.AreEqual(expected.FunctionPrivileges?.Count, actual.FunctionPrivileges?.Count);
            Assert.AreEqual(expected.DataPrivileges?.Count, actual.DataPrivileges?.Count);
        }

        [TestMethod]
        public async Task CacheMiss_LoadsTheSameUserAsTheGetter_AndCachesItUnderTheSameKey()
        {
            AddUser("authuser", name: "Auth User");
            var viaAsync = CreateContext("authuser");
            var viaGetter = CreateContext("authuser");

            var a = await viaAsync.EnsureLoginUserInfoAsync();
            var b = viaGetter.LoginUserInfo;

            AssertSameUser(b, a);
            Assert.AreEqual("Auth User", a.Name);
            Assert.AreEqual("token", a.RemoteToken, "the same token content as the sync path");
            Assert.AreSame(a, viaAsync.LoginUserInfo, "the property now finds it ready");
            AssertSameUser(viaGetter.Cache.Get<LoginUserInfo>(Key("authuser")), viaAsync.Cache.Get<LoginUserInfo>(Key("authuser")));
            _tokens.Verify(x => x.IssueTokenAsync(It.IsAny<LoginUserInfo>()), Times.Once);
        }

        [TestMethod]
        public async Task CacheHit_DoesNotTouchTheDatabaseOrTheTokenService()
        {
            AddUser("authuser");
            var wtm = CreateContext("authuser");
            wtm.Cache.Add(Key("authuser"), new LoginUserInfo { ITCode = "authuser", Name = "from-cache" });

            var user = await wtm.EnsureLoginUserInfoAsync();

            Assert.AreEqual("from-cache", user.Name);
            _tokens.Verify(x => x.IssueTokenAsync(It.IsAny<LoginUserInfo>()), Times.Never);
        }

        [TestMethod]
        public async Task ADisabledOrUnknownUser_GivesNull_NoTokenAndNoCacheEntry()
        {
            AddUser("disabled", isValid: false);
            foreach (var itcode in new[] { "disabled", "ghost" })
            {
                var viaAsync = CreateContext(itcode);
                var viaGetter = CreateContext(itcode);

                Assert.IsNull(await viaAsync.EnsureLoginUserInfoAsync(), itcode);
                Assert.IsNull(viaGetter.LoginUserInfo, itcode);
                Assert.IsNull(viaAsync.Cache.Get<LoginUserInfo>(Key(itcode)), itcode);
                _tokens.Verify(x => x.IssueTokenAsync(It.IsAny<LoginUserInfo>()), Times.Never);
            }
        }

        [TestMethod]
        public async Task TheTenantOfTheClaim_SelectsTheTenantAccount_LikeTheGetter()
        {
            AddUser("both", name: "main account");
            AddUser("both", tenant: "t1", name: "tenant account");
            var viaAsync = CreateContext("both", "t1");
            var viaGetter = CreateContext("both", "t1");

            var a = await viaAsync.EnsureLoginUserInfoAsync();
            var b = viaGetter.LoginUserInfo;

            AssertSameUser(b, a);
            Assert.AreEqual("t1", a.TenantCode);
            Assert.AreEqual("tenant account", a.Name);
            Assert.IsNotNull(viaAsync.Cache.Get<LoginUserInfo>(Key("both", "t1")));
            Assert.IsNull(viaAsync.Cache.Get<LoginUserInfo>(Key("both")), "not cached for the main tenant");

            // no tenant claim: the account of the main tenant
            var main = await CreateContext("both").EnsureLoginUserInfoAsync();
            Assert.AreEqual("main account", main.Name);
            Assert.IsNull(main.TenantCode);

            // a tenant that does not exist: nobody
            var unknownTenant = CreateContext("both", "t1");
            unknownTenant.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant>());
            Assert.IsNull(await unknownTenant.EnsureLoginUserInfoAsync());
        }

        [TestMethod]
        public async Task ReloadUserAsync_GivesWhatReloadUserGives()
        {
            AddUser("authuser");
            var a = await CreateContext("authuser").ReloadUserAsync("ignored");
            var b = CreateContext("authuser").ReloadUser("ignored");

            AssertSameUser(b, a);
            Assert.AreEqual("authuser", a.ITCode, "the account of the verified sign-in, as with ReloadUser");

            Assert.IsNull(await CreateContext("ghost").ReloadUserAsync("ignored"));
        }

        [TestMethod]
        public async Task TheTokenIsAwaited_NotBlockedOn()
        {
            AddUser("authuser");
            var gate = new TaskCompletionSource<Token>(TaskCreationOptions.RunContinuationsAsynchronously);
            var wtm = CreateContext("authuser", token: gate.Task);

            var pending = wtm.EnsureLoginUserInfoAsync();
            // the data is read first; wait until the token is asked for
            for (var i = 0; i < 200 && _tokens.Invocations.Count == 0; i++)
            {
                await Task.Delay(10);
            }

            Assert.AreEqual(1, _tokens.Invocations.Count, "the token has been asked for");
            Assert.IsFalse(pending.IsCompleted, "the request thread is free while the token is being issued");
            gate.SetResult(new Token { AccessToken = "late" });
            var user = await pending;
            Assert.AreEqual("late", user.RemoteToken);
        }
    }
}
