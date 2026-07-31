---
paths:
  - "src/**/*.cs"
  - "src/**/*.cshtml"
  - "src/**/*.js"
---

# Security Invariants (src/)

Properties the code must never lose — none of them are visible from reading it.

## `framework_layui.js` — dialog init islands & the `bindSubmit` trust boundary (#470/#558)

Dialog and form init are JSON-island-driven (`ff.DispatchAction`), never inline `<script>`. The file carries exactly **one executable `eval(`** — the deprecated `IsScript` path; the other `eval(` tokens in it are comments. Never add a second.

`bindSubmit`'s `beforeSubmit` / `filter` / `url` values are compile-time, developer-authored Razor literals emitted by `FormTagHelper` — **never user, field, or request data**. Any change that lets request-derived data reach those fields breaks the security model. The `beforeSubmit` resolver invokes `window[name]` only when the name is a plain identifier (`/^[A-Za-z_$][\w$]*$/`), is not on the denylist (`eval`, `Function`, …), is an own property of `window`, and is `typeof === 'function'`; otherwise the gate is silently skipped — no throw, no eval. Changes here require adversarial review.

## The ordering *is* the boundary

- **Analysis Mode:** the field whitelist is enforced **before** Expression Tree compilation, and filter operators are validated against an allowlist **before** the tree is built. Moving either check after compilation looks behaviour-preserving and silently opens arbitrary property access.
- **`WtmVmFactory.CreateVM`** rejects non-`BaseVM` types **before** constructor invocation.
- **`IsQuickDebug = true` throws outside Development** — an RBAC-bypass guard, not a convenience flag.

## Other invariants

- `Selector` requires authentication (`[AllRights]`, never `[Public]`).
- **`UpdateModelProperty` deliberately does NOT route through `DoEdit()`/`DoEditPrepare`** (#797, PR #809). It persists only the one requested property plus `UpdateTime`/`UpdateBy`, and still enforces the sensitive-field blocklist, the `CanEditProperty` hook, the duplicate check, and the `[AuditChanges]` ChangeLog row. `DoEditPrepare`'s edit-mutation pass nulls `TopBasePoco` navigations and resyncs child collections from `Selected*IDs`, so routing a single-field inline edit through it writes collateral changes the caller never asked for — restoring `DoEdit()` here reopens that. Also note `ValidateDuplicateData()` is **not side-effect-free**: it sets `IsValid = true` and `TenantCode` on the entity, so the controller snapshots and restores those around the call; removing that snapshot silently discards the client's value for exactly those two fields and makes the audit row record a transition that never happened.
- Tenant scope: `SetDuplicatedCheck` scopes uniqueness queries to the current tenant for `ITenant` entities; `FileUploadOptions.EnforceTenantFileScope` is opt-in; `[AuditChanges]` on framework RBAC entities records permission mutations.
- Webhook sink and Analysis Mode REST widget data sources are SSRF-hardened: DNS-pinned, HTTPS-only, private-IP/IMDS blocked, redirects disabled.
- Grid colour values are validated against a hex/named-colour allowlist and HTML-encoded; TagHelper dynamic values are `HtmlEncode`d.
- Startup order matters: `AddWtmContext` → `UseWtmContext` → `UseWtmStaticFiles`. New entity sets need an EF Core migration before use.
