# 更新日志

## [Unreleased]

### Added
- **Analysis Mode: `AnalysisLimits` configurable result/raw-row caps** — Lifts the previously-hardcoded `10,000` (group result rows) and `50,000` (raw materialize rows) constants into a single `AnalysisLimits` static class so apps with bigger reporting surfaces (monthly SKU breakdowns, multi-tenant aggregates, year-end pivots) can raise the caps without forking the framework. `AnalysisLimits.MaxResultRows` (default 10,000) is read by both `InProcessGroupByStrategy` and `ServerSideGroupByStrategy` as well as the engine-level truncation guard, AND tracks the upper bound for `AnalysisQueryRequest.TopN` validation — raising the cap automatically widens the legal TopN range so the two settings always describe the same dimension. `AnalysisLimits.MaxMaterializeRows` (default 50,000) is the in-process raw-data load cap. Set-once-at-startup; tests override per case via `try / finally`. Defaults preserve the values WTM shipped through 10.4.x so callers that don't touch the class get identical behaviour. Closes the doc'd "Excel exports limited to 10,000 rows" Phase 1 limitation. 6 new tests cover defaults, lowered/raised result-row caps, TopN range tracking, and the materialize-row wrapper. Targeted Analysis regression: 504/504 green.
- **Analysis Mode: CSV exporter renders `GrandTotalRow` as footer line** — Pairs with the previous Excel-exporter change so `xlsx` and `csv` agree on what a "Total" row looks like (was: dashboard had total → Excel had total → CSV silently dropped it). `_AnalysisController.BuildCsv()` now appends a footer when `result.GrandTotalRow != null`: first `null` dimension cell carries the "總計" label, subsequent `null` dimension cells stay blank, `null` measure cells render as empty strings (not `"0"` — same anti-misleading rule as the Excel exporter), real measure values pass through `ToString()` and the existing CSV escaper. Defense-in-depth: even if a caller somehow lands a string starting with `=` / `+` / `-` / `@` in the grand-total cell, the same CSV-formula-injection escape (RFC 4180 + leading-tab) kicks in. Default behaviour (`GrandTotalRow == null`) is unchanged — every existing csv export keeps the previous bytes. Metadata block + footer coexist correctly.
- **Analysis Mode: Excel exporter renders `GrandTotalRow` as bolded yellow footer** — Closes the loose end from the previous `IncludeGrandTotal` change: the JSON response carried the rollup but `AnalysisExcelExporter.Export()` ignored it, so users got a total in the dashboard but a totals-less spreadsheet — exactly the scenario the feature was meant to eliminate. Now when `GrandTotalRow != null` the exporter appends an extra row after the body data with a light-yellow fill and bold font (eye-catches without overstyling), the first `null`-dimension cell carries the "總計" label, subsequent `null`-dim cells stay blank to mirror how Excel users typically write manual total rows by hand. Numeric measure cells render as `Numeric` cells (so Excel SUM / formatting still applies) and inherit the column's `MeasureFormat` (currency / percent / etc. stay consistent with the body). `null` measure cells (Avg / DistinctCount, by design) render as clean empty strings rather than `0` (which would falsely suggest "average is zero"). The chart anchor is shifted down one row when the footer is present so embedded charts still sit cleanly below the data. Default behaviour (`GrandTotalRow == null`) is unchanged — every existing exporter caller keeps the previous output bytes.
- **Analysis Mode: `IncludeGrandTotal` + `GrandTotalRow` for footer-row reporting** — Closes the dashboard pain point that report consumers always want a "grand total" footer row but the response had no slot for it; every WTM app re-implemented the rollup in JS / Excel post-processing. New `AnalysisQueryRequest.IncludeGrandTotal: bool` (default `false` — back-compat). When `true`, `AnalysisQueryResponse.GrandTotalRow` is populated. Aggregation rules per measure: `Sum` / `Count` → sum of group values; `Max` → max; `Min` → min; `Avg` / `DistinctCount` → `null` (a meaningful weighted Avg requires per-group sample counts the GroupBy result drops; per-group distincts can't be summed without re-querying — silent wrong answers are worse than `null`). Dimension cells emit `null` so the client is free to append "Total" / "總計" anywhere it fits the rendering surface. Total scope is **post-HAVING / pre-TopN** — same universe as `TotalCount`, so "showing 3 of 5 (sum 1,234)" stays consistent regardless of how many rows TopN trims off the visible list. Empty-result case still emits `GrandTotalRow` with `null` measure cells (rather than `0`, which would be misleading) so the footer can render an empty state. Both sync `Execute` and async `ExecuteAsync` paths wired identically.
- **Analysis Mode: `HavingFilters` post-aggregation filter** — Closes the BI gap that the analysis surface had `Filters` (pre-aggregation, equivalent to SQL `WHERE`) and Sort/TopN (post-aggregation), but no way to express the canonical "regions where Sum(Amount) ≥ 1M" / "categories with > 100 distinct customers" question — apps had to fetch all groups and filter client-side, then risk losing the genuine survivors at the 10,000-row truncation. New optional `AnalysisQueryRequest.HavingFilters: List<HavingFilter>?`. Each `HavingFilter` carries `Field` (must be a `{measure.Field}_{measure.Func}` of a requested measure — same naming as `Sort` and `AnalysisQueryResponse.Columns`), `Operator` (numeric subset of `FilterOperator`: `Eq` / `NotEq` / `Gt` / `Gte` / `Lt` / `Lte`), and `Value` (parsed to `decimal` with `InvariantCulture`). Multiple filters AND together. Validation rejects out-of-whitelist fields, empty fields, and unsupported operators (`Contains` / `In` / etc. — they don't apply to scalar aggregate values). Execution pipeline now matches SQL standard: `GROUP BY → HAVING → ORDER BY → LIMIT`. `TotalCount` reflects the **post-having** group cardinality, so the client can render "showing 3 of 5" against the user's filtered universe rather than the pre-filter baseline. Conservative on malformed values: an unparsable `Value` filters out all groups (rather than silently widening). Default request (no `HavingFilters`) is unchanged — every existing caller keeps the previous engine behaviour. Both sync `Execute` and async `ExecuteAsync` paths wired identically.
- **Security: `UseWtmCspReport()` server-side CSP-violation report endpoint** — Closes #845. Pairs with `WtmCspOptions.ReportUri`: when the operator points the directive at `/_csp/report` (default), the middleware now actually has somewhere to land. Reads the browser-posted JSON (canonical `{"csp-report": { ... }}` envelope or the looser flat shape some non-Chromium clients emit), parses it into a strongly-typed `CspViolationReport` (kebab-case property mapping via `[JsonPropertyName]`), and either invokes `WtmCspReportOptions.OnReport` callback or emits a structured `Warning` Serilog line `CspViolation Document=… Directive=… Blocked=… Source=…:…` so existing log pipelines become the policy-violation observability surface — no Sentry / Datadog adapter required out of the box. Defenses against amplification: per-IP sliding-60-second rate limit (default 10/min, returns 429 when exceeded; set 0 to disable), 8 KiB body cap (returns 413 when exceeded), POST-only method gate (other verbs fall through so the path can host other handlers if reused), `OnReport` callback exceptions are caught and logged so a faulty dispatcher cannot 500 the report endpoint and leak retry traffic. Browser receives `204 No Content` on success, `400` on malformed body, `429` when rate-limited, `413` when oversized. `WtmCspReportMiddleware.ParseReport` and `CheckAndRecordRate` are public for unit-test determinism. Opt-in via `app.UseWtmCspReport()` placed before `UseWtmContentSecurityPolicy()`. Zero breaking change.
- **Security: `WtmCspMode` three-state mode for CSP middleware** — Closes #846. Replaces the boolean `WtmCspOptions.ReportOnly` with a `WtmCspMode` enum (`Disabled` / `ReportOnly` / `Enforce`, default `Enforce`). The new `Disabled` state is the operationally-essential third option that lets ops kill the policy via `appsettings.{Env}.json` toggle without ripping out the `app.UseWtmContentSecurityPolicy()` call — incident response, dev debugging, canary rollouts. The legacy `ReportOnly` bool stays in place with `[Obsolete]` so existing callers keep working unchanged; `Mode` wins when set non-default. Middleware short-circuits to a true no-op when `Mode == Disabled` (no header, no `OnStarting` registration). `WtmCspMiddleware.ResolveHeaderName()` is public for unit-test determinism.
- **Security: `WtmCspOptions.FrameAncestors` clickjacking defense** — Closes #844. Adds the modern `frame-ancestors` directive, the CSP-native clickjacking control that supersedes the legacy `X-Frame-Options` header (per OWASP Clickjacking Cheat Sheet + MDN). Default `'none'` for safe-by-default; set `'self'` for legitimate self-embedding (modal preview, admin sub-frames). When `null` the directive is omitted from the emitted header. `frame-src` (outward) and `frame-ancestors` (inward) coexist — they answer opposite questions.
- **Security: `WtmSecureHeadersOptions.Overwrite` defense-in-depth flag** — Closes #843. Default `false` keeps the existing first-writer-wins semantics. Set `true` for banking / healthcare / regulated apps that must guarantee certain headers regardless of upstream proxy configuration — typical scenario: proxy injected `X-Frame-Options: SAMEORIGIN` but the app needs to enforce `DENY`. `Overwrite = true` does NOT bypass the HTTPS-only HSTS gate (HSTS still requires `Request.IsHttps`), and disabled headers (`Options.X = null`) stay disabled — the flag only flips the precedence, not the disable contract. New `WtmSecureHeadersMiddleware.SetHeader()` is public for unit-test determinism.
- **Analysis Mode: `DistinctCount` aggregate function** — Closes the long-standing pain point that the analysis surface had `Count` (rows per group) and `Sum`/`Avg`/`Max`/`Min` (numeric reductions) but no way to ask the canonical BI question "how many *unique* customers in each region?" or "how many distinct products were sold per category?" — apps had to issue two separate queries and stitch the result. New `AggregateFunc.DistinctCount = 32` flag, valid in `[Measure(AllowedFuncs = ...)]` like every other aggregate, with display name "不重複計數". Both groupby strategies implement it: `InProcessGroupByStrategy` runs `g.Select(propGet).Where(non-null).Distinct().Count()` on the materialised group (handles non-numeric values gracefully — important for FK-id columns); `ServerSideGroupByStrategy` emits the canonical EF Core 8+ pattern `g.Select(e => e.Prop).Distinct().Count()` which translates to SQL `COUNT(DISTINCT col)` — pushed down to the database for the entire row set, no in-memory materialisation. NULLs are excluded from both paths (matches ANSI-SQL `COUNT(DISTINCT)` semantics). Result column follows the existing `{Field}_{Func}` naming so the new `Sort` field can target a `CustomerId_DistinctCount` column. `_AnalysisController.GetAllowedFuncNames()` exposes "DistinctCount" to the front-end picker. Existing 5 aggregate functions unchanged. 6 tests cover flag-bit independence, in-process per-group + null-exclusion, EF Core server-side round-trip, AllowedFuncs gating, and Sort integration. Targeted Analysis suite (QueryEngine, both GroupBy strategies, Sort+TopN, RelativeDateTokens, DistinctCount): 150/150 green.
- **Analysis Mode: 9 new relative-date filter tokens** — Closes the dashboard pain point where the filter UI's "this week / last 30 days" presets stopped at 8 tokens; common BI questions like "yesterday's exceptions" or "Q3 vs Q4" required hand-rolled date pairs. New tokens (case-insensitive, like the existing set): `@yesterday` (single day = today − 1), `@nextWeek` / `@nextMonth` (forecast windows for ops dashboards / scheduled jobs), `@last7days` / `@last90days` / `@last365days` (rolling windows alongside the existing `@last30days`), `@lastQuarter` (full previous calendar quarter — distinct from `@thisQuarter` which is to-date semantics), `@thisYear` (Jan 1 through Dec 31 — distinct from `@ytd` which clamps to today), `@lastYear` (full previous calendar year). Same emit semantics as the pre-existing tokens — each expands to a `(Gte, Lte)` pair on the original filter's field, so server-side type conversion + Expression-tree assembly are unchanged. 13 tests cover each new token's bounds, the `@thisYear` ↔ `@ytd` distinction (matters except on Dec 31), the `@lastQuarter` 3-month length invariant, case-insensitivity, backwards compatibility on all 8 pre-existing tokens, and the unknown-token error message. Pre-existing 8 token tests remain green. Zero breaking change — additive switch arms only.
- **Analysis Mode: result `Sort` + `TopN` on `AnalysisQueryRequest`** — Closes the long-standing gap that the analysis surface had no way to express "top 5 regions by sales DESC" — callers had to fetch up to 10,000 rows and sort client-side, then risk having the genuine top-N truncated by the row cap. Two new optional fields on `AnalysisQueryRequest`: `Sort: List<SortSpec>?` (each `SortSpec` carries `Field` + `Descending`; multi-key sort by repeating); `TopN: int?` (1‒10,000). `Sort.Field` must reference either a requested dimension or a measure-result column (`{Field}_{Func}`, e.g. `Amount_Sum`) — same naming as `AnalysisQueryResponse.Columns`. Out-of-whitelist sort fields are rejected at validation with a clear error so callers can't smuggle in fields the engine never selected; out-of-range `TopN` (≤0 or >10,000) is also rejected. `TopN` trims `Rows` but `TotalCount` continues to report the full underlying group cardinality, so the client can render "showing 3 of 5". `SortValueComparer` handles the mixed-type measure values cleanly: numeric types unify via `decimal`, dates compare directly, mixed-type pairs fall back to ordinal string compare so the sort is deterministic instead of throwing. Default request (no `Sort`, no `TopN`) is unchanged — every existing caller keeps the previous engine behaviour. Both sync `Execute` and async `ExecuteAsync` paths wired identically.
- **Reliability: `UseWtmRequestTimeouts()` per-request deadline middleware** — Natural pair with `UseWtmSlowRequestLogging()` (#840): the latter logs slow outliers, this kills runaways. Replaces `IHttpRequestLifetimeFeature` with a deadline-linked `CancellationToken` so EF Core / `HttpClient` / `Task.Delay(_, ct)` and any other framework that observes `HttpContext.RequestAborted` cancel cooperatively. When the deadline trips before the response started, the middleware writes a `504 Gateway Timeout` with `application/problem+json` (`type` / `title` / `status` / `detail` / `timeoutMs` / `traceId`) and emits a structured `Warning` Serilog line `RequestTimeout Path={Path} Method={Method} TimeoutMs={Timeout}`. If the response has already started (chunked stream, SSE), the connection is aborted instead — the only correct option since we can't retroactively send a 504 over a partially-written body. `WtmRequestTimeoutsOptions`: `DefaultTimeoutMs` (default 30 s — generous for ordinary CRUD, tight enough that a runaway query / external call doesn't park a thread for minutes; set to 0 to disable the global default), `PathOverrides` (longest-prefix-wins; e.g. `/api/export` → 5 min, `/api/admin/migrate` → 10 min, `/sse` → 0 to opt out for streaming endpoints), `PathExclusions` (defaults match the rest of the bundle: `/healthz`, `/_framework`, `/_js`, `/_content`, `/favicon.ico`). Even when a handler swallows the cancellation and returns normally, the middleware still converts an empty 200 to a 504 — a misbehaving handler can't accidentally hide a deadline trip from observability. Distinguishes "deadline killed handler" from "client gave up first" (both surface as `OperationCanceledException`) by tracking whether the original `RequestAborted` cancelled separately — only the former produces a 504. Opt-in via `app.UseWtmRequestTimeouts()` immediately after `UseRouting`. Zero breaking change.
- **Caching: `[WtmNoCache]` + `[WtmCacheControl]` response-cache-control attributes** — Two small, developer-facing attributes covering the opposite halves of HTTP cache-control: suppression and explicit declaration. `[WtmNoCache]` emits the full defence-in-depth suppression bundle on the decorated controller/action — `Cache-Control: no-store, no-cache, must-revalidate, max-age=0, private`, `Pragma: no-cache` (HTTP/1.0 fallback still found in enterprise proxies), `Expires: 0` — fixing the classic "after logout the browser back-button still shows the profile page" production bug in one line. `[WtmCacheControl("public, max-age=300")]` sets an explicit `Cache-Control` value verbatim (no parsing — callers own RFC-7234 correctness); optional `Vary = "Accept-Encoding, Authorization"` property adds the paired `Vary` header so intermediaries know what to key on. Both implement `IAsyncResultFilter` so MVC auto-discovers them — no `Program.cs` registration. Headers are written via `Response.OnStarting` so they land even when downstream result writers short-circuit. First-writer-wins: an upstream-set `Cache-Control` (reverse proxy, more-specific sibling filter) is preserved untouched. `[WtmCacheControl]` ctor rejects null/whitespace values with a clear `ArgumentException` so a typo is caught at startup. Zero breaking change.
- **Security: `[WtmIpAllowList]` CIDR-based IP allow-list attribute** — Decorate a controller or action with `[WtmIpAllowList("10.0.0.0/8", "192.168.0.0/16", "172.16.0.0/12")]` and any request whose resolved client IP falls outside every supplied CIDR short-circuits with `403 Forbidden` before the action body runs. Multiple CIDRs are OR'd together; single-host rules use `/32` (IPv4) or `/128` (IPv6). Backed by `System.Net.IPNetwork` so IPv4 and IPv6 work uniformly. Implements `IAsyncAuthorizationFilter` so MVC auto-discovers it — zero Program.cs wiring. `FallbackStatusCode` lets callers switch to `404` (hide endpoint existence) / `451` / custom codes. Rejections are logged at `Warning` with the resolved IP and the CIDR list so the "who got blocked?" question is answerable from the normal log pipeline. Input CIDRs are validated at attribute-construction time (malformed strings throw `ArgumentException` with the offending index + value at the `[WtmIpAllowList(...)]` call site — surfaces the typo at startup, not on first request). Tolerates reverse-proxy quirks: accepts "addr, addr2" comma-lists from `X-Forwarded-For` by using the first entry (matches `GetRemoteIpAddress()` convention). Security caveat documented inline: `X-Forwarded-For` is trivially spoofable when the app is not behind a trusted proxy — combine with network-layer firewalling / auth tokens for public-internet exposure. Value is defense-in-depth: even when nginx / Cloudflare / ALB already enforces the same allow-list at the edge, carrying the rule in code means a proxy misconfiguration during a rushed deploy doesn't silently drop the control.
- **Rapid dev: `WtmDataSeeder` idempotent fixture seeder** — `await WtmDataSeeder.SeedAsync(dc, items, x => x.Code)` inserts every row whose business key (any `Expression<Func<TEntity, TKey>>`: string / int / Guid / …) is not already present in the table and returns a `WtmSeedResult(Added, Skipped)` report. Re-running the same seed call — whether from `Program.cs`, a test `[TestInitialize]`, a migration job, or a demo-restore hook — yields zero inserts, so "idempotent seed on every startup" becomes a one-liner. Existence check is a single `SELECT … WHERE Key IN (…)` round trip instead of per-item EXISTS, so seeding hundreds of fixture rows costs one query plus one `SaveChangesAsync`. `SaveChangesAsync` is called at most once and only when at least one row was actually added, so a pure-skip re-seed never triggers audit interceptors / SaveChanges filters — and never accidentally persists dirty in-memory state that happened to be attached to the DbContext. Duplicate keys *within* the input array collapse to the first occurrence (safe against cut-and-paste mistakes in fixture data). Insert-only semantics: seeder never updates existing rows (call site retains control over upsert if needed). Ships in `WalkingTec.Mvvm.Core.Helper`; zero API surface added to `DataContext` / `IDataContext`.
- **Bandwidth: `UseWtmETag()` conditional-request middleware** — Attaches a SHA-256-based `ETag` header to 2xx `GET` / `HEAD` responses and returns `304 Not Modified` (body-less) when the client sends a matching `If-None-Match`. Saves bandwidth on dashboard / reference-data / admin-list endpoints whose contents change only when someone actually mutates data — the server still computes the response (no compute savings vs. OutputCache), but the wire only carries ~300 bytes of headers instead of the full payload. `WtmETagOptions`: `EligibleMethods` (default `GET`+`HEAD` — `POST`/`PUT` ETag-ing is a footgun per RFC 9110 §15.4 and intentionally unsupported), `PathExclusions` (defaults match the other middleware bundles: `/healthz`, `/_framework`, `/_js`, `/_content`, `/favicon.ico` — static pipelines have their own strong ETags), `MaxBufferedBytes` (default 2 MiB — larger responses pass through unchanged so a rogue payload can't turn the middleware into a memory hog), `EmitWeakETag` (default `false`; switch on when downstream compression / minification may alter bytes without changing semantics). Matching follows RFC 9110 §13.1.2 / §8.8.3.2: wildcard `*` always matches, comma-separated list of tags, `W/` prefix ignored on both sides (weak comparison). Upstream-set `ETag` headers are preserved (first-writer-wins) so a reverse proxy / CDN that already computed a stronger hash wins. Hash is base64url + 22-char truncation → 132 bits of entropy, plenty of collision resistance per endpoint. Opt-in via `app.UseWtmETag()` after `UseRouting` and before compression (so the hash runs on the uncompressed representation). Zero breaking change.
- **Reliability: `[WtmIdempotent]` + `UseWtmIdempotency()` idempotency-key middleware** — Retry-safe mutating endpoints without the action author having to think about duplicate-submit. Decorate an action (or controller) with `[WtmIdempotent(WindowSeconds = 300, RequireKey = false)]`; when the client sends the same `Idempotency-Key` header within the window the middleware replays the cached 2xx response verbatim (status + content-type + body) and adds `Idempotency-Replay: true` so tests / clients can distinguish a cache hit from a fresh invocation. Follows the Stripe / PayPal / AWS convention (draft-ietf-httpapi-idempotency-key-header) — most modern API SDKs already send the header, so wiring this middleware retroactively makes existing endpoints mobile-retry-safe. `WtmIdempotencyOptions`: `HeaderName` (default `Idempotency-Key`), `DefaultWindowSeconds` (300, used when `WindowSeconds <= 0` on the attribute), `MaxKeyLength` (128 — UUID/ULID fit; prevents key-based cache bloat), `EligibleMethods` (`POST`/`PUT`/`PATCH`/`DELETE` — `GET`/`HEAD` intentionally excluded as already RFC-9110 safe+idempotent, caching would mask bugs), `MaxCachedBodyBytes` (1 MiB — larger 2xx pass through but are not cached). Cache key is scoped by `method + path + idempotency key` so a stolen key cannot replay a response captured on a different endpoint. Key characters are allow-listed (letters/digits/`-_.:`) to reject control chars / UTF-8 / log-injection. Only 2xx responses are cached — 4xx / 5xx pass through so a transient failure cannot poison the slot. Response headers other than `Content-Type` (e.g. `Set-Cookie`) are NOT replayed, preventing a previous caller's session from leaking into a retry. Missing key on a non-`RequireKey` endpoint is a no-op (best-effort); `RequireKey = true` + missing returns `400 application/problem+json` with the header name echoed in `detail`. Malformed / oversized keys also return 400. Backing store is `IMemoryCache` (in-process; multi-instance deployments need sticky LB or a distributed wrap). Opt-in via `app.UseWtmIdempotency()` after `UseRouting`. Zero breaking change.
- **Feature flags: `IWtmFeatureFlags` service + `[WtmFeatureGate]` MVC attribute** — First-class feature-flag primitive for WTM apps without pulling in `Microsoft.FeatureManagement` or an external SaaS adapter (LaunchDarkly, Unleash, etc.). Register with `services.AddWtmFeatureFlags()` (optionally `AddWtmFeatureFlags(opt => { opt.Defaults["new-checkout"] = true; opt.Resolver = (ctx, name) => ...; })`). Resolution order is deterministic and testable: (1) `WtmFeatureFlagsOptions.Resolver` delegate — per-request override point for tenant / canary / user-id rollouts or external-SDK adapters; returning `null` means "no opinion, fall through"; (2) `IConfiguration` binding at `FeatureFlags:<name>` — configuration-bound values are re-read on every call so `appsettings.json` watcher / `IConfigurationRoot.Reload()` changes propagate without restart; (3) `Options.Defaults` static dictionary (case-insensitive by default); (4) `false` — fail-closed for undeclared names. Resolver exceptions are caught and logged at `Warning` so a flag-service outage cannot take down production. `Snapshot()` enumerates every known flag with its currently-resolved value for admin diagnostic pages. `IHttpContextAccessor` is optional so background services / hosted jobs can consume flags without a request scope.\
`[WtmFeatureGate("new-checkout")]` decorates an action or controller; when the flag is disabled the action short-circuits with `404 Not Found` (default — gives attackers / curious users zero signal that a gated endpoint exists) and `FallbackStatusCode` lets apps switch to 403 / 503 when the intent is "exists but disabled right now". Fails closed when `AddWtmFeatureFlags` hasn't been called (missing service → treat as disabled), so forgetting the registration never accidentally exposes a pre-release endpoint. Zero breaking change — purely additive.
- **Observability: `UseWtmServerTiming()` W3C Server-Timing middleware** — Attaches a `Server-Timing: app;dur=<ms>` response header on every non-excluded request so browser DevTools (Chrome → Network → Timing → "Server Timing") surfaces per-request backend latency without wiring an APM. Complementary to `UseWtmSlowRequestLogging()` (#840): the former logs to the backend aggregator, the latter surfaces the same number in-band for front-end devs. `WtmServerTimingOptions`: `MetricName` (default `"app"`), `Description` (optional, rendered as quoted `desc="..."` with backslash + double-quote escaped), `MinDurationMs` (default 0 — emit always; raise to 200 to tag only slow outliers), `PathExclusions` (defaults match slow-request logging: `/healthz`, `/_framework`, `/_js`, `/_content`, `/favicon.ico`). Excluded paths pay zero stopwatch cost (short-circuits before `Stopwatch.StartNew`). Header attached via `Response.OnStarting` so it lands even when downstream middleware short-circuits. First-writer-wins at the header level — upstream CDN / reverse-proxy `Server-Timing` values are preserved; the middleware `.Append(...)`s its own metric so multiple layers coexist per the W3C comma-list semantics. Metric-name token is sanitised against RFC 7230 (rejects spaces, separators, control chars) — a defective caller value yields empty header rather than malformed output. Decimal formatting is culture-invariant (avoids `12,34` in de-DE locales). Opt-in via explicit `app.UseWtmServerTiming()` placed early after `UseRouting`. Zero breaking change.
- **API lifecycle: `[WtmDeprecated]` endpoint deprecation attribute** — Decorate a controller class or action method with `[WtmDeprecated(Message = "...", Sunset = "2026-12-31", Link = "</api/v2/orders>; rel=\"successor-version\"")]` and every invocation emits the IETF-standard `Deprecation: true` header (per draft-ietf-httpapi-deprecation-header), an RFC 8594 `Sunset: <HTTP-date>` header when a parsable date is supplied, and an RFC 8288 `Link` header pointing at the successor endpoint. The attribute implements `IAsyncResultFilter` so MVC auto-discovers it — no `Program.cs` registration required. Headers are attached via `Response.OnStarting` and respect first-writer-wins (a reverse-proxy or upstream-middleware-set `Deprecation` is preserved). An `Information`-level Serilog entry (`DeprecatedEndpointHit Path=… Method=… Message=… Since=…`) is emitted per hit so ops can query "who's still calling v1?" from the existing log pipeline; opt out with `LogHit = false`. Malformed `Sunset` values are ignored (header simply not emitted, warning logged once per process via lazy-init) instead of crashing the action. Zero breaking change — affects only endpoints that opt in. Fixture controllers in the test suite cover class-level inheritance, per-action override, `Link` emission, and Sunset-date formatting.
- **Ops: `UseWtmMaintenanceMode()` opt-in maintenance-mode middleware** — Planned-outage / deploy-window kill-switch for non-allow-listed traffic. When `WtmMaintenanceModeOptions.Enabled = true` (or the dynamic `IsEnabled` delegate returns true), every request that doesn't hit an `AllowedPathPrefixes` entry or an `AllowedClientIps` entry short-circuits with `503 Service Unavailable` + `Retry-After: 60` + an `application/problem+json` body (`type`, `title`, `status`, `detail`, `retryAfterSeconds`, `traceId`). Default allow-list keeps `/healthz`, `/_framework`, `/_js`, `/_content`, `/_admin` reachable so (a) k8s readiness probes stay green through the outage and (b) operators can still drive the admin plane to toggle maintenance mode off again. `AllowedClientIps` gives ops a bastion / jump-host bypass. `IsEnabled` is a `Func<HttpContext, bool>` — wire it to `IOptionsMonitor<WtmMaintenanceModeOptions>`, a Redis feature flag, or a LaunchDarkly-style service to flip the switch without restarting the app; a throwing delegate is caught and falls back to the static `Enabled` flag so a flag-service outage does not crash production. `ContentType` defaults to `application/problem+json` (aligns with `UseWtmProblemDetails`) but `text/html` is supported via a minimal inline HTML fallback or a custom `HtmlBodyFactory`. When `Enabled` is `false` and no provider is set, the middleware is a single bool check — effectively free. Opt-in via explicit `app.UseWtmMaintenanceMode()` placed after `UseRouting` but before auth / MVC so the 503 short-circuits before expensive middleware runs. Zero breaking change.

## [10.4.0] - 2026-04-18

### Added
- **Observability: `UseWtmSlowRequestLogging()` slow-request middleware** — Wraps the request pipeline with `Stopwatch` and emits a structured `Warning` (or configurable level) log entry whenever elapsed exceeds `ThresholdMs` (default 1000 ms). Structured template `SlowRequest Path={Path} Method={Method} Status={StatusCode} ElapsedMs={Elapsed} User={User} ClientIp={ClientIp}` lets Loki / Seq / Elastic pivot on path / user / IP for a zero-APM "top slow endpoints" dashboard. PII-safe defaults: `IncludeQueryString = false` (tokens / IDs / emails commonly leak via query strings into indexed logs), `IncludeClientIp = true` (already sanitised). Default `PathExclusions` cover `/healthz`, `/_framework`, `/_js`, `/_content`, `/favicon.ico` so a naive 1 s threshold doesn't spam on static assets + health probes. Excluded paths pay zero measurement cost (short-circuits before starting the stopwatch). `LogSanitizer` neutralises CR/LF / control-char log-injection. Opt-in via explicit `app.UseWtmSlowRequestLogging()` — zero breaking change. Manual §10.12. (#840)
- **Security: `UseWtmSecureHeaders()` opt-in hardening headers middleware** — Writes the OWASP Secure Headers Project bundle on every response, sibling to the existing `UseWtmContentSecurityPolicy()` (#789 Phase 3D). Defaults: `X-Content-Type-Options: nosniff`, `X-Frame-Options: SAMEORIGIN`, `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy` denying accelerometer/camera/geolocation/gyroscope/magnetometer/microphone/payment/usb. Any header can be disabled with `opt.X = null` or overridden with a custom value. `Strict-Transport-Security` is **opt-in** (`HstsEnabled = false` default) and only emitted when the request is HTTPS — avoiding the classic HSTS-over-HTTP-during-TLS-rollout foot-gun. HSTS configurables: `HstsMaxAgeSeconds` (default 31536000), `HstsIncludeSubDomains` (default true), `HstsIncludePreload` (default false, opt-in after submitting to hstspreload.org). First-writer-wins at every header so upstream reverse-proxy values (ALB, Cloudflare, nginx) are preserved. Zero breaking change — opt-in via explicit `app.UseWtmSecureHeaders()` call. Manual §10.11. (#838)
- **Observability: framework-owned health checks + JSON response writer** — `AddWtmHealthChecks` previously shipped only a "self" liveness probe; every WTM-based app then reinvented the same `IDataContext.CanConnectAsync` check and the same SRE JSON formatter in its own `Program.cs`. Now:
  - `builder.AddWtmDataContextCheck(name = "datacontext", timeout = 2s, tags = ["ready"])` extension method registers the canonical DB probe. Returns `Unhealthy` with the exception message if the underlying provider rejects the connection; returns `Unhealthy` with a timeout diagnostic if the probe exceeds the configured deadline. Attaches `dbType` / `durationMs` to the entry's `data` for observability dashboards.
  - `WtmHealthCheckResponseWriter.WriteJsonResponse` emits `application/json` with `status`, `totalDurationMs`, and a `checks[]` array containing `name` / `status` / `durationMs` / `description` / `exception` / `data` / `tags` per entry — Kubernetes, Prometheus, and Datadog can scrape structured fields instead of parsing plain text.
  - `UseWtmHealthChecks(useJsonResponse: true)` opts into the JSON writer. Default is `false` so existing apps continue to receive the ASP.NET Core plain-text body (`"Healthy"` / `"Unhealthy"`) without any behaviour change.
  Zero breaking change — both the DB check registration and the JSON writer are opt-in. Manual §14A. (#836)
- **ETL: dry-run preview mode** — New opt-in `EtlPipelineConfig.IsDryRun` flag + `POST /_EtlJob/DryRun?id=&sampleSize=` admin endpoint let operators preview an ETL job without touching the target DB. When `IsDryRun = true`, the executor runs Extract (validates SQL syntax / permissions / column presence) + Transform on the first batch only, then returns extracted row count, first N transformed rows as preview (default 10, up to 100), validation warnings (missing `MergeKeyColumn` in source columns, empty source, etc.), and the watermark value that *would* be committed — but skips `EnsureStagingTable`, `TruncateStaging`, `BulkLoad`, `Merge`, and watermark commit. Bypasses Quartz, never writes an `EtlRunLog` row (no audit-trail pollution). Catches the most common ETL misconfigurations (bad query, wrong merge key, wrong column names) before they damage production data. Zero breaking change — `IsDryRun` defaults false. Manual §8.7.1. (#834)
- **Audit: `AddWtmActionLogRetention(opt)` opt-in background retention service** — Bounds unbounded growth of the `ActionLogs` table without requiring DBA-level partitioning. Per-`LogType` TTL (default: Normal 90d, Exception 365d, Debug 30d, Job 90d); zero or negative days disables retention for that type. Daily sweep runs once per calendar day at configurable local hour (default 03:00). Deletion uses EF Core 7+ `ExecuteDeleteAsync` batched at `BatchSize` (default 5000) with `OrderBy(ActionTime).Take(BatchSize)` looping until drained — avoids the transaction-log bloat / lock contention a single massive `DELETE` would cause. Fault-tolerant: per-iteration exceptions logged as Warning, service never crashes the host. Structured Serilog output per sweep: `ActionLogRetention: sweep complete. Normal={N} Exception={E} Debug={D} Job={J} total={T}`. Opt-out: `Enabled = false` for apps managing retention via external archival pipeline. Manual §10.6.1. (#832)
- **Observability: `UseWtmCorrelationId()` opt-in correlation-ID middleware** — Inbound reads an `X-Correlation-Id`-style header (configurable), sanitizes and adopts it as `HttpContext.TraceIdentifier` so Serilog scope, `Activity.Current`, and `WtmProblemDetails.traceId` all emit the caller-propagated ID automatically. Fallback: auto-generate `Guid.NewGuid("N")` UUID when inbound missing / invalid. Outbound: echoes chosen ID in the response header. Sanitization rejects CR/LF / control chars / non-ASCII (log injection defense); rejects non-`[A-Za-z0-9\-_.]` chars; length cap default 128. Opt-outs: `AdoptInbound=false` for zero-trust; `EmitOutbound=false` to suppress echo. Manual §10.10. (#830)
- **Security: `[WtmRateLimit]` per-endpoint rate limit attribute** — Brute-force protection for login / password-reset / forgot-password style endpoints without lowering the global limiter's ceiling. Decorate a controller action or class with `[WtmRateLimit(permits, windowSeconds, queueLimit=0)]` to enforce a **per-IP-per-endpoint** quota on top of `AddWtmRateLimiting`'s global limiter. At startup, assembly scan collects unique `(permits, window, queue)` tuples and registers one named policy per tuple (dedup'd — no policy explosion). An `IActionModelConvention` injects ASP.NET Core's built-in `EnableRateLimitingAttribute` into each decorated action's metadata at model-build time. Exceeded quota returns HTTP 429 with an existing `Rate limit exceeded` Serilog warning. Zero breaking change — existing `AddWtmRateLimiting(opt => { ... })` calls work unchanged. Manual §10.9. (#828)
- **Cache: Lookup Cache stats / health API** — New `ILookupCacheService.GetStats()` / `.GetStats(Type)` methods expose hits, misses, invalidate count, last-access / last-warm / last-invalidate timestamps, and currently-cached tenant key count per type. Closes the "is my cache actually warm?" / "did the invalidate take effect?" observability gap. Process-lifetime counters (reset on restart) via `Interlocked`; tenant-key set guarded by `Lock`. Framework stays SDK-agnostic — apps wire `LookupCacheStats` into their telemetry (Prometheus / OpenTelemetry / custom). Manual §12.8. (#826)
- **Dashboard: `RestWidgetDataSource` (no C# required for external HTTP endpoints)** — New built-in `WidgetDataSourceKind.Rest` lets admins configure REST-backed widgets via Dashboard JSON config: `{"kind":"Rest","options":{"url":...,"jsonPath":"$.data",...}}`. Supports GET/POST with custom headers, simple dot-path JSON extraction, response caching via `IMemoryCache`, response size cap (default 1 MiB), request timeout (default 10 s). SSRF guard rejects private/loopback/link-local/multicast/IPv6-ULA IPs by default (incl. AWS IMDS `169.254.169.254`); opt-in via `allowPrivateNetwork: true`. HTTPS-only by default; opt-in `allowHttp: true` for internal plain-HTTP endpoints. Zero breaking change — existing `Custom` and `Analysis` data sources untouched. Manual §9.4.1. (#824)

### Changed
- **Tests: migrate `WalkingTec.Mvvm.Mvc.Tests` from deprecated xunit 2.9.3 to MSTest** — The only test project still on xunit is now unified with the rest of the WTM test suite (Core/Admin/Api/Etl/Integration all MSTest). Removes the last `dotnet list package --deprecated` hit. 38 tests unchanged; `FluentAssertions` style preserved (framework-agnostic). `[Fact]` → `[TestMethod]`, `[Theory]` + `[InlineData]` → `[TestMethod]` + `[DataRow]`, constructor → `[TestInitialize]`, `IDisposable.Dispose` → `[TestCleanup]`. (#822)
- **Demo: migrate `FFResult()` → `FFResultJson()` across all demo Controllers** — 123 call sites across 21 files in `demo/WalkingTec.Mvvm.Demo/Controllers/` + `demo/.../Areas/_Admin/Controllers/` updated to the CSP-safe JSON dispatcher path introduced in 10.3.0 (#789 Phase 3C). Demo is now the canonical reference implementation for downstream WTM apps — copy-paste starter code uses the non-deprecated API. Zero `WTM789` obsolete warnings from the demo project (was 123). Build warning count drops 340 → 218. No behavior change: `Alert/Message/CloseDialog/RefreshGrid/RefreshGridRow` have identical wire semantics between `FResult` and `WtmActionResult`. (#818)

### Fixed
- **Security: eliminate NU1903 from `System.Security.Cryptography.Xml 8.0.2` transitive** — NPOI 2.7.6 was pulling a vulnerable 8.0.2 version (GHSA-37gx-xxp4-5rgx + GHSA-w3x6-4m5h-cxqf, both HIGH severity) into every test/demo project in the solution. Add a direct `PackageReference` to `WalkingTec.Mvvm.Core.csproj` pinning to 10.0.6 via new `<SystemSecurityCryptographyXmlVersion>` variable in `common.props` (aligns with the rest of the central-version table). All downstream projects now inherit the patched version; `dotnet list package --vulnerable --include-transitive` returns zero NU1903 rows. NPOI unchanged. (#816)

## [10.3.0] - 2026-04-17

Security hardening release. Issue #789 six-phase `framework_layui.js`
refactor plus post-merge follow-ups (#804 / #805 / #806 / #811 / #813).
Active-code `eval(` count in `framework_layui.js`: **18 → 1** (single centralized
legacy fallback behind console deprecation warning). DOM-XSS sinks: **7 → 0**.
All changes are additive / opt-in — no breaking defaults.

### Added
- **Security: `UseWtmContentSecurityPolicy()` opt-in CSP middleware** — Apps can now register a Content-Security-Policy response header via `app.UseWtmContentSecurityPolicy()`. Default policy enforces `script-src 'self' 'unsafe-inline'` — `'unsafe-eval'` is intentionally omitted, which closes the issue #789 eval()-execution attack surface built up through Phases 1/2/3A/3B/3C. Keeps `'unsafe-inline'` so existing Razor TagHelper inline scripts still work without a nonce refactor. Supports `ReportOnly` mode and a custom `report-uri` for staged rollout. First-writer-wins: respects any CSP header already set by upstream middleware or reverse proxy. **Breaking for apps that still call `eval()` in their own JS** — opt-in means the default is unchanged; to enable, add `app.UseWtmContentSecurityPolicy()` after eliminating app-level `eval()`. (#789 Phase 3D)

### Fixed
- **Security: CSP-safe JSON action dispatcher replaces `IsScript` eval()** — `framework_layui.js` now routes AJAX responses through `ff.DispatchAction` when the server returns `X-WTM-Action: application/json` (via new `FFResultJson()` + `WtmActionResult`). The legacy `IsScript: true` path still works but emits a deprecation warning through the single centralized `ff._legacyScriptEval` helper. Active-code `eval(` count in `framework_layui.js` dropped from 18 (pre-#789) to 1. `FFResult()` is marked `[Obsolete(DiagnosticId = "WTM789")]` — suppressible per-project via `<NoWarn>WTM789</NoWarn>`. Framework-owned callers migrated (`_FrameworkController`, `_CodeGenController`, `_EtlJobController`, `Controller.txt` codegen template). Demo/app callers stay on the legacy path for one release. (#789 Phase 3C, #802)
- **Security: sanitize AJAX response HTML via DOMPurify** — The four `.html(data)` / `innerHTML = str` sinks in `framework_layui.js` (`LoadPage1`, `PostForm`, `BgRequest`, `OpenDialog`) now pass raw AJAX bodies through `ff.SafeHtml` which wraps DOMPurify. Vendored `dompurify.js` as an EmbeddedResource served alongside `framework_layui.js`; load order documented in `_Layout.cshtml`. Fail-closed: when DOMPurify did not load, SafeHtml returns empty string rather than rendering attacker HTML. (#789 Phase 3B, #801)
- **Security: DOM XSS via cookie-to-id and iframe URL concatenation** — Cookie-sourced ids (`$.cookie("divid")`) are now passed through jQuery `.attr()` / DOM `setAttribute` rather than concatenated into HTML strings, and iframe URLs are set via the `.src` setter. (#789 Phase 3A, #800)
- **Security: eliminate remaining non-FFResult eval() sites** — combo/tc/selector global-variable access and three TagHelper-templated call sites now use the safe `window[name]` property-lookup pattern. (#789 Phase 2, #799)
- **Security: `WtmActionResult.Redirect` rejects non-relative URLs** — Open-redirect (CWE-601) hardening. `WtmActionResultExtension.Redirect(url)` now throws `ArgumentException` on `null` / empty / absolute (`http://`, `https://`) / protocol-relative (`//evil.com`) / non-http schemes (`javascript:`, `data:`). Only relative shapes (`/...`, `~/...`, `#...`, `?...`) are accepted. Client-side `ff.DispatchAction` adds a defense-in-depth shape check via `charAt(0)` before assigning `location.href`. (#804, PR #808)
- **Security: escape `WtmAction.Message` / `Title` before `layui.layer.alert`** — Research confirmed layui 2.5.7's `layer.alert(msg)` concatenates `msg` into `innerHTML`, so the new Phase 3C dispatcher must escape before handing off. `ff.DispatchAction` now routes `action.message` + `action.title` through a new `ff.EscapeText` helper (jQuery `.text()/.html()` idiom) on the `alert` and `message` branches. `ff.Alert` / `ff.Msg` themselves unchanged — legacy callers that pass intentional HTML still work. (#805, PR #809)
- **Security: `CookieOption.SecurePolicy` is now configurable** — `AddWtmSession` and `AddWtmAuthentication` previously hardcoded `CookieSecurePolicy.SameAsRequest`. Behind a TLS-terminating reverse proxy (nginx, Azure App Service, IIS ARR, Cloudflare) the app sees HTTP from the proxy, so cookies were emitted without the `Secure` flag. New `CookieOption.SecurePolicy` property lets operators set `Always` for production via `appsettings.json` (`CookieOptions:SecurePolicy`). Default preserved as `SameAsRequest` — zero breaking change; explicit opt-in for the hardened behavior. (#813, PR #814)
- **Security: chart-related eval() removal** — 11 chart dispatch sites in `framework_layui.js` no longer call `eval(identifier + suffix)`. (#789 Phase 1, #798)

### Improved
- **DX: Integration.Test SQL reachability probe + docker-compose** — `IntegrationTestBase.EnsureSqlServerAvailable()` now runs a short 2 s TCP probe once per assembly (cached via `Lazy<T>`); when SQL Server is not reachable, each integration test reports `AssertInconclusive` (yellow) with a multi-line setup guide instead of `Failed` (red). Added `docker-compose.yml` at repo root (`docker compose up -d mssql`) and `test/WalkingTec.Mvvm.Integration.Test/README.md` with three run-options runbook. From-fresh-clone `dotnet test` now shows 9 yellow skipped (was 9 red failed / 1m13s). CI unchanged — probe succeeds against the service container. (#811, PR #812)
- **Quality: `#789` tidy-up bundle** — `[Obsolete(DiagnosticId = "WTM789")]` now also on `FResult` class declaration + `FResultExtension` class (previously only on the `FFResult()` factory method). `WtmAction.Type` converted from raw `string` to typed `WtmActionType` enum with `JsonStringEnumConverter(CamelCase)` — wire format unchanged, type system closed. `WtmActionResultExtension.RefreshGrid` emits `Index` only when non-zero (honors `WhenWritingNull`). `WtmCspOptions.ReportUri` XML doc no longer conflates `report-uri` with `report-to`. (#806, PR #810)

## [10.2.0] - 2026-04-11

### Changed
- **BREAKING: Remove deprecated Elsa Workflow integration** — Delete all `[Obsolete]` workflow stubs that were non-functional since v8.3.0. Removed: `IWorkflow`, `FrameworkWorkflow`, `FlowInfoTagHelper`, `AddWtmWorkflow()`, `BaseCRUDVM.StartWorkflowAsync/ContinueWorkflowAsync/GetWorkflowTimeLineAsync/GetWorkflowInstanceAsync`, `BasePagedListVM.GetMyApproves()`, `DataContext.FrameworkWorkflows` DbSet, `TypeExtension.GetParentWorkflowPoco()`, and related WorkFlow DTOs. **Migration**: remove all references to these types and methods — they were already returning null/empty (#762)

### Fixed
- **Build: exclude Blazor WASM Client from Release build** — Prevent NETSDK1082 error when `wasm-tools` workload is not installed. Blazor demo packages upgraded 10.0.3 → 10.0.5 (#764)
- **Security (CRITICAL): validate JWT signature for `_remotetoken`** — Standalone deployments accepted unsigned JWT tokens, allowing identity impersonation. Now validates signature, issuer, and audience (#765)
- **Security (HIGH): block mass assignment in `UpdateModelProperty`** — Inline cell editing accepted arbitrary field names. Now rejects sensitive fields and navigation paths (#766)
- **Security (HIGH): validate VM type in `CreateVM(string)`** — User-supplied type names were passed to `Type.GetType()` without restriction. Now requires `BaseVM` subclass (#767)
- **Security (HIGH): stop leaking exception messages in error page** — Production error handler returned raw `ex.Message` to clients. Now returns generic localized message (#769)
- **Security (MEDIUM): prevent path traversal in file upload** — `subdir` parameter was concatenated without validation. Now enforces upload root boundary (#770)
- **TokenService: convert recursive revocation to iterative** — Prevent StackOverflow on circular refresh-token chains with max depth 50 (#771)
- **GetFile: guard image processing against non-image files** — Only attempt `Image.Load` for known image extensions, safely reset stream on failure (#775)
- **Export: cross-platform paths and temp directory cleanup** — Replace Windows-only path separators with `Path.Combine()`, delete temp directories in `finally` block (#776)

### Improved
- **WtmTenantService: replace blocking `Wait()` with `WaitAsync()`** — Prevent thread pool starvation under concurrent load (#772)
- **ETL: set CommandTimeout to 300s** — Replace infinite timeout (0) with 5-minute safety net in MssqlSource and OracleSource (#773)
- **Regex: static compilation in `_FrameworkController.Selector`** — Avoid per-request Regex allocation (#774)
- **Exception logging: replace 13 bare `catch {}` blocks** — Add `ILogger.LogWarning` in WTMContext (6), BaseCRUDVM (4), FrameworkFilter (3) to surface silent failures (#777)

## [10.1.1] - 2026-04-04

### Fixed
- **Security hardening** — Resolve XSS (reflected user input in error pages), path traversal (file provider), and information disclosure (API client error responses) (#748)
- **CI** — Update xunit.runner.visualstudio to 3.0.0 for .NET 10 compatibility

### Improved
- **C# 12 collection expressions** — Extended `List<T>` → `[]` modernization from Core (10.0.1) to Mvc (11 files), TagHelpers.LayUI (11 files), and Etl (2 files), covering controllers, filters, helpers, data grids, tree views, form tag helpers, and pipeline loaders (52 files total)
- **Code comments** — Added English explanatory comments to non-obvious security logic (TokenService token-reuse detection, PasswordHashHelper legacy hash chain) and complex algorithms (DataContext multi-tenant filter, AnalysisQueryEngine expression trees, ServerSideGroupByStrategy measure projection) (#754, #755)

## [10.1.0] - 2026-03-28

### Added
- **Clean Architecture: 8 extracted services** — Decompose WTMContext God Object (1670 lines) into 8 focused, single-responsibility services using the Strangler Fig pattern (#727):
  - `IWtmApiClient` / `WtmApiClient` — HTTP API calls (CallAPI)
  - `IWtmLogService` / `WtmLogService` — structured action logging (DoLog)
  - `IWtmAuthorizationService` / `WtmAuthorizationService` — URL access control (IsAccessable, IsUrlPublic)
  - `IWtmDataContextFactory` / `WtmDataContextFactory` — DataContext creation with tenant/CS resolution
  - `IWtmAuthService` / `WtmAuthService` — password verification, remote auth, token refresh
  - `IWtmUserCacheService` / `WtmUserCacheService` — user info cache invalidation
  - `IWtmTenantService` / `WtmTenantService` — tenant groups/roles with caching
  - `IWtmVmFactory` / `WtmVmFactory` — ViewModel creation and initialization
- **WTMContext facade delegation** — WTMContext methods now delegate to extracted services when available via DI, with inline fallback for environments without DI (#730)
- **WtmUIOptions** — Centralized, configurable UI styling system with overridable CSS class names, sizing defaults, and required field markers. Defaults match LayUI for zero-config backwards compatibility (#730)
- All 8 services registered in DI for both web (`AddWtmContext`) and console (`AddWtmContextForConsole`) apps

### Improved
- **WtmAuthorizationService** — Compiled regex cache (`ConcurrentDictionary` + `RegexOptions.Compiled`) eliminates per-request allocation in singleton service
- **WtmTenantService** — Cache stampede protection via per-key `SemaphoreSlim` with double-check locking
- **Structured logging** — Replace empty `catch {}` blocks with `ILogger.LogWarning` in WtmAuthorizationService and WtmTenantService
- **Nullable safety** — Fix pre-existing nullable warnings in WTMContext.cs (WindowIds, ReloadUser, BaseUserQuery, DoLoginAsync)

### Fixed
- **WtmAuthService.RefreshTokenAsync** — Pass user's RemoteToken as authToken when calling remote refresh endpoint (QA review finding)
- **9 pre-existing test failures** — Fix FrameworkTenant_CreateDC (missing constructor), DoLoginAsync tests (missing IWtmTenantService mock), SearchTest (missing mock in MockWtmContext) (#730)

### Notes
- WTMContext's original method bodies are preserved as fallback — zero breaking changes for existing consumers
- New code can optionally inject services directly via DI instead of going through WTMContext
- Minor version bump (10.0.1 → 10.1.0) per compatibility policy: new public API surface (8 service interfaces)

## [10.0.1] - 2026-03-21

### Changed
- **TimeProvider 全面遷移** — 將 `DateTime.Now`/`UtcNow` 替換為 `TimeProvider`，覆蓋 Core VMs、Mvc Controllers/Filters、TokenService、DataContext、WTMLogger、FileHandlers、Dashboard、ETL 等 27 個檔案。測試可透過 `FakeTimeProvider` 控制時間（#697, #706, #708, #711, #712）
  - `WTMContext.TimeProvider`（Phase 1）
  - Core VMs: BaseCRUDVM, BaseBatchVM, BaseImportVM, BasePagedListVM, BaseTemplateVM（Phase 2）
  - Mvc: _FrameworkController, FrameworkFilter, _AnalysisController, FileExtension（Phase 2）
  - Auth: TokenService — 建構子注入 `TimeProvider`（Phase 2）
  - Core: DataContext（新增 `EmptyContext.TimeProvider` 屬性）、WTMLogger、FileHandlers（Phase 3）
  - Dashboard: JsonFileDashboardService — 建構子注入 `TimeProvider`（Phase 3）
  - ETL: EtlQuartzJob, EtlSchedulerService（Phase 3）
  - DateRange: 6 個 `CreateUtc*()` factory methods 支援 `TimeProvider?` 可選參數，原 static properties 保持向後相容（Phase 3）
- **Collection expressions 現代化** — Analysis DTOs 和 BasePagedListVM 使用 C# 12 collection expressions（#707）
- **.NET 10 效能優化 Phase 1a** — FrozenDictionary、Lock、Span/stackalloc、string interpolation（#702）

### Fixed
- **Analysis Saved Query 存取控制** — 強化 VM 存取檢查，防止未授權存取（#710）
- **BaseImportVM nullable 修正** — 移除 Core 專案最後一個 `#nullable disable`（#703）
- **Build 健康度** — 解決 NU1603、NU1510 警告，移除未使用的 import（#693）

### Performance
- **ExecuteDeleteAsync** — Analysis Saved Query 刪除改用 `ExecuteDeleteAsync` 提升效能（#704）

### Notes
- `DateTimeOffset.UtcDateTime`（而非 `.DateTime`）用於所有 UTC 語境，避免 `DateTimeKind.Unspecified` 導致時區誤判
- Property initializers（RefreshTokenEntity, ChangeLog 等）、static display timestamps、codegen templates 刻意保留 `DateTime`

## [10.0.0] - 2026-03-20

### Changed
- **BREAKING: .NET 10 升級** — 所有專案目標框架從 `net8.0` 升級至 `net10.0`（.NET 10 LTS）
- **BREAKING: 版本號跟隨 .NET 版本** — `VersionPrefix` 從 `8.6.1` 升至 `10.0.0`
- **BREAKING: MySQL Provider 替換** — 從 `Pomelo.EntityFrameworkCore.MySql` 改為官方 `MySql.EntityFrameworkCore` 10.0.1
  - `UseMySql()` → `UseMySQL()`（大寫 SQL）
  - 不再需要 `ServerVersion` 參數
  - `MySqlSchemaBehavior` 不再可用
  - `MySqlConnector` namespace → `MySql.Data.MySqlClient`
- **BREAKING: API Versioning 套件替換** — `Microsoft.AspNetCore.Mvc.Versioning` → `Asp.Versioning.Mvc` 8.1.1
  - `AddVersionedApiExplorer()` → `AddApiVersioning().AddApiExplorer()`
  - 新增 `using Asp.Versioning;` namespace
- **EF Core 10** — 全部 EF Core 套件升至 10.0.4
- **ASP.NET Core 10** — 全部 ASP.NET Core 套件升至 10.0.4
- **Swashbuckle 10** — 從 6.6.2 升至 10.1.5（依賴 OpenAPI.NET v2）
- **Serilog 10** — `Serilog.AspNetCore` 從 8.0.3 升至 10.0.0
- **Npgsql 10** — `Npgsql.EntityFrameworkCore.PostgreSQL` 從 8.0.11 升至 10.0.1
- **Oracle EF Core 10** — `Oracle.EntityFrameworkCore` 從 8.23.70 升至 10.23.60
- **SDK** — `global.json` 升至 .NET SDK 10.0.0
- **Dockerfile** — 基底映像從 .NET 3.1 更新至 .NET 10
- **CI** — GitHub Actions 的 `dotnet-version` 從 `8.0.x` 更新至 `10.0.x`
- **套件版本集中管理** — `common.props` 新增 12 個 MSBuild 版本變數，統一管理 Microsoft、EF Core、ASP.NET Core 及主要第三方套件版本
- **System.Text.Json** 10.0.0 → 10.0.4
- **Quartz** 3.16.0 → 3.16.1
- 修正遺留 demo 測試專案：`net5.0`/`net6.0` → `net10.0`

### Migration Guide
1. 將您的專案 `TargetFramework` 從 `net8.0` 改為 `net10.0`
2. 安裝 .NET 10 SDK
3. 若使用 MySQL：將 `Pomelo.EntityFrameworkCore.MySql` 替換為 `MySql.EntityFrameworkCore`
   - `using MySqlConnector;` → `using MySql.Data.MySqlClient;`
   - `optionsBuilder.UseMySql(cs, serverVersion, ...)` → `optionsBuilder.UseMySQL(cs)`
4. 若使用 API Versioning：更新套件引用及 namespace
   - `Microsoft.AspNetCore.Mvc.Versioning` → `Asp.Versioning.Mvc`
5. 更新所有 Microsoft.EntityFrameworkCore.* 套件至 10.0.x
6. 更新所有 Microsoft.AspNetCore.* 套件至 10.0.x

### Added
- **BaseCRUDVM — 批量刪除預覽**：新增 `GetDeletePreviewString()` 虛擬方法，回傳實體的人類可讀標籤（依序搜尋 Name/Title/Code/ITCode 等屬性，否則回退至主鍵）；同時新增 `IBaseCRUDVM<T>.GetDeletePreviewString()` 介面成員（#619）。
- **`_FrameworkController` — 批量刪除預覽端點**：POST `/_Framework/GetDeletePreview` — 接受 vmType 名稱與最多 10 個 ID，回傳 `[{id, label}]` 陣列供前端確認對話框使用（#619）。
- **`_FrameworkController` — 批量指派角色端點**：POST `/_Framework/BatchAssignRoles` — 將一個角色指派給多位使用者（upsert `FrameworkUserRole`），完成後清除受影響的快取（#619）。
- **ComboBox 遠端搜尋**：`wt:combobox` 新增 `remote-url` 屬性；設定後啟用 xmSelect `remoteSearch + remoteMethod`，每次輸入觸發 `GET {remote-url}?q=<keyword>` 並即時更新選項（#565）。
- **TreeSelect 懶加載**：`wt:tree` 新增 `lazy-url` 屬性；設定後啟用 xmSelect `lazy + load`，展開節點時觸發 `GET {lazy-url}?id=<nodeValue>` 並動態載入子節點（#565）。
- **ETL 管理 UI 改進**：`EtlJobListVM` 覆寫 `InitGridAction()` 增加 Create/Edit/Delete 標準動作，以及每行操作按鈕：立即執行（確認 POST）、暫停、恢復、中止、執行記錄（開啟篩選後的 RunLog 對話框）；新增 ConsecutiveFailureCount 欄位（#540）。
- **ETL Create/Edit 表單補全**：Demo 的 Create.cshtml 與 Edit.cshtml 補入 QueryTemplate（textarea）、AlertEmail、AlertWebhookUrl、AlertAfterConsecutiveFailures 欄位（#540）。
- **BaseCRUDVM — 樂觀並行衝突處理**：`DoEdit` / `DoEditAsync` 捕獲 `DbUpdateConcurrencyException`，設定 `IsConcurrencyConflict = true` 並新增模型錯誤，而非直接拋出例外；新增介面屬性 `IBaseCRUDVM<T>.IsConcurrencyConflict`（#620）。
- **BaseImportVM — 匯入進度回報**：`BatchSaveData(IProgress<ImportProgress>? progress = null)` 在驗證與儲存階段回報 `ImportProgress { Processed, Total, Phase }`（#607）。
- **BaseImportVM — 行內錯誤清單**：新增 `InlineErrors` 屬性（最多 `InlineErrorLimit` 筆，預設 50）供 API 端點直接回傳驗證錯誤（#615）。
- **BaseTemplateVM — 欄位說明列**：`ShowDescriptionRow = true`（預設）時，於模板第二行插入淺綠色斜體說明列，標示 Required/Optional、資料類型、min/max 限制；匯入時自動識別並跳過說明列（v2 標記）（#615）。

### Fixed
- **Test suite — EF Core 10 API**：`DataContext` 中 `modelBuilder.Model.SetMaxIdentifierLength(30)` 改為 `modelBuilder.HasMaxIdentifierLength(30)`，修正 EF Core 10 將內部 API 設為不可存取的問題（#676）。
- **Test suite — MSTest / Test.Sdk 版本**：`MSTest.TestAdapter` / `MSTest.TestFramework` 從 3.2.2 升至 3.6.4；`Microsoft.NET.Test.Sdk` 從 17.9.0 升至 17.12.0，確保與 .NET 10 測試主機相容（#676）。
- **Test suite — coverlet / xunit runner**：`coverlet.collector` 從 6.0.2 升至 6.0.4；`xunit.runner.visualstudio` 從 2.8.2 升至 2.8.3，修正與 Test.Sdk 17.12.0 的相容性（#676）。
- **Test suite — Serilog Sinks**：`Serilog.Sinks.InMemory` 從 0.11.0 升至 1.0.0，解決與 `Serilog.AspNetCore 10.0.0`（使用 Serilog 4.x）的型別衝突（#676）。

### Deprecated
- **Workflow API**：內建 Elsa workflow 整合（`IWorkflow`、`FrameworkWorkflow`、`ApproveTimeLine`、`ApproveInfo`、`FlowInfoTagHelper`、`IBaseCRUDVM` 工作流程方法、`DataContext.FrameworkWorkflows`）標記為 `[Obsolete]`，將於下一個主版本移除（#586）。
  - **遷移指引**：若仍需工作流程功能，請直接引用 Elsa 或改用其他工作流程引擎；移除 `IWorkflow` 介面實作及相關 TagHelper。

## [8.6.1] - 2026-03-17

### Added
- **ETL — `CompositeEtlSource`**：多來源 Union 模式，支援跨資料庫合併載入（#425）
- **Dashboard — `inferChartType`**：依維度/度量組合自動選擇最佳圖表類型（Bar/Line/Stacked/Card）（#431）
- **Dashboard — KPI 告警閾值著色**：超過風控閾值時 Card Widget 自動標色，支援即時風控場景（#386）
- **Dashboard — 響應式斷點**：Widget 格局依視窗寬度自動調整欄數（#344）
- **Analysis — Excel 匯出格式化**：Bold header、自動欄寬、`#,##0.00` 數字格式（#378）
- **Analysis — 合規匯出 Metadata**：`includeMetadata=true` 參數新增 Metadata 工作表，含查詢條件、欄位清單、QueryHash（#385）
- **Analysis — VM 層級 RBAC**：`[AllowedRoles]` attribute 限制整個 ListVM 的可見度（#339）
- **Analysis — 欄位層級 RBAC**：`[AllowedRoles]` 可套用於個別 Dimension/Measure 欄位（#341）
- **Analysis — 結構化 Logging**：`AnalysisController` 所有端點加入 structured log（#340）
- **Analysis — CancellationToken**：Query/Export 路徑支援請求取消，避免 DB 長跑查詢浪費資源（#326）
- **Analysis — Ad-hoc Filter UI**：BA 可在前端動態新增篩選條件列（欄位 + 運算子 + 值）（#313）
- **Analysis — 雙 Y 軸 + 金額縮放**：2 個度量最大值差距 ≥ 10 倍時自動啟用雙 Y 軸；依最大值自動選擇元/萬元/百萬元/億元縮放單位（#281）
- **ETL — MergeKeyColumn 唯一性驗證**：Job 建立時驗證 MergeKey 欄在目標表為唯一索引（#342）
- **ETL — 5 欄位 Unix Cron 自動轉換**：輸入標準 5 欄位 Cron 運算式自動轉 Quartz 6 欄位格式（#343）
- **Analysis — 快取 Hash 含租戶/使用者 ID**：防止跨租戶快取穿透（#314）

### Fixed
- **ETL**：`EtlJobDefinitionVM.Validate()` catch 範圍縮小，DB 連線錯誤現在正確回報至 UI（#353）
- **ETL**：`Rerun` / `TriggerNow` / `Pause` / `Resume` / `SkipNext` 端點加入 `InvalidOperationException` 處理，回傳 400 而非 500（#359 #367）
- **Analysis**：CSV 匯出加入 UTF-8 BOM，Windows Excel 開啟不再出現亂碼（#379）
- **Analysis**：空資料時 Query 回傳 `{ rows: [], truncated: false }` 並附帶友善提示訊息，不再回傳空 body（#383）
- **Analysis**：GroupBy 維度 key 中的 null bytes（`\0`）改為空字串，防止 ECharts 標籤顯示異常（#372）
- **Analysis**：`collectFilters` 欄位名稱 `op` → `operator`，並新增最少一個維度的前端驗證（#347 #348 #349）
- **Analysis**：`Avg` / `Max` / `Min` 在分組無資料時回傳 `null`，不再誤回傳 `0`（#336）
- **Analysis**：`FilterOperator.In` 過濾運算子補回（#331）
- **Analysis**：GroupBy 分隔符改為 Unicode 私用區字元，防止維度值含逗號時分組錯誤（#332）
- **Analysis**：切換圖表類型時正確 dispose 舊 ECharts 實例，避免記憶體洩漏（#333）
- **Analysis**：`Count` 聚合改為計算非 null 值數量（#337）
- **Analysis**：`DateHierarchy` 模式下自動降級為 InProcess 策略（#338）
- **Analysis**：`AutoSizeColumn` 加 try-catch，修復 CI Linux 無字型環境下的例外（#378）
- **Dashboard**：`Create` / `Update` 端點驗證 `WidgetType` 不得為空字串或空白（#382）

## [8.6.0] - 2026-03-12

### Added
- **ETL Module** (`WalkingTec.Mvvm.Etl`): Batch data import pipeline
  - Support for MSSQL and Oracle source databases
  - `EtlBulkJob` (zero-transform) and `EtlMappedJob<TIn,TOut>` (with transform) base classes
  - Staging Table → MERGE INTO pattern for idempotent upserts
  - Three watermark modes: FullLoad, Timestamp, Identity
  - Quartz.NET-based scheduling with dynamic cron management
  - Management UI: Job CRUD, execution history, real-time progress monitoring
  - Operations: trigger now, pause, resume, abort, skip next, reschedule
  - RBAC integration via existing PrivilegeFilter
  - `MockBulkLoader` and `MockEtlSource` for unit testing
  - Developer documentation (`docs/etl-module.md`)
  - Docker Compose test environment (`test/docker-compose.etl-test.yml`)
  - 65+ unit tests, 15 integration tests
- **Developer Manual** (`docs/wtm-developer-manual.md`): 3000+ line comprehensive manual
  - 16 sections covering all framework features with scene-based examples
  - Full API Controller CRUD example, TagHelper scenarios (Selector, Upload, Dialog, Cascade)
  - ETL end-to-end scenarios (MSSQL/Oracle/Transform), watermark lifecycle
  - Security deep-dive: PBKDF2 migration flow, JWT token rotation diagram, DataPrivilege usage
  - Multi-tenant Global Query Filter explanation with SaaS scenarios
  - Lookup Cache stampede protection walkthrough, invalidation flow
  - Code Generator step-by-step guide with generated file structure
  - 10 FAQ entries including deployment checklist

### Fixed
- **fix(dashboard):** Path traversal vulnerability in dashboard file storage (#220)
- **fix(dashboard):** Tenant isolation bypass — non-owner tenants could access dashboards by ID (#220)
- **fix(etl):** `GetMaxValue` crash on empty result set (#220)

## 8.5.1 (2026-03-11)

### 修復

* **fix(codegen)：** `InjectAnalysisAttributes` 重複 attribute 注入問題 — 原正則要求 attribute 緊鄰屬性宣告，當中間有其他 attribute（如 `[Required]`）或註解時會重複插入導致編譯錯誤。改用字串區塊搜尋取代嚴格正則（#162）

## 8.5.0 (2026-03-11)

### Code Generator — Analysis Mode 整合

Code Generator 新增 Analysis Mode 支援，自動產生 `[EnableAnalysis]`、`[Dimension]`、`[Measure]` attribute。

* **feat(codegen)：** 新增 `EnableAnalysis` checkbox 及 `IsDimension`/`IsMeasure` 欄位選擇（#156）
* **feat(codegen)：** 智慧預設 — string/enum→Dimension、數值→Measure、DateTime→Dimension(Month)（#156）
* **feat(codegen)：** ListVM 模板自動加入 `[EnableAnalysis]` attribute + using（#157）
* **feat(codegen)：** 生成後自動在 Model .cs 檔案插入 `[Dimension]`/`[Measure]` attribute — 正則插入、幂等保護、DateTime 自動 Hierarchy（#158）
* **feat(codegen)：** `FindModelFile` 從 MainDir 往上搜尋 Model source file，排除 bin/obj（#158）

### 測試與文件

* 10 個新 MSTest 測試覆蓋 attribute 注入、幂等性、檔案搜尋（#159）
* 更新 `docs/analysis-mode.md` 加入 Code Generator 整合說明（#159）

## 8.4.1 (2026-03-11)

### 修復

* **fix(cache)：** `RefreshAsync` 快取踩踏（Cache Stampede）漏洞 — 將 `InvalidateType` + `LoadFromDbAsync` + `SetCache` 三步移入 per-key SemaphoreSlim 鎖內，消除 invalidate 與 reload 之間的競爭條件（#153）
* **fix(security)：** `DecryptString` 補上 `FormatException` catch — 傳入非法 Base64 字串時回傳空字串而非拋出例外，與舊版 `DecryptStringLegacy` 行為一致（#153）

## 8.4.0 (2026-03-11)

### Lookup Cache — 靜態表/查找表快取系統

全新的 Cache-Aside 快取機制，為頻繁讀取但極少變更的查找表（如系統代碼、組織結構、角色列表等）提供零配置自動快取。

#### 核心功能

* **feat(cache)：** `[CacheLookup]` Attribute 標記模型即啟用快取，支援 `WarmOnStartup`、`TenantIsolation`、`ConnectionKey` 設定（#139）
* **feat(cache)：** `ILookupCacheService` + `LookupCacheService` — `IMemoryCache` 為底層的 Cache-Aside 實作，per-type `CancellationTokenSource` 批次失效（#139）
* **feat(cache)：** `WTMContext.GetLookup<T>()` / `GetLookupAsync<T>()` — 框架級快取存取 API，自動解析 `ConnectionKey` 與 `TenantIsolation`（#142）
* **feat(cache)：** `GetLookupItem<T>(predicate)` 單筆查詢 + `GetLookupSelectList<T>()` 下拉選單整合（#142）

#### 架構改善

* **refactor(cache)：** 從 `LookupInvalidationInterceptor`（EF SaveChanges 攔截器）重構為 `FrameworkContext.SaveChanges()` override — 消除 Activator.CreateInstance 的 interceptor 注入限制（#139）
* **refactor(cache)：** Warmup 從 `Task.Delay(3s)` 改為 `IHostApplicationLifetime.ApplicationStarted` 事件驅動 — 不再猜測啟動時間（#143）
* **feat(cache)：** `IReadOnlyList<T>` 回傳型態防止快取被意外修改（#142）
* **feat(cache)：** 全域 `LookupCacheOptions.DefaultTenantIsolation` + Attribute 三態覆寫（Unset/true/false sentinel pattern）（#142）

#### 穩定性

* **feat(cache)：** Per-key `SemaphoreSlim(1,1)` stampede 防護 — 10s timeout fallback，防止快取過期時大量並發同時打 DB（#146）
* **feat(cache)：** `RefreshAsync<T>()` 手動刷新 API（#146）

#### 測試

* 36 個 MSTest 涵蓋：快取命中/失效/過期、tenant 隔離、warmup、stampede 競爭、ConnectionKey 多 DB、GetLookupItem/SelectList、RefreshAsync

### 安全修復

* **feat(security)：** 連線字串加密從 DES（56-bit）升級為 AES-256-CBC — SHA-256 key derivation、random IV、`DecryptString` 自動 fallback DES 相容舊資料（#148）

### 品質改善

* **fix(core)：** `DPWhere` / `AppendSelfDPWhere` 支援 in-memory `IQueryable` — 偵測 `EnumerableQuery<T>` 自動切換 `Enumerable.Any`，解決 Lookup Cache 整合及測試場景的 Expression Tree 失敗（#149）
* **fix(ci)：** Tag push 時自動上傳 `.nupkg` 為 Release assets（#137）
* **test：** 統一測試框架 + 覆蓋率門檻、DataPrivilege API / BaseImportVM / async BaseCRUDVM / 密碼遷移等 49 個新測試（#128–#132）

## 8.3.1 (2026-03-10)

### Analysis Mode Phase 2 — 跨 DB 分析引擎優化

本版本完成 Analysis Mode Phase 2 全部 16 項子任務（#91–#106），涵蓋跨 DB 策略、日期鑽取、Pivot 樞紐表、圖表互動等功能。

#### 基礎設施（Wave 1）

* **feat(analysis)：** 新增 `DateHierarchy` enum（Year/Quarter/Month/Day）與 `AnalysisFieldMeta.IsDate` 自動偵測（#91, #92, #93）
* **feat(analysis)：** 提取 `IGroupByStrategy` 策略模式 — `ServerSideGroupByStrategy`（EF Core SQL push）+ `InProcessGroupByStrategy`（記憶體聚合），`GroupByStrategyResolver` 依 DBType 路由（#94）
* **feat(analysis)：** `ServerSideGroupByStrategy` 實作，MSSQL/Oracle 優先 server-side GroupBy（#95）
* **feat(analysis)：** `AnalysisQueryEngine` 整合雙軌策略 + ServerSide → InProcess 自動 fallback（透過 exception filter）（#96）
* **feat(analysis)：** `IAnalysisCache` + `QueryHash` 快取層，SHA256-based 查詢結果快取（#97）

#### 功能擴展（Wave 2）

* **feat(analysis)：** `DateTruncator` 跨 DB 日期截斷，用 `.Year`/`.Month`/`.Day` CLR 屬性 + 整數 key 格式（#98）
* **feat(analysis)：** 前端日期鑽取 UI — hierarchy dropdown（年/季/月/日）、`formatDateKey` 純函式（#99）
* **feat(analysis)：** API DTO 更新 — `DimensionHierarchies` 字典加入 `AnalysisQueryRequest`（#100）
* **feat(analysis)：** `AnalysisPivotEngine` — 純記憶體 row-column 交叉轉置，max 50 pivot values（#101）
* **feat(analysis)：** Pivot 前端 — 樞紐模式 toggle、pivot radio selector、`renderPivotTable`（動態表頭、null→"-"、水平捲動）、`renderPivotChart`（stacked bar）（#102）
* **feat(analysis)：** `POST /_analysis/pivot` API endpoint（#103）

#### 進階互動（Wave 3）

* **feat(analysis)：** 圖表 drill-down 互動 — ECharts click handler、drill history stack（push/pop/reset）、日期自動降階（Year→Quarter→Month→Day）、麵包屑 UI（#104）
* **feat(analysis)：** `IAnalysisFieldPolicy` 欄位級權限控制介面（#105）
* **feat(analysis)：** `FilterOperator.In` 支援多值篩選（#106）

#### 測試

* 新增 Analysis Mode 相關 MSTest 與 Jest 測試，涵蓋 Engine fallback、DateTruncator、PivotEngine（9 tests）、Controller pivot endpoint（4 tests）、前端 drill-down（10 tests）、前端 pivot（9 tests）等

## 8.3.0 (2026-03-09)

### ⚠️ Breaking Changes

* **移除 Elsa 工作流整合：** 完全解耦 Elsa 2.x 依賴（Controller、Model、UI assets、NuGet 引用）。如需工作流功能，請自行整合 Elsa 或其他方案

### 安全修復

* **fix(deps)：** `System.Text.Json` 8.0.0 → 8.0.6 — 修復 2× High CVE（GHSA-hh2w-p6rv-4g7w, GHSA-8g4q-xg66-9fp4）
* **fix(deps)：** `SixLabors.ImageSharp` 3.1.3 → 3.1.12 — 修復 2× High + 4× Moderate CVE
* **fix(deps)：** `DUWENINK.Captcha` 0.7.0 → 0.8.0（最後一個 .NET 8 相容版本）
* **chore(deps)：** ReactDemo 與 Vue3Demo npm audit fix（200→22, 46→3 殘留漏洞）

### 品質改善

* **refactor(nullable)：** 完成 Core 專案全部 168 個 `#nullable disable` 檔案的 nullable 現代化（Models, Support, Grid, Config, Helpers, ViewModels 等），僅餘 `BaseImportVM.cs` 內 5 行局部 scope
* **fix(nullable)：** 修復 7 個高優先檔案中的 null-forgiving 問題（`MSD!` → `MSD?`、double-`!` chain 拆解、catch block null guard）
* **quality：** 減少 Core nullable 警告基線（56 項清理）
* **refactor(nullable)：** `WTMContext.cs`、`DataContext.cs`、`IDataContext.cs`、`DCExtension.cs`、`PropertyHelper.cs`、`ListVMExtension.cs` 等核心檔案 nullable 啟用

### 功能改善

* **feat(analysis)：** `collectSelection()` querySelector 從全域 `document` 改為 scoped 到 panel 元素，避免多 gridId 衝突
* **fix(taghelper)：** 修復 `ipr_xs`/`ipr_sm` 巢狀 container 未清除的 context 洩漏問題
* **test(taghelper)：** 新增 `DateTimeTagHelper` rendering-path 測試（`IsRange`/`RangeStartName`/`RangeEndName`）

### 依賴更新

* Dependabot 批次更新：cross-spawn, nanoid, zrender/echarts, vue, vite, @babel/helpers, @babel/runtime, lodash, axios, qs, express 等

### 文件與工具

* **docs：** 新增 Project Mission 與 Development Principles 至 `CLAUDE.md`
* **feat：** 新增 dry-run 發布工作流 + package smoke QA + 一鍵發布腳本
* **test：** CI 新增 release tooling 驗證

## 8.2.0 (2026-03-06)

* **新增（結構化日誌）：** Opt-in Serilog 整合，透過 `AddWtmSerilog()` / `UseWtmSerilog()` 啟用。支援 Console + JSON 檔案 Sink，每日滾動、30 天保留、HTTP 請求記錄、自訂 Sink 擴充。與現有 WTMLogger / ActionLog 並存不衝突
* **新增（ProblemDetails）：** Opt-in RFC 7807 錯誤回應，透過 `UseWtmProblemDetails()` 啟用。API 路由（`/api/*` 或 `Accept: application/json`）回傳結構化 JSON 錯誤含 traceId 串聯。MVC 頁面行為不受影響
* **新增（審計攔截器）：** `EmptyContext.SaveChanges` 自動填入 `CreateBy`/`CreateTime`/`UpdateBy`/`UpdateTime`。僅填空值不覆寫，與現有 VM 手動設定完全相容。透過 `IDataContext.CurrentUserCode` 傳遞當前用戶，`WTMContext.DC` getter 自動注入
* **新增（Rate Limiting）：** Opt-in IP 限流，透過 `AddWtmRateLimiting()` / `UseWtmRateLimiting()` 啟用。預設 100 次/60 秒固定窗口，超限回 429。支援自訂 `PermitLimit`、`WindowSeconds`、`QueueLimit` 及 `CustomConfig` 完整覆寫
* **清理：** 移除 `SearchPanelTagHelper.cs` 未使用的 `using Newtonsoft.Json.Schema`
* **文件：** 新增 `docs/structured-logging.md` — 完整使用手冊，含設定、生產環境建議、log 查詢技巧、前端整合範例
* **依賴：** 新增 `Serilog.AspNetCore` 8.0.3
* **測試：** Serilog 6 tests + ProblemDetails 7 tests + AuditInterceptor 6 tests + RateLimiting 3 tests，全部 CI ✅

## 8.1.17 (2026-03-05)

* **新增（Analysis Mode）：** `[Dimension]` / `[Measure]` / `[EnableAnalysis]` 屬性標注系統，無需額外程式碼即可在列表頁切換分析模式
* **新增（Analysis Mode）：** `/_analysis/meta`、`/_analysis/query`、`/_analysis/export` 三個 API，支援動態 GroupBy 聚合（Sum / Count / Avg / Max / Min）
* **新增（Analysis Mode）：** `DataTableTagHelper.EnableAnalysis` 屬性，一行 HTML 啟用分析按鈕
* **新增（Analysis Mode）：** `framework_analysis.js` 前端 UI，含 ECharts 自動選型圖表（Bar / Stacked Bar / Line / 數字卡片）
* **新增（Analysis Mode）：** Excel (.xlsx) 與 CSV 匯出；CSV 防 formula injection（`=`, `+`, `-`, `@` 前置 tab）
* **安全：** `AnalysisVmRegistry` 白名單機制；`AnalysisQueryEngine` 全程 Expression Tree，無 SQL 字串拼接
* **修正：** `ExecuteDynamic` 未 unwrap `TargetInvocationException`，導致無效欄位回 500 而非 400
* **修正：** 移除未實作的 `FilterOperator.In`（宣告但無 switch case，原回 `NotSupportedException`）
* **效能：** 資料載入硬上限 `MaxMaterializeRows = 50,000` 防止 OOM；結果超過 10,000 列自動截斷
* **測試：** Engine 30 tests + Exporter 6 tests + Controller 16 tests + JS 42 tests，全部 CI ✅

## 8.1.16 (2026-03-04)

* **修正（BUG-1）：** CodeGenVM `GetRandomValues()` 對 readonly 欄位 NullReferenceException；`GenerateAddFKModel` null guard
* **修正（BUG-2）：** `UploadImage` 上傳非圖片檔回 500，改為 try-catch 回 400 Bad Request
* **修正（BUG-3）：** 多租戶下 `FileAttachment`（實作 `ITenant`）受 EF global query filter 影響不可見；改用 `IgnoreQueryFilters()`
* **修正（BUG-4）：** Elsa + MySQL 不自動建表；MySQL treats schema=database，`WtmElsaContext.Schema` 對 MySQL 回 null
* **修正（BUG-5）：** CodeGen 路徑在非標準環境 `IndexOf("\bin\Debug\")` 回 -1 導致 `Substring(0,-1)` 例外；改用 `Directory.GetCurrentDirectory()` fallback
* **修正（BUG-6）：** ComboBox `selectVal` 空時強制所有 `item.Selected = false`；改為只在 `selectVal.Count > 0` 時覆蓋
* **新增（BUG-7）：** Oracle 連線新增 `Enabled` 屬性（預設 true），`appsettings.json` 可設 `Enabled: false` 停用個別連線
* **效能（BUG-8）：** 新增 `DbConnectionWarmupService`（BackgroundService），啟動 2s 後呼叫 `CanConnectAsync()` 消除 Oracle 冷啟動延遲

## 8.1.15 (2026-03-04)

* **測試：** 50+ MSTest 測試，覆蓋 BaseCRUDVM、BasePagedListVM、BatchVM、RBAC、TokenService、PasswordHelper
* **CI：** 新增覆蓋率流水線（dotnet-coverage + Coverlet）
* **修正：** JWT `GenerateAccessToken` 新增 `jti` claim（`Guid.NewGuid().ToString("N")`），避免同秒產生相同 Token

## 8.1.14 (2026-03-04)

* **非同步（ASYNC-1）：** 消除全部 `.Result` / `.Wait()` 同步等待，解決 ThreadPool 飢餓風險
* **依賴（DEPS-1）：** EF Core → 8.0.22, Quartz → 3.16.0, Elsa → 2.15.2, Swashbuckle → 6.6.2
* **程式碼品質（QUALITY-1）：** 更新 `.editorconfig` 規則
* **程式碼品質（QUALITY-2）：** 啟用 Nullable，168 個遺留檔案加 `#nullable disable`
* **測試（TEST-1）：** 建立 xUnit 測試專案，17 個基礎測試
* **CI：** dotnet test TRX logger + artifact 上傳

## 8.1.13 (2026-03-04)

* **安全(Breaking Change)：** 密碼儲存從 MD5 升級至 PBKDF2（ASP.NET Core Identity PasswordHasher），自動向下相容舊 MD5 帳號；**部署前必須執行 db-migration-8.1.13.sql 以擴展 Password 欄位至 256 字元**
* **安全：** JWT 新增 Refresh Token 機制，支援 Token Rotation 防止重複使用，提供 /api/_account/refreshtoken 與 /api/_account/revoketoken 端點
* **依賴：** DotNetCore.NPOI 1.2.3 → NPOI 2.7.6
* **依賴：** 移除 .NET Core 2.1.x 舊版依賴套件
* **CI：** 新增 GitHub Actions 建置、測試與套件弱點掃描工作流

##8.1.12(2024-10-10)
* **修改：**   修复Blazor和Vue下多租户工作流的Bug

##8.1.10(2024-7-15)
* **修改：**   修复Layui导出查询条件不变的bug
* **修改：**   修复自带代码生成器Blazor生成代码大小写的问题

##8.0.6(2024-3-18)

* **修改：**   更新Elsa工作流到2.14.1，更好的支持.net8
* **修改：**   更新验证码的处理，避免linux缺少字体的问题，感谢DUWENINK提供

##8.0.4(2024-2-21)

* **修改：**   修复工作流的一些bug


##8.0.2(2024-2-1)

* **修改：**   修复EF8.0默认不支持sqlserver 2014一下的问题
* **修改：**   修复Layui模式下TreeTagHelper不选择也会提交空字符串的问题

* ##8.0.1(2024-1-19)
* **修改：**   修复工作流在Oracle中不能正常启动的问题

##8.0.0(2024-1-11)
* **修改：**  全面升级支持dotnet8



## v6.x.x

##6.5.1(2023-11-19)
* **修改：**  修复GetSelectItemList方法的bug
* **修改：**  修复LayUI Tab页中使用按钮组会导致Tab页切换失效的问题

##6.4.9(2023-11-14)
* **新增：**  AddWorkflow函数添加了option参数，可以添加自定义工作流节点

##6.4.8(2023-11-13)
* **新增：**  工作流审批节点增加角色，部门，部门负责人审批等选项
* **新增：**  工作流现在可以读取CRUDVM中的SetInclude指定的关联表
* **修改：**  删除表单数据时会自动删除相关的工作流数据
* **修改：**  修复单点登录时登陆主站可能引发异常的问题

##6.4.3(2023-10-7)
* **新增：**  LayUI下新增FlowInfoTagHelper，用来显示审批记录
* **修改：**  修复工作流在Sqlite中报错的问题

##6.4.1(2023-9-16)
* **新增：**  修复工作流在MySql数据库中报错的问题
* **修改：**  修复多租户用户使用不同域名登陆和切换的问题

##6.4.0(2023-9-3)
* **新增：**  添加工作流功能，老系统需要重新生成数据库，因为增加了一些系统表
* **修改：**  用户组名称更改为部门
* **修改：**  修复LayUI中TreeContainer的Bug

##6.3.30(2023-7-28)
* **修改：**  修复创建默认数据库时，默认菜单Api方法的菜单显示为false
* **修改：**  修复了LayUI模式联动会覆盖初始绑定的数据的问题
* **修改：**  继承BaseApiController的控制器没有权限的时候会返回403错误

##6.3.29(2023-6-29)
* **修改：**  默认的Json序列化，数字类型仍然会被输出为字符串，这是因为很多前端控件都需要对比字符串
* **修改：**  修复了LayUI模式下导出之后搜索的问题
* **修改：**  优化了LayUI模式下TreeContainer控件
* **修改：**  默认的Json序列化，添加了可为空的类型的读写判断


##6.3.25(2023-6-8)
* **新增：**  新增针对WtmPlus中生成的vue3的项目的简易代码生成器
* **修改：**  修复了多租户模式下GetUserDC方法
* **修改：**  修复了多租户模式下Excel导入的问题
* **修改：**  修复了LayUI模式下TreeContainer和Searcher组合查询的问题
* **修改：**  修改了默认的Json序列化，数字类型不再被序列化为字符串
* **修改：**  修改FileHandler，现在FileHandler中可以使用Wtm进行数据库操作

##6.3.22(2023-4-4)
* **修改：**  FileProvider加入异常判断
* **修改：**  修复LayUI模式中TreeTagHelper多选的问题
* **修改：**  修复IPersistPoco数据唯一性验证没有考虑IsValid字段的问题

##6.3.20(2023-2-19)
* **修改：**  修复日志记录的配置问题
* **修改：**  修复LayUI模式中重置搜索表单不能重置多个下拉菜单的问题
* **修改：**  修复LayUI模式中子表控件删除行之后赋值的问题
* **修改：**  修复LayUI模式中设置默认搜索条件后导出的问题


##6.3.19(2023-2-11)
* **修改：**  修复LayUI模式下联动Tree控件的选中问题
* **修改：**  优化BatchVM中批量修改的默认操作


##6.3.18(2023-2-6)
* **修改：**  修复LayUI模式下重置按钮不能重置下拉菜单的问题
* **修改：**  修复LayUI模式下子表控件对于日期类型的保存问题

##6.3.16(2023-2-2)
* **修改：**  修复LayUI模式下Tree控件的选中问题
* **修改：**  修复LayUI模式下DateTime控件绑定int的问题


##6.3.15(2023-1-30)
* **修改：**  更新Blazor控件库到最新版本
* **修改：**  修复Layui模式下的一些js错误
* **修改：**  修复Layui模式下TextBox的Change-Func和Done-Func无效的问题

##6.3.14(2023-1-13)
* **修改：**  修复多租户情况下上传和导入可能出现的异常
* **修改：**  修复Layui模式下Tree和Combobox的bug
* **修改：**  修复OSS上传图片没有自动指定ContentType的问题


##6.3.9(2022-11-17)
* **修改：**  系统自带的部门，角色，租户等表可以通过自定义类继承基类的方式增加字段


##6.3.8(2022-11-15)
* **修改：**  更新版本以适应新版的Bootstrap Blazor 7
* **修改：**  Blazor现在可以通过appsettings中的PageMode来设置默认是单一页还是多Tab页
* **修改：**  修复了LayUI子表控件超过10行删除数据的bug
* **修改：**  LayUI模式子表现在可以通过SetEditType方法设置子表控件是否只读
* **修改：**  LayUI模式子表现在可以通过SetEditType方法设置子表中日期控件的格式
* **修改：**  修复内置Login方法大小写判断的bug

##6.3.7(2022-10-19)
* **修改：**  更新版本以适应新版的Bootstrap Blazor控件库
* **修改：**  修复了SoftKey属性引发的bug
* **修改：**  增加了默认菜单表页面名称字段的长度
* **修改：**  修复了Blazor模式OpdenDialog方法

##6.3.4(2022-8-10)
* **修改：**  修复了主子表数据维护多租户没有正确赋值的问题
* **修改：**  修复了LayUI模式Selector控件显示不正确的问题
* **修改：**  修复了ListVM对特殊字符的过滤

##6.3.1(2022-7-25)
* **修改：**  修复了LayUI模式下Selector联动的问题
* **修改：**  修复了非外键关联的Include字段问题
* **修改：**  修复了多租户根据url判断租户的问题

##6.3.0(2022-7-22)
* **修改：**  优化Blazor的用户信息，提升页面效率
* **修改：**  配合BootStrapBlazor控件的最新版本，更新树形列表
* **修改：**  Blazor文件上传控件增加了文件下载
* **修改：**  修复多数据库导入的bug
* **修改：**  修复Layui模式Selector绑定非主键时显示的问题
* **修改：**  修复Layui模式Display控件的样式

##6.2.6(2022-7-5)
* **修改：**  修复Jwt登录时间验证的问题
* **修改：**  修复Layui模式中Tree控件禁用的问题
* **修改：**  移除过时引用
* **修改：**  修复主键类型为string时Crud的问题
* **修改：**  修复WtmJob Displose时的bug
* **修改：**  优化Blazor菜单，感谢akin的PR

##6.2.4(2022-6-16)
* **修改：**  优化登录
* **修改：**  修复WtmFileProvider直接使用的问题
* **新增：**  为配合WtmPlus的新功能，框架底层增加SoftKey,SoftFK属性，用于标记非主键关联的模型

##6.2.3(2022-6-12)
* **新增：**  QuartzRepeatAttribute增加了DelaySeconds参数，可以控制延迟多少秒启动服务
* **修改：**  恢复LoginUserInfo中的UserId以兼容老系统
* **修改：**  修复Layui模式下Combobox处理默认值的问题
* **修改：**  修复登录时Token会加长的问题
* **修改：**  现在DoEdit方法会自动检查继承TreePoco的模型，其父级ID不能被修改为本身ID
* **修改：**  增加了一些验证，避免中间件报错，提高性能

##6.2.2(2022-6-7)
* **HotFix：**  修复附件上传问题

##6.2.1(2022-6-7)

* **新增：**  新增CanNotEditAttribute，用于标记在模型属性上，指明该字段不应该被修改。
* **新增：**  优化VM内包含其他VM时框架默认的处理方法，现在框架默认会自动给子VM赋必须的值，并和表单提交的值对应。
* **修改：**  修复MainTenantOnly属性会导致权限失效的问题
* **修改：**  修复登录时Token会加长的问题
* **修改：**  修复Blazor模式无法删除租户的问题
* **修改：**  修复Blazor模式添加外部菜单的显示问题
* **修改：**  修复LayUI模式Combobox在Https下无法下载数据的问题

##6.2.0(2022-6-5)

本次为大版本更新，包含中断性更改，老项目升级时需要手动更新旧数据库以及覆盖默认生成的项目文件

* **新增：**  新增多租户支持，支持单数据库，独立数据库以及混合模式，使用方法参见文档 https://wtmdoc.walkingtec.cn/#/Global/MultiTenant
* **新增：**  新增单点登录支持，使用方法参见文档 https://wtmdoc.walkingtec.cn/#/Global/SSO
* **新增：**  新增统一用户，角色，用户组管理支持，WTM现在可以用来架构微服务风格的分布式系统。
* **新增：**  Layui和Blazor新增默认的多租户管理界面，其他UI后续会添加
* **新增：**  新增MainTenantOnlyAttribute，用于标记方法不能被子租户使用
* **修改：**  用户组修改为树形结构，可作为部门组织结构使用，为下一步工作流做好准备
* **修改：**  由于用户组修改为树形结构，用户组的数据权限也可以向下继承
* **修改：**  重构用户登录，重新登陆，权限验证等逻辑，更大程度上使用缓存，大幅提高性能
* **修改：**  用户表的其他字段现在会被自动读取到LoginUserInfo.Attributes中
* **修改：**  Blazor支持最新的BB控件库
* **修改：**  修复了文件上传的一些安全性问题
* **修改：**  优化导出操作
* **修改：**  修复Layui Combobox和Tree控件的一些bug
* **中断性修改：**  移除了系统自带的PersistedGrant表,简化了jwt登录流程，现在不再需要一个单独的RefreshToken来刷新Token，而是登陆后调用RefreshToken接口刷新当前用户的Token
* **中断性修改：**  系统自带的表，除了FrameworkMenu外，都新增了TenantCode字段
* **中断性修改：**  系统自带的FrameworkGroup字段发生了改变，变为树形结构，且增加了Manager字段
* **中断性修改：**  新增新的系统表FrameworkTenant
* **中断性修改：**  Appsettings文件中新增EnableTenant配置



##6.1.1(2022-4-1) 
* **新增：**  集成Quartz作业调度，为后续工作流所需内部定时任务做好准备，使用方法参见文档 https://wtmdoc.walkingtec.cn/#/Global/Quartz
* **修改：**  日志分类中增加了“作业”一项
* **修改：**  Layui模式下登录现在会统一调用/api/_account/login方法，为后续单点登录做好准备
* **修改：**  现在当用户缓存失效时，框架会自动调用/api/_account/login或/api/_account/loginJwt获取用户信息，如无特殊情况，不需要再重写ReloadUser方法了
* **修改：**  修复layui数据权限维护时下拉菜单的bug
老项目更新时，除了升级Nuget包，还应该通过官网或Plus生成新项目，将系统自带的Controller和View覆盖一下

##6.0.7(2022-3-21) 
* **HotFix：**  修复使用用xmselect引发的联动和TreeContainer的bug

##6.0.6(2022-3-20) 
* **修改：**  使用xmselect控件重写了Layui的Combobox和Tree，老用户更新的时候需要手动添加xmselect的js文件：https://gitee.com/maplemei/xm-select/releases/v1.2.4
把这个文件放到wwwroot/layui下面。LayUI项目的用户还需要手动在/Views/Shared/_Layout.cshtml文件中加入对这个js的引用， 其他UI类型的项目不需要，但是因为其他UI类型的项目代码生成器也是用的layui的界面，所以还是需要copy xm-select的js文件到wwwroot/layui下面的

* **修改：**  Layui模式下修复了数据列表指定line-height的bug
* **修改：**  Layui模式下MakeViewButton方法现在可以生成更美观的图片预览
* **修改：**  验证码现在会自动读取系统安装的字体，linux下部署的时候不会出现找不到字体的问题了
* **修改：**  Blazor项目默认使用自带的字体文件
* **修改：**  修复Blazor项目中向localstorage存储大量数据报错的问题
* **修改：**  Blazor的弹出窗口现在可以最大化和拖动
* **修改：**  修复Blazor项目中切换多语言有可能报错的问题

##6.0.5(2022-2-28) 
* **修改：**  默认初始化数据中加入了普通用户角色
* **修改：**  优化了数据权限查询语句
* **修改：**  修复登录用户保存信息是并发的问题
* **修改：**  修复了layui模式radio控件绑定空值的bug


##6.0.2(2021-12-26) 
* **修改：**  修改Blazor默认代码生成，适应BootstrapStrap 6.x新版本
* **修改：**  再次更新数据权限逻辑，新的默认逻辑为，如果设定了数据权限的表本身继承了BasePoco，而且没有给当前用户配置任何数据权限，那么用户默认可以看到自己加的数据。如果给用户配置了数据权限，则根据数据权限的配置显示数据，不管是否是当前用户添加的。
* **修改：**  修改了layui transfer控件绑定数据的bug
* **修改：**  修改了layui多选控件设置必填的bug

## v5.x.x

##5.10.30(2023-7-28)
* **修改：**  修复创建默认数据库时，默认菜单Api方法的菜单显示为false
* **修改：**  修复了LayUI模式联动会覆盖初始绑定的数据的问题
* **修改：**  继承BaseApiController的控制器没有权限的时候会返回403错误


##5.10.29(2023-6-29)
* **修改：**  默认的Json序列化，数字类型仍然会被输出为字符串，这是因为很多前端控件都需要对比字符串
* **修改：**  修复了LayUI模式下导出之后搜索的问题
* **修改：**  优化了LayUI模式下TreeContainer控件
* **修改：**  默认的Json序列化，添加了可为空的类型的读写判断

##5.10.25(2023-6-8)
* **新增：**  新增针对WtmPlus中生成的vue3的项目的简易代码生成器
* **修改：**  修复了多租户模式下GetUserDC方法
* **修改：**  修复了多租户模式下Excel导入的问题
* **修改：**  修复了LayUI模式下TreeContainer和Searcher组合查询的问题
* **修改：**  修改了默认的Json序列化，数字类型不再被序列化为字符串
* **修改：**  修改FileHandler，现在FileHandler中可以使用Wtm进行数据库操作

##5.10.22(2023-4-4)
* **修改：**  FileProvider加入异常判断
* **修改：**  修复LayUI模式中TreeTagHelper多选的问题
* **修改：**  修复IPersistPoco数据唯一性验证没有考虑IsValid字段的问题

##5.10.20(2023-2-19)
* **修改：**  修复日志记录的配置问题
* **修改：**  修复LayUI模式中重置搜索表单不能重置多个下拉菜单的问题
* **修改：**  修复LayUI模式中子表控件删除行之后赋值的问题
* **修改：**  修复LayUI模式中设置默认搜索条件后导出的问题

##5.10.19(2023-2-11)
* **修改：**  修复LayUI模式下联动Tree控件的选中问题
* **修改：**  优化BatchVM中批量修改的默认操作

##5.10.18(2023-2-6)
* **修改：**  修复LayUI模式下重置按钮不能重置下拉菜单的问题
* **修改：**  修复LayUI模式下子表控件对于日期类型的保存问题

##5.10.16(2023-2-2)
* **修改：**  修复LayUI模式下Tree控件的选中问题
* **修改：**  修复LayUI模式下DateTime控件绑定int的问题


##5.10.15(2023-1-30)
* **修改：**  更新Blazor控件库到最新版本
* **修改：**  修复Layui模式下的一些js错误
* **修改：**  修复Layui模式下TextBox的Change-Func和Done-Func无效的问题

##5.10.14(2023-1-13)
* **修改：**  修复多租户情况下上传和导入可能出现的异常
* **修改：**  修复Layui模式下Tree和Combobox的bug
* **修改：**  修复OSS上传图片没有自动指定ContentType的问题

##5.10.9(2022-11-17)
* **修改：**  系统自带的部门，角色，租户等表可以通过自定义类继承基类的方式增加字段

##5.10.8(2022-11-15)
* **修改：**  更新版本以适应新版的Bootstrap Blazor 7
* **修改：**  Blazor现在可以通过appsettings中的PageMode来设置默认是单一页还是多Tab页
* **修改：**  修复了LayUI子表控件超过10行删除数据的bug
* **修改：**  LayUI模式子表现在可以通过SetEditType方法设置子表控件是否只读
* **修改：**  LayUI模式子表现在可以通过SetEditType方法设置子表中日期控件的格式
* **修改：**  修复内置Login方法大小写判断的bug


##5.10.7(2022-10-19)
* **修改：**  更新版本以适应新版的Bootstrap Blazor控件库
* **修改：**  修复了SoftKey属性引发的bug
* **修改：**  增加了默认菜单表页面名称字段的长度
* **修改：**  修复了Blazor模式OpdenDialog方法

##5.10.4(2022-8-10)
* **修改：**  修复了主子表数据维护多租户没有正确赋值的问题
* **修改：**  修复了LayUI模式Selector控件显示不正确的问题
* **修改：**  修复了ListVM对特殊字符的过滤

##5.10.1(2022-7-25)
* **修改：**  修复了LayUI模式下Selector联动的问题
* **修改：**  修复了非外键关联的Include字段问题
* **修改：**  修复了多租户根据url判断租户的问题

##5.10.0(2022-7-22)
* **修改：**  优化Blazor的用户信息，提升页面效率
* **修改：**  配合BootStrapBlazor控件的最新版本，更新树形列表
* **修改：**  Blazor文件上传控件增加了文件下载
* **修改：**  修复多数据库导入的bug
* **修改：**  修复Layui模式Selector绑定非主键时显示的问题
* **修改：**  修复Layui模式Display控件的样式

##5.9.6(2022-7-5)
* **修改：**  修复Jwt登录时间验证的问题
* **修改：**  修复Layui模式中Tree控件禁用的问题
* **修改：**  移除过时引用
* **修改：**  修复主键类型为string时Crud的问题
* **修改：**  修复WtmJob Displose时的bug
* **修改：**  优化Blazor菜单，感谢akin的PR

##5.9.4(2022-6-16)
* **修改：**  优化登录
* **修改：**  修复WtmFileProvider直接使用的问题
* **新增：**  为配合WtmPlus的新功能，框架底层增加SoftKey,SoftFK属性，用于标记非主键关联的模型


##5.9.3(2022-6-12)
* **新增：**  QuartzRepeatAttribute增加了DelaySeconds参数，可以控制延迟多少秒启动服务
* **修改：**  恢复LoginUserInfo中的UserId以兼容老系统
* **修改：**  修复Layui模式下Combobox处理默认值的问题
* **修改：**  修复登录时Token会加长的问题
* **修改：**  现在DoEdit方法会自动检查继承TreePoco的模型，其父级ID不能被修改为本身ID
* **修改：**  增加了一些验证，避免中间件报错，提高性能

##5.9.2(2022-6-7)
* **HotFix：**  修复附件上传问题

##5.9.1(2022-6-7)

* **新增：**  新增CanNotEditAttribute，用于标记在模型属性上，指明该字段不应该被修改。
* **新增：**  优化VM内包含其他VM时框架默认的处理方法，现在框架默认会自动给子VM赋必须的值，并和表单提交的值对应。
* **修改：**  修复MainTenantOnly属性会导致权限失效的问题
* **修改：**  修复登录时Token会加长的问题
* **修改：**  修复Blazor模式无法删除租户的问题
* **修改：**  修复Blazor模式添加外部菜单的显示问题
* **修改：**  修复LayUI模式Combobox在Https下无法下载数据的问题


##5.9.0(2022-6-5)

本次为大版本更新，包含中断性更改，老项目升级时需要手动更新旧数据库

* **新增：**  新增多租户支持，支持单数据库，独立数据库以及混合模式，使用方法参见文档 https://wtmdoc.walkingtec.cn/#/Global/MultiTenant
* **新增：**  新增单点登录支持，使用方法参见文档 https://wtmdoc.walkingtec.cn/#/Global/SSO
* **新增：**  新增统一用户，角色，用户组管理支持，WTM现在可以用来架构微服务风格的分布式系统。
* **新增：**  Layui和Blazor新增默认的多租户管理界面，其他UI后续会添加
* **修改：**  用户组修改为树形结构，可作为部门组织结构使用，为下一步工作流做好准备
* **修改：**  由于用户组修改为树形结构，用户组的数据权限也可以向下继承
* **修改：**  重构用户登录，重新登陆，权限验证等逻辑，更大程度上使用缓存，大幅提高性能
* **修改：**  Blazor支持最新的BB控件库
* **修改：**  修复了文件上传的一些安全性问题
* **修改：**  优化导出操作
* **修改：**  修复Layui Combobox和Tree控件的一些bug
* **中断性修改：**  移除了系统自带的PersistedGrant表,简化了jwt登录流程，现在不再需要一个单独的RefreshToken来刷新Token，而是登陆后调用RefreshToken接口刷新当前用户的Token
* **中断性修改：**  系统自带的表，除了FrameworkMenu外，都新增了TenantCode字段
* **中断性修改：**  系统自带的FrameworkGroup字段发生了改变，变为树形结构，且增加了Manager字段
* **中断性修改：**  新增新的系统表FrameworkTenant
* **中断性修改：**  Appsettings文件中新增EnableTenant配置


##5.8.3(2022-4-1) 
* **新增：**  集成Quartz作业调度，为后续工作流所需内部定时任务做好准备，使用方法参见文档 https://wtmdoc.walkingtec.cn/#/Global/Quartz
* **修改：**  日志分类中增加了“作业”一项
* **修改：**  Layui模式下登录现在会统一调用/api/_account/login方法，为后续单点登录做好准备
* **修改：**  现在当用户缓存失效时，框架会自动调用/api/_account/login或/api/_account/loginJwt获取用户信息，如无特殊情况，不需要再重写ReloadUser方法了
* **修改：**  修复layui数据权限维护时下拉菜单的bug
* **修改：**  图形操作改用ImageSharp
老项目更新时，除了升级Nuget包，还应该通过官网或Plus生成新项目，将系统自带的Controller和View覆盖一下

##5.7.9(2022-3-21) 
* **HotFix：**  修复使用用xmselect引发的联动和TreeContainer的bug

##5.7.7(2022-3-20) 
* **修改：**  使用xmselect控件重写了Layui的Combobox和Tree，老用户更新的时候需要手动添加xmselect的js文件：https://gitee.com/maplemei/xm-select/releases/v1.2.4
把这个文件放到wwwroot/layui下面。LayUI项目的用户还需要手动在/Views/Shared/_Layout.cshtml文件中加入对这个js的引用， 其他UI类型的项目不需要，但是因为其他UI类型的项目代码生成器也是用的layui的界面，所以还是需要copy xm-select的js文件到wwwroot/layui下面的

* **修改：**  Layui模式下修复了数据列表指定line-height的bug
* **修改：**  Layui模式下MakeViewButton方法现在可以生成更美观的图片预览
* **修改：**  验证码现在会自动读取系统安装的字体，linux下部署的时候不会出现找不到字体的问题了
* **修改：**  Blazor项目默认使用自带的字体文件
* **修改：**  修复Blazor项目中向localstorage存储大量数据报错的问题
* **修改：**  Blazor的弹出窗口现在可以最大化和拖动
* **修改：**  修复Blazor项目中切换多语言有可能报错的问题

##5.7.6(2022-2-28) 
* **修改：**  默认初始化数据中加入了普通用户角色
* **修改：**  优化了数据权限查询语句
* **修改：**  修复登录用户保存信息是并发的问题
* **修改：**  修复了layui模式radio控件绑定空值的bug

##5.7.3 (2021-12-26) 
* **修改：**  再次更新数据权限逻辑，新的默认逻辑为，如果设定了数据权限的表本身继承了BasePoco，而且没有给当前用户配置任何数据权限，那么用户默认可以看到自己加的数据。如果给用户配置了数据权限，则根据数据权限的配置显示数据，不管是否是当前用户添加的。
* **修改：**  修改了layui transfer控件绑定数据的bug
* **修改：**  修改了layui多选控件设置必填的bug

##5.7.1 (2021-12-8) 
* **修改：**  还原了数据权限的默认逻辑
* **修改：**  修复了批量修改时将某些字段清空的bug
* **修改：**  修复了TreeContainer联动列表的bug
* **修改：**  修复了更新时因字段大小写引发的验证失败的bug

##5.7.0 (2021-12-2) 
* **修改：**  添加了BoolStringConverter，用于将字符串序列化为布尔值
* **修改：**  修复了导入时唯一性条件的判断
* **修改：**  现在用户缓存里会写入TenantCode，方便扩展多租户
* **修改：**  修复了layui控件中关于默认值的逻辑，现在绑定字段没有值的情况下才会使用默认值
* **修改：**  修复了子表控件在最大化弹出窗口中显示不完全的问题
* **修改：**  修复了全局错误处理会引发其他http错误的问题
* **修改：**  修复了某些情况下文件无法删除的问题
* **修改：**  其他配合WtmPlus的修改

##5.6.3 (2021-10-29) 
* **修改：**  修复了上个版本引发的权限配置搜索的bug
* **修改：**  修复了自带的代码生成器中生成BatchVM中的日期类型字段的错误

##5.6.1 (2021-10-27) 
* **修改：**  修复了上个版本引发的Selector报错的问题
* **修改：**  修复了Blazor模式下删除文件的bug

##5.6.0 (2021-10-26) 
* **修改：**  修复了数据权限读取的问题
* **修改：**  修复了layui模式下英文界面搜索后列表分页栏显示中文的问题
* **修改：**  修复了layui模式下修改了刷新列表会回到第一页的问题
* **修改：**  修复了批量导入时有的字段不识别的问题
* **修改：**  修复了公共页面配置无效的问题
* **修改：**  修复了Blazor模式下js加载的问题

##5.5.5 (2021-10-3) 
* **修改：**  修复了图表显示的一些问题
* **修改：**  修复了WtmContext中引用HttpContext造成非web项目使用报错的问题
* **修改：**  默认的Cors添加了Content-Disposition头
* **修改：**  修复了Blazor文件下载url错误的问题
* **修改：**  修复了Layui模式编辑菜单目录报错的问题
* **修改：**  修复了Layui Transfer控件默认值的bug
* **修改：**  修复了导出Excel时枚举描述为数字时产生的bug

##5.5.0 (2021-9-16) 
* **修改：**  升级自带的BootStrapBlazor到5.10.8版本，修复了wasm模式下刷新页面报错的问题
* **修改：**  修复了Blazor编写公开页面的问题
* **修改：**  修复了layui模式中一些控件的高度问题
* **修改：**  修复了layui模式环状图表会显示坐标的问题
* **修改：**  修改了Blazor模式中导入的默认代码
* **修改：**  修改了layui模式中Selector单选不能清空的问题

##5.4.9 (2021-9-12) 
* **修改：**  修复<wt:radio>控件绑定布尔值的bug
* **修改：**  新增生成测试数据的方法
* **修改：**  修复了Blazor模式公开页面的bug

##5.4.8 (2021-9-10) 
* **修改：**  Layui模式下SearchPanel可以通过设置ChartId来对图表进行数据搜索
* **修改：**  修复了在菜单管理中设置页面公开无效的问题
* **修改：**  修复了Blazor模式中导出有时报错的问题
* **修改：**  修复了默认的字符过滤逻辑，防止xss攻击

##5.4.7 (2021-9-4) 
* **修改：**  修复了Layui下图表在Tab页和弹出窗口中刷新的问题
* **修改：**  修复了Taghelper组合style有可能无效的问题

##5.4.6 (2021-9-3) 
* **新增：**  受益于WtmPlus的需求，Layui新增了图表控件，支持柱状，饼图，环状，折线和散点，以及多种主题。具体请参考文档。
* **修改：**  Layui默认登陆页面统一成React，Vue，Blazor相同的样式
* **修改：**  移除了Layui自带的echartjs，使用echart官方的版本
* **修改：**  修复了Layui模式下Radio和CheckBox绑定布尔值无效的bug
* **修改：**  修改了自带代码生成器生成单元测试的逻辑
* **修改：**  修复了偶发登录后又跳出的问题
* **修改：**  修复了Layui中维护菜单时添加api报错的问题


##5.4.5 (2021-8-27) 
* **修改：**  修复了Layui模式下上传图片控件异步加载的问题
* **修改：**  修复了Layui模式下ListVM中使用SetFixed方法会导致内部按钮不能点击的bug
* **修改：**  修改了框架默认的生成缩略图的逻辑
* **修改：**  Blazor模式下搜索框默认的打开状态现在可以正确读取appsettings里的全局设置
* **修改：**  修复了使用低版本IE无法登录的问题

##5.4.4 (2021-8-25) 
* **修改：**  增强了Layui模式下图片上传控件的预览效果，感谢Rea同学的代码
* **修改：**  修复Layui模式下可能出现的联动的问题
* **修改：**  Layui模式下现在可以在combobox,radio,checkbox 和 transfer控件上使用 item-url 来替代items实现类似前后端分离的获取初始数据的效果
* **修改：**  AddWtmSwagger方法现在可以设置一个参数来指定是否使用全类名
* **修改：**  更新了框架对.netcore的相关类库依赖到5.0.9
* **修改：**  自带的代码生成器会提示使用WtmPlus可以提高工作效率，哈哈。

##5.3.8 (2021-8-11) 
* **修改：**  修复GetTreeSelectListItems方法数据权限过滤的bug
* **修改：**  修复LayUI模式下联动的问题
* **修改：**  修复Blazor模式下代码生成器的一些问题

##5.3.5 (2021-8-3) 
* **HotFix：**  修复Blazor代码生成器

##5.3.4 (2021-8-3) 
* **修改：**  优化了layui模式中联动的功能，多选下拉菜单现在不受联动的影响
* **修改：**  Blazor模式更新到BootStrap Blazor控件库的最新版本
* **修改：**  修复了主子表同时修改的一些bug

##5.3.2 (2021-7-26) 
* **修改：**  修复了主子表导入的问题
* **修改：**  修复了用户组中设置数据权限的bug
* **修改：**  为配合WtmPlus的发布，layui模式中的upload和multiupload现在可以点击预览图看大图，并支持disabled模式
* **修改：**  默认生成的代码中RefreshToken方法不再需要登陆

##5.3.1 (2021-7-18) 
* **hotfix：**  修复了上一个版本引发的Selector不能正确显示的bug

##5.3.0 (2021-7-18) 
* **修改：**  修复了有些地址权限控制无法识别的bug
* **修改：**  默认的用户加载现在会读取用户名和头像
* **修改：**  修复了一些EF生成的语句不支持sqlite的问题
* **修改：**  日志现在可以根据当前的数据库连接字符串记录到不同的数据库里
* **修改：**  Blazor模式中配置外部链接现在可以正常显示
* **修改：**  Blazor模式中在页面上添加 @layout EmptyLayout 和 @attribute [Public] 属性，可以制作不受权限控制的独立的页面

##5.2.8 (2021-7-12) 
* **修改：**  修复了layui模式下在弹出窗口刷新grid的问题
* **修改：**  修复了Blazor模式下关联主表后提交表单失败的问题
* **修改：**  修复了Vue模式下导出错误的问题

##5.2.7 (2021-7-4) 
* **修改：**  修复了layui模式下表单错误的显示位置问题
* **修改：**  修复了初始化默认数据有可能报错的问题
* **修改：**  TokenService现在会默认使用名为tokendefault的连接字符串，便于将token相关表保存在其他数据库或内存中

##5.2.6 (2021-6-27) 
* **修改：**  修复了layui模式下设置组件style和class有时无效的问题
* **修改：**  Layui模式下添加了wt:card组件
* **修改：**  修复了Wtm中的Cache依赖HttpContext的问题

##5.2.5 (2021-6-20) 
* **修改：**  修复了layui模式下slider控件无法提交的bug
* **修改：**  修复了GetSelectListItems and GetTreeSelectListItems 方法在sqlite下有可能引发错误的bug

##5.2.4 (2021-6-9) 
* **修改：**  修复了Layui的Selector读取数据的bug
* **修改：**  修复了Layui的Display没有自动换行的问题
* **修改：**  修改了Blazor项目默认代码，更新了BB版本，并修复了访问不存在的地址会报错的问题

##5.2.3 (2021-6-1) 
* **修改：**  修复了项目默认代码Blazor模式中配置角色页面权限的bug
* **修改：**  修复了项目默认代码删除用户时没有同时删除角色及用户组关联数据的bug
* **修改：**  修复了项目默认代码LoginJwt方法对密码进行了小写操作的bug
* **修改：**  修复了Layui模式中Selector控件只能绑定id值的bug
* **修改：**  默认生成的Blazor项目更改为server模式

##5.2.2 (2021-5-24) 
* **修改：**  修复了代码生成器生成单元测试的一些bug
* **修改：**  优化了DC.RunSql函数，现在可以在事务中间使用DC.RunSql
* **修改：**  更新了默认项目中的FrameworkUserVM，使其在修改和删除用户时刷新用户权限缓存。
* **修改：**  更新了Blazor项目默认代码，修复了修改密码无法找到Api的问题，修复了wasm模式中必须指定后台地址的问题
* **修改：**  修复了Layui模式中Selecor设置disable无效的问题
* **修改：**  Layui模式中，子表Grid单元格编辑时，现在可以设置SetEditType(EditTypeEnum.Datetime)来支持日期选择
* **修改：**  优化了数据唯一性的查询语句，修复了mysql中生成的语句报错的问题

##5.2.1 (2021-5-15) 
* **修改：**  修复了代码生成器生成中间表相关ViewModel和页面错误的bug
* **修改：**  修复了BaseCrudVM中默认修改方法没有过滤掉[NotMapped]字段的错误

##5.2.0 (2021-5-14) 
* **新增：**  新增对Blazor的支持，现在可以在官网生成Blazor模式的项目，代码生成器现在也可以生成Blazor的代码，具体请见文档http://wtmdoc.walkingtec.cn/#/Blazor/Intro
* **新增：**  [ActionDescription]中加入了IsPage属性，设置了这个属性的方法可以在菜单管理中添加，解决了一个Controller下只能配置一个主页面的问题
* **修改：**  修改了代码生成器的一些内部实现
* **修改：**  修复了默认代码中非管理员设置数据权限出现的错误
* **修改：**  修复了DynamicDataConverter读取数据报错的问题
* **修改：**  修复了在配置文件中将jwt的SecurityKey设置的过短会报错的问题
* **修改：**  修复了Layui模式下，搜索框折叠后下方列表错位的问题

##5.1.9 (2021-4-30) 
* **修改：**  移除了默认的 wt:grid onedit 方法重载，用户可以自己写js来实现功能
* **修改：**  修复了OssFileHandler读取文件时没有正确使用groupname的问题
* **修改：**  修复了layui模式下selector没有正确读取[FixedConnection]中指定DC的问题
* **修改：**  修改了BaseCrunVM中的DoEdit方法中主子表同时开启事务修改数据会报错的问题
* **修改：**  修改了api提交多层嵌套的json数据，有时没有正确验证的问题
* **突变：**  修改了读取appsettings文件的问题，老用户可以在线生成新项目，替换默认的program和startup文件

##5.1.7 (2021-4-19) 
* **修改：**  修复了json同时提交主子表数据不能自动更新子表的bug
* **修改：**  修复了更新失败时没有返回错误信息的bug

##5.1.6 (2021-4-18) 
* **修改：**  默认Json序列化加入了JsonNumberHandling.WriteAsString
* **修改：**  默认Json序列化加入了自定义日期，去掉了默认日期和时间之间的那个T
* **修改：**  修复了导入时IsOverWriteExistData=false的时候没有验证重复数据的bug
* **修改：**  修复了导入时已经删除的PersistPoco会被更新的bug

##5.1.4 (2021-4-11) 
* **修改：**  修复了数据权限指向自定义ID表时无法设置的bug
* **修改：**  现在生成的新项目使用微软默认的加载config文件的方式
* **修改：**  appsettings文件中的CookieOptions里面可以指定"Domain"，比如"Domain":".abc.com",可以允许所有abc.com的二级域名设置cookie，解决前后台分别部署在不同域名设置cookie无效的问题。
* **修改：**  修复了重启服务获取不到userid的问题
* **修改：**  ISubFile中的order字段改为Order，纯粹是强迫症，老项目可以批量替换一下，影响不大
* **修改：**  修复了React模式下数据权限报错的问题
* **修改：**  修复了获取中文文件名的一些问题

##5.1.2 (2021-4-6) 
* **修改：**  新生成的项目加入了默认的favicon文件
* **修改：**  新生成的Layui项目更新至layui至2.6.3
* **修改：**  新生成的Vue项目修复了设置默认搜索条件不生效的问题
* **修改：**  修复了预览pdf文件时不显示的问题
* **修改：**  更新了BaseImportVM中的报错逻辑
* **修改：**  TreePoco中加入了HasChildren字段

对于在线新建项目时生成的默认文件，更新时可以生成一个新项目，并将生成的默认文件覆盖老项目

##5.1.1 (2021-3-28) 
* **修改：**  wt:image现在也可以显示label，默认不显示，通过设置hide-label=false来显示label
* **修改：**  ListVM种和wt:display中显示时间时，使用yyyy-MM-dd或者yyyy-MM-dd HH:mm:ss作为默认格式
* **修改：**  修复了wt:tree显示上的一些bug
* **修改：**  修复了代码生成器生成多对多假删除时的逻辑

##5.1.0 (2021-3-22) 
* **修改：**  修复了wtm.CreateDC时，connectionstring名称只能认小写字母的bug
* **修改：**  修复了_framework/getfile方法有时不能显示附件的bug
* **修改：**  代码生成时加入了一些模型字段验证，可以更快的发现模型设计的问题
* **修改：**  修复了代码生成器生成单元测试的一些bug

##5.0.9 (2021-3-14) 
* **修改：**  现在导入的模板会优先从ImportVM中字段的[DisplayName]上读取文字作为模板的列名
* **修改：**  修复在事务中操作附件造成阻塞的bug
* **新增：**  新增DynamicData类，在DC.RunSql<>和wtm.CallApi<>以及其他自定义方法中都可以使用DynamicData作为泛型，框架会自动序列化这种类型的变量。当我们懒得为了返回格式化的json定义新类的时候，可以使用这个类。
* **修改：**  修复了导入时唯一性验证的bug
* **修改：**  修复了导入时必填验证的bug
* **修改：**  修复了layui模式下在空间中指定Required无效的问题
* **修改：**  修复了layui模式下Combobox在禁用状态下指定emptytext无效的问题
* **修改：**  修复了layui模式下多个selector冲突的问题
* **修改：**  修复了layui模式下对于列名相同的不同ListVM设置背景色冲突的问题
* **修改：**  修复了默认项目生成的一些基础代码

##5.0.8 (2021-3-6) 
* **修改：**  现在附件保存在本地时，如果配置的是相对路径，数据库中也会记录相对路径，便于迁移
* **新增：**  修复了导出枚举字段没有正确显示文字的问题
* **修改：**  修复了layui模式中FF.RefreshPage()不能正确刷新页面的问题
* **修改：**  修复了layui模式中登陆过期后，重新登陆跳转到原有页面地址错误的问题。（老项目可以从官网重新生成新项目，然后替换HomeVms中的LoginVM文件）
* **修改：**  修改了默认生成的单元测试项目中的MockUtility文件，可测试ModelState中保存的内容

##5.0.7 (2021-2-27) 
* **修改：**  修复React修改当前用户密码失败的bug
* **新增：**  修复了React登录失败时的文字提示
* **修改：**  修复了代码生成器对于非Guid主键的关联表也会生成new Guid()语句的bug
* **修改：**  修复了有的空菜单目录没有隐藏的bug
* **修改：**  修复了Layui中对enum字段使用SetFormat不生效的bug
* **修改：**  修复了Layui中对指定NeedPage=false的不分页的列表，再次点击搜索还是会分页的bug
* **修改：**  修复了excel导入时没有验证模型字段的bug

##5.0.6 (2021-2-21) 
* **修改：**  修复了ListVM处理枚举列的一个小bug
* **新增：**  AddWtmMultiLanguages函数现在可以指定一个option，用来指定自定义的多语言文件
* **修改：**  现在默认允许集合结尾多写逗号的json格式
* **修改：**  修复了React模式下switch控件不选择无法提交的bug
* **修改：**  oracle终于支持.net5了，更新了对他的引用，现在不会再报警告了
* **突变：**  移除了默认项目中引用的Microsoft.EntityFrameworkCore.Tools和Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation，因为他们已经在wtm的dll中引用了。5.0以上的项目可以手动从依赖中删除这两个包。

##5.0.4 (2021-2-7)
* **HotFix：**  紧急修复5.0.3版本在各层分离的多项目模式下无法登录的bug

##5.0.3 (2021-2-7)
* **突变：**  用了新的方式修复了用户表不能自定义主键的问题。老版本更新后请手动将原始代码中的FrameworkUesrBase的引用统一更新成FrameworkUser，或者下载新项目把公共部分copy过去
* **突变：**  wtm.CallApi方法重构，增加了返回code，错误信息等字段。默认文件中HomeController中的GetGithubInfo方法使用了CallApi，老版本请手动将返回值修改为返回CallApi的结果.Data
* **新增：**  Layui的文件预览现在可以播放mp4的附件
* **新增：**  ImportVM中的GenerateTemplate现在可以重写，通过重写该方法可以指定上传模板的文件名
* **修改：**  修复了api中Json序列化引起的循环引用的错误

##5.0.2 (2021-1-31)
* **新增：**  新增IBasePoco和IPersistPoco两个接口，提升定义模型的灵活性。比如继承了TreePoco的树形模型如果同时实现IBasePoco框架也会自动设置添加人，添加事件，修改人，修改时间。详情请查阅文档中模型定义的部分。
* **新增：**  支持发布为单一文件
* **修改：**  修复了<wt:switch>为false时没有正确提交的bug
* **修改：**  修复了删除主表有时没有正确删除附件子表中的文件的bug
* **修改：**  修复了<wt:selector>中combobox赋值的bug
* **修改：**  现在api默认也会过滤<>
* **修改：**  修复了某些情况下上传大文件会被限制的bug
* **修改：**  修复了VUE使用element控件多次刷新界面的bug

##5.0.1 (2021-1-23)
* **修改：**  修复了<wt:Transfer>控件绑定字符串数组的bug
* **修改：**  修改了<wt:MultiUpload>控件，使其正确的删除文件
* **修改：**  修改了<wt:Combobox>控件默认值的bug
* **修改：**  修复了代码生成器生成单元测试代码的问题
* **修改：**  修复了代码生成器生成修改页面时默认文字的多语言错误
* **修改：**  新生成的项目自带的layui版本升级到2.5.7
* **修改：**  新生成的项目Admin模块中增加了用户批量修改角色的代码

##5.0.0 (2021-1-17)
* **新增：**  全面支持.net 5.0
* **新增：**  全新的WtmContext类
* **新增：**  针对性能做了大幅代码优化，访问速度肉眼可见的提高
* **新增：**  重构了文件上传和下载的功能，内置支持阿里云OSS
* **修改：**  移除了对NewtonJson的引用，使用微软默认的System.Text.Json
* **修改：**  Startup文件回归
* **修改：**  内置管理模块的代码直接包含在项目中
* **修改：**  将FrameworkUser用户表提取出来直接生成在项目中，便于大家扩展
* **修改：**  修改了内置一些数据库表结构，为后续功能扩展做好准备

## v3.x.x （2.x.x同步更新）

##3.8.0 以及 2.8.0 (2020-12-27)
* **新增：**  增加了CallApiStream方法用户调用返回二进制数组的api
* **修改：**  修复了枚举类型在多表头列表导出时不显示的问题
* **修改：**  修复了SubmitButton上添加ComformText不提交的问题
* **修改：**  修复了ImportVM中SetDuplicateCheck方法的bug，框架现在会优先使用ImportVM中的SetDuplicateCheck方法
* **修改：**  PersistPoco中IsValid字段默认值改为true
* **修改：**  修复了按钮无法指定layui-btn-normal样式的问题

##3.7.9 以及 2.7.9 (2020-11-29)
* **修改：**  修复layui模式下搜索没有重置页码的bug
* **修改：**  修复DpWhere方法在某些情况下报错的bug

##3.7.8 以及 2.7.8 (2020-11-24)
* **修改：**  彻底修复GetProperty反射与自定义主键引发的一些冲突


##3.7.7 以及 2.7.7 (2020-11-21)
* **修改：**  修复了DpWhere方法的一些bug
* **修改：**  Layui默认的数据权限管理添加了搜索功能
* **修改：**  <wt:grid>增加了auto-search属性，用来控制是否自动进行搜索，默认是true
* **修改：**  优化了一些框架自动生成的表达式语句
* **修改：**  将内部引用的.netcore mvc和ef相关的包升级到了最新版

##3.7.6 以及 2.7.6 (2020-11-15)
* **修改：**  TreeContainer现在可以设置默认选中的项目，通过设置TreeSelectListItem中的Selected属性
* **修改：**  修复了数据权限在过滤自定义主键的模型时的bug
* **修改：**  修复若干小问题

##3.7.5 以及 2.7.5 (2020-10-19)
* **修改：**  Layui模式下Selector控件也可以使用trigger-url和link-field来联动其他控件了（鸣谢AaronLucas)
* **修改：**  修复了多表头列表导出excel时表头显示错误的问题
* **修改：**  修复VUE左侧菜单收缩后不显示图标的问题

##3.7.4 以及 2.7.4 (2020-9-22)
* **修改：**  修复Layui模式下多选Combobx联动的问题
* **修改：**  优化CheckBetween方法所生成的sql语句
* **修改：**  修复ListVM中以数组作为列生成的json错误的问题
* **修改：**  修复了使用保存的cookie在登出后仍然可以访问系统的问题

##3.7.3 以及 2.7.3 (2020-9-12)
* **修改：**  修复Layui模式下数据权限管理页面的bug
* **修改：**  修复Layui模式下用户管理无法取消角色和用户组的bug
* **修改：**  修复<wt:tree>控件无法取消选择的bug
* **修改：**  修复链接多个数据库时有一个无法连接就会导致系统无法启动的问题
* **修改：**  修复ListVM在对PersistPoco搜索时如果数据源不是PersistPoco报错的问题

##3.7.2 以及 2.7.2 (2020-9-7)
* **修改：**  修改默认跨域逻辑，不配置跨域信息会默认允许所有跨域
* **修改：**  修复RefreshToken方法在pgsql中报错的问题
* **修改：**  修复菜单管理模块显示的bug，以及修改菜单会造成其他角色都有权限的bug
* **修改：**  修复多表头导出时表头显示的问题
* **修改：**  修改导入逻辑，默认不启用sqlserver的bulkcopy，可以通过在ImportVM中设置UseBulkSave属性来启用

##3.7.1 以及 2.7.1 (2020-8-26)
* **新增：**  Layui模式下新增<wt:colorpicker>颜色选择器，参考文档 https://wtmdoc.walkingtec.cn/#/UI/ColorPicker
* **修改：**  修复<wt:checkbox>多选显示的bug
* **修改：**  修复了一些layui模式下自带的系统管理页面
* **修改：**  修复了导出excel时数字格式为null值时报错的问题
* **修改：**  修复了FixConnectionAttribute没有优先检查方法的问题
* **修改：**  修复了IssueTokenAsync方法并发时报错的问题

##3.7.0 以及 2.7.0 (2020-8-18)

* **修改：**  导出excel时，如果ListVM中设置了多表头，导出的excel也会显示多表头
* **修改：**  修复了3.x版本中ListVM调用存储过程报错的问题，并更新了相关文档
* **修改：**  修复了layui下SearchPanel的重置按钮没有正确重置表单的问题
* **修改：**  修复了layui下多表头的列表显示错位的问题
* **修改：**  新生成的3.x的项目添加了RuntimeCompilation，并默认设置为调试模式下Razor页面可以动态编译
* **修改：**  修复VUE和React模式中菜单管理设置为不显示菜单时仍然显示的错误
* **修改：**  修复VUE和React模式中获取和显示文件的接口不受IsFilePublic属性控制的问题

##3.6.9 以及 2.6.9 (2020-7-23)
* **修改：**  修改了PersistPoco的假删除逻辑
* **修改：**  修复了layui下使用TreeContainer搜索，搜索条件会消失的bug
* **修改：**  导出excel时，如果时数字格式，现在会自动把excel的列设置成数值格式，方便再加工
* **修改：**  修复了使用代码生成器生成api时会生成一些无用代码的bug
* **修改：**  layui下<wt:grid>增加了line-height属性，可以指定行高，适合列表显示图片之类的情况使用
* **修改：**  ListVM中DateTime类型的字段默认格式改回yyyy-MM-dd hh:mm:ss
* **修改：**  修复了admin中修改用户密码，输入过长时显示的bug
* **修改：**  修复VUE中代码生成列表搜索框默认控件数量不对的bug
* **修改：**  修复VUE中列表没有充满屏幕的bug

##3.6.8 以及 2.6.8 (2020-7-6)
* **修改：**  增强了对oracle的支持，注：3.x版本的oracle仍然是beta版，可跑起框架，但可能有未知问题
* **修改：**  修复了layui搜索相关的一些bug
* **修改：**  代码生成器现在对于bool类型的变量在layui下默认使用<wt:switch>控件
* **修改：**  ListVM中DateTime类型的字段默认使用yyyy-MM-dd的格式
* **修改：**  appsettings中的UploadLimit修改为long类型，可是指定更大的数字
* **修改：**  修复VUE中没有角色的用户登录时重复刷新的bug
* **修改：**  修复VUE中列表没有充满屏幕的bug

##3.6.7 以及 2.6.7 (2020-6-30)
3.6.6/2.6.6 热更新

##3.6.6 以及 2.6.6 (2020-6-30)
* **修改：**  修复sqlserver bulk导入时枚举类型没有正确赋值的问题
* **修改：**  Layui修复了Combobox，CheckBox，Radio等控件设定default-value无效的问题
* **修改：**  Layui修复了Combobox禁用无效的问题
* **修改：**  Layui修复了取消某个搜索条件仍然按之前条件搜索的问题
* **修改：**  Layui修复了按钮组的显示问题
* **修改：**  VUE修复了编辑后再添加id重复的问题
* **修改：**  VUE修复了数据权限管理页面错误的问题
* **修改：**  VUE和React修复了菜单有目录的情况下排序的问题
* **修改：**  修改了一些多语言英文文本

##3.6.5 以及 2.6.5 (2020-6-15)
* **修改：**  修复操作列和菜单的多语言问题
* **修改：**  修复一对多删除时某些情况下失败的问题
* **修改：**  修复代码生成器生成的api单元测试报错的问题

##3.6.4 以及 2.6.4 (2020-6-3)
* **修改：**  修复了默认初始化数据找不到Action报错的问题
* **修改：**  修复了代码生成器在关联多个外键的同名字段时，生成的列表显示错误的问题
* **修改：**  修复了权限认证时没有正确处理Async方法的问题
* **修改：**  修复了LayUI模式下SetBindVisiableColName失效的问题
* **修改：**  修复了VUE模式下菜单模块的多语言显示错误的问题
* **修改：**  修复了React模式下一些文字错误

##3.6.3 以及 2.6.3 (2020-5-26)
* **修改：**  修复了上一版本引发的搜索报错的问题
* **修改：**  修复了bulk导入的一个小bug
* **修改：**  代码生成器生成页面时加入了多语言，老项目请在_ViewImports.cshtml文件中加入一行 @using Microsoft.Extensions.Localization;

##3.6.2 以及 2.6.2 (2020-5-25)
* **修改：**  修复了包含自定义列名的模型导入失败的bug
* **修改：**  修复了Index页面没有正确判断页面权限的bug
* **修改：**  现在Searcher也可以写Validate方法，查询条件后台返回的错误可以正确显示
* **修改：**  修复了控制台Log没有显示时间的bug
* **修改：**  修复了React和Vue配置页面权限的文字错误

##3.6.1 以及 2.6.1 (2020-5-22)
3.6.0/2.6.0 的热更新，修复代码生成器生成Controller的一个bug

##3.6.0 以及 2.6.0 (2020-5-22)
* **新增：**  导出优化，支持xlsx格式，单个excel文件现在最大可导出100万行，可设置单个文件最大行数，超过最大行数时会自动下载包含多个excel文件的zip包。详情请参见文档https://wtmdoc.walkingtec.cn/#/VM/Export
* **新增：**  导入优化，支持xlsx格式，支持公式，使用sqlserver时自动使用bulk导入，提高大批量数据的导入速度。
* **新增：**  ListVM中的MakeGridHeader方法现在可以正确绑定任何其定义lambda表达式
* **新增：**  修复有关联关系的数据无法正常删除的bug
* **修改：**  Layui模式中所有按钮的TagHelper现在都可以指定confirm-text来弹出一个询问框
* **修改：**  Layui模式修复默认下载按钮失效的bug
* **修改：**  Layui模式修复Display TagHelper绑定附件时显示错误的bug
* **修改：**  React模式修复代码生成器生成一对多控件时的问题
* **修改：**  Vue模式增加多语言支持
* **修改：**  VUE模式修复一些近期反馈的小bug



##3.5.7 以及 2.5.7 (2020-5-6)
* **新增：**  SubmitButton中新增SubmitUrl属性，用于多个提交按钮提交到不同的地址
* **新增：**  BaseController和BaseApiController增加可重写的GetLoginUserInfo方法，用于自定义用户认证
* **修改：**  优化认证逻辑，加快响应速度
* **修改：**  修复jwt无效时返回登录界面的错误，现在可以正确返回401，修复jwt token过期时间不准确的问题
* **修改：**  完善多语言支持
* **修改：**  修复DoDelete和SetInclude冲突的bug
* **修改：**  VUE修复菜单空目录bug
* **修改：**  VUE修复权限配置和搜索的bug
* **修改：**  React完善多语言支持

##3.5.6 以及 2.5.5 (2020-4-13)
* **新增：**  ConnectionString配置中可以设置Version字段，用于控制mysql的具体版本
* **修改：**  移除了动态控制器，因为和动态编译页面产生冲突
* **修改：**  IsFilePublic现在可以正常工作
* **修改：**  更新了默认生成的VUE项目代码，修复了一些bug

##3.5.4 以及 2.5.4 (2020-4-3)
* **新增：**  新增了动态控制器，老项目需要手动在Project文件的 \<PropertyGroup\>中加入\<PreserveCompilationReferences>true</PreserveCompilationReferences\>节点

* **修改：**  修复vue代码生成没有正确生成某些api的bug
* **修改：**  修复vue自带系统管理中的一些bug，整体更稳定
* **修改：**  IsFilePublic配置在3.x下可以正常工作
* **修改：**  修复框架自带GetFile和ViewFile方法无法正常调用的bug

##3.5.2 以及 2.5.2 (2020-3-29)
* **修改：**  修复vue代码生成下拉菜单少了一个逗号的bug
* **修改：**  修复vue发布时的问题
* **修改：**  修复vue列表高度计算的问题
* **修改：**  修复vue数据权限列表的删除bug

* **新增：**  Layui. 现在ListVM中的GridAction可以通过SetButtonClass方法设置按钮颜色
* **新增：**  Layui. UIService中新增MakeButton方法替换之前有问题的MakeRedirectButton方法
* **修改：**  修复GetGridActions会被调用两次的问题（这其实是.netcore的bug...)


##3.5.1 以及 2.5.1 (2020-3-26)
* **修改：**  修复vue菜单相关的一些bug
* **修改：**  修复vue代码生成器对于布尔值的控件生成的bug
* **修改：**  修复vue代码生成器对于下拉菜单生成的bug

##3.5.0 以及 2.5.0 发布，你心心念的Vue来了！！！vue目前还属于预览版，欢迎大家多提宝贵意见
* **新增：**  现在官网可以生成Vue的项目了
* **新增：**  VUE项目可以使用和Layui，React相同的代码生成
* **新增：**  appsettings文件中增加了Domains的配置，用来注册httpclient。在Controller和VM中通过ConfigInfo.Domains["key"].CallAPI来方便高效的调用其他网站的api
* **修改：**  修复代码生成器会将bool的搜索条件啊生成两次的bug
* **修改：**  修复继承自TopBasePoco的Model在DoAdd中没有正确的添加子表数据的bug
* **修改：**  修复用户没有权限时没有正确返回401错误的bug

## v3.1.x

3.1版本正式发布，支持.netcore 3.1，与2.4.x最新版本在功能上同步更新


## v2.4.x

v2.4.9(2020-3-15)
* **修改：**  重构日志，使用.netcore默认的日志记录流程和规则。 在.ConfigureLogging中可以使用AddWTMLogger来添加WTM的日志功能，并可以在appsetting文件中配置Logging来指定需要记录日志的级别，就像你操作其他Console，Debug这些日志一样。
* **修改：**  修复layui下日期控件默认显示当前日期的问题
* **修改：**  修复form和其中的searchpanel同时指定label-width会报错的问题
* **修改：**  代码生成现在会默认为DateTime类型的搜索条件生成时间区间的搜索
* **修改：**  修复了jwt认证失败没有正确返回401的问题

v2.4.7(2020-3-9)

* **新增：**  现在Layui模式下列表可以列筛选和打印
* **新增：**  现在ListVM中的Action按钮可以通过SetPromptMessage设置询问对话框
* **新增：**  现在数据权限可以识别多对多和树形结构

* **修改：**  修改了新生成的项目LoginVM和RegVM错位的问题
* **修改：**  修复了设置不分页不起作用的bug
* **修改：**  修改了view强制要求model继承BaseVM的bug
* **修改：**  修复了Combobox在disable状态下的显示问题
* **修改：**  修复了代码生成器在多个DataContext时候的生成问题
* **修改：**  修复了SearchPanel中Combobox多选时提交数据错误的问题

v2.4.6(2020-2-22)
本次更新加入了在连接字符串上指定数据库类型和DataContext的功能，并修复了近一阶段的bug。
* **新增：**  现在在appsettings中的ConnectionStrings里面可以指定每一个连接字符串的DbType和DbContext
* **新增：**  现在新增了一个EmptyContext基类，FrameworkContext会包含框架自带的表，而EmptyContext不会，这对于我们使用WTM连接其他系统的数据库十分有用
* **注意：**  老版本升级时需要在DataContext文件中加入一个新的构造函数：
        public DataContext(CS cs)
             : base(cs)
        {
        }
* **新增：**  增加了NoLog标记，用来指定某个方法不记录系统日志
* **修改：**  移除了不必要的验证，提升webapi的响应速度
* **新增：**  Layui模式下登陆页面新增了用户注册的演示页面
* **修改：**  修复了layui模式下autocomplete textbox在有初始值时的js错误
* **修改：**  修复了layui模式下可编辑grid表头错位的bug
* **修改：**  修复了PIndex页面的js错误
* **修改：**  移除了React模式下对node-sass的依赖
* **修改：**  移除了React模式下数据权限管理的bug
* **修改：**  修复了WebApi在非调试模式下权限认证的bug
* **修改：**  修复了代码生成器生成React菜单的bug
* **修改：**  修复了代码生成器生成标记了[Range(xxx.Max)]字段的bug






v2.4.5 (2020-1-4)
本次为累积更新，修复了一个月以来issue上提出的主要bug
* **修改：**  修复了获取PersistPoco的下拉选项时，没有过滤IsVaild=false的问题
* **修改：**  修复了弹出窗口在手机上显示不全的问题
* **修改：**  修复了UEditor单图上传错误的问题
* **修改：**  修复当搜索条件只有一个时，在搜索框中按回车键会出现异常页面的问题
* **修改：**  修复了Transfer 穿梭框显示问题的问题
* **修改：**  修复了在form表达外使用ImageTagHelper 会获得一个异常的问题
* **修改：**  修复了textbox加了padding-text之后tab无法切换的问题
* **修改：**  修复了selector display="true"时，显示错误的问题
* **修改：**  修复了使用一些第三方控件导致view无法显示的问题
* **修改：**  修复了代码生成器中点击关闭按钮报错的问题
* **修改：**  修复了连续三级空菜单没有隐藏的问题
* **修改：**  React模式系统自带管理模块加入了中英文多语言

* **新增：**  upload控件增加了进度条，通过设置ShowProgress可以选择是否显示（鸣谢 ‘阿拉斯没有家’同学 https://github.com/buffonlwx）
* **新增：**  ListVM中的GridAction中增加了下载类型的按钮
* **新增：**  BaseController中的CreateDC方法现在可以使用连接字符串的key，而不需要写死整个连接字符串
* **新增：**  BaseController中的CreateDC方法现在可以指定数据库类型
* **新增：**  菜单维护时外部菜单可以使用/aaa/bbb的形式来指定一个内部地址，这样方便大家把一个具体方法配置到左侧菜单上

### v2.4.3 / v3.0.4 (2019-12-8)

* **新增：**  增加多附件上传控件，特别鸣谢‘草监牛寺’同学，参见文档 https://wtmdoc.walkingtec.cn/#/UI/UploadMulti
* **修改：**  Appsettings文件中增加了IsOldSqlServer配置，对于使用sqlserver 2008以前的用户使用
* **修改：**  修复某些模型生成单元测试时的bug
* **修改：**  修复自定义ID的模型attach时可能失败的bug
* **修改：**  修复主子表操作时没有判断PersistPoco的bug


### v2.4.2 (2019-11-22)

* **修改：**  修复Add-Migration报错的问题
* **修改：**  修复刷新菜单无效的问题
* **修改：**  修复无法添加多级菜单的问题
* **修改：**  修复在导出时SearchMode仍然为Search的问题
* **修改：**  修复权限控制无法识别中文url的问题

### v2.4.1 (2019-11-16)

* **修改：**  修复同时使用Cookie和jwt登陆时报错的bug（不建议混合两种模式）

#### 前后端不分离模式
* **修改：**  修复Combobox联动由于没有图表而报错的bug

#### React前后端分离模式
* **新增：**  多语言支持
* **修改：**  修复菜单地址bug
* **修改：**  修复菜单管理，数去权限管理页面问题

### v2.4.0 (2019-11-5)
本次更新为大版本更新，废弃了之前Session的模式，使用Jwt和cookie两种方式进行登陆认证。
框架目前支持Cookie和Jwt两种模式，继承BaseController和BaseApiController的控制器将默认支持Cookie模式。
已有使用session认证的代码不需要修改，用户使用过程中并不会感觉到变化。
用户可以通过[AuthorizeCookie],[AuthorizeJwt],[AuthorizeJwtWithCookie]三种标签来指定Controller的验证方式。
详情请参考https://wtmdoc.walkingtec.cn/#/Global/jwt
系统增加了persistedgrants表来存储jwt持久化信息，另外菜单的默认数据也发生了改变，建议已有系统重新生成数据库或手动同步数据库

* **新增：**  Jwt支持
* **新增：**  Swagger jwt支持
* **修改：**  修复多语言验证信息bug
* **修改：**  菜单管理支持不同Area下同名Controller的配置


## v2.3.x

### v2.3.9 (2019-10-19)

* **新增：**  多语言支持。https://wtmdoc.walkingtec.cn/#/Global/MultiLanguages
老版本升级后会遇到单元测试项目中MockController.cs文件报错，将报错的行替换为
_controller.GlobaInfo.SetModuleGetFunc(() => new List\<FrameworkModule\>());
即可。

* **新增：**  dotnet 3.0支持，线上新建项目时可选择dotnetcore3.0版本的项目

#### 前后端不分离模式
* **新增：**  集成了UEditor。https://wtmdoc.walkingtec.cn/#/UI/UEditor
* **新增：**  列表按钮现在可以设置Max属性，来控制打开窗体时最大化
* **修改：**  现在View页面不再强制要求Model必须继承BaseVM
* **修改：**  修改菜单无法删除的历史遗留bug

### v2.3.6 (2019-9-27)

* **新增：**  Debug模式下，debug窗口会输出ef执行的sql语句
* **修改：**  移除EnableCors属性，集成dotnetcore自带的Cors实现跨域，并可在appsettings文件中进行配置

#### 前后端不分离模式
* **修改：**  代码生成器会为Controller生成独立的搜索和导出方法，方便对搜索和导出进行权限控制，之前公共方法仍然保留
* **修改：**  修复IE11下的显示问题
* **修改：**  修复PersistPoco导入时没有给IsValid赋值的问题
* **修改：**  修复SearchPanel中显示树形列表的问题

### v2.3.5 (2019-9-19)

本次更新增加了自定主键功能，除了默认的guid主键外，框架现在还支持自增整形和string类型的主键。
同时代码生成器也可以准确识别主键类型，生成对应的代码。
具体使用方式参见文档 https://wtmdoc.walkingtec.cn/#/Model/CustomKey

由于主键不一定是guid了，老项目更新的时候需要手动修改之前的文件，主要是两部分：
1. Controller里 BatchEdit，BatchDelete中的ids参数由guid[] 变为 string[]
2. batchvm中的CheckIfCanDelete方法，第一个参数由guid变为object
3. 老数据库中DataPrivileges表RelatedId字段类型由Guid变为Nvarchar
改起来还是比较简单的

#### 前后端不分离模式
* **修改：**  修复layui模式下三级菜单无法显示的bug
* **修改：**  修复selector控件不能搜索，不初始化的bug

#### React前后端分离模式
* **新增：**  增加菜单对字体图标的支持
* **修改：**  修复react模式下三级菜单无法显示的bug

### v2.3.4 (2019-9-5)

#### 前后端不分离模式
* **新增：**  现在Layui模式可以直接用代码生成器生成api
* **新增：**  现在Layui模式的菜单管理也可以配置api的权限，包括框架自带的api
* **新增：**  新建layui项目时自动添加swagger的支持，可以查看api文档
* **修改：**  修复grid排序时搜索条件不起作用的bug

#### React前后端分离模式
* **新增：**  新增Tab页关闭其他，关闭当前，关闭所有的功能

### v2.3.3 (2019-9-3)

* **新增：**  新增对Oracle的支持（鸣谢：hd2y）Oracle database version 18c is required.
* **新增：**  新增对DC使用事务的支持（鸣谢：AaronLucas）
* **修改：**  更新了默认数据的添加逻辑，现在使用EF的migration不会担心没有初始数据了
* **修改：**  新生成的项目会在DataContext.cs中自动加入IDesignTimeDbContextFactory类，方便使用Migration

#### 前后端不分离模式
* **修改：**  现在首页默认不展开菜单
* **修改：**  直接访问具体url可以准确定位左侧菜单
* **修改：**  更新默认生成的Login单元测试
* **修改：**  修复批量修改的一些bug
* **修改：**  修复维护菜单会改变菜单id的bug

#### React前后端分离模式
* **修改：**  修复grid中分组之后排序的bug
* **修改：**  修复grid中有时出现横向滚动条的bug
* **修改：**  新登陆页面


### v2.3.1 (2019-8-31)
修复了2.3.0版本中的一些bug
* **修改：**  修复菜单图标显示的问题
* **修改：**  修复主子表修改报错的问题
* **修改：**  修复Selector在IIS下无法显示的问题

### v2.3.0 (2019-8-30)

本次更新是一个大版本的更新，彻底重构了不分离模式的前端UI，大家可以愉快且免费的使用LayuiAdmin了

老项目更新说明：
本次更新全面支持了图标字体，放弃了使用图片附件作为菜单图标。FrameworkMenu表去掉了IconId和CustomIcon字段，新加入了字符串格式的Icon字段
老用户最快的升级方法是线上生成一个同名的新LayUI的项目，然后把你的model，viewmodel，controller考过去。。。

#### 前后端不分离模式
* **新增：**  LayUI升级为LayUIAdmin
* **新增：**  添加对图标字体的支持
* **新增：**  更新默认登录页
* **新增：**  新增Tree和TreeContainer控件
* **新增：**  新增Combobox和DateTime控件的默认配置（鸣谢：AaronLucas）
* **新增：**  新增对Sqlite数据库支持（鸣谢：xuegaoge)
* **修改：**  修复单元格内控件错位的bug
* **修改：**  修复多表头列表导出的bug
* **修改：**  修复LinkButton无法在当前页显示的问题

#### React前后端分离模式
* **修改：**  更新菜单管理模块，支持图标字体
* **修改：**  更新列表显示的逻辑

#### React前后端分离模式
还在路上，预计下个版本会和大家见面~~~

## v2.2.x

### v2.2.52 (2019-8-7)

本次更新主要修复了近期用户反馈的一些bug

#### 前后端不分离模式

* **新增：** GridAction的按钮现在可以通过IsRedirect属性设置在当前页或者Tab页，或者新窗口中显示
* **修改：** 修复layui普通checkbox样式问题
* **修改：** 修复菜单添加报错的bug
* **修改：** 修复多表头显示问题
* **修改：** 修复GridAction中IconCls属性不起作用的问题

#### React前后端分离模式

* **修改：** 修复Area下的api地址解析问题
* **修改：** 修复下拉菜单显示枚举错误的问题
* **修改：** 修复列表中switch现实问题

### v2.2.50 (2019-7-30)

* **修改：** 代码生成器加入对模型基类的验证
* **修改：** 修复日志过长导致截断的bug
* **修改：** 使用新Logo

#### 前后端不分离模式

* **新增：** 新增slider滑块控件
* **新增：** 新增transfer穿梭框控件
* **新增：** 新增对列表汇总行的支持
* **修改：** 修复了绑定字段为数组引起的bug
* **修改：** 修复菜单管理和数据权限管理中历史遗留的bug
* **修改：** 控件的默认id添加vm名称前缀，防止多tab页时出现id重名的控件
* **修改：** 修复checkbox无法触发change-func函数的bug
* **修改：** 使用layui的template重写列表前景色和背景色的实现

#### React前后端分离模式

* **修改：** 优化页面异步加载机制 路由规则调整
* **新增：** 新增aggrid，替代antd自带的grid

### v2.2.48 (2019-7-12)

* **修改：** 修复导入时，同样唯一性数据没有自动更新的bug
* **修改：** 修复批量修改时的验证bug
* **修改：** 修复前后端分离模式菜单排序的bug
* **修改：** 修复layui模式下，searchpanel中oldpost参数无效的bug

### v2.2.47 (2019-7-5)

* **新增：** 新增ValidateFormItemOnly属性，可以加在controller的方法上，用来指示框架只验证表单提交的字段

#### 前后端不分离模式

* **修改：** 修复Admin中更新用户的bug
* **修改：** 修复grid中form控件id的错误

### v2.2.46 (2019-7-3)

* **优化：** 优化ListVM查询速度
* **优化：** 修改ListVM对存储过程的支持，并配套相关文档

#### 前后端不分离模式

* **修改：** 修复grid中使用表单组件的bug
* **修改：** 修复GetFile方法没有正确输入视频content-type的bug

#### React前后端分离模式

* **优化：** 优化配置文件
* **修改：** 修复添加操作没有正确验证model的bug

### v2.2.45 (2019-6-17)

* **新增：** wt:grid 增加MultiLine属性，用于控制是否允许单元格自动换行
* **修改：** 修改默认首页的样式，增加了对于调试模式的提示，以免新用户迷茫

### v2.2.44 (2019-6-14)

* **新增：** 新增自动化单元测试。在线生成项目时会同时生成单元测试项目，代码生成器也会在生成代码的同时为Controller生成单元测试。同时支持分离和不分离两种模式
* **修改：** 修复主子表导入bug，文档中ImportVM中同时加入了主子表导入的范例
* **修改：** 修复默认多字段排序的bug

#### 前后端不分离模式

* **修改：** 修复用户组数据权限修改时的bug

#### React前后端分离模式

* **新增：** 顶部菜单配置，可以在全局设置里将菜单配置成顶部显示
* **新增：** 富文控件中增加文件上传
* **新增：** 增加时间区间控件
* **新增：** 添加了默认的修改密码页面
* **优化：** 优化表格

#### VUE前后端分离模式

VUE目前进展稍慢，距离和大家见面还需要一段时间

### v2.2.42 (2019-5-28)

* **修改：** FrameworkMenu表中加入string类型的CustumIcon字段，已有项目可以手动修改数据库，添加这个字段

#### React前后端分离模式

* **新增：** 菜单维护加入自定义图标，可以设置antd自带图标
* **优化：** 优化编译速度，优化布局

#### 前后端不分离模式

* **新增：** Appsettings中增加TabMode配置，设置layui模式下Tab页样式 ，可选配置有Default和Simple
* **新增：** Appsettings中增加IsFilePublic配置，可以设置附件是否不需要登陆就可以查看和下载

### v2.2.40 (2019-5-18)

* **修改：** 修复枚举导出时不显示的bug

#### 前后端分离模式

* **修改：** 修复React依赖报错的问题

### v2.2.39 (2019-5-10)

* **修改：** 修复代码生成器对关联字段生成ImportVM时的bug

#### 前后端不分离模式

* **修改：** upload控件增加修改尺寸的选项，当UploadType为ImageFile时，通过设置ThumbWidth和ThumbHeight，可以让服务器保存缩小后的图片
* **修改：** upload控件增加缩略图预览，当UploadType为ImageFile时默认开启缩略图预览，通过ShowPreview，PreviewWidth，PreviewHeight等参数可配置
* **修改：** 更改绑定布尔值时checkbox的样式
* **修改：** 修复layui模式下登录用户头像显示的问题，已经生成的项目可以将以下两个文件覆盖自己的项目对应的文件
    1. [LoginVM.cs](https://github.com/dotnetcore/WTM/blob/develop/demo/WalkingTec.Mvvm.Demo/ViewModels/HomeVMs/LoginVM.cs)
    1. [Header.cshtml](https://github.com/dotnetcore/WTM/blob/develop/demo/WalkingTec.Mvvm.Demo/Views/Home/Header.cshtml)

### v2.2.38 (2019-4-29)

* **修改：** 修改DpWhere逻辑，DpWhere中多个字段参数之间现在是or的关系

#### 前后端不分离模式

* **新增：** 新增layui下根据数据控制行内动作按钮是否显示。现在可以在Action上调用BindVisiableColName方法来指定某个隐藏列的名称，该隐藏列值为字符串'true'的时候，action按钮才显示
* **修改：** 修改了tab页模式下，多层弹出窗口grid不刷新的bug
* **修改：** 修复selector在searchpanel里，清空了不起作用的bug
* **修改：** 修改pindex页面使用tab，导致代码生成器显示错位的bug

### v2.2.36 (2019-4-25)

#### 前后端不分离模式

* **新增：** 新增layui下列表后台排序功能，在ListVM中设置列的时候，对需要排序的列调用SetSort(true)即可

### v2.2.35 (2019-4-22)

* **修改：** 修复代码生成器在Mac系统下无法工作的问题
* **修改：** 修复UpdateTime和UpdateBy字段没有自动写入的问题

#### 前后端不分离模式

* **修改：** 修复checkbox为false，表单字段不提交的bug

#### 前后端分离模式

* **修改：** 修复api在标记了Area属性时框架无法识别的bug

### v2.2.34 (2019-4-20)

#### 前后端不分离模式

* **修改：** 修复菜单管理中，修改菜单的bug

#### 前后端分离模式

* **修改：** 修复IsQuickDebug为false时，api无法正常访问的问题
* **修改：** 修复Excel导入的bug

### v2.2.33 (2019-4-16)

#### Features

* **修改：** 修改前后端分离模式中表单提交的逻辑，现在表单中没有定义的字段将不会被更新
* **修改：** 修改前后端分离模式中框架自带的一些前台页面的bug
* **修改：** 修改BaseCRUDVM中的删除逻辑，现在可以正确删除include了其他关联类的Entity

### v2.2.32 (2019-4-16)

#### Features

* **新增：** React前后端分离模式RTM版本正式发布！欢迎大家使用

### v2.2.28 (2019-4-9)

#### Features

* **修改：** 系统菜单表新增ClassName和MethodName两个字符串字段，已有项目升级时可手动在库中添加这两个字段
* **修改：** React的前后端分离模式接近完成

### v2.2.26 (2019-4-1)

#### Features

* **新增：** 新增对pgsql的支持
* **修改：** 重写菜单管理逻辑，菜单配置更简便
* **修改：** React的前后端分离模式稳步推进

### v2.2.22 (2019-3-20)

#### Features

* **修改：** 代码生成器会在Controller和VM的类上加入partial，这样方便大家另起一个文件写自定义的代码，而不用担心再次生成代码的时候会被覆盖
* **修改：** 修改<wt:combobox>支持多选枚举的绑定
* **修改：** React的前后端分离模式增加了多个控件，修改了若干实际项目中反应的问题，向正式版又前进了一大步

### v2.2.10 (2019-2-27)

#### Features

* **新增：** 新增在线生成项目，[https://wtmdoc.walkingtec.cn/setup](https://wtmdoc.walkingtec.cn/setup) ，生成WTM项目更快捷
* **修改：** 进一步完善React的前后端分离模式，当然目前还是预览

### v2.2.8 (2019-2-24)

#### Features

* **新增：** 框架开始支持前后端分离模式，可以创建React的前后端分离模式的项目，并生成前后端分离模式的代码
* **新增：** 新增MiddleTableAttribute，对于多对多的关系，可以在中间表的模型上标记[MiddleTable]，框架的代码生成器根据这个标记可以正确生成多对多关系的增删改查代码

### v2.2.4 (2019-01-11)

#### Bug Fixes

* 修改默认连接字符串的bug ([318840c](https://github.com/WalkingTec/WalkingTec.Mvvm/commit/318840c))
* 修改密码的bug ([1427fb7](https://github.com/WalkingTec/WalkingTec.Mvvm/commit/1427fb7))

### v2.2.3 (2019-01-08)

#### Bug Fixes

* 修改外部地址菜单刷新的bug ([918560f](https://github.com/WalkingTec/WalkingTec.Mvvm/commit/918560f))

### v2.2.2 (2019-01-04)

#### Bug Fixes

* 修改富文本必填验证的bug ([e9f2cd0](https://github.com/WalkingTec/WalkingTec.Mvvm/commit/e9f2cd0))

### v2.2.1 (2018-12-23)

#### Features

* **修改：** 修改文件上传相关配置，将 `SaveFileMode` 及 `UploadDir` 更改到 `FileUploadOptions` 中

#### Bug Fixes

* 解决 .net core 2.2下 IIS Inprogres s运行的问题 ([90256fe](https://github.com/WalkingTec/WalkingTec.Mvvm/commit/90256fe))

### v2.2.0 (2018-12-20)

#### Features

* **新增：** 添加富文本组件
* **新增：** 添加自定义路由的简便入口
* **修改：** CrossDomainAttribute现在可以指定允许的域名
