#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Core.Dashboard;

namespace WalkingTec.Mvvm.Core.Test.Dashboard
{
    /// <summary>
    /// Issue #957: pure unit coverage for <see cref="DashboardCredentialMasking"/> — no
    /// controller, no HTTP, no real storage backend. End-to-end proof through the real
    /// <c>_DashboardController</c> + <c>JsonFileDashboardService</c> (including the
    /// non-destructive-cache and Update-merge-semantics tests) lives in
    /// <c>DashboardCredentialMaskingEndToEndTests.cs</c>.
    /// </summary>
    [TestClass]
    public class DashboardCredentialMaskingTests
    {
        private const string DefaultUrl = "https://8.8.8.8/api";

        private static DashboardDefinition DashboardWithRestHeaders(
            Dictionary<string, string>? headers, string widgetId = "w1", string url = DefaultUrl)
        {
            return new DashboardDefinition
            {
                Id = "d1",
                Title = "Test Dashboard",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    [widgetId] = new WidgetDefinition
                    {
                        Type = "chart",
                        Source = new WidgetSourceDefinition
                        {
                            Kind = "rest",
                            RestOptions = new RestWidgetDataSourceOptions
                            {
                                Url = url,
                                Headers = headers
                            }
                        }
                    }
                }
            };
        }

        // ── MaskForRead ─────────────────────────────────────────────────────────

        [TestMethod]
        public void MaskForRead_masks_header_values_but_preserves_keys()
        {
            var dashboard = DashboardWithRestHeaders(new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer super-secret-token",
                ["X-Api-Key"] = "api-key-xyz"
            });

            var masked = DashboardCredentialMasking.MaskForRead(dashboard);

