#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Dashboard;
using WalkingTec.Mvvm.Core.Support.Json;
using WalkingTec.Mvvm.Mvc;
using WalkingTec.Mvvm.Test.Mock;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    /// <summary>
    /// Issue #957: end-to-end proof, through the real HTTP-facing controllers backed by a
    /// real <see cref="JsonFileDashboardService"/> (not a mock — a mocked
    /// <see cref="IDashboardService"/> would bypass the definition cache entirely and could
    /// not prove the non-destructive-masking requirement), that:
    ///   1. a viewer who only has CanAccess never receives a REST widget's real header
    ///      values, but does see header names;
    ///   2. masking a GET response never corrupts what a subsequent GET, or an internal
    ///      widget-data fetch, sees (JsonFileDashboardService._defCache hands back a SHARED
    ///      instance on a cache hit — mutating it while masking would be silent, permanent
    ///      corruption); and
    ///   3. Create/Update reconcile REST widget headers per the five-case table in
    ///      DashboardCredentialMasking.ReconcileWidgetHeaders, including the exact JSON shape
    ///      the shipped designer (framework_dashboard_designer.js's _readForm — no headers
    ///      editing UI, never sends a "headers" key) produces on save.
    /// Mirrors the structure of DashboardSsrfWriteValidationEndToEndTests.cs.
    /// </summary>
    [TestClass]
    public class DashboardCredentialMaskingEndToEndTests
    {
        private const string RealSecret = "Bearer real-secret-should-never-reach-a-viewer";

        private string _tempDir = "";
        private JsonFileDashboardService _service = null!;
        private CapturingRestDataSource _restDataSource = null!;

        [TestInitialize]
        public void Init()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var options = Options.Create(new DashboardOptions { DashboardDirectory = _tempDir });
            _restDataSource = new CapturingRestDataSource();
            _service = new JsonFileDashboardService(options, new IWidgetDataSource[] { _restDataSource },
                Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonFileDashboardService>.Instance);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }

        /// <summary>Records the WidgetDataRequest it receives — stands in for the real
        /// RestWidgetDataSource so tests can inspect the "options" JSON (which carries the
        /// widget's RestOptions, headers included) that would actually be used for the
        /// upstream HTTP call, without making a real network request.</summary>
        private class CapturingRestDataSource : IWidgetDataSource
        {
            public string Name => "rest";
            public WidgetDataSourceKind Kind => WidgetDataSourceKind.Rest;
            public WidgetDataRequest? CapturedRequest { get; private set; }

            public Task<WidgetDataResult> GetDataAsync(WidgetDataRequest request, CancellationToken ct = default)
            {
                CapturedRequest = request;
                return Task.FromResult(new WidgetDataResult { Value = 1 });
            }
        }

        private _DashboardController BuildController(string userId, string tenant = "")
        {
            var opts = Options.Create(new DashboardOptions());
            var ctrl = new _DashboardController(_service, opts, new IWidgetDataSource[] { _restDataSource })
            {
                Wtm = MockWtmContext.CreateWtmContext()
            };
            ctrl.Wtm.LoginUserInfo = new LoginUserInfo { ITCode = userId, TenantCode = tenant };
            ctrl.Wtm.LoginUserInfo.Roles = new List<SimpleRole>();
            ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return ctrl;
        }

        private _DashboardDesignerController BuildDesignerController(string userId = "owner")
        {
            var opts = Options.Create(new DashboardOptions());
            var ctrl = new _DashboardDesignerController(_service, opts)
            {
                Wtm = MockWtmContext.CreateWtmContext()
            };
            ctrl.Wtm.LoginUserInfo = new LoginUserInfo { ITCode = userId };
            ctrl.Wtm.LoginUserInfo.Roles = new List<SimpleRole>();
            ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return ctrl;
        }

        private static WidgetDefinition RestWidget(string url, Dictionary<string, string>? headers) => new()
        {
            Type = "chart",
            Source = new WidgetSourceDefinition
            {
                Kind = "rest",
                RestOptions = new RestWidgetDataSourceOptions { Url = url, Headers = headers }
            }
        };

        // ── 1. A viewer never receives real header values ──────────────────────

        [TestMethod]
        public async Task Get_masks_header_values_for_a_viewer_who_can_access_but_preserves_header_names()
        {
            var owner = BuildController("owner");
            var dashboard = new DashboardDefinition
            {
                Title = "shared-dashboard",
                Sharing = new SharingDefinition { Mode = "public" },
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = RestWidget("https://8.8.8.8/api", new Dictionary<string, string>
                    {
                        ["Authorization"] = RealSecret,
                        ["X-Api-Key"] = "another-real-secret"
                    })
                }
            };
            var createResult = await owner.Create(dashboard) as OkObjectResult;
            var id = (string)createResult!.Value!;

            // A different, non-owner viewer who only qualifies via the public Sharing mode.
            var viewer = BuildController("viewer");
            var result = await viewer.Get(id) as OkObjectResult;

            result.Should().NotBeNull();
            var seen = (DashboardDefinition)result!.Value!;
            var headers = seen.Widgets["w1"].Source.RestOptions!.Headers!;

            headers.Should().ContainKey("Authorization", "header NAMES must survive so a future editor UI can list them");
            headers.Should().ContainKey("X-Api-Key");
            headers["Authorization"].Should().Be(DashboardCredentialMasking.MaskedHeaderValue);
            headers["X-Api-Key"].Should().Be(DashboardCredentialMasking.MaskedHeaderValue);

            // Belt-and-suspenders: the real secret must not appear anywhere in the
            // serialized response, not just in the one field we asserted on directly.
            var json = JsonSerializer.Serialize(seen);
            json.Should().NotContain(RealSecret, "a viewer must never receive the real credential in any form");
            json.Should().NotContain("another-real-secret");
        }

        // ── 2. Masking a GET must not corrupt the cache or an internal fetch ────

        [TestMethod]
        public async Task Get_does_not_corrupt_a_subsequent_direct_GetAsync_call()
        {
            var owner = BuildController("owner");
            var dashboard = new DashboardDefinition
            {
                Title = "cache-corruption-check",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = RestWidget("https://8.8.8.8/api",
                        new Dictionary<string, string> { ["Authorization"] = RealSecret })
                }
            };
            var createResult = await owner.Create(dashboard) as OkObjectResult;
            var id = (string)createResult!.Value!;

            // Force the definition into JsonFileDashboardService's _defCache and get back a
            // masked response from the controller.
            var firstGet = await owner.Get(id) as OkObjectResult;
            ((DashboardDefinition)firstGet!.Value!).Widgets["w1"].Source.RestOptions!.Headers!["Authorization"]
                .Should().Be(DashboardCredentialMasking.MaskedHeaderValue);

            // Bypass the controller/masking layer entirely and read the SAME cached instance
            // JsonFileDashboardService.GetAsync would return on a cache hit.
            var direct = await _service.GetAsync(id);

            direct.Should().NotBeNull();
            direct!.Widgets["w1"].Source.RestOptions!.Headers!["Authorization"].Should().Be(RealSecret,
                "the masked GET response must never have mutated the definition cache — a subsequent " +
                "direct read (as any internal caller, not just another masked GET, would see) must still " +
                "see the real header value");

            // And a second masked GET must independently mask again (not "already masked").
            var secondGet = await owner.Get(id) as OkObjectResult;
            ((DashboardDefinition)secondGet!.Value!).Widgets["w1"].Source.RestOptions!.Headers!["Authorization"]
                .Should().Be(DashboardCredentialMasking.MaskedHeaderValue);
        }

        [TestMethod]
        public async Task Get_does_not_corrupt_the_internal_widget_data_fetch()
        {
            var owner = BuildController("owner");
            var dashboard = new DashboardDefinition
            {
                Title = "internal-fetch-check",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = RestWidget("https://8.8.8.8/api",
                        new Dictionary<string, string> { ["Authorization"] = RealSecret })
                }
            };
            var createResult = await owner.Create(dashboard) as OkObjectResult;
            var id = (string)createResult!.Value!;

            // Populate the cache via a masked read, exactly as a real viewer's browser would
            // trigger via framework_dashboard.js's DashboardManager._loadDashboard.
            await owner.Get(id);

            // Now trigger the SAME internal pipeline RestWidgetDataSource would use to make
            // the actual upstream HTTP call.
            var widgetDataResult = await owner.GetWidgetData(id, "w1", CancellationToken.None);

            widgetDataResult.Should().BeOfType<OkObjectResult>(
                "the widget-data fetch itself must succeed — if masking had corrupted the cache this " +
                "would still return 200, which is exactly why asserting on the CAPTURED options JSON " +
                "below (not just on this status) is the real proof");

            _restDataSource.CapturedRequest.Should().NotBeNull();
            var optionsJson = _restDataSource.CapturedRequest!.Parameters["options"];
            var forwardedOptions = JsonSerializer.Deserialize<RestWidgetDataSourceOptions>(optionsJson);

            forwardedOptions!.Headers!["Authorization"].Should().Be(RealSecret,
                "the real REST fetch pipeline must receive the REAL header value, not the masked sentinel " +
                "a prior GET response showed a viewer — otherwise every REST widget would break the moment " +
                "anyone viewed the dashboard");
        }

        // ── 3a. Update: headers absent (designer-shaped payload) preserves existing ────

        [TestMethod]
        public async Task Update_with_headers_key_absent_from_the_JSON_preserves_existing_headers()
        {
            // Reproduces framework_dashboard_designer.js's _readForm() output exactly:
            //   source = { kind: 'rest', restOptions: { url: _val('dsd-rest-url') } }
            // — no "headers" key at all. Deserializing that shape must bind Headers to null
            // (see RestWidgetDataSourceOptionsHeadersBindingTests), which ReconcileWidgetHeaders
            // must then treat as "preserve", or every save through the shipped designer
            // silently wipes the widget's headers (issue #957's second defect).
            var owner = BuildController("owner");
            var created = new DashboardDefinition
            {
                Title = "designer-save-check",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = RestWidget("https://8.8.8.8/api",
                        new Dictionary<string, string> { ["Authorization"] = RealSecret })
                }
            };
            var createResult = await owner.Create(created) as OkObjectResult;
            var id = (string)createResult!.Value!;

            var designerJson =
                "{\"Id\":\"" + id + "\",\"Title\":\"designer-save-check\",\"Widgets\":{\"w1\":{" +
                "\"Type\":\"chart\",\"Source\":{\"Kind\":\"rest\",\"RestOptions\":{\"Url\":\"https://8.8.8.8/api\"}}" +
                "}}}";
            var designerShapedUpdate = JsonSerializer.Deserialize<DashboardDefinition>(designerJson)!;

            // Confirm the premise before asserting the outcome: this really is the "absent" case.
            designerShapedUpdate.Widgets["w1"].Source.RestOptions!.Headers.Should().BeNull();

            var updateResult = await owner.Update(id, designerShapedUpdate);
            updateResult.Should().BeOfType<OkResult>();

            var stored = await _service.GetAsync(id);
            stored!.Widgets["w1"].Source.RestOptions!.Headers.Should().NotBeNull()
                .And.ContainKey("Authorization");
            stored.Widgets["w1"].Source.RestOptions!.Headers!["Authorization"].Should().Be(RealSecret,
                "a save that never touched headers must not wipe them — this is the live data-loss bug " +
                "the shipped designer triggers today without this fix");
        }

        // ── 3a-bis. Coordinator follow-up: same check, but against a JSON FILE that already
        // existed on disk before this fix — not data this test's own Create() call produced. ──

        [TestMethod]
        public async Task Update_with_headers_absent_preserves_headers_from_a_dashboard_file_written_before_this_fix_existed()
        {
            // RestWidgetDataSourceOptions.Headers' nullability is the ONLY thing this fix changed
            // about the model; a populated Headers dictionary serializes identically regardless of
            // that annotation (nullability only changes what an ABSENT or EMPTY key binds to, never
            // a populated one). So this exact JSON — Headers carrying a real operator-set value — is
            // precisely what pre-#957 JsonFileDashboardService.CreateAsync would have written for a
            // REST widget with a real Authorization header. Written straight to disk, bypassing this
            // test's own Create() entirely, so GetAsync below performs a genuine first-time parse of
            // data that already existed before upgrading, not something this test run produced.
            var dashboardDir = Path.Combine(_tempDir, "_default");
            Directory.CreateDirectory(dashboardDir);
            const string preFixShapedJson =
                "{" +
                  "\"SchemaVersion\":1,\"Id\":\"pre-fix-d1\",\"Title\":\"pre-fix-dashboard\",\"Owner\":\"owner\"," +
                  "\"TenantId\":null,\"Sharing\":{\"Mode\":\"private\",\"Roles\":null}," +
                  "\"RefreshInterval\":60,\"Layout\":[]," +
                  "\"Widgets\":{\"w1\":{" +
                    "\"Type\":\"chart\",\"Title\":\"\"," +
                    "\"Source\":{\"Kind\":\"rest\",\"Name\":null,\"ListVmType\":null,\"Dimensions\":null," +
                      "\"Measures\":null,\"Filters\":null," +
                      "\"RestOptions\":{\"Url\":\"https://8.8.8.8/api\",\"Method\":\"GET\"," +
                        "\"Headers\":{\"Authorization\":\"Bearer pre-fix-real-secret\"}," +
                        "\"Body\":null,\"JsonPath\":\"$\",\"CacheTtlSeconds\":60,\"TimeoutSeconds\":10," +
                        "\"MaxResponseBytes\":1048576,\"AllowPrivateNetwork\":false,\"AllowHttp\":false," +
                        "\"AllowedPorts\":[80,443,8080,8443]}}," +
                    "\"Config\":{},\"Thresholds\":null,\"DrillDown\":null" +
                  "}}," +
                  "\"Filters\":null,\"Links\":null," +
                  "\"CreatedAt\":\"2026-01-01T00:00:00Z\",\"UpdatedAt\":\"2026-01-01T00:00:00Z\"" +
                "}";
            await File.WriteAllTextAsync(Path.Combine(dashboardDir, "pre-fix-d1.json"), preFixShapedJson);

            var owner = BuildController("owner");

            // Designer-shaped update: no "headers" key anywhere, matching _readForm()'s actual output.
            var designerJson =
                "{\"Id\":\"pre-fix-d1\",\"Title\":\"pre-fix-dashboard\",\"Widgets\":{\"w1\":{" +
                "\"Type\":\"chart\",\"Source\":{\"Kind\":\"rest\",\"RestOptions\":{\"Url\":\"https://8.8.8.8/api\"}}" +
                "}}}";
            var designerShapedUpdate = JsonSerializer.Deserialize<DashboardDefinition>(designerJson)!;
            designerShapedUpdate.Widgets["w1"].Source.RestOptions!.Headers.Should().BeNull(); // premise check

            var updateResult = await owner.Update("pre-fix-d1", designerShapedUpdate);
            updateResult.Should().BeOfType<OkResult>();

            // Force a genuine disk re-read: a brand-new JsonFileDashboardService instance pointed at
            // the same directory has never seen this file, so its first GetAsync cannot be a cache hit.
            var freshOptions = Options.Create(new DashboardOptions { DashboardDirectory = _tempDir });
            var freshService = new JsonFileDashboardService(freshOptions, Array.Empty<IWidgetDataSource>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonFileDashboardService>.Instance);

            var stored = await freshService.GetAsync("pre-fix-d1");

            stored.Should().NotBeNull();
            stored!.Widgets["w1"].Source.RestOptions!.Headers.Should().NotBeNull().And.ContainKey("Authorization");
            stored.Widgets["w1"].Source.RestOptions!.Headers!["Authorization"].Should().Be("Bearer pre-fix-real-secret",
                "data that already existed on disk before this fix shipped (Headers populated — the one " +
                "shape unaffected by the nullability change) must survive a designer-shaped save exactly " +
                "like freshly-created data does, verified against a real file this test did not create");
        }

        // ── F1 (#960): the full attack script — editor retypes a widget's URL through the
        // designer, real credential must NOT follow it to the new host. ────────────────────

        [TestMethod]
        public async Task Update_that_retypes_the_URL_to_a_different_host_through_the_designer_does_not_forward_the_stored_credential()
        {
            // Reproduces the adversarial review's exact script:
            //  1. widget w1: url=https://api.vendor.com/v1, Authorization: Bearer <real>
            //  2. an editor opens the designer, retypes the URL box to an attacker-chosen host
            //  3. the designer PUTs restOptions: { url: ... } with NO headers key (case a)
            // Naive case (a) would copy the real token onto the attacker's host. F1 must stop
            // that while still allowing the URL edit itself to succeed (the designer has no
            // way to re-supply headers, so blocking the save outright would be worse).
            var owner = BuildController("owner");
            var created = new DashboardDefinition
            {
                Title = "url-retype-attack",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = RestWidget("https://api.vendor.com/v1", new Dictionary<string, string>
                    {
                        ["Authorization"] = RealSecret
                    })
                }
            };
            var createResult = await owner.Create(created) as OkObjectResult;
            var id = (string)createResult!.Value!;

            // Exact designer _readForm() shape: only { kind, restOptions: { url } }, no headers key.
            var designerJson =
                "{\"Id\":\"" + id + "\",\"Title\":\"url-retype-attack\",\"Widgets\":{\"w1\":{" +
                "\"Type\":\"chart\",\"Source\":{\"Kind\":\"rest\",\"RestOptions\":{\"Url\":\"https://evil.example/collect\"}}" +
                "}}}";
            var designerShapedUpdate = JsonSerializer.Deserialize<DashboardDefinition>(designerJson)!;

            var updateResult = await owner.Update(id, designerShapedUpdate);

            updateResult.Should().BeOfType<OkResult>(
                "the URL edit itself must still succeed — the designer has no headers UI to re-supply them, " +
                "so rejecting the save outright would permanently lock the widget's URL once it had a header");

            var stored = await _service.GetAsync(id);
            stored!.Widgets["w1"].Source.RestOptions!.Url.Should().Be("https://evil.example/collect",
                "the URL edit itself is legitimate and must take effect");
            stored.Widgets["w1"].Source.RestOptions!.Headers.Should().BeNullOrEmpty(
                "the real credential bound to api.vendor.com must NOT have been carried over to evil.example — " +
                "this is the credential-forwarding defect F1 closes");

            // And the credential must not be reachable through the actual fetch pipeline either
            // — the widget-data fetch must not carry the old Authorization value to the new host.
            var widgetDataResult = await owner.GetWidgetData(id, "w1", CancellationToken.None);
            widgetDataResult.Should().BeOfType<OkObjectResult>();
            var optionsJson = _restDataSource.CapturedRequest!.Parameters["options"];
            var forwardedOptions = JsonSerializer.Deserialize<RestWidgetDataSourceOptions>(optionsJson);
            (forwardedOptions!.Headers == null || !forwardedOptions.Headers.ContainsKey("Authorization"))
                .Should().BeTrue("the real REST fetch pipeline must not carry the old host's credential to the new host");
        }

        // ── 3b. Update: headers present-and-empty clears ────────────────────────

        [TestMethod]
        public async Task Update_with_headers_present_and_empty_clears_existing_headers()
        {
            var owner = BuildController("owner");
            var created = new DashboardDefinition
            {
                Title = "clear-check",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = RestWidget("https://8.8.8.8/api",
                        new Dictionary<string, string> { ["Authorization"] = RealSecret })
                }
            };
            var createResult = await owner.Create(created) as OkObjectResult;
            var id = (string)createResult!.Value!;

            var update = new DashboardDefinition
            {
                Id = id,
                Title = "clear-check",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = RestWidget("https://8.8.8.8/api", new Dictionary<string, string>()) // explicit {}
                }
            };

            var updateResult = await owner.Update(id, update);
            updateResult.Should().BeOfType<OkResult>();

            var stored = await _service.GetAsync(id);
            stored!.Widgets["w1"].Source.RestOptions!.Headers.Should().NotBeNull().And.BeEmpty(
                "an explicit empty Headers object means the caller intentionally wants zero headers");
        }

        // ── 3c. Update: a real value sets it ────────────────────────────────────

        [TestMethod]
        public async Task Update_with_a_real_header_value_stores_it()
        {
            var owner = BuildController("owner");
            var created = new DashboardDefinition
            {
                Title = "set-check",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = RestWidget("https://8.8.8.8/api", headers: null)
                }
            };
            var createResult = await owner.Create(created) as OkObjectResult;
            var id = (string)createResult!.Value!;

            var update = new DashboardDefinition
            {
                Id = id,
                Title = "set-check",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = RestWidget("https://8.8.8.8/api",
                        new Dictionary<string, string> { ["Authorization"] = "freshly-set-value" })
                }
            };

            var updateResult = await owner.Update(id, update);
            updateResult.Should().BeOfType<OkResult>();

            var stored = await _service.GetAsync(id);
            stored!.Widgets["w1"].Source.RestOptions!.Headers!["Authorization"].Should().Be("freshly-set-value");
        }

        // ── 3d. Update: sentinel round-tripped from a masked GET preserves the real value ──

        [TestMethod]
        public async Task Update_with_the_masked_sentinel_round_tripped_from_a_prior_GET_preserves_the_real_value()
        {
            var owner = BuildController("owner");
            var created = new DashboardDefinition
            {
                Title = "round-trip-check",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = RestWidget("https://8.8.8.8/api",
                        new Dictionary<string, string> { ["Authorization"] = RealSecret })
                }
            };
            var createResult = await owner.Create(created) as OkObjectResult;
            var id = (string)createResult!.Value!;

            // Simulate a real editor round trip: GET (masked) → change nothing about the
            // header → PUT the same (now-masked) object back.
            var getResult = await owner.Get(id) as OkObjectResult;
            var roundTripped = (DashboardDefinition)getResult!.Value!;
            roundTripped.Widgets["w1"].Source.RestOptions!.Headers!["Authorization"]
                .Should().Be(DashboardCredentialMasking.MaskedHeaderValue); // premise check

            var updateResult = await owner.Update(id, roundTripped);
            updateResult.Should().BeOfType<OkResult>();

            var stored = await _service.GetAsync(id);
            stored!.Widgets["w1"].Source.RestOptions!.Headers!["Authorization"].Should().Be(RealSecret,
                "re-submitting the masked value the caller itself received must restore the real " +
                "credential, not persist the sentinel literal");
        }

        // ── 3e. Update: sentinel for a brand-new key is rejected ────────────────

        [TestMethod]
        public async Task Update_with_the_masked_sentinel_for_a_key_with_no_existing_value_is_rejected()
        {
            var owner = BuildController("owner");
            var created = new DashboardDefinition
            {
                Title = "reject-check",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = RestWidget("https://8.8.8.8/api",
                        new Dictionary<string, string> { ["Authorization"] = RealSecret })
                }
            };
            var createResult = await owner.Create(created) as OkObjectResult;
            var id = (string)createResult!.Value!;

            var update = new DashboardDefinition
            {
                Id = id,
                Title = "reject-check",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = RestWidget("https://8.8.8.8/api", new Dictionary<string, string>
                    {
                        ["Authorization"] = RealSecret, // unchanged, valid
                        ["X-Brand-New"] = DashboardCredentialMasking.MaskedHeaderValue // never existed
                    })
                }
            };

            var result = await owner.Update(id, update) as BadRequestObjectResult;

            result.Should().NotBeNull("a masked-sentinel value for a header that never existed must be rejected, " +
                                       "not silently stored as a literal credential");

            var stored = await _service.GetAsync(id);
            stored!.Widgets["w1"].Source.RestOptions!.Headers.Should().NotContainKey("X-Brand-New",
                "a rejected Update must not have persisted anything");
            stored.Widgets["w1"].Source.RestOptions!.Headers!["Authorization"].Should().Be(RealSecret,
                "the original widget must survive a rejected Update untouched");
        }

        // ── Create: sentinel is always rejected (no existing widget to fall back to) ──

        [TestMethod]
        public async Task Create_with_the_masked_sentinel_header_value_is_rejected()
        {
            var owner = BuildController("owner");
            var dashboard = new DashboardDefinition
            {
                Title = "create-reject-check",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = RestWidget("https://8.8.8.8/api", new Dictionary<string, string>
                    {
                        ["Authorization"] = DashboardCredentialMasking.MaskedHeaderValue
                    })
                }
            };

            var result = await owner.Create(dashboard) as BadRequestObjectResult;

            result.Should().NotBeNull("Create has no existing widget to fall back to, so a masked-sentinel " +
                                       "header value can never be legitimate");

            var list = await owner.List() as OkObjectResult;
            ((List<DashboardSummary>)list!.Value!).Should().BeEmpty("nothing should have been persisted");
        }

        // ── F5 (#960): _DashboardDesignerController.Preview's own reconciliation guard had
        // NO test in any of the three original files — deleting that call site turned nothing
        // red. These close that gap directly against the real _DashboardDesignerController. ──

        [TestMethod]
        public async Task Preview_with_masked_sentinel_header_value_is_rejected()
        {
            var designerCtrl = BuildDesignerController();
            var widget = RestWidget("https://8.8.8.8/api", new Dictionary<string, string>
            {
                ["Authorization"] = DashboardCredentialMasking.MaskedHeaderValue
            });

            var result = await designerCtrl.Preview(widget, CancellationToken.None) as BadRequestObjectResult;

            result.Should().NotBeNull(
                "Preview always builds a brand-new transient widget, so there is never an existing value " +
                "to restore — a masked-sentinel header value can never be legitimate here, same as Create");

            // Nothing should have reached CreateAsync at all — the transient dashboard must never
            // have been persisted (even transiently) for a rejected preview.
            var list = await BuildController("owner").List() as OkObjectResult;
            ((List<DashboardSummary>)list!.Value!).Should().BeEmpty(
                "a rejected Preview must reject before CreateAsync — nothing, not even a transient row, is persisted");
        }

        [TestMethod]
        public async Task Preview_with_a_real_header_value_is_accepted_by_the_header_guard()
        {
            // Positive control: Preview's header reconciliation must not reject ordinary,
            // legitimate header values — only the masked sentinel. This test's own service has
            // no "rest" IWidgetDataSource wired with a real network stack, so the actual data
            // fetch behaviour is not what is being asserted here (mirrors the positive-control
            // pattern in DashboardSsrfWriteValidationEndToEndTests.cs) — only that the header
            // guard itself does not fire for a real value.
            var designerCtrl = BuildDesignerController();
            var widget = RestWidget("https://8.8.8.8/api", new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer a-real-value"
            });

            var result = await designerCtrl.Preview(widget, CancellationToken.None);

            result.Should().NotBeOfType<BadRequestObjectResult>(
                "a real, non-sentinel header value must pass Preview's header reconciliation guard");
        }
    }
}
