using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Auth;
using WalkingTec.Mvvm.Core.Extensions;
using WalkingTec.Mvvm.Core.Implement;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Mvc.Filters;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test
{
    /// <summary>
    /// The asynchronous way to load LoginUserInfo (EnsureLoginUserInfoAsync, ReloadUserAsync), the filter that calls it early in the request
    /// and the SetAsync session extension. The property keeps working on its own, as before.
    /// </summary>
    [TestClass]
    public class LoginUserInfoAsyncTest
    {
        private static string Key(string itcode, string tenant = null)
        {
            return $"{GlobalConstants.CacheKey.UserInfo}:{itcode + "$`$" + tenant}";
        }

        private static ClaimsPrincipal SignedIn(string itcode, string tenant = null)
        {
            var claims = new List<Claim> { new Claim(AuthConstants.JwtClaimTypes.Subject, itcode) };
            if (tenant != null)
            {
                claims.Add(new Claim(AuthConstants.JwtClaimTypes.TenantCode, tenant));
            }
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        /// <summary>A signed-in request whose user is not loaded yet (nothing cached for it).</summary>
        private static WTMContext CreateContext(ClaimsPrincipal user)
        {
            var wtm = MockWtmContext.CreateWtmContext(usercode: "template");
            wtm.LoginUserInfo = null;
            Mock.Get(wtm.HttpContext).Setup(x => x.User).Returns(user);
            return wtm;
        }

        /// <summary>A context that reloads through its own async method, counting the calls, so a test can tell the async path from the sync one.</summary>
        private class ProbeContext : WTMContext
        {
            public int AsyncReloads;
            public Func<string, Task<LoginUserInfo>> OnReload = itcode => Task.FromResult(new LoginUserInfo { ITCode = itcode, Name = "async" });

            public ProbeContext(WTMContext template)
                : base(null, new GlobalData(), new HttpContextAccessor { HttpContext = template.HttpContext }, new DefaultUIService(), null, template.DC, null, null, null, template.Cache)
            {
            }

            public override Task<LoginUserInfo> ReloadUserAsync(string itcode)
            {
                AsyncReloads++;
                return OnReload(itcode);
            }
        }

        /// <summary>A context that replaces the synchronous reload, as an application could.</summary>
        private class OverridingContext : WTMContext
        {
            public int SyncReloads;

            public OverridingContext(WTMContext template)
                : base(null, new GlobalData(), new HttpContextAccessor { HttpContext = template.HttpContext }, new DefaultUIService(), null, template.DC, null, null, null, template.Cache)
            {
            }

            public override LoginUserInfo ReloadUser(string itcode)
            {
                SyncReloads++;
                return new LoginUserInfo { ITCode = itcode, Name = "overridden" };
            }
        }

        private static ProbeContext CreateProbe(ClaimsPrincipal user)
        {
            return new ProbeContext(CreateContext(user));
        }

        // ReloadUserFunc is static; it counts the synchronous reloads of the tests that need to know there were none (or how many there were)
        private static async Task<T> WithReloadUserFunc<T>(Func<WTMContext, string, LoginUserInfo> func, Func<Task<T>> body)
        {
            var previous = WTMContext.ReloadUserFunc;
            try
            {
                WTMContext.ReloadUserFunc = func;
                return await body();
            }
            finally
            {
                WTMContext.ReloadUserFunc = previous;
            }
        }

        [TestMethod]
        public async Task CacheHit_ReturnsTheCachedUser_WithoutReloading()
        {
            var wtm = CreateProbe(SignedIn("cached"));
            var cached = new LoginUserInfo { ITCode = "cached" };
            wtm.Cache.Add(Key("cached"), cached);

            var user = await wtm.EnsureLoginUserInfoAsync();

            Assert.AreEqual("cached", user.ITCode);
            Assert.AreEqual(0, wtm.AsyncReloads);
            Assert.AreEqual("cached", wtm.LoginUserInfo.ITCode, "the property finds it ready");
        }

        [TestMethod]
        public async Task CacheMiss_ReloadsAsynchronously_AddsToTheCache_AndTheGetterFindsItReady()
        {
            var wtm = CreateProbe(SignedIn("fresh"));
            var syncReloads = 0;

            var user = await WithReloadUserFunc((c, i) => { syncReloads++; return null; }, async () =>
            {
                var rv = await wtm.EnsureLoginUserInfoAsync();
                Assert.AreEqual("fresh", wtm.LoginUserInfo.ITCode);
                return rv;
            });

            Assert.AreEqual("fresh", user.ITCode);
            Assert.AreEqual("async", user.Name);
            Assert.AreEqual(1, wtm.AsyncReloads);
            Assert.AreEqual(0, syncReloads, "the getter did not have to reload on the request thread");
            var cached = wtm.Cache.Get<LoginUserInfo>(Key("fresh"));
            Assert.IsNotNull(cached);
            Assert.AreEqual("fresh", cached.ITCode);
        }

        [TestMethod]
        public async Task CacheMiss_GivesTheSameResultAsTheGetter_AndFillsTheSameCacheEntry()
        {
            await WithReloadUserFunc((c, i) => new LoginUserInfo { ITCode = i, Name = "from-func", TenantCode = "t9" }, async () =>
            {
                var viaAsync = CreateContext(SignedIn("same", "t9"));
                var viaGetter = CreateContext(SignedIn("same", "t9"));

                var a = await viaAsync.EnsureLoginUserInfoAsync();
                var b = viaGetter.LoginUserInfo;

                Assert.AreEqual(b.ITCode, a.ITCode);
                Assert.AreEqual(b.Name, a.Name);
                Assert.AreEqual(b.TenantCode, a.TenantCode);
                Assert.IsNotNull(viaAsync.Cache.Get<LoginUserInfo>(Key("same", "t9")));
                Assert.IsNotNull(viaGetter.Cache.Get<LoginUserInfo>(Key("same", "t9")));
                return 0;
            });
        }

        [TestMethod]
        public async Task UnknownUser_GivesNull_WithoutExceptionOrCacheEntry_AndTheGetterStillFallsBack()
        {
            var wtm = CreateProbe(SignedIn("nobody"));
            wtm.OnReload = itcode => Task.FromResult<LoginUserInfo>(null);
            var syncReloads = 0;

            await WithReloadUserFunc((c, i) => { syncReloads++; return null; }, async () =>
            {
                Assert.IsNull(await wtm.EnsureLoginUserInfoAsync());
                Assert.IsNull(wtm.Cache.Get<LoginUserInfo>(Key("nobody")));
                Assert.AreEqual(0, syncReloads);
                Assert.IsNull(wtm.LoginUserInfo, "the property also gives null");
                Assert.AreEqual(1, syncReloads, "and it tries to reload it again on its own, as it always did");
                return 0;
            });
        }

        [TestMethod]
        public async Task AFailingReload_IsSwallowed_AsInTheGetter()
        {
            var wtm = CreateProbe(SignedIn("broken"));
            wtm.OnReload = itcode => throw new InvalidOperationException("boom");

            var user = await wtm.EnsureLoginUserInfoAsync();

            Assert.IsNull(user);
            Assert.IsNull(wtm.Cache.Get<LoginUserInfo>(Key("broken")));

            var faulted = CreateProbe(SignedIn("broken"));
            faulted.OnReload = itcode => Task.FromException<LoginUserInfo>(new InvalidOperationException("boom"));
            Assert.IsNull(await faulted.EnsureLoginUserInfoAsync(), "a faulted task is swallowed the same way");
        }

        [TestMethod]
        public async Task TheTenantClaim_DecidesTheCacheEntry()
        {
            var noTenant = CreateProbe(SignedIn("both"));
            var withTenant = CreateProbe(SignedIn("both", "t1"));
            foreach (var wtm in new[] { noTenant, withTenant })
            {
                wtm.Cache.Add(Key("both", "t1"), new LoginUserInfo { ITCode = "both", TenantCode = "t1" });
                wtm.Cache.Add(Key("both"), new LoginUserInfo { ITCode = "both", TenantCode = null });
            }

            Assert.IsNull((await noTenant.EnsureLoginUserInfoAsync()).TenantCode, "no tenant claim: the entry without tenant");
            Assert.AreEqual("t1", (await withTenant.EnsureLoginUserInfoAsync()).TenantCode, "tenant claim: the entry of that tenant");
            Assert.AreEqual(0, noTenant.AsyncReloads + withTenant.AsyncReloads);

            var miss = CreateProbe(SignedIn("other", "t2"));
            await miss.EnsureLoginUserInfoAsync();
            Assert.IsNotNull(miss.Cache.Get<LoginUserInfo>(Key("other", "t2")), "a loaded user is cached under the tenant of the claim");
            Assert.IsNull(miss.Cache.Get<LoginUserInfo>(Key("other")));
        }

        [TestMethod]
        public async Task WithoutHttpContext_NothingIsLoaded_AndTheGetterIsUnchanged()
        {
            var wtm = new WTMContext(null, new GlobalData(), null, new DefaultUIService(), null, null, null, null, null, MockWtmContext.CreateWtmContext().Cache);
            var syncReloads = 0;

            await WithReloadUserFunc((c, i) => { syncReloads++; return new LoginUserInfo { ITCode = i }; }, async () =>
            {
                Assert.IsNull(wtm.HttpContext);
                Assert.IsNull(await wtm.EnsureLoginUserInfoAsync());
                Assert.IsNull(wtm.LoginUserInfo);
                return 0;
            });

            Assert.AreEqual(0, syncReloads);
            // a background job that sets the user itself keeps working
            wtm.LoginUserInfo = new LoginUserInfo { ITCode = "job" };
            Assert.AreEqual("job", (await wtm.EnsureLoginUserInfoAsync()).ITCode);
            Assert.AreEqual("job", wtm.LoginUserInfo.ITCode);
        }

        [TestMethod]
        public async Task ARequestThatIsNotSignedIn_IsLeftToTheGetter()
        {
            var wtm = CreateProbe(new ClaimsPrincipal(new ClaimsIdentity()));

            Assert.IsNull(await wtm.EnsureLoginUserInfoAsync());
            Assert.AreEqual(0, wtm.AsyncReloads);
        }

        [TestMethod]
        public async Task AnAlreadyLoadedUser_IsReturnedAsIs()
        {
            var wtm = CreateProbe(SignedIn("loaded"));
            var loaded = new LoginUserInfo { ITCode = "loaded" };
            wtm.LoginUserInfo = loaded;

            Assert.AreSame(loaded, await wtm.EnsureLoginUserInfoAsync());
            Assert.AreEqual(0, wtm.AsyncReloads);
        }

        [TestMethod]
        public async Task TheReload_IsAwaited_NotBlocked()
        {
            var wtm = CreateProbe(SignedIn("slow"));
            var gate = new TaskCompletionSource<LoginUserInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            wtm.OnReload = itcode => gate.Task;

            var pending = wtm.EnsureLoginUserInfoAsync();

            Assert.IsFalse(pending.IsCompleted, "the caller got control back while the user is still being loaded");
            gate.SetResult(new LoginUserInfo { ITCode = "slow" });
            Assert.AreEqual("slow", (await pending).ITCode);
        }

        [TestMethod]
        public async Task ReloadUserFunc_StillDecides_InTheAsyncReload()
        {
            var wtm = CreateContext(SignedIn("viafunc"));

            var user = await WithReloadUserFunc((c, i) => new LoginUserInfo { ITCode = i, Name = "from-func" }, () => wtm.EnsureLoginUserInfoAsync());

            Assert.AreEqual("from-func", user.Name);
        }

        [TestMethod]
        public async Task AnOverriddenReloadUser_IsNotBypassedByTheAsyncReload()
        {
            var wtm = new OverridingContext(CreateContext(SignedIn("custom")));

            var user = await wtm.EnsureLoginUserInfoAsync();

            Assert.AreEqual("overridden", user.Name);
            Assert.AreEqual(1, wtm.SyncReloads);
            Assert.AreEqual("overridden", wtm.Cache.Get<LoginUserInfo>(Key("custom")).Name);
        }

        // ---- the filter ----

        private class RecordingPrivilegeFilter : PrivilegeFilter
        {
            public readonly List<string> Steps = new List<string>();
            public bool LoadedWhenExecutingStarted;
            public int AsyncReloadsWhenExecutingStarted;

            public override void OnActionExecuting(ActionExecutingContext context)
            {
                var probe = (ProbeContext)((IBaseController)context.Controller).Wtm;
                Steps.Add("executing");
                AsyncReloadsWhenExecutingStarted = probe.AsyncReloads;
                // the property finds the user ready: no reload on this thread
                LoadedWhenExecutingStarted = probe.LoginUserInfo?.ITCode == "filtered" && probe.AsyncReloads == 1;
            }

            public override void OnActionExecuted(ActionExecutedContext context)
            {
                Steps.Add("executed");
            }
        }

        private static ActionExecutingContext CreateActionContext(ProbeContext wtm, ClaimsPrincipal user)
        {
            var controller = new Mock<IBaseController>();
            controller.SetupProperty(x => x.Wtm, wtm);
            var http = new Mock<HttpContext>();
            http.Setup(x => x.User).Returns(user);
            var services = new Mock<IServiceProvider>();
            services.Setup(x => x.GetService(typeof(WTMContext))).Returns(wtm);
            http.Setup(x => x.RequestServices).Returns(services.Object);
            var actionContext = new ActionContext(http.Object, new RouteData(), new ActionDescriptor());
            return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object>(), controller.Object);
        }

        [TestMethod]
        public async Task ThePrivilegeFilter_LoadsTheUserAsynchronously_BeforeOnActionExecuting_AndKeepsTheOrder()
        {
            var wtm = CreateProbe(SignedIn("filtered"));
            var filter = new RecordingPrivilegeFilter();
            var context = CreateActionContext(wtm, SignedIn("filtered"));
            var nextCalled = false;

            await filter.OnActionExecutionAsync(context, () =>
            {
                nextCalled = true;
                filter.Steps.Add("action");
                return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), context.Controller));
            });

            Assert.AreEqual(1, wtm.AsyncReloads);
            Assert.IsTrue(filter.LoadedWhenExecutingStarted, "the user was already loaded when OnActionExecuting started");
            Assert.IsTrue(nextCalled);
            CollectionAssert.AreEqual(new[] { "executing", "action", "executed" }, filter.Steps);
        }

        [TestMethod]
        public async Task ThePrivilegeFilter_DoesNotPreloadAnonymousRequests_NorStopsTheAction()
        {
            var wtm = CreateProbe(new ClaimsPrincipal(new ClaimsIdentity()));
            var filter = new RecordingPrivilegeFilter();
            var context = CreateActionContext(wtm, new ClaimsPrincipal(new ClaimsIdentity()));

            await filter.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), context.Controller)));

            Assert.AreEqual(0, wtm.AsyncReloads);
            CollectionAssert.AreEqual(new[] { "executing", "executed" }, filter.Steps);
        }

        [TestMethod]
        public async Task ThePrivilegeFilter_LeavesACachedUserAlone()
        {
            var wtm = CreateProbe(SignedIn("filtered"));
            wtm.Cache.Add(Key("filtered"), new LoginUserInfo { ITCode = "filtered" });
            var filter = new RecordingPrivilegeFilter();
            var context = CreateActionContext(wtm, SignedIn("filtered"));

            await filter.OnActionExecutionAsync(context, () => Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), context.Controller)));

            Assert.AreEqual(0, wtm.AsyncReloads);
            Assert.AreEqual("filtered", wtm.LoginUserInfo.ITCode);
        }

        // ---- the session ----

        private class Payload
        {
            public string Name { get; set; }
            public int Count { get; set; }
        }

        [TestMethod]
        public async Task SetAsync_StoresTheSameValueAsSet_AndCommits()
        {
            ISession viaSet = new MockHttpSession();
            ISession viaSetAsync = new MockHttpSession();

            viaSet.Set("k", new Payload { Name = "a", Count = 2 });
            await viaSetAsync.SetAsync("k", new Payload { Name = "a", Count = 2 });

            Assert.AreEqual(viaSet.GetString("k"), viaSetAsync.GetString("k"));
            var back = viaSetAsync.Get<Payload>("k");
            Assert.AreEqual("a", back.Name);
            Assert.AreEqual(2, back.Count);
            Assert.IsNull(viaSetAsync.Get<Payload>("missing"));
        }

        [TestMethod]
        public async Task SetAsync_PassesTheCancellationTokenToTheCommit_AndSetKeepsItsOwnBehaviour()
        {
            using var cts = new CancellationTokenSource();
            var session = new Mock<ISession>();
            session.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            await session.Object.SetAsync("k", 1, cts.Token);
            session.Verify(x => x.Set("k", It.IsAny<byte[]>()), Times.Once);
            session.Verify(x => x.CommitAsync(cts.Token), Times.Once);

            session.Object.Set("k", 1);
            session.Verify(x => x.CommitAsync(default), Times.Once);

            var gate = new TaskCompletionSource();
            session.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>())).Returns(gate.Task);
            var pending = session.Object.SetAsync("k", 1);
            Assert.IsFalse(pending.IsCompleted, "the commit is awaited, not blocked on");
            gate.SetResult();
            await pending;
        }
    }
}
