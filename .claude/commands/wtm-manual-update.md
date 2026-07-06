Update `docs/wtm-developer-manual.md` so it reflects the current framework version. Bumps the version header, prepends a concise "version highlights" blockquote for the release(s) since the last manual update, and edits the specific sections where user-facing behaviour actually changed. The mechanical parts (header, highlight digest) are automatable from `CHANGELOG.md`; the section edits require judgement about what a downstream developer needs to know.

**Usage:** `/wtm-manual-update`
Run after `version.props` + `CHANGELOG.md` are updated for the release, before (or as part of) the release PR.

---

## 1. Establish the gap

```bash
grep -n "版本\|最後更新" docs/wtm-developer-manual.md | head -2   # current manual version + date
grep VersionPrefix version.props                                   # target version
grep -E "^## \[" CHANGELOG.md | head -25                           # releases to fold in
```

The manual header line 3 looks like:
`> **版本**：X.Y.Z | **目標框架**：.NET 10 (LTS) | **最後更新**：YYYY-MM-DD`

Everything between the manual's current version and `version.props`'s version is the backlog to reflect.

## 2. Update the header

- Bump the version + `最後更新` date on line 3.
- Prepend **version-highlights blockquotes** (the `>` blocks that follow the header line), **newest first**, one per *notable* release. Match the existing house style: concise Traditional Chinese, name the key APIs/behaviour, cross-reference the section (`§N`) and `CHANGELOG.md [x.y.z]`.
- **Do not** write one blockquote per patch. The header lists **minors + genuinely notable patches** only. A swarm of related patches (e.g. a hardening series) should be **one consolidated blockquote** ("X.Y.z 重點 (A → B)") rather than a dozen entries.
- Lead the newest blockquote with any **user-facing default/behaviour change** — that is the thing a reader upgrading most needs to see.

Source of truth is `CHANGELOG.md`. Do not invent claims; if unsure of a detail, read the corresponding `CHANGELOG` entry or the code.

## 3. Section edits (the judgement part)

For each release in the gap, decide whether it changed something a developer *uses*, and edit that section:

- **New opt-in feature / API** → add or extend the relevant section (§6 LayUI, §7 Analysis, §8 ETL, §9 Dashboard, §10 Security, §16 CodeGen, §17 Config, §18 WorkFlow …).
- **New config key** → add a row to §17.3 (`關鍵配置說明`) and, if load-bearing, the §17.1 appsettings example.
- **Behaviour/default change** → document the new default AND the opt-out/rollback, prominently.
- **Deprecation** → note it where the feature is documented (e.g. `[Obsolete]` targets).
- Pure internal fixes / perf with no observable API change → header highlight is enough; no section edit needed.

Patch releases with no API change usually need **only** the header highlight.

## 4. Consistency checks

```bash
# ToC only lists top-level sections — a new §N.M subsection needs NO ToC entry, but a new top-level §N does.
sed -n '/## 目錄/,/^## 1\./p' docs/wtm-developer-manual.md
# Header blockquote chain must stay contiguous > lines (no stray blank line breaking it):
awk 'NR>=3 && !/^>/ {print "first non-blockquote line: "NR; exit}' docs/wtm-developer-manual.md
```

- New top-level section → add a ToC entry; new subsection (§N.M) → no ToC change.
- Verify the `>` header-blockquote chain is unbroken and the version anchor links still resolve.

## 5. Ship

The manual is a doc — the main session may edit it directly. Commit on the release branch (or a `docs/manual-<version>` branch) with `docs(manual): update developer manual to <version>`, reference the tracking issue, PR → CI → merge. This is typically folded into the release, not a standalone release itself.
