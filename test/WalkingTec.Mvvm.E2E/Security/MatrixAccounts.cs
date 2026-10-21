using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WalkingTec.Mvvm.E2E.Infrastructure;
using WalkingTec.Mvvm.E2E.Pages.Vue3;

namespace WalkingTec.Mvvm.E2E.Security
{
    /// <summary>
    /// The identities of the permission matrix, per site and run:
    /// <list type="bullet">
    /// <item><c>anonymous</c>: no credentials;</item>
    /// <item><c>norole</c>: an account that exists and is valid but has no role or group;</item>
    /// <item><c>limited</c>: an account whose role has privileges on a few actions only (see the descriptions below);</item>
    /// <item><c>admin</c>: an account of the administrator role. It is a second administrator, not the one the browser tests use, so
    /// that the matrix requests never change the session of those tests.</item>
    /// </list>
    /// Passwords are generated for every run (<see cref="SiteProcess.MatrixNoRolePassword"/> and friends) and exist only in environment and memory.
    /// </summary>
    public static class MatrixAccounts
    {
        public const string NoRole = "e2enorole";
        public const string Limited = "e2elimited";
        public const string Admin = "e2ematrixadmin";

        /// <summary>Role code of the limited role (the framework accepts digits only).</summary>
        public const string LimitedRoleCode = "900";

        private static readonly ConcurrentDictionary<string, Lazy<Task<List<MatrixIdentity>>>> Cache = new();

        public static Task<List<MatrixIdentity>> LayUiAsync(SiteProcess site)
        {
            return Cache.GetOrAdd(site.Target.Name, _ => new Lazy<Task<List<MatrixIdentity>>>(() => OpenLayUiAsync(site))).Value;
        }

        public static Task<List<MatrixIdentity>> Vue3Async(SiteProcess site)
        {
            return Cache.GetOrAdd(site.Target.Name, _ => new Lazy<Task<List<MatrixIdentity>>>(() => OpenVue3Async(site))).Value;
        }

        // ---- LayUI test host: the accounts are seeded by the host (DataInit); a form login gives the session cookie --------------------------

        private static async Task<List<MatrixIdentity>> OpenLayUiAsync(SiteProcess site)
        {
            using var http = NewClient(site);
            return new List<MatrixIdentity>
            {
                MatrixIdentity.Anonymous(),
                MatrixIdentity.Cookie("norole", await FormLoginAsync(http, "/Login/Login", "ITCode", NoRole, site.MatrixNoRolePassword)),
                MatrixIdentity.Cookie("limited", await FormLoginAsync(http, "/Login/Login", "ITCode", Limited, site.MatrixLimitedPassword)),
                MatrixIdentity.Cookie("admin", await FormLoginAsync(http, "/Login/Login", "ITCode", Admin, site.MatrixAdminPassword)),
            };
        }

        // ---- Vue3Demo: the accounts are created through the API of the admin pages, the way an operator would -----------------------------

        private static async Task<List<MatrixIdentity>> OpenVue3Async(SiteProcess site)
        {
            var playwright = await E2ERun.GetPlaywrightAsync();
            await using (var api = await Vue3Api.CreateAsync(playwright, site))
            {
                await Vue3Seed.EnsureAsync(api);
                await CreateLimitedRoleAsync(api, site);
                await CreateUserAsync(api, NoRole, "E2E No Role", site.MatrixNoRolePassword, Array.Empty<string>());
                await CreateUserAsync(api, Limited, "E2E Limited", site.MatrixLimitedPassword, new[] { LimitedRoleCode });
                await CreateUserAsync(api, Admin, "E2E Matrix Admin", site.MatrixAdminPassword, new[] { "001" });
            }
            using var http = NewClient(site);
            return new List<MatrixIdentity>
            {
                MatrixIdentity.Anonymous(),
                MatrixIdentity.Bearer("norole", await TokenLoginAsync(http, NoRole, site.MatrixNoRolePassword)),
                MatrixIdentity.Bearer("limited", await TokenLoginAsync(http, Limited, site.MatrixLimitedPassword)),
                MatrixIdentity.Bearer("admin", await TokenLoginAsync(http, Admin, site.MatrixAdminPassword)),
                MatrixIdentity.Cookie("admin-cookie", await FormLoginAsync(http, "/api/_account/login", "account", Admin, site.MatrixAdminPassword, "password")),
            };
        }

