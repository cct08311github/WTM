# WalkingTec.Mvvm (WTM) — dotnet10 fork

This package is part of a personal, actively-maintained fork of the WalkingTec MVVM
Framework (WTM), targeting **.NET 10**. It tracks upstream WTM's rapid-development
philosophy (LayUI/React/Vue/Blazor UI, code generator, `BaseVM`-centric architecture)
while adding security hardening, Clean Architecture refactors, observability/BI/ETL
extensions, and an actively-updated dependency baseline.

## Install

This package is published to **GitHub Packages**:

```bash
dotnet nuget add source "https://nuget.pkg.github.com/cct08311github/index.json" \
  --name github-wtm --username <github-user> --password <github-token>

dotnet add package WalkingTec.Mvvm.Core --source github-wtm
```

A GitHub Personal Access Token with the `read:packages` scope is required to consume
GitHub Packages, even for public packages — create one at
<https://github.com/settings/tokens>.

## Source & Documentation

- Repository (this mirror): https://github.com/cct08311github/WTM
- Changelog: see `CHANGELOG.md` in the repository root
- Developer manual: `docs/wtm-developer-manual.md`

## License

MIT — see `LICENSE` in the repository root.
