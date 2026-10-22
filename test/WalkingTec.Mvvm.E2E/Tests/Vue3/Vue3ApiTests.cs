using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.E2E.Infrastructure;
using WalkingTec.Mvvm.E2E.Pages.Vue3;
using WalkingTec.Mvvm.E2E.Support;

namespace WalkingTec.Mvvm.E2E.Tests.Vue3
{
    /// <summary>
    /// The JSON API of the Vue3Demo without the browser: actions that have no screen (or whose screen does not reach them), such as
    /// the cookie login, token refresh, selector endpoints, the file API and the Student and Major modules. The SPA is a thin client
    /// of exactly this API, so these tests complete the route coverage; each one checks what the call changed or returned.
    /// </summary>
    [TestClass]
    [TestCategory("vue3demo")]
    public class Vue3ApiTests : Vue3Test
    {
        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        private async Task<Vue3Api> StartAsync()
        {
            var api = await ApiAsync();
            await Vue3Seed.EnsureAsync(api);
            return api;
        }

        /// <summary>Calls an export action and checks that the answer is a workbook; returns its sheet XML.</summary>
        private static async Task<string> ExportAsync(Vue3Api api, string url, object body)
        {
            var res = await api.PostRawAsync(url, body);
            Assert.AreEqual(200, res.Status, url);
            var bytes = await res.BodyAsync();
            Assert.AreEqual("PK", Encoding.ASCII.GetString(bytes, 0, 2), url + " returns a zip container (xlsx)");
            return ExcelTemplate.SheetXml(bytes);
        }

        private static async Task<byte[]> TemplateAsync(Vue3Api api, string url)
        {
            var res = await api.GetRawAsync(url);
            Assert.AreEqual(200, res.Status, url);
            return await res.BodyAsync();
        }

