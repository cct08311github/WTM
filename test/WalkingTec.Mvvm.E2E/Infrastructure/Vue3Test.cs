using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Pages.Vue3;

namespace WalkingTec.Mvvm.E2E.Infrastructure
{
    /// <summary>
    /// Base class of the Vue3Demo browser tests: runs against <see cref="SiteTargets.Vue3Demo"/> and offers the signed in
    /// application and an authenticated API client for test data set up (the API calls are part of the route coverage, too).
    /// Each test creates the rows it needs under a unique name and removes what it created.
    /// </summary>
    public abstract class Vue3Test : E2ETest
    {
        private Vue3App _app;
        private Vue3Api _api;

        protected override SiteTarget Target => SiteTargets.Vue3Demo;

        protected Vue3App App => _app ??= new Vue3App(Page);

        protected static string UniqueName(string prefix) => prefix + Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>The ids of the rows of a list (search) answer.</summary>
        protected static string[] Ids(JsonElement list, string property = "ID") =>
            list.GetProperty("Data").EnumerateArray().Select(x => x.GetProperty(property).GetString()).ToArray();

        /// <summary>Random digits for fields that only accept digits (group code).</summary>
        protected static string UniqueDigits(int length) => string.Concat(Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10))));

        /// <summary>Signs in through the login page; the first call of a run also creates the shared menus (<see cref="Vue3Seed"/>).</summary>
        protected async Task SignInAsync()
        {
            await Vue3Seed.EnsureAsync(await ApiAsync());
            await App.LoginAsync(Site.AdminAccount, Site.AdminPassword);
        }

        /// <summary>API client signed in as the administrator (JWT), created on first use.</summary>
        protected async Task<Vue3Api> ApiAsync()
        {
            return _api ??= await Vue3Api.CreateAsync(Playwright, Site);
        }

        [TestCleanup]
        public async Task DisposeApi()
        {
            if (_api != null)
            {
                await _api.DisposeAsync();
            }
        }
    }
}
