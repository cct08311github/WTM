# WalkingTec.Mvvm for ASP.NET Core

WalkingTec.Mvvm (WTM) is a rapid-development framework for ASP.NET Core on .NET 10. It supports LayUI, React, Vue 2/3, and Blazor, and ships a built-in code generator to speed up server + client development.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

This GitHub repository is a public mirror. The current published versions are available from **GitHub Packages**.

## NuGet packages

| Package | Latest |
|---|---|
| `WalkingTec.Mvvm.Core` | 10.5.3 |
| `WalkingTec.Mvvm.Mvc` | 10.5.3 |
| `WalkingTec.Mvvm.TagHelpers.LayUI` | 10.5.3 |

## Quick start

### 1. Add the GitHub Packages NuGet source

You need a GitHub Personal Access Token with the `read:packages` scope. Create one at <https://github.com/settings/tokens>.

```bash
dotnet nuget add source "https://nuget.pkg.github.com/cct08311github/index.json" \
  --name wtm-github \
  --username YOUR_GITHUB_USERNAME \
  --password YOUR_GITHUB_PAT \
  --store-password-in-clear-text
```

### 2. Install the packages

```bash
dotnet add package WalkingTec.Mvvm.Core --version 10.5.3 --source wtm-github
dotnet add package WalkingTec.Mvvm.Mvc --version 10.5.3 --source wtm-github
dotnet add package WalkingTec.Mvvm.TagHelpers.LayUI --version 10.5.3 --source wtm-github
```

### 3. Minimal `Program.cs`

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddWtmContext(builder.Configuration)
    .AddWtmMvc();

var app = builder.Build();
app.UseWtmContext();
app.UseWtmStaticFiles();
app.MapControllers();
app.Run();
```

See [docs/getting-started.md](./docs/getting-started.md) for the full setup walk-through including database configuration.

## Highlights

- **Four ViewModel base types** — `BaseCRUDVM<T>`, `BasePagedListVM<T, S>`, `BaseImportVM<T>`, `BaseBatchVM<T>` — cover the most common CRUD / list / import / batch patterns.
- **Analysis Mode** — attribute-driven ad-hoc analytics on any list ViewModel (`[Dimension]`, `[Measure]`, `[EnableAnalysis]`). See [`docs/analysis-mode.md`](./docs/analysis-mode.md).
- **ETL module** — pipeline, watermark, schema service. See [`docs/etl-module.md`](./docs/etl-module.md).
- **Opt-in security middleware** — CSP, secure headers, JWT lifetime hardening, request timeouts, ETag, idempotency, server-timing, etc.
- **Dashboard / Lookup Cache / Structured logging** — production-grade infrastructure baked in.
- **Multi-tenant** via `ITenant` global query filter; multi-DB (MSSQL / MySQL / PostgreSQL / SQLite / Oracle).

## Documentation

- [Getting Started](./docs/getting-started.md)
- [Developer Manual](./docs/wtm-developer-manual.md) — 18-section complete reference
- [System Architecture](./docs/system-architecture.md)
- [Analysis Mode Guide](./docs/analysis-mode.md)
- [Lookup Cache](./docs/lookup-cache.md)
- [Structured Logging](./docs/structured-logging.md)
- [ETL Module](./docs/etl-module.md)
- [Dashboard User Guide](./docs/dashboard-user-guide.md) | [Dashboard Developer Guide](./docs/dashboard-dev-guide.md)
- [Integration Guide](./docs/integration-guide.md)
- [Dependency Management](./docs/dependency-management.md)
- [Production Readiness](./docs/production-readiness.md)
- [Changelog](./CHANGELOG.md)

Upstream documentation site: <http://wtmdoc.walkingtec.cn>

## License

MIT. See [LICENSE](./LICENSE).
