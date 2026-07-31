---
paths:
  - "Directory.Packages.props"
  - "**/Directory.Packages.props"
  - "Directory.Build.props"
  - "**/Directory.Build.props"
  - "common.props"
  - "version.props"
  - "global.json"
  - "NuGet.Config"
  - "**/*.csproj"
---

# Dependency Management

Central Package Management: every `<PackageReference>` omits `Version=` and resolves from `Directory.Packages.props`. Change versions there, never in a csproj.

## NU1510 has two opposite meanings

| Scenario | What it means | Action |
|---|---|---|
| A. True redundancy | The explicit reference duplicates what the framework already provides | Remove it |
| B. **Security override** | The explicit reference exists to override a *vulnerable* transitive dependency | **Keep it** — NU1510 is proof the override is taking effect |

Deleting a scenario-B reference "to clean up the warning" reintroduces the CVE. Issue #13 is the post-mortem of that already happening once. Live scenario-B pins — the version numbers are whatever `Directory.Packages.props` currently says, and the inline `<!-- Issue #N -->` comments are the record:

- `System.Security.Cryptography.Xml` — overrides NPOI's transitive pull of 8.0.2 (GHSA-37gx-xxp4-5rgx, GHSA-w3x6-4m5h-cxqf). Removing it makes 13+ projects emit NU1903.
- `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 — the fix for GHSA-2m69-gcr7-jv3q (#393); promotes the EF Core Sqlite chain off the vulnerable `lib.e_sqlite3` 2.1.11, which then leaves the tree entirely. NU1510 on this line is expected.

## Other pins that are not preferences

- `Common.Logging` 3.4.1 — NPOI's legacy 1.2.3 compat shim pulls Common.Logging 1.2.0, whose malformed TFM makes modern NuGet fail restore with **NU1202**. This pin is a build fix; dropping it as redundant breaks restore in a way that looks like an unrelated NuGet bug.
- **NPOI stays at 2.7.6.** 2.8.0 does not fix the Crypto.Xml dependency and adds SkiaSharp native-binary risk. Do not upgrade until upstream fixes it (#15).

## Before removing any `<PackageReference>`

1. `git log --oneline -- <file>` — look for `fix(security)`, `pin`, `override`
2. Read the inline XML comments — security pins carry `<!-- Issue #N -->`
3. Diff the vulnerability scan across the change:
   `dotnet list WalkingTec.Mvvm.sln package --vulnerable --include-transitive` on the base branch and again after. Any new NU1903 → revert immediately.
4. The PR must state which references were removed, why they are genuinely redundant, and the scan diff.

## Priority

- Fixable NU1903 (a patched version exists) = **P0** — pin or bump before release
- Unfixable NU1903 (`first_patched: None`) = accepted exception **only with a tracking issue**, documented here and in CHANGELOG known-issues, bumped the moment upstream ships
- NU1510 = P3 informational noise, an acceptable price for zero vulnerabilities

Upgrade loop: bump in `Directory.Packages.props` → `dotnet restore` (no NU1605 / NU1107) → `--vulnerable` scan shows 0 NU1903 → build and test. A security fix also needs a `CHANGELOG.md` entry.

Full background → `docs/dependency-management.md`
