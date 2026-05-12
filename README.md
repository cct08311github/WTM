English | [简体中文](./README.zh-CN.md)

# WalkingTec.Mvvm for asp.net core

WalkingTec.Mvvm framework (WTM) is a rapid development framework based on .NET 10. It supports LayUI, React, Vue 2/3, and Blazor. WTM has a built-in code generator to maximize development efficiency. It is a powerful tool for efficient web development.

[![Build Status](https://github.com/cct08311github/WTM/actions/workflows/build.yml/badge.svg?branch=dotnet10)](https://github.com/cct08311github/WTM/actions/workflows/build.yml)
[![GitHub license](https://img.shields.io/github/license/dotnetcore/WTM.svg)](https://github.com/dotnetcore/WTM/blob/master/LICENSE)

## CI Build Status

| Platform | Build Server | SDK | Branch | Status |
| -------- | ------------ | ---- |--------|--------|
| GitHub Actions | Ubuntu | .NET 10 | dotnet10 | [![Build Status](https://github.com/cct08311github/WTM/actions/workflows/build.yml/badge.svg?branch=dotnet10)](https://github.com/cct08311github/WTM/actions/workflows/build.yml) |
## Nuget Packages

Package name                              | Version                     | Downloads
------------------------------------------|-----------------------------|-------------
`WalkingTec.Mvvm.Core` | [![NuGet](https://img.shields.io/nuget/v/WalkingTec.Mvvm.Core.svg?style=flat-square&label=nuget)](https://www.nuget.org/packages/WalkingTec.Mvvm.Core/) | ![downloads](https://img.shields.io/nuget/dt/WalkingTec.Mvvm.Core.svg)
`WalkingTec.Mvvm.Mvc` | [![NuGet](https://img.shields.io/nuget/v/WalkingTec.Mvvm.Mvc.svg?style=flat-square&label=nuget)](https://www.nuget.org/packages/WalkingTec.Mvvm.Mvc/) | ![downloads](https://img.shields.io/nuget/dt/WalkingTec.Mvvm.Mvc.svg)
`WalkingTec.Mvvm.Mvc.Admin` | [![NuGet](https://img.shields.io/nuget/v/WalkingTec.Mvvm.Mvc.Admin.svg?style=flat-square&label=nuget)](https://www.nuget.org/packages/WalkingTec.Mvvm.Mvc.Admin/) | ![downloads](https://img.shields.io/nuget/dt/WalkingTec.Mvvm.Mvc.Admin.svg)
`WalkingTec.Mvvm.TagHelpers.LayUI` | [![NuGet](https://img.shields.io/nuget/v/WalkingTec.Mvvm.TagHelpers.LayUI.svg?style=flat-square&label=nuget)](https://www.nuget.org/packages/WalkingTec.Mvvm.TagHelpers.LayUI/) | ![downloads](https://img.shields.io/nuget/dt/WalkingTec.Mvvm.TagHelpers.LayUI.svg)

## Quick Start

> 完整安裝指南與 DB 配置請見 [Getting Started](docs/getting-started.md)

**1. 配置 GitHub Packages NuGet source**

```bash
dotnet nuget add source "https://nuget.pkg.github.com/cct08311github/index.json" \
  --name github-wtm --username YOUR_GITHUB_USERNAME --password YOUR_GITHUB_TOKEN
```

**2. 安裝套件**

```bash
dotnet add package WalkingTec.Mvvm.Core --version 10.0.1 --source github-wtm
dotnet add package WalkingTec.Mvvm.Mvc --version 10.0.1 --source github-wtm
dotnet add package WalkingTec.Mvvm.TagHelpers.LayUI --version 10.0.1 --source github-wtm
```

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
- [WTM System Architecture Guide](./docs/system-architecture.md)
- [WTM Analysis Mode Guide](./docs/analysis-mode.md)
- [GitHub Packages Guide](./docs/github-packages.md)
- [WTM 8.3 Roadmap](./docs/roadmap-8.3.md)

version 5.0x is in VNext branch

## Click <a href="http://wtmdoc.walkingtec.cn/setup">here</a>  to generate a WTM project online and experience the beauty of WTM immediately~~~

At present, we are a team of 7 developers. We are looking for all kinds of C#, React, VUE experts to join us!

If WTM hepls you:

<a href="https://www.paypal.me/dotnetWTM" target="_blank"><img src="https://wtmdoc.walkingtec.cn/imgs/pp_h_rgb.webp"  width="150"></a>

<!-- ci-trigger gitea actions 1778588452 -->
