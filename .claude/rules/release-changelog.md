---
paths:
  - "CHANGELOG.md"
  - "version.props"
  - "common.props"
---

# Releasing & CHANGELOG

Commit scopes used in this repo: `core`, `mvc`, `layui`, `etl`, `analysis`, `dashboard`, `security`, `deps`, `ci` — e.g. `fix(security): scope duplicate-check to current tenant`.

## CHANGELOG.md

- Updated for every release; entries grouped under `### Security`, `### Added`, `### Changed`, `### Fixed`, `### Improved`, `### Migration`
- Security fixes always get their own `### Security` section
- A breaking change listed under `### Changed` must carry migration instructions

## Release flow

1. Bump `<VersionPrefix>` in `version.props` — the single source of the framework version
2. Update `CHANGELOG.md`
3. `/wtm-release-check` — **blocking** gate (build + test + local vulnerability scan)
4. Tag and push to Gitea
5. `dotnet pack -c Release` and publish to the Gitea NuGet registry
