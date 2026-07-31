#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Demo.Models;

namespace WalkingTec.Mvvm.Api.Test;

/// <summary>
/// Issue #947 — real end-to-end HTTP proof that <c>_FrameworkController.Selector</c>'s
/// <c>Ids</c> branch keeps row-level DataPrivilege authorization when it restricts a ListVM's
/// result set to a caller-supplied <c>Ids</c> list, instead of stripping every <c>Where</c> node
/// — DataPrivilege included — the way <c>SearcherMode.Batch</c> + <c>ReplaceWhere</c> used to:
/// that path ran the query through <c>WhereReplaceModifier</c>, which deletes every
/// <c>Where</c> in <c>GetSearchQuery()</c> before rebuilding with only the <c>Ids</c>
/// restriction. #867 fixed the same underlying mechanism one call site over, in
/// <c>GetPagingData</c> — see <c>RequestBindingScopeHttpTests867</c> in this same directory.
///
/// <para>
/// <b>#953 review (adversarial review of the original #947 fix) simplification:</b> the
/// original fix routed <c>Selector</c> through a new <c>PopulateSelectedEntities</c> method
/// that bypassed <c>DoSearch()</c> entirely — which silently skipped
/// <c>GetSearchCommand()</c>-backed ListVMs (raw SQL/stored-procedure sources) and
/// <c>Searcher.SortInfo</c>, and required a new public interface member
/// (<c>IBasePagedListVM.PopulateSelectedEntities</c>, a breaking change for external
/// implementers). The review's key finding: with <c>BasePagedListVM.GetBatchQuery()</c>'s
/// default (no explicit <c>ReplaceWhere</c>) branch already fixed to call the new
/// <c>GetAuthorizedIdsQuery</c>, <c>Selector</c> does not need to call anything special at
/// all — <c>SearcherMode</c> is already <c>Batch</c> and <c>ReplaceWhere</c> is simply never
/// set, so the normal <c>GetDataJson()</c> → <c>DoSearch()</c> → <c>GetBatchQuery()</c>
/// pipeline reaches the same fixed code, with <c>GetSearchCommand()</c> and
/// <c>SortInfo</c> honoured and no new public API. Adopted; <c>PopulateSelectedEntities</c>
/// no longer exists.
/// </para>
///
/// <para>
/// <b>Why <c>MajorListVM</c>/<c>School</c>:</b> the demo app's own
/// <c>Startup.DataPrivilegeSettings()</c> ships with the <c>School</c>/<c>Major</c>/<c>City</c>
/// registrations commented out, so <c>MajorListVM.GetSearchQuery()</c>'s existing
/// <c>.DPWhere(Wtm, x =&gt; x.SchoolId)</c> call is a no-op under the plain
/// <see cref="DemoWebApplicationFactory"/>. This class's own isolated factory
/// (<see cref="_dpFactory"/>) registers <c>DataPrivilegeInfo&lt;School&gt;</c> via
/// <c>ConfigureServices</c> so the restriction is live — scoped to this class's own factory
/// instance only, so no other test class sharing the physical demo.db is affected (their own
/// factories' <c>DataPrivilegeSettings</c> stay empty, and <c>DPWhere</c> skips filtering
/// entirely for any table with no matching entry there, regardless of what
/// <c>DataPrivilege</c> rows exist in the shared database).
/// </para>
///
/// <para>
/// <b>Why HTML-scraping, not a direct <c>_FrameworkController.Selector(...)</c> call:</b>
/// <c>Selector</c>'s <c>Ids</c> branch calls <c>Wtm.CreateDC()</c> with no explicit connection
/// key — per this repo's own testing lesson (<c>.claude/rules/testing.md</c>),
/// that path "cannot be mocked" through <c>MockController</c>/<c>MockWtmContext</c> and needs a
/// real integration test. <c>Selector</c> also returns a rendered Razor partial
/// (<c>Selector.cshtml</c>), not JSON — <c>ViewBag.SelectData</c> is embedded as
/// <c>var var_XXXX = [...];</c> inside the response's <c>&lt;script&gt;</c> block, so
/// <see cref="ExtractSelectDataIds"/> regex-extracts and JSON-parses that array.
/// </para>
/// </summary>
[TestClass]
public class SelectorDataPrivilegeTests947
{
    private static DemoWebApplicationFactory _factory = null!;