        /// <summary>Pages (menu URL) and actions (action URL) the limited role may use in the Vue3Demo: read access to cities, schools and roles.</summary>
        public static readonly string[] LimitedRoleUrls =
        {
            "/schooldata/city", "/api/City/Search", "/api/City/{id}",
            "/_admin/school", "/api/School/Search",
            "/_admin/frameworkrole", "/api/_FrameworkRole/Search", "/api/_FrameworkRole/{id}",
        };

        private static async Task CreateLimitedRoleAsync(Vue3Api api, SiteProcess site)
        {
            await api.PostAsync("/api/_FrameworkRole/Add", new { Entity = new { RoleCode = LimitedRoleCode, RoleName = "E2E Limited" } });
            var roles = await api.PostAsync("/api/_FrameworkRole/Search", new { RoleCode = LimitedRoleCode });
            var roleId = roles.GetProperty("Data")[0].GetProperty("ID").GetString();
            var page = await api.GetAsync("/api/_FrameworkRole/GetPageActions/" + roleId);
            var allowed = MenuIdsOf(site, LimitedRoleUrls);
            var pages = page.GetProperty("Pages").EnumerateArray().Select(p => new Dictionary<string, object>
            {
                ["ID"] = p.GetProperty("ID").GetString(),
                ["Name"] = p.GetProperty("Name").GetString(),
                ["Actions"] = p.GetProperty("AllActions").EnumerateArray().Select(a => a.GetProperty("Value").GetString()).Where(v => allowed.Contains(v)).ToArray()
            }).ToArray();
            var entity = page.GetProperty("Entity");
            await api.PutAsync("/api/_FrameworkRole/EditPrivilege", new
            {
                Entity = new { ID = roleId, RoleCode = entity.GetProperty("RoleCode").GetString(), RoleName = entity.GetProperty("RoleName").GetString() },
                Pages = pages
            });
            if (allowed.Count != LimitedRoleUrls.Length)
            {
                throw new InvalidOperationException($"Expected {LimitedRoleUrls.Length} menu entries for the limited role but found {allowed.Count}; the menus of the demo may have changed.");
            }
        }

        /// <summary>Ids (lower case) of the menu rows with the given URLs; read from the database file of this run, whose menu rows the pages above use.</summary>
        private static HashSet<string> MenuIdsOf(SiteProcess site, IEnumerable<string> urls)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = site.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            foreach (var url in urls)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT ID FROM FrameworkMenus WHERE lower(Url) = lower($url)";
                command.Parameters.AddWithValue("$url", url);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(reader.GetString(0).ToLowerInvariant());
                }
            }
            return result;
        }

        private static Task CreateUserAsync(Vue3Api api, string account, string name, string password, string[] roleCodes)
        {
            return api.PostAsync("/api/_FrameworkUser/Add", new
            {
                Entity = new { ITCode = account, Password = password, Name = name },
                SelectedRolesCodes = roleCodes,
                SelectedGroupCodes = Array.Empty<string>()
            });
        }

        // ---- HTTP logins ----------------------------------------------------------------------------------------------------------------

        private static HttpClient NewClient(SiteProcess site)
        {
            return new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = site.BaseUri, Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <summary>Posts a login form and returns the cookies the site set, as a Cookie header value.</summary>
        private static async Task<string> FormLoginAsync(HttpClient http, string path, string accountField, string account, string password, string passwordField = "Password")
        {
            using var response = await http.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { [accountField] = account, [passwordField] = password }));
            var cookies = response.Headers.TryGetValues("Set-Cookie", out var values)
                ? values.Select(x => x.Split(';')[0]).Where(x => x.Contains('=') && x.EndsWith("=") == false).ToList()
                : new List<string>();
            if (cookies.Count == 0)
            {
                throw new InvalidOperationException($"The login of '{account}' did not return a session cookie (HTTP {(int)response.StatusCode}).");
            }
            return string.Join("; ", cookies);
        }

        private static async Task<string> TokenLoginAsync(HttpClient http, string account, string password)
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { Account = account, Password = password }), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("/api/_account/loginjwt", content);
            var text = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode == false)
            {
                throw new InvalidOperationException($"The token login of '{account}' failed (HTTP {(int)response.StatusCode}).");
            }
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.GetProperty("access_token").GetString();
        }
    }
}