            var maskedHeaders = masked.Widgets["w1"].Source.RestOptions!.Headers!;
            maskedHeaders.Should().ContainKey("Authorization", "header names must survive masking so a future editor UI can list them");
            maskedHeaders.Should().ContainKey("X-Api-Key");
            maskedHeaders["Authorization"].Should().Be(DashboardCredentialMasking.MaskedHeaderValue);
            maskedHeaders["X-Api-Key"].Should().Be(DashboardCredentialMasking.MaskedHeaderValue);
            maskedHeaders["Authorization"].Should().NotContain("super-secret-token");
            maskedHeaders["X-Api-Key"].Should().NotContain("api-key-xyz");
        }

        [TestMethod]
        public void MaskForRead_does_not_mutate_the_source_object()
        {
            var dashboard = DashboardWithRestHeaders(new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer do-not-corrupt-me"
            });
            var originalHeadersRef = dashboard.Widgets["w1"].Source.RestOptions!.Headers;

            var masked = DashboardCredentialMasking.MaskForRead(dashboard);

            dashboard.Widgets["w1"].Source.RestOptions!.Headers.Should().BeSameAs(originalHeadersRef,
                "MaskForRead must not replace the original object's Headers dictionary reference");
            dashboard.Widgets["w1"].Source.RestOptions!.Headers!["Authorization"].Should().Be("Bearer do-not-corrupt-me",
                "the original (unmasked) DashboardDefinition — which may be a cache-shared instance — must be untouched");
            masked.Should().NotBeSameAs(dashboard, "a new DashboardDefinition must be returned when masking actually changed something");
        }

        [TestMethod]
        public void MaskForRead_returns_the_same_instance_when_no_widget_has_headers()
        {
            var dashboard = DashboardWithRestHeaders(headers: null);

            var masked = DashboardCredentialMasking.MaskForRead(dashboard);

            masked.Should().BeSameAs(dashboard,
                "when nothing needs masking, returning the same reference avoids an unnecessary allocation and keeps identity for existing callers");
        }

        [TestMethod]
        public void MaskForRead_returns_the_same_instance_when_headers_is_an_empty_dictionary()
        {
            var dashboard = DashboardWithRestHeaders(new Dictionary<string, string>());

            var masked = DashboardCredentialMasking.MaskForRead(dashboard);

            masked.Should().BeSameAs(dashboard);
        }

        [TestMethod]
        public void MaskForRead_handles_a_dashboard_with_no_widgets()
        {
            var dashboard = new DashboardDefinition { Id = "empty", Title = "Empty" };

            var masked = DashboardCredentialMasking.MaskForRead(dashboard);

            masked.Should().BeSameAs(dashboard);
        }

        [TestMethod]
        public void MaskForRead_masks_only_the_widget_that_has_headers_in_a_multi_widget_dashboard()
        {
            var dashboard = new DashboardDefinition
            {
                Id = "d1",
                Widgets = new Dictionary<string, WidgetDefinition>
                {
                    ["w1"] = new WidgetDefinition
                    {
                        Type = "chart",
                        Source = new WidgetSourceDefinition
                        {
                            Kind = "rest",
                            RestOptions = new RestWidgetDataSourceOptions
                            {
                                Url = "https://8.8.8.8/a",
                                Headers = new Dictionary<string, string> { ["Authorization"] = "secret-a" }
                            }
                        }
                    },
                    ["w2"] = new WidgetDefinition
                    {
                        Type = "kpi",
                        Source = new WidgetSourceDefinition { Kind = "analysis", ListVmType = "Foo" }
                    }
                }
            };

            var masked = DashboardCredentialMasking.MaskForRead(dashboard);

            masked.Widgets["w1"].Source.RestOptions!.Headers!["Authorization"]
                .Should().Be(DashboardCredentialMasking.MaskedHeaderValue);
            masked.Widgets["w2"].Should().BeSameAs(dashboard.Widgets["w2"],
                "a widget with no REST headers has nothing to mask and should keep its reference");
        }

        // ── ReconcileWidgetHeaders: the case table (a)-(f) ──────────────────────

        private static Dictionary<string, WidgetDefinition> Widgets(
            Dictionary<string, string>? headers, string widgetId = "w1", string url = DefaultUrl)
            => DashboardWithRestHeaders(headers, widgetId, url).Widgets;

        [TestMethod]
        public void ReconcileWidgetHeaders_case_a_null_headers_preserves_all_existing_headers()
        {
            var incoming = Widgets(headers: null); // absent / explicit JSON null
            var existing = Widgets(new Dictionary<string, string>
            {
                ["Authorization"] = "real-secret",
                ["X-Trace"] = "trace-value"
            });

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            warnings.Should().BeEmpty("same destination — nothing was dropped");
            incoming["w1"].Source.RestOptions!.Headers.Should().BeEquivalentTo(new Dictionary<string, string>
            {
                ["Authorization"] = "real-secret",
                ["X-Trace"] = "trace-value"
            });
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_case_b_empty_headers_clears_all_headers()
        {
            var incoming = Widgets(new Dictionary<string, string>()); // present, empty
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "real-secret" });

            var (error, _) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            incoming["w1"].Source.RestOptions!.Headers.Should().NotBeNull().And.BeEmpty();
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_case_c_a_real_value_is_stored_as_is()
        {
            var incoming = Widgets(new Dictionary<string, string> { ["Authorization"] = "brand-new-real-value" });
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "old-value" });

            var (error, _) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            incoming["w1"].Source.RestOptions!.Headers!["Authorization"].Should().Be("brand-new-real-value");
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_case_c_a_real_value_is_stored_as_is_even_across_a_destination_change()
        {
            // Case (c) never "carries anything over" — the caller is setting the value
            // directly, so it is safe (and correct) regardless of whether the URL also changed.
            var incoming = Widgets(new Dictionary<string, string> { ["Authorization"] = "fresh-value-for-new-host" },
                url: "https://new-host.example/api");
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "old-value" },
                url: "https://old-host.example/api");

            var (error, _) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            incoming["w1"].Source.RestOptions!.Headers!["Authorization"].Should().Be("fresh-value-for-new-host");
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_case_d_sentinel_for_existing_key_preserves_existing_value()
        {
            var incoming = Widgets(new Dictionary<string, string>
            {
                ["Authorization"] = DashboardCredentialMasking.MaskedHeaderValue
            });
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "real-secret-preserved" });

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            warnings.Should().BeEmpty();
            incoming["w1"].Source.RestOptions!.Headers!["Authorization"].Should().Be("real-secret-preserved",
                "the sentinel means 'unchanged' — the real stored value must be restored, not the sentinel literal");
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_case_e_sentinel_for_unknown_key_is_rejected()
        {
            var incoming = Widgets(new Dictionary<string, string>
            {
                ["X-Brand-New-Header"] = DashboardCredentialMasking.MaskedHeaderValue
            });
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "unrelated" });

            var (error, _) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().NotBeNull("there is no existing value to restore, and storing the sentinel literal " +
                                      "as a credential must never happen");
            // ReconcileWidgetHeaders returns an error instead of finishing the merge — it is
            // the CALLER's job (the controller, proven by the end-to-end
            // Update_with_the_masked_sentinel_for_a_key_with_no_existing_value_is_rejected
            // test) to return 400 and never call CreateAsync/UpdateAsync at all when this is
            // non-null, so nothing derived from `incoming` is ever persisted.
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_case_e_on_create_rejects_any_sentinel_value_since_nothing_exists_yet()
        {
            var incoming = Widgets(new Dictionary<string, string>
            {
                ["Authorization"] = DashboardCredentialMasking.MaskedHeaderValue
            });

            // Create: existingWidgets is null — nothing persisted yet for ANY key.
            var (error, _) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existingWidgets: null);

            error.Should().NotBeNull("Create can never legitimately receive the masked sentinel back for a key it never had");
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_handles_null_incoming_widgets_dictionary()
        {
            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(null, null);
            error.Should().BeNull();
            warnings.Should().BeEmpty();
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_mixed_keys_apply_each_case_independently()
        {
            // A: sentinel for an existing key → preserved.
            // B: a real value for a different key → stored as-is.
            var incoming = Widgets(new Dictionary<string, string>
            {
                ["Authorization"] = DashboardCredentialMasking.MaskedHeaderValue,
                ["X-Trace"] = "new-trace-value"
            });
            var existing = Widgets(new Dictionary<string, string>
            {
                ["Authorization"] = "preserved-secret",
                ["X-Trace"] = "old-trace-value"
            });

            var (error, _) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            incoming["w1"].Source.RestOptions!.Headers!["Authorization"].Should().Be("preserved-secret");
            incoming["w1"].Source.RestOptions!.Headers!["X-Trace"].Should().Be("new-trace-value");
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_a_key_dropped_from_a_non_empty_incoming_dictionary_is_removed()
        {
            // Non-empty Headers is a full replacement set — a key present in the existing
            // headers but absent from a non-empty incoming Headers is dropped, same as (b).
            var incoming = Widgets(new Dictionary<string, string> { ["X-Trace"] = "kept" });
            var existing = Widgets(new Dictionary<string, string>
            {
                ["Authorization"] = "should-be-dropped",
                ["X-Trace"] = "old"
            });

            var (error, _) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            incoming["w1"].Source.RestOptions!.Headers.Should().BeEquivalentTo(
                new Dictionary<string, string> { ["X-Trace"] = "kept" });
        }

        // ── F1 (#960): credentials are host-scoped ──────────────────────────────
        //
        // Adversarial review: naive case-(a) "always preserve when Headers absent" turns the
        // designer's accidental fail-safe (pre-#957: any save silently wiped headers) into
        // credential forwarding — an editor who merely retypes a widget's URL (the ONLY field
        // the designer's REST UI exposes) through the shipped designer, with no
        // headers-editing UI to warn them, would otherwise carry the real header value onto
        // whatever new host they typed. These tests prove headers only carry over when the
        // widget's REST destination (scheme+host+port) is unchanged.

        [TestMethod]
        public void ReconcileWidgetHeaders_case_a_same_host_different_path_still_preserves_headers()
        {
            // The common case F1 must not break: an editor tweaks the path/query on the SAME
            // host (e.g. a different endpoint on the same vendor API) — headers must still
            // carry over, or the designer's "save without touching headers" flow would drop
            // headers on ANY url edit, not just a cross-host one.
            var incoming = Widgets(headers: null, url: "https://api.vendor.com/v2/other-endpoint");
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "Bearer real-token" },
                url: "https://api.vendor.com/v1/data");

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            warnings.Should().BeEmpty();
            incoming["w1"].Source.RestOptions!.Headers!["Authorization"].Should().Be("Bearer real-token");
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_case_a_cross_host_edit_drops_headers_and_returns_a_warning()
        {
            // The attack script F1 closes: editor retypes the URL to an attacker-chosen host,
            // designer sends no headers key (case a) — headers must NOT follow.
            var incoming = Widgets(headers: null, url: "https://evil.example/collect");
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "Bearer real-token" },
                url: "https://api.vendor.com/v1/data");

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull("a plain URL edit must not be blocked outright — the designer has no way to re-supply headers");
            incoming["w1"].Source.RestOptions!.Headers.Should().BeNull(
                "stored credentials must not silently follow a widget to a different destination");
            warnings.Should().ContainSingle();
            warnings[0].WidgetId.Should().Be("w1");
            warnings[0].OldDestination.Should().Be("https://api.vendor.com:443");
            warnings[0].NewDestination.Should().Be("https://evil.example:443");
            warnings[0].DroppedHeaderCount.Should().Be(1);
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_case_d_cross_host_sentinel_is_rejected_not_silently_restored()
        {
            // Case (d) is the same forwarding risk at per-key granularity: a caller (not the
            // shipped designer, which never round-trips headers at all — a direct API caller)
            // sends the masked sentinel back for a key that DID exist, but for the OLD host.
            // Restoring the real value onto the NEW host would be exactly the same credential
            // forwarding case (a) closes, just reached a different way — routed into case (e)'s
            // reject instead of a silent restore.
            var incoming = Widgets(new Dictionary<string, string> { ["Authorization"] = DashboardCredentialMasking.MaskedHeaderValue },
                url: "https://evil.example/collect");
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "Bearer real-token" },
                url: "https://api.vendor.com/v1/data");

            var (error, _) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().NotBeNull(
                "there is no value stored for the NEW destination to restore — restoring the OLD host's value here would be credential forwarding");
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_destination_comparison_is_case_insensitive_for_host()
        {
            var incoming = Widgets(headers: null, url: "https://API.VENDOR.com/v1/data");
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "Bearer real-token" },
                url: "https://api.vendor.com/v1/data");

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            warnings.Should().BeEmpty("only casing differs — same destination");
            incoming["w1"].Source.RestOptions!.Headers!["Authorization"].Should().Be("Bearer real-token");
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_destination_comparison_normalizes_default_https_port()
        {
            var incoming = Widgets(headers: null, url: "https://api.vendor.com:443/v1/data");
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "Bearer real-token" },
                url: "https://api.vendor.com/v1/data"); // no explicit port

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            warnings.Should().BeEmpty("https://h and https://h:443 are the same destination");
            incoming["w1"].Source.RestOptions!.Headers!["Authorization"].Should().Be("Bearer real-token");
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_destination_comparison_normalizes_default_http_port()
        {
            var incoming = Widgets(headers: null, url: "http://api.vendor.com:80/v1/data");
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "Bearer real-token" },
                url: "http://api.vendor.com/v1/data"); // no explicit port

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            warnings.Should().BeEmpty("http://h and http://h:80 are the same destination");
            incoming["w1"].Source.RestOptions!.Headers!["Authorization"].Should().Be("Bearer real-token");
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_same_host_but_different_port_is_a_different_destination()
        {
            var incoming = Widgets(headers: null, url: "https://api.vendor.com:8443/v1/data");
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "Bearer real-token" },
                url: "https://api.vendor.com/v1/data"); // default 443

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            incoming["w1"].Source.RestOptions!.Headers.Should().BeNull("different port is a different destination");
            warnings.Should().ContainSingle();
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_cross_scheme_is_a_different_destination()
        {
            var incoming = Widgets(headers: null, url: "http://api.vendor.com/v1/data");
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "Bearer real-token" },
                url: "https://api.vendor.com/v1/data");

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            incoming["w1"].Source.RestOptions!.Headers.Should().BeNull("http vs https is a different destination even for the same host");
            warnings.Should().ContainSingle();
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_unparseable_incoming_url_never_preserves_headers()
        {
            // Malformed-URL case (decide, don't assume): an incoming Url that fails to parse
            // must be treated as "not the same destination", never fall into the preserve
            // branch on an ambiguous comparison.
            var incoming = Widgets(headers: null, url: "not a url at all");
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "Bearer real-token" });

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            incoming["w1"].Source.RestOptions!.Headers.Should().BeNull(
                "an unparseable incoming URL must never be treated as 'same destination'");
            warnings.Should().ContainSingle();
            warnings[0].NewDestination.Should().Be("(unparseable or missing URL)");
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_empty_incoming_url_never_preserves_headers()
        {
            var incoming = Widgets(headers: null, url: ""); // RestWidgetDataSourceOptions.Url defaults to ""
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "Bearer real-token" });

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            incoming["w1"].Source.RestOptions!.Headers.Should().BeNull();
            warnings.Should().ContainSingle();
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_unparseable_existing_url_never_preserves_headers()
        {
            // Defensive: an existing RestOptions.Url that somehow doesn't parse (or is empty)
            // must also never be treated as "same destination" as a valid incoming one.
            var incoming = Widgets(headers: null, url: "https://api.vendor.com/v1/data");
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "Bearer real-token" }, url: "");

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            incoming["w1"].Source.RestOptions!.Headers.Should().BeNull();
            warnings.Should().ContainSingle();
            warnings[0].OldDestination.Should().Be("(unparseable or missing URL)");
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_no_existing_widget_at_all_drops_silently_without_a_warning()
        {
            // No prior widget (e.g. brand-new widget id on Update, or Create) — nothing to warn
            // about even though the destination technically "differs" (there is no old one).
            var incoming = Widgets(headers: null, url: "https://api.vendor.com/v1/data");

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existingWidgets: null);

            error.Should().BeNull();
            incoming["w1"].Source.RestOptions!.Headers.Should().BeNull();
            warnings.Should().BeEmpty("nothing was actually dropped — there was never an existing value");
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_cross_host_with_no_existing_headers_does_not_warn()
        {
            // Destination changed, but the existing widget never had any headers to begin
            // with — nothing meaningful was dropped, so no warning should fire (avoids log noise).
            var incoming = Widgets(headers: null, url: "https://evil.example/collect");
            var existing = Widgets(headers: null, url: "https://api.vendor.com/v1/data");

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            warnings.Should().BeEmpty("there were no stored headers to drop");
        }

        // ── F4 (#960): sixth case — RestOptions itself absent ───────────────────

        [TestMethod]
        public void ReconcileWidgetHeaders_case_f_missing_RestOptions_for_a_rest_widget_preserves_existing_RestOptions()
        {
            // The exact inverse of case (a), one level up: the incoming widget still declares
            // Kind "rest" but sends no RestOptions node at all. Absence in a partial update
            // means "didn't touch this", not "delete it" — so the WHOLE existing RestOptions
            // (URL and headers) must be restored, not silently wiped.
            var incoming = new Dictionary<string, WidgetDefinition>
            {
                ["w1"] = new WidgetDefinition
                {
                    Type = "chart",
                    Source = new WidgetSourceDefinition { Kind = "rest" } // no RestOptions at all
                }
            };
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "Bearer real-token" },
                url: "https://api.vendor.com/v1/data");

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            warnings.Should().BeEmpty();
            incoming["w1"].Source!.RestOptions.Should().NotBeNull(
                "a 'rest' widget saved with no RestOptions node at all must not silently wipe its stored REST config");
            incoming["w1"].Source!.RestOptions!.Url.Should().Be("https://api.vendor.com/v1/data");
            incoming["w1"].Source!.RestOptions!.Headers!["Authorization"].Should().Be("Bearer real-token");
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_case_f_preserved_RestOptions_is_a_copy_not_an_alias()
        {
            var incoming = new Dictionary<string, WidgetDefinition>
            {
                ["w1"] = new WidgetDefinition
                {
                    Type = "chart",
                    Source = new WidgetSourceDefinition { Kind = "rest" }
                }
            };
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "Bearer real-token" });
            var existingRestOptionsRef = existing["w1"].Source.RestOptions;

            var (error, _) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            incoming["w1"].Source!.RestOptions.Should().NotBeSameAs(existingRestOptionsRef,
                "preserving RestOptions wholesale must clone it, not alias the existing (possibly cache-shared) instance");
            incoming["w1"].Source!.RestOptions!.Headers.Should().NotBeSameAs(existingRestOptionsRef!.Headers);
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_non_rest_widget_without_RestOptions_is_a_no_op()
        {
            // Renamed from "..._ignores_widgets_without_RestOptions", which asserted only
            // error == null and never pinned the actual (wipe) behaviour in either direction —
            // the name claimed something the test didn't check. This is the boundary case (f)
            // does NOT apply to: Kind is "analysis", not "rest", so RestOptions genuinely has
            // no meaning here and there is nothing to preserve or wipe either way.
            var incoming = new Dictionary<string, WidgetDefinition>
            {
                ["w1"] = new WidgetDefinition { Type = "kpi", Source = new WidgetSourceDefinition { Kind = "analysis" } }
            };

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existingWidgets: null);

            error.Should().BeNull();
            warnings.Should().BeEmpty();
            incoming["w1"].Source!.RestOptions.Should().BeNull("a non-'rest' widget has no RestOptions to preserve");
        }

        [TestMethod]
        public void ReconcileWidgetHeaders_kind_changed_away_from_rest_correctly_wipes_RestOptions()
        {
            // Pins the boundary explicitly: switching a widget's Kind AWAY from "rest" (e.g.
            // the designer's own sourceKind dropdown) and sending no RestOptions is the
            // legitimate "I no longer want REST config on this widget" case — case (f) must
            // NOT apply here, or a caller could never actually get rid of a REST widget's config.
            var incoming = new Dictionary<string, WidgetDefinition>
            {
                ["w1"] = new WidgetDefinition
                {
                    Type = "kpi",
                    Source = new WidgetSourceDefinition { Kind = "analysis", ListVmType = "Foo" } // switched away from "rest"
                }
            };
            var existing = Widgets(new Dictionary<string, string> { ["Authorization"] = "Bearer real-token" });

            var (error, warnings) = DashboardCredentialMasking.ReconcileWidgetHeaders(incoming, existing);

            error.Should().BeNull();
            warnings.Should().BeEmpty();
            incoming["w1"].Source!.RestOptions.Should().BeNull(
                "Kind no longer 'rest' — dropping RestOptions here is intentional, not the F4 defect");
        }
    }

    /// <summary>
    /// Issue #957, task item 3: empirically confirms the System.Text.Json binding behaviour
    /// <see cref="DashboardCredentialMasking.ReconcileWidgetHeaders"/>'s case (a)/(b) split
    /// depends on. Before this fix, <c>RestWidgetDataSourceOptions.Headers</c> had type
    /// <c>Dictionary&lt;string, string&gt;</c> with a <c>= new()</c> default — an absent JSON
    /// key and an explicit <c>{}</c> both bound to the SAME (empty, non-null) dictionary,
    /// making (a) "preserve" and (b) "clear" indistinguishable. The property is now
    /// <c>Dictionary&lt;string, string&gt;?</c> with no default initializer specifically so
    /// these three bindings differ, per this test class.
    /// </summary>
    [TestClass]
    public class RestWidgetDataSourceOptionsHeadersBindingTests
    {
        [TestMethod]
        public void Absent_headers_key_binds_to_null()
        {
            var json = "{\"Url\":\"https://example.com/api\"}"; // no "Headers" key at all
            var opts = JsonSerializer.Deserialize<RestWidgetDataSourceOptions>(json);

            opts.Should().NotBeNull();
            opts!.Headers.Should().BeNull(
                "System.Text.Json never touches a property whose JSON key is absent, so it stays at its no-initializer default (null)");
        }

        [TestMethod]
        public void Explicit_null_headers_binds_to_null()
        {
            var json = "{\"Url\":\"https://example.com/api\",\"Headers\":null}";
            var opts = JsonSerializer.Deserialize<RestWidgetDataSourceOptions>(json);

            opts.Should().NotBeNull();
            opts!.Headers.Should().BeNull();
        }

        [TestMethod]
        public void Empty_headers_object_binds_to_an_empty_non_null_dictionary()
        {
            var json = "{\"Url\":\"https://example.com/api\",\"Headers\":{}}";
            var opts = JsonSerializer.Deserialize<RestWidgetDataSourceOptions>(json);

            opts.Should().NotBeNull();
            opts!.Headers.Should().NotBeNull(
                "an explicit {} must be distinguishable from an absent/null key");
            opts.Headers.Should().BeEmpty();
        }

        [TestMethod]
        public void Populated_headers_object_binds_to_a_dictionary_with_those_entries()
        {
            var json = "{\"Url\":\"https://example.com/api\",\"Headers\":{\"Authorization\":\"Bearer x\"}}";
            var opts = JsonSerializer.Deserialize<RestWidgetDataSourceOptions>(json);

            opts.Should().NotBeNull();
            opts!.Headers.Should().NotBeNull();
            opts.Headers!["Authorization"].Should().Be("Bearer x");
        }

        // ── Coordinator follow-up: does a persisted null Headers stay "preserve" on the NEXT
        // save/reload, or does the round trip silently turn it into "clear"? ──────────────────
        //
        // ReconcileWidgetHeaders' case (a) can leave RestOptions.Headers explicitly null on an
        // object about to be persisted (no existing headers to preserve). JsonFileDashboardService
        // persists with JsonSerializer.SerializeAsync(fs, dashboard) and reads with
        // JsonSerializer.DeserializeAsync<DashboardDefinition>(fs) — both plain, no
        // JsonSerializerOptions passed, i.e. the exact calls these two tests reproduce. If a
        // persisted null ever silently became "{}" on the next read, case (a) "preserve" would be
        // indistinguishable from case (b) "clear" the moment a widget with no headers is saved and
        // reloaded once — a real bug. Verified below, not assumed.

        [TestMethod]
        public void Serializing_a_null_Headers_value_produces_an_explicit_JSON_null_not_omission()
        {
            var opts = new RestWidgetDataSourceOptions { Url = "https://example.com/api", Headers = null };

            var json = JsonSerializer.Serialize(opts);

            json.Should().Contain("\"Headers\":null",
                "JsonFileDashboardService's actual persistence calls (CreateAsync/UpdateAsync) use " +
                "JsonSerializer.SerializeAsync with default options — DefaultIgnoreCondition defaults " +
                "to Never, so a null property is written out explicitly, not omitted");
        }

        [TestMethod]
        public void A_serialized_null_Headers_round_trips_back_to_null_not_an_empty_dictionary()
        {
            var opts = new RestWidgetDataSourceOptions { Url = "https://example.com/api", Headers = null };
            var json = JsonSerializer.Serialize(opts);

            var roundTripped = JsonSerializer.Deserialize<RestWidgetDataSourceOptions>(json);

            roundTripped.Should().NotBeNull();
            roundTripped!.Headers.Should().BeNull(
                "if a persisted null silently became {} on read, ReconcileWidgetHeaders' case (a) " +
                "'preserve existing headers' would be indistinguishable from case (b) 'clear all " +
                "headers' after exactly one save-reload cycle with no existing headers to preserve — " +
                "verified clean here, not assumed");
        }
    }
}
