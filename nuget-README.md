# WalkingTec.Mvvm (WTM) — dotnet10 fork

This package is part of a personal, actively-maintained fork of the WalkingTec MVVM
Framework (WTM), targeting **.NET 10**. It tracks upstream WTM's rapid-development
philosophy (LayUI/React/Vue/Blazor UI, code generator, `BaseVM`-centric architecture)
while adding security hardening, Clean Architecture refactors, observability/BI/ETL
extensions, and an actively-updated dependency baseline.

## Install

The canonical source is a self-hosted Gitea NuGet registry; a sanitized mirror is
published to GitHub Packages for external consumers:

```bash
# Gitea (authoritative)
dotnet nuget add source "https://mac-mini.tailde842d.ts.net/api/packages/chiu0831/nuget/index.json" \
  --name gitea --username <user> --password <token>

# GitHub Packages (mirror)
dotnet nuget add source "https://nuget.pkg.github.com/cct08311github/index.json" \
  --name github-wtm --username <github-user> --password <github-token>

dotnet add package WalkingTec.Mvvm.Core
```

## Source & Documentation

- Repository (Gitea, authoritative): https://mac-mini.tailde842d.ts.net/chiu0831/WTM
- Public mirror: https://github.com/cct08311github/WTM
- Changelog: see `CHANGELOG.md` in the repository root
- Developer manual: `docs/wtm-developer-manual.md`

## License

MIT — see `LICENSE` in the repository root.
