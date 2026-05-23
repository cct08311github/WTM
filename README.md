English | [简体中文](./README.zh-CN.md)

# WalkingTec.Mvvm for asp.net core

WalkingTec.Mvvm framework (WTM) is a rapid development framework based on .NET 10. It supports LayUI, React, Vue 2/3, and Blazor. WTM has a built-in code generator to maximize development efficiency. It is a powerful tool for efficient web development.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## CI Build Status

| Platform | Build Server | SDK | Branch | Status |
| -------- | ------------ | ---- |--------|--------|
| Gitea Actions | Ubuntu (act_runner 0.6.1) | .NET 10 | dotnet10 | see `.github/workflows/ci-build.yml` runs on Gitea |
## Nuget Packages

Package name                              | Version                     | Downloads
------------------------------------------|-----------------------------|-------------
`WalkingTec.Mvvm.Core` | [![NuGet](https://img.shields.io/nuget/v/WalkingTec.Mvvm.Core.svg?style=flat-square&label=nuget)](https://www.nuget.org/packages/WalkingTec.Mvvm.Core/) | ![downloads](https://img.shields.io/nuget/dt/WalkingTec.Mvvm.Core.svg)
`WalkingTec.Mvvm.Mvc` | [![NuGet](https://img.shields.io/nuget/v/WalkingTec.Mvvm.Mvc.svg?style=flat-square&label=nuget)](https://www.nuget.org/packages/WalkingTec.Mvvm.Mvc/) | ![downloads](https://img.shields.io/nuget/dt/WalkingTec.Mvvm.Mvc.svg)
`WalkingTec.Mvvm.Mvc.Admin` | [![NuGet](https://img.shields.io/nuget/v/WalkingTec.Mvvm.Mvc.Admin.svg?style=flat-square&label=nuget)](https://www.nuget.org/packages/WalkingTec.Mvvm.Mvc.Admin/) | ![downloads](https://img.shields.io/nuget/dt/WalkingTec.Mvvm.Mvc.Admin.svg)
`WalkingTec.Mvvm.TagHelpers.LayUI` | [![NuGet](https://img.shields.io/nuget/v/WalkingTec.Mvvm.TagHelpers.LayUI.svg?style=flat-square&label=nuget)](https://www.nuget.org/packages/WalkingTec.Mvvm.TagHelpers.LayUI/) | ![downloads](https://img.shields.io/nuget/dt/WalkingTec.Mvvm.TagHelpers.LayUI.svg)

## Quick Start

> 完整安裝指南與 DB 配置請見 [Getting Started](docs/getting-started.md)

**1. 配置 Gitea NuGet source**

需要一個有 `read:package` scope 的 Gitea PAT（可在 Gitea UI → Settings → Applications 建立）。

```bash
dotnet nuget add source "https://mac-mini.tailde842d.ts.net/api/packages/chiu0831/nuget/index.json" \
  --name gitea-wtm --username YOUR_GITEA_USERNAME --password YOUR_GITEA_PAT \
  --store-password-in-clear-text
```

**2. 安裝套件**

```bash
dotnet add package WalkingTec.Mvvm.Core --version 10.5.1 --source gitea-wtm
dotnet add package WalkingTec.Mvvm.Mvc --version 10.5.1 --source gitea-wtm
dotnet add package WalkingTec.Mvvm.TagHelpers.LayUI --version 10.5.1 --source gitea-wtm
```

> 詳細安裝/發佈說明見 [`docs/gitea-packages.md`](docs/gitea-packages.md)

**3. 最小 Program.cs**

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDistributedMemoryCache();
builder.Services.AddWtmSession(3600, builder.Configuration);
builder.Services.AddWtmAuthentication(builder.Configuration);
builder.Services.AddMvc();
builder.Services.AddWtmContext(builder.Configuration);

var app = builder.Build();
app.UseStaticFiles();
app.UseWtmStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseSession();
app.UseWtm();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();
```

## WTM Features

WTM provides 4 types of ViewModel, covering all of the common functionalities of mainstream web applications.

- CrudVM provides most common functionalities for data addition, deletion and modification.

- ListVM provides paging and exporting functionality.

- ImportVM & TemplateVM provides importing via excel functionality.

- BatchVM provides batch operation functionality.

- WTM has its own code generator, which makes development efficient and fast.

- WTM provides dozens of client-side controls, including Form, Grid, Panel, Dialog and quite alot of other common controls.

- WTM provides built-in user, role, user group, Data permission, page permission, menu, log, mail, SMS, file and other common back-end  functionalities;

- WTM supports single sign on, portal and distributed database;

- WTM provides simplified integration with libraries such as Redis, DFS etc.

- WTM provides both server-side and client-side frameworks for building user interfaces.


| Mode | UI | Status  |
|--------- |------------- |---------|
|Server-side   |LayUI |Stable|
|Client-side   |React |Stable|
|Client-side   |VUE |Stable|
|Server/Client |Blazor |Stable|


Under WTM framework's client-side mode, you can also use code generator to generate server-side and client-side code at the same time, greatly reducing the communication cost of front-end and back-end developers, essentially improving the development efficiency, so that "separation" is no longer complex and expensive.

Framework document address: http://wtmdoc.walkingtec.cn

Frame QQ communication group: 694148336(full), 892848149 (group2)

## Local Docs

- [Getting Started 快速入門](./docs/getting-started.md)
- [Production Readiness 評估](./docs/production-readiness.md) — 哪些場景可以上 prod、補強清單
- [Dependency Management](./docs/dependency-management.md) — 套件版本政策、NU1510 雙意義警告、NPOI security pin
- [CI Operations](./docs/ci-operations.md) — Gitea Actions 工作流、四大已知不相容、排錯 SOP
- [WTM Developer Manual 開發手冊](./docs/wtm-developer-manual.md) — 18 章節完整參考
- [WTM System Architecture Guide](./docs/system-architecture.md)
- [WTM Analysis Mode Guide](./docs/analysis-mode.md)
- [Gitea Packages Guide](./docs/gitea-packages.md) — Gitea NuGet registry 安裝/發佈
- [WTM 8.3 Roadmap](./docs/roadmap-8.3.md) (legacy)

version 5.0x is in VNext branch

## Click <a href="http://wtmdoc.walkingtec.cn/setup">here</a>  to generate a WTM project online and experience the beauty of WTM immediately~~~

At present, we are a team of 7 developers. We are looking for all kinds of C#, React, VUE experts to join us!

If WTM hepls you:

<a href="https://www.paypal.me/dotnetWTM" target="_blank"><img src="https://wtmdoc.walkingtec.cn/imgs/pp_h_rgb.webp"  width="150"></a>