        /// <summary>Uploads a workbook built from the template of the module and runs its import action; returns the answer.</summary>
        private async Task<JsonElement> ImportAsync(Vue3Api api, string module, params string[][] rows)
        {
            var template = await TemplateAsync(api, $"/api/{module}/GetExcelTemplate");
            var fileId = await api.UploadAsync("import.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ExcelTemplate.WithRows(template, rows));
            return await api.PostAsync($"/api/{module}/Import", new { UploadFileId = fileId });
        }

        // ---- account ----

        [TestMethod]
        public async Task Account_SignedInCallers_StillNeedTheirPasswordToGetANewSignIn()
        {
            var api = await StartAsync();

            // signed in with a token: the credentials of the request are verified, no new token for a wrong password
            var wrongWithToken = await api.Context.PostAsync("/api/_account/loginjwt", new() { DataObject = new { Account = Site.AdminAccount, Password = "not-the-password" } });
            Assert.AreEqual(400, wrongWithToken.Status);
            var emptyWithToken = await api.Context.PostAsync("/api/_account/loginjwt", new() { DataObject = new { Account = Site.AdminAccount, Password = "" } });
            Assert.AreEqual(400, emptyWithToken.Status);
            var rightWithToken = await api.Context.PostAsync("/api/_account/loginjwt", new() { DataObject = new { Account = Site.AdminAccount, Password = Site.AdminPassword } });
            Assert.AreEqual(200, rightWithToken.Status);

            // signed in with the cookie
            await using var session = await Playwright.APIRequest.NewContextAsync(new() { BaseURL = Site.BaseUri.ToString() });
            var cookie = await session.PostAsync("/api/_account/login", new() { Form = session.CreateFormData().Set("account", Site.AdminAccount).Set("password", Site.AdminPassword) });
            Assert.AreEqual(200, cookie.Status);
            var wrongWithCookie = await session.PostAsync("/api/_account/login", new() { Form = session.CreateFormData().Set("account", Site.AdminAccount).Set("password", "not-the-password") });
            Assert.AreEqual(400, wrongWithCookie.Status);
            var wrongJwtWithCookie = await session.PostAsync("/api/_account/loginjwt", new() { DataObject = new { Account = Site.AdminAccount, Password = "not-the-password" } });
            Assert.AreEqual(400, wrongJwtWithCookie.Status);
        }

        [TestMethod]
        public async Task Account_TokenCookieAndSelectorEndpoints()
        {
            var api = await StartAsync();

            // the cookie login (used by server rendered front ends) and the token login answer with the user info
            var anonymous = await Playwright.APIRequest.NewContextAsync(new() { BaseURL = Site.BaseUri.ToString() });
            var cookie = await anonymous.PostAsync("/api/_account/login", new() { Form = anonymous.CreateFormData().Set("account", Site.AdminAccount).Set("password", Site.AdminPassword) });
            Assert.AreEqual(200, cookie.Status);
            StringAssert.Contains(await cookie.TextAsync(), "\"ITCode\":\"" + Site.AdminAccount + "\"");
            var stranger = await Playwright.APIRequest.NewContextAsync(new() { BaseURL = Site.BaseUri.ToString() });
            var wrong = await stranger.PostAsync("/api/_account/login", new() { Form = stranger.CreateFormData().Set("account", Site.AdminAccount).Set("password", "wrong") });
            Assert.AreEqual(400, wrong.Status);
            await stranger.DisposeAsync();
            // the session of the cookie login is what the next calls use
            var info = await anonymous.GetAsync("/api/_account/checkuserinfo");
            Assert.AreEqual(200, info.Status);
            var remote = await anonymous.GetAsync("/api/_account/LoginRemote");
            Assert.AreEqual(200, remote.Status);
            var logout = await anonymous.GetAsync("/api/_account/Logout");
            Assert.AreEqual(200, logout.Status);
            Assert.AreEqual("/", await logout.TextAsync());
            await anonymous.DisposeAsync();

            // refresh token
            var tokenContext = await Playwright.APIRequest.NewContextAsync(new() { BaseURL = Site.BaseUri.ToString() });
            var login = await tokenContext.PostAsync("/api/_account/loginjwt", new() { DataObject = new { Account = Site.AdminAccount, Password = Site.AdminPassword } });
            using var token = JsonDocument.Parse(await login.TextAsync());
            var refreshed = await api.Context.PostAsync("/api/_account/RefreshToken?refreshToken=" + Uri.EscapeDataString(token.RootElement.GetProperty("refresh_token").GetString()));
            Assert.AreEqual(200, refreshed.Status, await refreshed.TextAsync());
            await tokenContext.DisposeAsync();

            // selectors the pages use for their drop downs
            Assert.IsTrue((await api.GetAsync("/api/_account/GetFrameworkRoles")).EnumerateArray().Any(x => x.GetProperty("Value").GetString() == "001"));
            await api.GetAsync("/api/_account/GetFrameworkGroups");
            await api.GetAsync("/api/_account/GetFrameworkGroupsTree");
            Assert.IsTrue((await api.GetAsync("/api/_account/GetUserById?keywords=" + Site.AdminAccount.Substring(0, 2))).EnumerateArray().Any());
            Assert.IsTrue((await api.GetAsync("/api/_account/GetUserByRole?keywords=001")).EnumerateArray().Any(x => x.GetString() == Site.AdminAccount));
            await api.GetAsync("/api/_account/GetUserByGroup?keywords=none");
            Assert.AreEqual("true", (await api.GetAsync("/api/_account/SetTenant?tenant=")).ToString().ToLowerInvariant());
        }

        [TestMethod]
        public async Task Account_Registration_CreatesAUserAndRefusesDuplicates()
        {
            var api = await StartAsync();
            var account = UniqueName("reg").ToLowerInvariant();
            var anonymous = await Playwright.APIRequest.NewContextAsync(new() { BaseURL = Site.BaseUri.ToString() });
            var body = new { ITCode = account, Name = "Registered", Password = "Pw-" + UniqueName("") };

            var created = await anonymous.PostAsync("/api/_account/Reg", new() { DataObject = body });
            var duplicate = await anonymous.PostAsync("/api/_account/Reg", new() { DataObject = body });
            var incomplete = await anonymous.PostAsync("/api/_account/Reg", new() { DataObject = new { ITCode = UniqueName("x") } });
            await anonymous.DisposeAsync();

            Assert.AreEqual(200, created.Status);
            Assert.AreEqual(400, duplicate.Status, "an account that exists is refused");
            Assert.AreEqual(400, incomplete.Status, "name and password are required");
            var found = await api.PostAsync("/api/_FrameworkUser/Search", new { ITCode = account });
            Assert.AreEqual(1, found.GetProperty("Data").GetArrayLength());
            await api.PostAsync("/api/_FrameworkUser/BatchDelete", Ids(found));
        }

        // ---- user management ----

        // the user API accepts a password but never returns one (#287)
        private static void AssertNoPassword(System.Text.Json.JsonElement response, string action)
        {
            var text = response.GetRawText();
            Assert.IsFalse(text.Contains("\"Password\""), $"{action} response must not contain the password field: {text}");
        }

        [TestMethod]
        public async Task User_Api_CreateReadEditPasswordSelectorsExportImportDelete()
        {
            var api = await StartAsync();
            var groupCode = UniqueDigits(8);
            await api.PostAsync("/api/_FrameworkGroup/Add", new { Entity = new { GroupCode = groupCode, GroupName = UniqueName("Team ") } });
            var account = UniqueName("api").ToLowerInvariant();
            var photoId = await api.UploadAsync("photo.png", "image/png", Png);

            var added = await api.PostAsync("/api/_FrameworkUser/Add", new
            {
                Entity = new { ITCode = account, Password = "Pw-" + UniqueName(""), Name = "Api User", PhotoId = photoId },
                SelectedRolesCodes = new[] { "002" },
                SelectedGroupCodes = new[] { groupCode }
            });
            AssertNoPassword(added, "Add");
            var id = Ids(await api.PostAsync("/api/_FrameworkUser/Search", new { ITCode = account })).Single();

            var read = await api.GetAsync("/api/_FrameworkUser/" + id);
            Assert.AreEqual("Api User", read.GetProperty("Entity").GetProperty("Name").GetString());
            AssertNoPassword(read, "Get");
            CollectionAssert.AreEqual(new[] { "002" }, read.GetProperty("SelectedRolesCodes").EnumerateArray().Select(x => x.GetString()).ToArray());
            var edited = await api.PutAsync("/api/_FrameworkUser/Edit", new
            {
                Entity = new { ID = id, ITCode = account, Name = "Api User 2", PhotoId = photoId },
                SelectedRolesCodes = new[] { "002" },
                SelectedGroupCodes = new string[0]
            });
            AssertNoPassword(edited, "Edit");
            Assert.AreEqual("Api User 2", (await api.GetAsync("/api/_FrameworkUser/" + id)).GetProperty("Entity").GetProperty("Name").GetString());

            // the password action replaces the password: the new one signs in
            var newPassword = "New-" + UniqueName("");
            var changed = await api.PutAsync("/api/_FrameworkUser/Password", new { Entity = new { ID = id, ITCode = account, Password = newPassword } });
            AssertNoPassword(changed, "Password");
            var anonymous = await Playwright.APIRequest.NewContextAsync(new() { BaseURL = Site.BaseUri.ToString() });
            Assert.IsFalse(string.IsNullOrEmpty(await Vue3Api.LoginAsync(anonymous, account, newPassword)));
            await anonymous.DisposeAsync();

            // selectors of the user module
            await api.GetAsync("/api/_FrameworkUser/GetFrameworkRoles");
            await api.GetAsync("/api/_FrameworkUser/GetFrameworkGroups");
            await api.GetAsync("/api/_FrameworkUser/GetFrameworkGroupsTree");
            Assert.IsTrue((await api.GetAsync("/api/_FrameworkUser/GetUserById?keywords=" + account.Substring(0, 4))).EnumerateArray().Any());
            Assert.IsTrue((await api.GetAsync("/api/_FrameworkUser/GetUserByRole?keywords=002")).EnumerateArray().Any(x => x.GetString() == account));
            await api.GetAsync("/api/_FrameworkUser/GetUserByGroup?keywords=" + groupCode);

            // export (all / selected) and import from the template
            StringAssert.Contains(await ExportAsync(api, "/api/_FrameworkUser/ExportExcel", new { }), account);
            StringAssert.Contains(await ExportAsync(api, "/api/_FrameworkUser/ExportExcelByIds", new[] { id }), account);
            var imported = UniqueName("imp").ToLowerInvariant();
            var answer = await ImportAsync(api, "_FrameworkUser", new[] { imported, "Pw-" + UniqueName(""), "Imported User" });
            Assert.AreEqual("1", answer.ToString());

            await api.PostAsync("/api/_FrameworkUser/BatchDelete", new[] { id }.Concat(Ids(await api.PostAsync("/api/_FrameworkUser/Search", new { ITCode = imported }))).ToArray());
            Assert.AreEqual(0, (await api.PostAsync("/api/_FrameworkUser/Search", new { ITCode = account })).GetProperty("Data").GetArrayLength());
            var group = await api.PostAsync("/api/_FrameworkGroup/Search", new { GroupCode = groupCode });
            await api.PostAsync("/api/_FrameworkGroup/BatchDelete", Ids(group));
        }

        // ---- modules of the _Admin area: exports and selectors ----

        [TestMethod]
        public async Task Role_Group_Menu_Tenant_ExportsAndSelectors()
        {
            var api = await StartAsync();
            var roleCode = UniqueDigits(8);
            var groupCode = UniqueDigits(8);
            var tenantCode = UniqueDigits(4);
            await api.PostAsync("/api/_FrameworkRole/Add", new { Entity = new { RoleCode = roleCode, RoleName = UniqueName("Role ") } });
            await api.PostAsync("/api/_FrameworkGroup/Add", new { Entity = new { GroupCode = groupCode, GroupName = UniqueName("Group ") } });
            await api.PostAsync("/api/_FrameworkTenant/Add", new { Entity = new { TCode = tenantCode, TName = UniqueName("Tenant "), Enabled = true, EnableSub = false }, AdminRoleCode = "001" });
            var roleId = Ids(await api.PostAsync("/api/_FrameworkRole/Search", new { RoleCode = roleCode })).Single();
            var groupId = Ids(await api.PostAsync("/api/_FrameworkGroup/Search", new { GroupCode = groupCode })).Single();
            var tenantId = Ids(await api.PostAsync("/api/_FrameworkTenant/Search", new { TCode = tenantCode })).Single();

            StringAssert.Contains(await ExportAsync(api, "/api/_FrameworkRole/ExportExcel", new { }), roleCode);
            StringAssert.Contains(await ExportAsync(api, "/api/_FrameworkRole/ExportExcelByIds", new[] { roleId }), roleCode);
            StringAssert.Contains(await ExportAsync(api, "/api/_FrameworkGroup/ExportExcel", new { }), groupCode);
            StringAssert.Contains(await ExportAsync(api, "/api/_FrameworkGroup/ExportExcelByIds", new[] { groupId }), groupCode);
            StringAssert.Contains(await ExportAsync(api, "/api/_FrameworkTenant/ExportExcel", new { }), tenantCode);
            StringAssert.Contains(await ExportAsync(api, "/api/_FrameworkTenant/ExportExcelByIds", new[] { tenantId }), tenantCode);
            await ExportAsync(api, "/api/_FrameworkMenu/ExportExcel", new { });
            var menuId = Ids(await api.PostAsync("/api/_FrameworkMenu/Search", new { })).First();
            await ExportAsync(api, "/api/_FrameworkMenu/ExportExcelByIds", new[] { menuId });

            // tenant template and import: code, name, database, database type, context, domain, enable sub, enabled
            var tenantImport = UniqueDigits(4);
            Assert.AreEqual("1", (await ImportAsync(api, "_FrameworkTenant", new[] { tenantImport, UniqueName("Imported tenant "), "", "", "", "", "No", "Yes" })).ToString());
            Assert.IsTrue((await api.GetAsync("/api/_FrameworkTenant/GetFrameworkTenants")).EnumerateArray().Any(x => x.GetProperty("Value").GetString() == tenantCode));

            // selectors of group and menu pages
            await api.GetAsync("/api/_FrameworkGroup/GetParents");
            await api.GetAsync("/api/_FrameworkGroup/GetParentsTree");
            var unset = await api.GetAsync("/api/_FrameworkMenu/UnsetPages");
            Assert.AreEqual(200, unset.GetProperty("Code").GetInt32());

            await api.PostAsync("/api/_FrameworkRole/BatchDelete", new[] { roleId });
            await api.PostAsync("/api/_FrameworkGroup/BatchDelete", new[] { groupId });
            await api.PostAsync("/api/_FrameworkTenant/BatchDelete", new[] { tenantId }.Concat(Ids(await api.PostAsync("/api/_FrameworkTenant/Search", new { TCode = tenantImport }))).ToArray());
        }

        [TestMethod]
        public async Task ActionLog_ExportAndBatchDelete()
        {
            var api = await StartAsync();
            var marker = UniqueName("marker").ToLowerInvariant();
            // a request that is logged with a recognizable url
            await api.PostAsync("/api/_FrameworkGroup/Search", new { GroupCode = marker });
            var logs = await api.PostAsync("/api/_ActionLog/Search", new { ITCode = Site.AdminAccount });
            var ids = Ids(logs).Take(2).ToArray();
            Assert.AreEqual(2, ids.Length);

            await ExportAsync(api, "/api/_ActionLog/ExportExcel", new { });
            await ExportAsync(api, "/api/_ActionLog/ExportExcelByIds", ids);
            var one = await api.GetAsync("/api/_ActionLog/" + ids[0]);
            Assert.AreEqual(ids[0], one.GetProperty("Entity").GetProperty("ID").GetString());
            await api.PostAsync("/api/_ActionLog/BatchDelete", ids);

            var remaining = Ids(await api.PostAsync("/api/_ActionLog/Search", new { ITCode = Site.AdminAccount }));
            Assert.IsFalse(remaining.Intersect(ids).Any(), "the deleted logs are gone");
        }

        [TestMethod]
        public async Task DataPrivilege_Api_UserAndGroupSelectors()
        {
            var api = await StartAsync();

            await api.GetAsync("/api/_DataPrivilege/GetUserGroups");
            await api.GetAsync("/api/_DataPrivilege/GetUserGroupsTree");
            Assert.IsTrue((await api.GetAsync("/api/_DataPrivilege/GetPrivileges")).EnumerateArray().Any(x => x.GetProperty("Value").GetString() == "City"));
            Assert.IsTrue((await api.GetAsync("/api/_DataPrivilege/GetPrivilegeByTableName?table=City")).ValueKind == JsonValueKind.Array);
        }

        // ---- file API: see Vue3FileApiTests ----

        // ---- sample modules without a screen: Major and Student, plus the generated pages' exports ----

        [TestMethod]
        public async Task SchoolMajorStudent_Api_CreateRelateSearchExportDelete()
        {
            var api = await StartAsync();
            var schoolCode = UniqueDigits(3);
            var schoolName = UniqueName("School ");
            await api.PostAsync("/api/School/Add", new { Entity = new { SchoolCode = schoolCode, SchoolName = schoolName, Level = 1 } });
            var schoolId = Ids(await api.PostAsync("/api/School/Search", new { })).Last();
            var city = UniqueName("City ");
            await api.PostAsync("/api/City/Add", new { Entity = new { Name = city, Level = 1 } });
            var cityId = Ids(await api.PostAsync("/api/City/Search", new { Name = city })).Single();

            // a major belongs to a school, a student takes majors
            var majorCode = UniqueDigits(3);
            var majorName = UniqueName("Major ");
            await api.PostAsync("/api/Major/Add", new { Entity = new { MajorCode = majorCode, MajorName = majorName, MajorType = 0, SchoolId = int.Parse(schoolId), CityId = cityId } });
            var majorId = Ids(await api.PostAsync("/api/Major/Search", new { MajorName = majorName })).Single();
            var studentLogin = UniqueName("stu").ToLowerInvariant();
            await api.PostAsync("/api/Student/Add", new
            {
                Entity = new
                {
                    ID = studentLogin,
                    Password = "Pw-" + UniqueName(""),
                    Name = "Student " + studentLogin,
                    EnRollDate = "2024-09-01T00:00:00",
                    IsValid = true,
                    StudentMajor = new[] { new { MajorId = majorId } }
                }
            });
            Assert.AreEqual(1, (await api.PostAsync("/api/Student/Search", new { ID = studentLogin })).GetProperty("Data").GetArrayLength());
            var student = await api.GetAsync("/api/Student/" + studentLogin);
            Assert.AreEqual("Student " + studentLogin, student.GetProperty("Entity").GetProperty("Name").GetString());
            await api.PutAsync("/api/Student/Edit", new { Entity = new { ID = studentLogin, Password = "Pw-" + UniqueName(""), Name = "Student edited", EnRollDate = "2024-09-01T00:00:00", IsValid = true, StudentMajor = new[] { new { MajorId = majorId } } } });
            Assert.AreEqual("Student edited", (await api.GetAsync("/api/Student/" + studentLogin)).GetProperty("Entity").GetProperty("Name").GetString());
            var major = await api.GetAsync("/api/Major/" + majorId);
            Assert.AreEqual(majorName, major.GetProperty("Entity").GetProperty("MajorName").GetString());
            await api.PutAsync("/api/Major/Edit", new { Entity = new { ID = majorId, MajorCode = majorCode, MajorName = majorName + " 2", MajorType = 1, SchoolId = int.Parse(schoolId) } });

            // selectors
            await api.GetAsync("/api/Major/GetSchools");
            await api.GetAsync("/api/Major/GetStudents");
            await api.GetAsync("/api/Student/GetMajors");
            await api.GetAsync("/api/Student/GetCitys");
            await api.GetAsync("/api/Student/GetSchoolsByCity/" + cityId);
            await api.GetAsync("/api/Student/GetMajorsBySchool/" + schoolId);
            await api.GetAsync("/api/School/GetCitys");

            // exports, templates and imports of both modules
            StringAssert.Contains(await ExportAsync(api, "/api/Major/ExportExcel", new { }), majorCode);
            await ExportAsync(api, "/api/Major/ExportExcelByIds", new[] { majorId });
            StringAssert.Contains(await ExportAsync(api, "/api/Student/ExportExcel", new { }), studentLogin);
            await ExportAsync(api, "/api/Student/ExportExcelByIds", new[] { studentLogin });
            await TemplateAsync(api, "/api/Major/GetExcelTemplate");
            await TemplateAsync(api, "/api/Student/GetExcelTemplate");
            await ExportAsync(api, "/api/School/ExportExcelByIds", new[] { schoolId });
            await ExportAsync(api, "/api/City/ExportExcel", new { });
            await ExportAsync(api, "/api/City/ExportExcelByIds", new[] { cityId });
            await ExportAsync(api, "/api/SchoolVue3/ExportExcel", new { });
            await ExportAsync(api, "/api/SchoolVue3/ExportExcelByIds", new string[0]);

            // import: major (code, name, type as named by the enum, remark, school by name) and student (account, password, email, name, ..., valid, date)
            var importedMajor = UniqueName("Imported major ");
            Assert.AreEqual("1", (await ImportAsync(api, "Major", new[] { UniqueDigits(3), importedMajor, "必修", "", schoolName })).ToString());
            var importedStudent = UniqueName("impstu").ToLowerInvariant();
            Assert.AreEqual("1", (await ImportAsync(api, "Student", new[] { importedStudent, "Pw-" + UniqueName(""), "", "Imported", "", "", "", "", "Yes", "2024-09-01" })).ToString());
            await api.PostAsync("/api/Student/BatchDelete", new[] { importedStudent });
            await api.PostAsync("/api/Major/BatchDelete", Ids(await api.PostAsync("/api/Major/Search", new { MajorName = importedMajor })));

            // batch edit of schools and the chart data of the demo page
            await api.PostAsync("/api/School/BatchEdit", new { Ids = new[] { schoolId }, LinkedVM = new { Remark = "batch edited" } });
            Assert.IsTrue((await api.PostAsync("/api/School/GetCharts", new { })).ValueKind != JsonValueKind.Undefined);

            await api.PostAsync("/api/Student/BatchDelete", new[] { studentLogin });
            await api.PostAsync("/api/Major/BatchDelete", new[] { majorId });
            await api.PostAsync("/api/City/BatchDelete", new[] { cityId });
            await api.PostAsync("/api/School/BatchDelete", new[] { schoolId });
            Assert.AreEqual(0, (await api.PostAsync("/api/Student/Search", new { ID = studentLogin })).GetProperty("Data").GetArrayLength());
        }
    }
}