    // Mirrors RequestBindingScopeHttpTests867's _strictFactory: IsQuickDebug=false so
    // NewAuthClientAsync's captcha-solving login flow is exercised the same proven way, plus
    // this class's own DataPrivilege<School> registration (see class doc comment above).
    private static WebApplicationFactory<WalkingTec.Mvvm.Demo.Program> _dpFactory = null!;

    private const string MajorListVm =
        "WalkingTec.Mvvm.Demo.ViewModels.MajorVMs.MajorListVM";

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        _factory = new DemoWebApplicationFactory();
        _dpFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["IsQuickDebug"] = "false",
                });
            });
            builder.ConfigureServices(services =>
            {
                // #947: see class doc comment — School has no DataPrivilege registration in
                // the demo app's own Startup.cs, so DPWhere would skip filtering entirely
                // without this override (its dpsSetting?.Where(x => x.ModelName == tableName)
                // check bails out before ever looking at the user's own DataPrivilege rows).
                services.RemoveAll<List<IDataPrivilege>>();
                services.AddSingleton<List<IDataPrivilege>>(new List<IDataPrivilege>
                {
                    new DataPrivilegeInfo<School>("#947 test school privilege", m => m.SchoolName)
                });
            });
        });
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        _dpFactory.Dispose();
        _factory.Dispose();
    }

    // Mirrors RequestBindingScopeHttpTests867.NewAuthClientAsync exactly (same proven
    // IsQuickDebug=false + CaptchaTestHelper login pattern), against this class's own
    // DataPrivilege-registering factory.
    private async Task<HttpClient> NewAuthClientAsync()
    {
        var client = _dpFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = true,
            HandleCookies = true,
        });
        var verifyCode = await CaptchaTestHelper.FetchAndSolveAsync(_dpFactory, client);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ITCode"] = "admin",
            ["Password"] = "000000",
            ["VerifyCode"] = verifyCode,
        });
        var loginResp = await client.PostAsync("/Login/Login", form);
        var loginBody = await loginResp.Content.ReadAsStringAsync();
        if (loginBody.Contains("login-error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"#947: NewAuthClientAsync's admin login did not succeed under _dpFactory " +
                $"(IsQuickDebug=false). Response: {loginBody[..Math.Min(500, loginBody.Length)]}");
        }
        return client;
    }

    // Selector.cshtml (src/WalkingTec.Mvvm.Mvc/Views/_Framework/Selector.cshtml) embeds
    // ViewBag.SelectData raw inside "var var_<random-guid>= @Html.Raw(ViewBag.SelectData);",
    // immediately followed by "function gridCheckedFunc(obj) {" on the next script line — a
    // fixed anchor this regex uses to bound a non-greedy capture of the JSON array.
    private static readonly Regex SelectDataRegex = new(
        @"var\s+var_[0-9a-fA-F]+\s*=\s*(\[.*?\]);\s*\r?\n\s*function\s+gridCheckedFunc",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Returns the "ID" value of each row in ViewBag.SelectData's embedded JSON array —
    // callers only ever need the ids, and extracting them here (rather than returning
    // JsonElements) avoids handing back JsonElements whose backing JsonDocument this method
    // has already disposed (JsonElement is only valid for the lifetime of its JsonDocument).
    private static List<string?> ExtractSelectDataIds(string html)
    {
        var match = SelectDataRegex.Match(html);
        Assert.IsTrue(match.Success,
            $"#947: could not locate the embedded 'var var_XXXX = [...]; function " +
            $"gridCheckedFunc' SelectData assignment in the Selector partial view response. " +
            $"Body head: {html[..Math.Min(2000, html.Length)]}");
        using var doc = JsonDocument.Parse(match.Groups[1].Value);
        return [.. doc.RootElement.EnumerateArray().Select(r => r.GetProperty("ID").GetString())];
    }

    /// <summary>
    /// Core #947 proof, in one request so all three assertions see the exact same query
    /// result: an admin restricted (via this class's DataPrivilege&lt;School&gt; registration)
    /// to only <c>allowedSchool</c> calls <c>Selector</c> naming BOTH a Major belonging to
    /// their allowed school and a Major belonging to a school outside their grant, while also
    /// posting a <c>Searcher.Remark</c> value that matches neither seeded Major's Remark.
    ///
    /// <list type="bullet">
    /// <item><b>Negative (authorization):</b> <c>forbiddenMajor</c> — outside the
    /// DataPrivilege grant — must be absent. Turns red if
    /// <c>_FrameworkController.cs</c>'s <c>listVM.SelectorValueField = _DONOT_USE_VFIELD;</c>
    /// line is reverted to the pre-#947 <c>listVM.ReplaceWhere =
    /// listVM.Ids.GetContainIdExpression(...)</c> assignment (mutant
    /// <c>947-selector-populateselectedentities-replacewhere-reintroduce</c>), OR if
    /// <c>BasePagedListVM.GetBatchQuery()</c>'s default branch is reverted to the
    /// pre-#947 <c>WhereReplaceModifier</c>-based rebuild (mutant
    /// <c>953-getbatchquery-wherereplacemodifier-reintroduce</c>) — this test now exercises
    /// both mechanisms, since <c>Selector</c> no longer calls anything that bypasses
    /// <c>GetBatchQuery()</c> (#953 review finding F2).</item>
    /// <item><b>Positive control:</b> <c>allowedMajor</c> — inside the grant — must be
    /// present. Without this, a fix that returns nothing for every <c>Ids</c> entry would
    /// pass the negative assertion for the wrong reason.</item>
    /// <item><b>Batch semantics preserved:</b> the SAME <c>allowedMajor</c> presence also
    /// proves the current UI search criteria (<c>Searcher.Remark</c>, posted above, matches
    /// neither seeded Major) is still ignored — the entire reason <c>Selector</c>'s <c>Ids</c>
    /// branch uses Batch semantics instead of pinning back to Search the way #867 did for
    /// <c>GetPagingData</c>. Turns red if <c>BasePagedListVM.GetAuthorizedIdsQuery</c>'s
    /// blank-<c>Searcher</c> swap is removed (i.e. if it called <c>GetSearchQuery()</c> with
    /// the real, request-bound <c>Searcher</c> instead).</item>
    /// </list>
    /// </summary>
    [TestMethod]
    public async Task Selector_DataPrivilegeRestrictedCaller_Ids_ExcludesUnauthorized_IncludesAuthorized_IgnoresSearchCriteria()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var allowedSchool = DbTestHelpers.Seed(_dpFactory, new School
        {
            SchoolCode = "947",
            SchoolName = $"AllowedSchool{suffix}",
            SchoolType = SchoolTypeEnum.PUB,
            Remark = "n/a",
        });
        var forbiddenSchool = DbTestHelpers.Seed(_dpFactory, new School
        {
            SchoolCode = "948",
            SchoolName = $"ForbiddenSchool{suffix}",
            SchoolType = SchoolTypeEnum.PUB,
            Remark = "n/a",
        });

        var allowedMajor = DbTestHelpers.Seed(_dpFactory, new Major
        {
            MajorCode = "100",
            MajorName = $"AllowedMajor{suffix}",
            MajorType = MajorTypeEnum.Required,
            Remark = $"allowed-remark-{suffix}",
            SchoolId = allowedSchool.ID,
        });
        var forbiddenMajor = DbTestHelpers.Seed(_dpFactory, new Major
        {
            MajorCode = "101",
            MajorName = $"ForbiddenMajor{suffix}",
            MajorType = MajorTypeEnum.Required,
            Remark = $"forbidden-remark-{suffix}",
            SchoolId = forbiddenSchool.ID,
        });

        // Restrict admin to only the allowed school's DataPrivilege grant.
        DbTestHelpers.Seed(_dpFactory, new DataPrivilege
        {
            TableName = nameof(School),
            UserCode = "admin",
            RelateId = allowedSchool.ID.ToString(),
        });

        var client = await NewAuthClientAsync();
        var form = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
        {
            new("_DONOT_USE_VMNAME", MajorListVm),
            new("_DONOT_USE_VFIELD", "ID"),
            new("_DONOT_USE_KFIELD", "MajorName"),
            new("_DONOT_USE_FIELD", "SelectedMajor"),
            new("_DONOT_USE_CURRENTCS", "default"),
            new("Ids", allowedMajor.ID.ToString()),
            new("Ids", forbiddenMajor.ID.ToString()),
            // Matches neither seeded Major's Remark — proves the Ids restriction ignores the
            // current UI search criteria (see the "Batch semantics preserved" bullet above).
            new("Searcher.Remark", $"does-not-match-anything-{suffix}"),
        });

        var resp = await client.PostAsync("/_Framework/Selector", form);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#947: Selector should succeed. Got {(int)resp.StatusCode}: {body[..Math.Min(500, body.Length)]}");

        var ids = ExtractSelectDataIds(body);

        Assert.IsFalse(ids.Contains(forbiddenMajor.ID.ToString()),
            "#947: a DataPrivilege-restricted caller must not receive a row (forbiddenMajor, " +
            "belonging to a school outside their DataPrivilege grant) they named directly in " +
            "the Ids they posted to Selector — SearcherMode.Batch + ReplaceWhere used to " +
            "delete the DataPrivilege Where node from GetSearchQuery() before rebuilding the " +
            "query with only the Ids restriction. " +
            $"SelectData returned: {string.Join(",", ids)}");

        Assert.IsTrue(ids.Contains(allowedMajor.ID.ToString()),
            "#947 positive control + batch-semantics guard: the SAME caller must still " +
            "receive rows they ARE entitled to (allowedMajor), even though the posted " +
            "Searcher.Remark matches neither seeded Major's Remark — proving both that the " +
            "fix does not simply return nothing (which would make the assertion above pass " +
            "for the wrong reason) and that the current UI search criteria is still ignored " +
            "the way Batch mode is designed to. " +
            $"SelectData returned: {string.Join(",", ids)}");
    }

    // Iterates every cell of every sheet looking for an exact string match — robust against
    // header-row/column-order details of MajorListVM's grid, unlike asserting a specific
    // row/column index.
    private static bool WorkbookContainsText(byte[] xlsxBytes, string text)
    {
        using var ms = new MemoryStream(xlsxBytes);
        using IWorkbook wb = new XSSFWorkbook(ms);
        for (int s = 0; s < wb.NumberOfSheets; s++)
        {
            var sheet = wb.GetSheetAt(s);
            for (int r = sheet.FirstRowNum; r <= sheet.LastRowNum; r++)
            {
                var row = sheet.GetRow(r);
                if (row == null) continue;
                for (int c = row.FirstCellNum; c < row.LastCellNum; c++)
                {
                    var cell = row.GetCell(c);
                    if (cell == null) continue;
                    string cellText = cell.CellType switch
                    {
                        CellType.String => cell.StringCellValue,
                        CellType.Numeric => cell.ToString() ?? "",
                        _ => cell.ToString() ?? "",
                    };
                    if (cellText.Contains(text, StringComparison.Ordinal))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// #953 review finding F4: the enumeration this fix's own commit narrative names
    /// (<c>GetExportExcel</c>/<c>GetExportExcelStream</c> → <c>CheckExport</c> →
    /// <c>GetCheckedExportQuery()</c> → <c>GetBatchQuery()</c>) had zero test coverage —
    /// reverting <c>GetBatchQuery()</c>'s default branch to the pre-#947
    /// <c>WhereReplaceModifier</c> rebuild left the whole suite green. This test drives
    /// <c>GetExportExcel</c> directly (not <c>Selector</c>) with the same
    /// DataPrivilege-restricted admin, so that half of the fix is proven independently of the
    /// <c>Selector</c> test above. <c>GetExportExcel</c> parses <c>Ids</c> the same way
    /// (<c>RedoUpdateModel</c>-bound), sets <c>SearcherMode = CheckExport</c> when
    /// <c>Ids.Count &gt; 0</c>, and <c>GenerateExcel()</c> → <c>GetCheckedExportQuery()</c> →
    /// <c>GetBatchQuery()</c> — the same default branch <c>Selector</c> now also relies on
    /// (#953 review finding F2), so this test and the one above jointly exercise
    /// <c>GetBatchQuery()</c> from two independent framework entry points.
    /// </summary>
    [TestMethod]
    public async Task GetExportExcel_DataPrivilegeRestrictedCaller_Ids_ExcludesUnauthorized_IncludesAuthorized()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var allowedSchool = DbTestHelpers.Seed(_dpFactory, new School
        {
            SchoolCode = "949",
            SchoolName = $"AllowedExportSchool{suffix}",
            SchoolType = SchoolTypeEnum.PUB,
            Remark = "n/a",
        });
        var forbiddenSchool = DbTestHelpers.Seed(_dpFactory, new School
        {
            SchoolCode = "950",
            SchoolName = $"ForbiddenExportSchool{suffix}",
            SchoolType = SchoolTypeEnum.PUB,
            Remark = "n/a",
        });

        var allowedMajor = DbTestHelpers.Seed(_dpFactory, new Major
        {
            MajorCode = "102",
            MajorName = $"AllowedExportMajor{suffix}",
            MajorType = MajorTypeEnum.Required,
            Remark = "n/a",
            SchoolId = allowedSchool.ID,
        });
        var forbiddenMajor = DbTestHelpers.Seed(_dpFactory, new Major
        {
            MajorCode = "103",
            MajorName = $"ForbiddenExportMajor{suffix}",
            MajorType = MajorTypeEnum.Required,
            Remark = "n/a",
            SchoolId = forbiddenSchool.ID,
        });

        // Restrict admin to only the allowed school's DataPrivilege grant. A fresh grant
        // (not reused from the Selector test above) so this test proves the property on its
        // own, independent of test execution order.
        DbTestHelpers.Seed(_dpFactory, new DataPrivilege
        {
            TableName = nameof(School),
            UserCode = "admin",
            RelateId = allowedSchool.ID.ToString(),
        });

        var client = await NewAuthClientAsync();
        var form = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
        {
            new("_DONOT_USE_VMNAME", MajorListVm),
            new("_DONOT_USE_CS", "default"),
            new("Ids", allowedMajor.ID.ToString()),
            new("Ids", forbiddenMajor.ID.ToString()),
        });

        var resp = await client.PostAsync("/_Framework/GetExportExcel", form);
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode,
            $"#953 F4: GetExportExcel should succeed. Got {(int)resp.StatusCode}.");
        var bytes = await resp.Content.ReadAsByteArrayAsync();

        Assert.IsFalse(WorkbookContainsText(bytes, forbiddenMajor.MajorName!),
            "#953 F4: a DataPrivilege-restricted caller must not receive a row " +
            "(forbiddenMajor, belonging to a school outside their DataPrivilege grant) in " +
            "an Excel export they named directly in the Ids they posted to " +
            "GetExportExcel — GetBatchQuery()'s default branch must keep row-level " +
            "DataPrivilege intact via GetAuthorizedIdsQuery, the same mechanism Selector " +
            "relies on.");

        Assert.IsTrue(WorkbookContainsText(bytes, allowedMajor.MajorName!),
            "#953 F4 positive control: the SAME caller must still receive rows they ARE " +
            "entitled to (allowedMajor) in the export — without this, a fix that excludes " +
            "everything would pass the assertion above for the wrong reason.");
    }
}
