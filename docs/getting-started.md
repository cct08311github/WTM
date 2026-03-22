# WTM 快速入門指南

> **版本**：10.0.1 | **目標框架**：.NET 10 (LTS) | **最後更新**：2026-03-22

本文件帶你從零開始建立一個 WTM 應用程式，涵蓋環境準備、套件安裝、最小可運行應用、資料庫配置，以及第一個完整的 CRUD 功能。

---

## 目錄

1. [前置需求](#1-前置需求)
2. [安裝 WTM 套件](#2-安裝-wtm-套件)
3. [最小可運行應用](#3-最小可運行應用)
4. [資料庫配置](#4-資料庫配置)
5. [第一個 CRUD](#5-第一個-crud)
6. [LayUI 前端](#6-layui-前端)
7. [下一步](#7-下一步)

---

## 1. 前置需求

### 1.1 .NET 10 SDK

WTM 10.0.1 以 .NET 10 (LTS) 為目標框架。請確認已安裝 .NET 10 SDK：

```bash
dotnet --version
# 應顯示 10.0.xxx
```

如尚未安裝，請至 [.NET 下載頁面](https://dotnet.microsoft.com/download/dotnet/10.0) 取得。

### 1.2 資料庫

WTM 支援以下資料庫（透過 EF Core）：

| 資料庫 | Provider |
|--------|----------|
| SQL Server | `Microsoft.EntityFrameworkCore.SqlServer` |
| Oracle | `Oracle.EntityFrameworkCore` |
| PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` |
| MySQL | `MySql.EntityFrameworkCore` |
| SQLite | `Microsoft.EntityFrameworkCore.Sqlite` |

本指南以 **SQL Server** 為主要範例，並附 Oracle 配置說明。

### 1.3 IDE（建議）

- Visual Studio 2022 17.14+ 或 Visual Studio Code + C# Dev Kit
- JetBrains Rider 2025.1+

---

## 2. 安裝 WTM 套件

WTM 套件發佈於 GitHub Packages。需先配置 NuGet source。

### 2.1 配置 GitHub Packages NuGet source

```bash
dotnet nuget add source "https://nuget.pkg.github.com/cct08311github/index.json" \
  --name github-wtm \
  --username YOUR_GITHUB_USERNAME \
  --password YOUR_GITHUB_TOKEN
```

> **注意**：`YOUR_GITHUB_TOKEN` 需要 `read:packages` 權限。可在 GitHub Settings > Developer settings > Personal access tokens 建立。

### 2.2 建立新專案

```bash
dotnet new web -n WtmDemo
cd WtmDemo
```

### 2.3 安裝三個核心套件

```bash
dotnet add package WalkingTec.Mvvm.Core --version 10.0.1 --source github-wtm
dotnet add package WalkingTec.Mvvm.Mvc --version 10.0.1 --source github-wtm
dotnet add package WalkingTec.Mvvm.TagHelpers.LayUI --version 10.0.1 --source github-wtm
```

| 套件 | 說明 |
|------|------|
| `WalkingTec.Mvvm.Core` | 核心：ViewModel、DataContext、Model 基類、Analysis 引擎 |
| `WalkingTec.Mvvm.Mvc` | Controller 基類、啟動擴充方法、內嵌 JS 資源 |
| `WalkingTec.Mvvm.TagHelpers.LayUI` | LayUI TagHelper（Form、Grid、Dialog 等） |

---

## 3. 最小可運行應用

### 3.1 Program.cs

將 `Program.cs` 替換為以下內容：

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDistributedMemoryCache();
builder.Services.AddWtmSession(3600, builder.Configuration);
builder.Services.AddWtmAuthentication(builder.Configuration);
builder.Services.AddMvc();
builder.Services.AddWtmContext(builder.Configuration);

var app = builder.Build();
app.UseStaticFiles();
app.UseWtmStaticFiles();     // 載入內嵌 JS (/_js/)
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseSession();
app.UseWtm();                // WTM 中介軟體
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();
```

**關鍵呼叫說明**：

| 方法 | 用途 |
|------|------|
| `AddWtmContext(config)` | 註冊 `WTMContext`、`AnalysisVmRegistry`、RBAC、DB 連線 |
| `UseWtmStaticFiles()` | 將內嵌的 LayUI JS/CSS 資源掛載到 `/_js/` 路徑 |
| `UseWtm()` | WTM 中介軟體，注入 `WTMContext` 至每個 Request |

### 3.2 appsettings.json

```json
{
  "ConnectionStrings": {
    "default": "Server=localhost;Database=WtmDemo;Trusted_Connection=True;TrustServerCertificate=True"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  }
}
```

### 3.3 啟動驗證

```bash
dotnet run
```

如果一切正確，應用程式將在 `http://localhost:5000` 啟動。瀏覽器開啟後應看到預設頁面。WTM 會自動建立資料庫並初始化系統表。

---

## 4. 資料庫配置

### 4.1 SQL Server

```json
{
  "ConnectionStrings": {
    "default": "Server=localhost;Database=WtmDemo;Trusted_Connection=True;TrustServerCertificate=True"
  }
}
```

使用 SQL Server Authentication 時：

```json
{
  "ConnectionStrings": {
    "default": "Server=localhost;Database=WtmDemo;User Id=sa;Password=YourPassword;TrustServerCertificate=True"
  }
}
```

### 4.2 Oracle

安裝 Oracle provider：

```bash
dotnet add package Oracle.EntityFrameworkCore
```

連線字串：

```json
{
  "ConnectionStrings": {
    "default": "Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=localhost)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=XEPDB1)));User Id=wtm_user;Password=YourPassword;"
  }
}
```

### 4.3 PostgreSQL

```bash
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
```

```json
{
  "ConnectionStrings": {
    "default": "Host=localhost;Database=WtmDemo;Username=postgres;Password=YourPassword"
  }
}
```

### 4.4 SQLite（適合開發/測試）

```bash
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
```

```json
{
  "ConnectionStrings": {
    "default": "Data Source=WtmDemo.db"
  }
}
```

---

## 5. 第一個 CRUD

### 5.1 建立 Model

WTM 的 Model 必須繼承 `BasePoco`（內建 `Guid` 型別的 `ID` 欄位）：

```csharp
using System.ComponentModel.DataAnnotations;
using WalkingTec.Mvvm.Core;

namespace WtmDemo.Models;

public class Student : BasePoco
{
    [Display(Name = "姓名")]
    [Required(ErrorMessage = "姓名為必填")]
    [StringLength(50)]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "年齡")]
    [Range(1, 150)]
    public int Age { get; set; }

    [Display(Name = "電子郵件")]
    [StringLength(200)]
    public string? Email { get; set; }
}
```

### 5.2 註冊至 DataContext

建立自訂 DataContext 並加入 `DbSet`：

```csharp
using Microsoft.EntityFrameworkCore;
using WalkingTec.Mvvm.Core;
using WtmDemo.Models;

namespace WtmDemo.Data;

public class DemoContext : DataContext
{
    public DemoContext(CS cs) : base(cs) { }

    public DemoContext(string cs, DBTypeEnum dbType)
        : base(cs, dbType) { }

    public DbSet<Student> Students { get; set; } = null!;
}
```

### 5.3 建立 ViewModel

WTM 的 CRUD 操作透過 `BaseCRUDVM<TModel>` 封裝：

```csharp
using WalkingTec.Mvvm.Core;
using WtmDemo.Models;

namespace WtmDemo.ViewModels;

public class StudentVM : BaseCRUDVM<Student>
{
    // BaseCRUDVM 已內建 DoAdd、DoEdit、DoDelete、GetById
    // 如需自訂驗證，可 override Validate() 方法
}
```

### 5.4 建立 ListVM（列表查詢）

```csharp
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Core.Extensions;
using WtmDemo.Models;

namespace WtmDemo.ViewModels;

public class StudentSearcher : BaseSearcher
{
    public string? Name { get; set; }
}

public class StudentListVM : BasePagedListVM<Student, StudentSearcher>
{
    protected override IEnumerable<IGridColumn<Student>> InitGridHeader()
        => new List<GridColumn<Student>>
        {
            this.MakeGridHeader(x => x.Name),
            this.MakeGridHeader(x => x.Age),
            this.MakeGridHeader(x => x.Email),
            this.MakeGridHeaderAction(width: 200),
        };

    public override IOrderedQueryable<Student> GetSearchQuery()
    {
        var query = DC!.Set<Student>().AsQueryable();

        if (!string.IsNullOrEmpty(Searcher.Name))
        {
            query = query.Where(x => x.Name.Contains(Searcher.Name));
        }

        return query.OrderByDescending(x => x.CreateTime);
    }
}
```

### 5.5 建立 Controller

WTM Controller 繼承 `BaseController`，透過 `Wtm.CreateVM<T>()` 建立 ViewModel：

```csharp
using Microsoft.AspNetCore.Mvc;
using WalkingTec.Mvvm.Core;
using WalkingTec.Mvvm.Mvc;
using WtmDemo.ViewModels;
using WtmDemo.Models;

namespace WtmDemo.Controllers;

[ActionDescription("學生管理")]
public class StudentController : BaseController
{
    [ActionDescription("列表")]
    public IActionResult Index()
    {
        var vm = Wtm.CreateVM<StudentListVM>();
        return PartialView(vm);
    }

    [ActionDescription("新增")]
    public IActionResult Create()
    {
        var vm = Wtm.CreateVM<StudentVM>();
        return PartialView(vm);
    }

    [HttpPost]
    [ActionDescription("新增")]
    public IActionResult Create(StudentVM vm)
    {
        if (!ModelState.IsValid)
        {
            return PartialView(vm);
        }
        vm.DoAdd();
        return FFResult()
            .CloseDialog()
            .RefreshGrid();
    }

    [ActionDescription("編輯")]
    public IActionResult Edit(string id)
    {
        var vm = Wtm.CreateVM<StudentVM>(id);
        return PartialView(vm);
    }

    [HttpPost]
    [ActionDescription("編輯")]
    public IActionResult Edit(StudentVM vm)
    {
        if (!ModelState.IsValid)
        {
            return PartialView(vm);
        }
        vm.DoEdit();
        return FFResult()
            .CloseDialog()
            .RefreshGrid();
    }

    [ActionDescription("刪除")]
    public IActionResult Delete(string id)
    {
        var vm = Wtm.CreateVM<StudentVM>(id);
        vm.DoDelete();
        return FFResult()
            .RefreshGrid();
    }
}
```

**核心模式**：
- Controller 只負責建立 VM 並呼叫 VM 方法，**不直接存取 DataContext**
- `Wtm.CreateVM<T>()` 自動注入 `WTMContext` 到 VM
- `Wtm.CreateVM<T>(id)` 建立 VM 並自動載入指定 ID 的資料
- `FFResult()` 回傳 WTM 前端操作指令（關閉 Dialog、重新整理 Grid 等）

---

## 6. LayUI 前端

WTM 提供 LayUI TagHelper，讓你在 `.cshtml` 中用宣告式語法建立 UI 元件。

### 6.1 列表頁面 (Index.cshtml)

```html
@model WtmDemo.ViewModels.StudentListVM

<wt:searchpanel vm="@Model">
    <wt:textbox field="Searcher.Name" />
</wt:searchpanel>

<wt:grid vm="@Model" />
```

`<wt:grid>` 會根據 `InitGridHeader()` 自動渲染表格欄位，並內建分頁、排序功能。

### 6.2 表單頁面 (Create.cshtml / Edit.cshtml)

```html
@model WtmDemo.ViewModels.StudentVM

<wt:form vm="@Model">
    <wt:textbox field="Entity.Name" />
    <wt:textbox field="Entity.Age" />
    <wt:textbox field="Entity.Email" />
    <wt:submitbutton />
</wt:form>
```

**TagHelper 重點**：
- `<wt:form>` 自動處理 POST 提交和驗證錯誤顯示
- `<wt:textbox>` 根據 Model 的 `[Display]` 屬性自動產生 label
- `<wt:grid>` 支援多選、行內編輯、匯出等功能
- 所有 LayUI JS/CSS 由 `UseWtmStaticFiles()` 自動載入，路徑為 `/_js/`

---

## 7. 下一步

完成第一個 CRUD 後，建議依序學習以下進階功能：

| 主題 | 說明 | 文件 |
|------|------|------|
| 四種 ViewModel 詳解 | BaseCRUDVM、BasePagedListVM、BaseImportVM、BaseBatchVM | [開發手冊 - 4. 四種 ViewModel](wtm-developer-manual.md#4-四種-viewmodel) |
| Analysis Mode | 屬性驅動的即時分析面板 | [開發手冊 - 7. Analysis Mode](wtm-developer-manual.md#7-analysis-mode-分析模式) |
| ETL 模組 | 資料匯入匯出排程 | [開發手冊 - 8. ETL 模組](wtm-developer-manual.md#8-etl-模組) |
| Dashboard | 儀表板視覺化 | [開發手冊 - 9. Dashboard 模組](wtm-developer-manual.md#9-dashboard-模組) |
| Lookup Cache | 快取常用查詢資料 | [開發手冊 - 12. Lookup Cache](wtm-developer-manual.md#12-lookup-cache) |
| 安全機制 | PBKDF2 密碼、JWT 刷新、RBAC | [開發手冊 - 10. 安全機制](wtm-developer-manual.md#10-安全機制) |
| 多租戶 | 全域查詢篩選器 | [開發手冊 - 11. 多租戶](wtm-developer-manual.md#11-多租戶) |
| 代碼生成器 | 一鍵生成完整 CRUD | [開發手冊 - 14. 代碼生成器](wtm-developer-manual.md#14-代碼生成器) |
| 測試指南 | MSTest + Mock 模式 | [開發手冊 - 13. 測試指南](wtm-developer-manual.md#13-測試指南) |

完整開發手冊：[wtm-developer-manual.md](wtm-developer-manual.md)
