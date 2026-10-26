using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.ConfigOptions;
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
        private WTMContext CreateContext(string subject, string tenantClaim = null, Task<Token> token = null, string rtokenClaim = null, string queryRemoteToken = null)
        {
            var wtm = MockWtmContext.CreateWtmContext(NewDc(), "template", remoteToken: queryRemoteToken);
            wtm.GlobaInfo.CustomUserType = typeof(FrameworkUser);
            wtm.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant>());
            wtm.LoginUserInfo = null;
            var claims = new List<Claim> { new Claim(AuthConstants.JwtClaimTypes.Subject, subject) };
            if (tenantClaim != null)
            {
                claims.Add(new Claim(AuthConstants.JwtClaimTypes.TenantCode, tenantClaim));
                wtm.GlobaInfo.SetTenantGetFunc(() => new List<FrameworkTenant> { new FrameworkTenant { TCode = tenantClaim, TName = tenantClaim, TDb = SqliteCs, TDbType = DBTypeEnum.SQLite, DbContext = "DataContext" } });
            }
            if (rtokenClaim != null)
            {
                claims.Add(new Claim(AuthConstants.JwtClaimTypes.RToken, rtokenClaim));
            }
            Mock.Get(wtm.HttpContext).Setup(x => x.User).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
            var tokenService = new Mock<ITokenService>();
            tokenService.Setup(x => x.IssueTokenAsync(It.IsAny<LoginUserInfo>())).Returns(token ?? Task.FromResult(new Token { AccessToken = "token" }));
            Mock.Get(wtm.HttpContext.RequestServices).Setup(x => x.GetService(typeof(ITokenService))).Returns(tokenService.Object);
            _tokens = tokenService;
            return wtm;
        }

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

        // waits for a task that should finish; one that never does fails the test instead of hanging it
        private static async Task<T> Within<T>(Task<T> task, string what)
        {
            var finished = await Task.WhenAny(task, Task.Delay(Patience));
            Assert.AreSame(task, finished, $"{what}: not finished within {Patience.TotalSeconds}s");
            return await task;
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

            // started on the thread pool and boxed: an implementation that blocks on the token fails the test (the call does not return) instead of hanging it
            var started = await Within(Task.Run(() => Tuple.Create(wtm.EnsureLoginUserInfoAsync())), "EnsureLoginUserInfoAsync (the call blocked instead of returning a pending task)");
            var pending = started.Item1;
            // the data is read first; wait until the token is asked for
            for (var i = 0; i < 200 && _tokens.Invocations.Count == 0; i++)
            {
                await Task.Delay(10);
            }

            Assert.AreEqual(1, _tokens.Invocations.Count, "the token has been asked for");
            Assert.IsFalse(pending.IsCompleted, "the request thread is free while the token is being issued");
            gate.SetResult(new Token { AccessToken = "late" });
            var user = await Within(pending, "the user to finish loading");
            Assert.AreEqual("late", user.RemoteToken);
        }

        // ---- main host: the branch that DoLoginCore and DoLoginCoreAsync duplicate ----

        private class StubMainHost : HttpMessageHandler
        {
            public readonly List<string> Authorizations = new List<string>();
            public readonly string Body;

            public StubMainHost(LoginUserInfo remote)
            {
                Body = JsonSerializer.Serialize(remote, CoreProgram.DefaultJsonOption);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Authorizations.Add(request.Headers.Authorization?.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
            }
        }

        private Guid _remoteRoleId = Guid.NewGuid();

        // the same request for a sub site with a main host; the main host answers checkuserinfo with the stubbed user
        private WTMContext CreateMainHostContext(StubMainHost mainHost, string rtokenClaim, string queryRemoteToken)
        {
            var wtm = CreateContext("authuser", rtokenClaim: rtokenClaim, queryRemoteToken: queryRemoteToken);
            wtm.ConfigInfo.Domains = new Dictionary<string, Domain> { { "mainhost", new Domain { Name = "mainhost", Address = "http://mainhost.test" } } };
            Assert.IsTrue(wtm.ConfigInfo.HasMainHost);
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(x => x.CreateClient("mainhost")).Returns(() => new HttpClient(mainHost, false) { BaseAddress = new Uri("http://mainhost.test") });
            Mock.Get(wtm.HttpContext.RequestServices).Setup(x => x.GetService(typeof(IHttpClientFactory))).Returns(factory.Object);
            return wtm;
        }

        private LoginUserInfo RemoteUser()
        {
            return new LoginUserInfo
            {
                ITCode = "authuser",
                Name = "Remote Name",
                Roles = new List<SimpleRole> { new SimpleRole { ID = _remoteRoleId, RoleCode = "R1", RoleName = "role" } },
                Groups = new List<SimpleGroup>()
            };
        }

        private void AddFunctionPrivilege(string roleCode)
        {
            using (var dc = NewDc())
            {
                dc.Set<FunctionPrivilege>().Add(new FunctionPrivilege { RoleCode = roleCode, MenuItemId = Guid.NewGuid(), Allowed = true });
                dc.SaveChanges();
            }
        }

        [TestMethod]
        public async Task MainHost_SyncAndAsyncPaths_GiveTheSameUser_TheTokenOfTheClaimWinsOverTheQuery_AndNoTokenIsIssued()
        {
            AddFunctionPrivilege("R1");
            var syncHost = new StubMainHost(RemoteUser());
            var asyncHost = new StubMainHost(RemoteUser());
            var viaGetter = CreateMainHostContext(syncHost, "claim-token", "query-token");
            var viaAsync = CreateMainHostContext(asyncHost, "claim-token", "query-token");
            var viaReload = CreateMainHostContext(new StubMainHost(RemoteUser()), "claim-token", "query-token");

            var b = viaGetter.LoginUserInfo;                                  // sync: LoginUserInfo -> ReloadUser -> DoLoginCore
            var a = await viaAsync.EnsureLoginUserInfoAsync();                // async: EnsureLoginUserInfoAsync -> ReloadUserAsync -> DoLoginCoreAsync
            var c = await viaReload.ReloadUserAsync("ignored");
            var d = CreateMainHostContext(new StubMainHost(RemoteUser()), "claim-token", "query-token").ReloadUser("ignored");

            AssertSameUser(b, a);
            AssertSameUser(b, c);
            AssertSameUser(b, d);
            Assert.AreEqual("Remote Name", a.Name);
            Assert.AreEqual("claim-token", a.RemoteToken, "the RToken claim of the sign-in, not the _remotetoken of the query");
            Assert.AreEqual(1, a.Roles.Count);
            Assert.AreEqual(1, a.FunctionPrivileges.Count, "LoadBasicInfo ran on the remote user: its role's privileges come from the local database");
            Assert.AreEqual(1, b.FunctionPrivileges.Count);
            CollectionAssert.AreEqual(syncHost.Authorizations, asyncHost.Authorizations);
            CollectionAssert.AreEqual(new[] { "Bearer claim-token" }, asyncHost.Authorizations, "the main host is asked with the token of the claim");
            _tokens.Verify(x => x.IssueTokenAsync(It.IsAny<LoginUserInfo>()), Times.Never);
            AssertSameUser(viaGetter.Cache.Get<LoginUserInfo>(Key("authuser")), viaAsync.Cache.Get<LoginUserInfo>(Key("authuser")));
        }

        [TestMethod]
        public async Task MainHost_WithoutATokenClaim_BothPathsGiveNull_AndNeverCallTheMainHost()
        {
            var syncHost = new StubMainHost(RemoteUser());
            var asyncHost = new StubMainHost(RemoteUser());
            var viaGetter = CreateMainHostContext(syncHost, null, "query-token");
            var viaAsync = CreateMainHostContext(asyncHost, null, "query-token");

            Assert.IsNull(viaGetter.LoginUserInfo, "a signed-in request is reloaded from its claims only: the query token is not used");
            Assert.IsNull(await viaAsync.EnsureLoginUserInfoAsync());
            Assert.AreEqual(0, syncHost.Authorizations.Count);
            Assert.AreEqual(0, asyncHost.Authorizations.Count);
        }
    }
}
