# WTM 開發與使用手冊

> **版本**：10.3.0 | **目標框架**：.NET 10 (LTS) | **最後更新**：2026-04-17

WalkingTec MVVM Framework (WTM) 是一套 ASP.NET Core 快速開發框架，以四種 ViewModel 類型為核心，搭配內建代碼生成器、LayUI TagHelper、Analysis Mode、ETL 模組與 Dashboard，提供完整的企業級 CRUD 開發體驗。

---

## 目錄

1. [快速開始](#1-快速開始)
2. [架構總覽](#2-架構總覽)
3. [Model 層](#3-model-層)
4. [四種 ViewModel](#4-四種-viewmodel)
5. [Controller 層](#5-controller-層)
6. [LayUI TagHelper](#6-layui-taghelper)
7. [Analysis Mode 分析模式](#7-analysis-mode-分析模式)
8. [ETL 模組](#8-etl-模組)
9. [Dashboard 模組](#9-dashboard-模組)
10. [安全機制](#10-安全機制)
11. [多租戶](#11-多租戶)
12. [Lookup Cache](#12-lookup-cache)
13. [測試指南](#13-測試指南)
14. [TimeProvider 時間抽象](#14-timeprovider-時間抽象)
15. [Integration Tests 整合測試](#15-integration-tests-整合測試)
16. [代碼生成器](#16-代碼生成器)
17. [配置參考](#17-配置參考)
18. [常見問題](#18-常見問題)

---

## 1. 快速開始

### 1.1 最小可運行應用

**Program.cs**
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
app.UseWtmContext();         // 每個請求注入 WTMContext
app.Run();
```

**appsettings.json（最小配置）**
```json
{
  "Connections": [
    {
      "Key": "default",
      "Value": "Data Source=app.db",
      "DbType": "SQLite"
    }
  ],
  "CookiePre": "WTM"
}
```

**DataContext.cs**
```csharp
public class DataContext : FrameworkContext
{
    public DbSet<Employee> Employees { get; set; } = null!;

    public DataContext(CS cs) : base(cs) { }
    public DataContext(string cs, DBTypeEnum dbType) : base(cs, dbType) { }
}
```

### 1.2 建置與測試

```bash
# 建置
dotnet build WalkingTec.Mvvm.sln -c Release

# 執行全部測試
dotnet test WalkingTec.Mvvm.sln -c Release --verbosity normal

# 執行單一測試類別
dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj \
  --filter "FullyQualifiedName~AnalysisControllerTests" -c Release

# JS 測試
cd test/WalkingTec.Mvvm.Js.Tests && npm ci && npm test
```

---

## 2. 架構總覽

### 2.1 專案結構

| 專案 | 角色 |
|------|------|
| `WalkingTec.Mvvm.Core` | 核心：ViewModel 基底類、DataContext、Model、Analysis 引擎、Lookup Cache |
| `WalkingTec.Mvvm.Mvc` | MVC 層：Controller 基底、Framework Controller、Filter、TagHelper 支援、內嵌 JS |
| `WalkingTec.Mvvm.TagHelpers.LayUI` | LayUI TagHelper（50+ 個：Grid、Form、Dialog、Selector 等） |
| `WalkingTec.Mvvm.Etl` | ETL 模組：Pipeline、MSSQL/Oracle Loader、Quartz 排程、管理 UI |

### 2.2 請求生命週期

```
HTTP Request
  → WtmMiddleware (request body buffering, upload limit)
  → DataContextFilter (選擇連線字串 + DB 類型)
  → PrivilegeFilter (權限檢查、URL 存取控制)
  → FrameworkFilter (注入 WTMContext、初始化 VM、驗證)
  → Controller Action (業務邏輯)
  → FrameworkFilter.OnActionExecuted (ViewData 注入、頁面標題)
  → FrameworkFilter.OnResultExecuted (寫入 ActionLog 審計日誌)
```

### 2.3 核心關係圖

```
BaseVM ← BaseCRUDVM<T>
       ← BasePagedListVM<TModel, TSearcher>
       ← BaseBatchVM<TModel, TEditModel>
       ← BaseImportVM<TTemplate, TEntity>
       ← BaseTemplateVM

BaseController → WTMContext → IDataContext (EF Core)
BaseApiController ↗              ↓
                          FrameworkContext (多資料庫)
```

---

## 3. Model 層

### 3.1 基底類繼承鏈

```
TopBasePoco          → Guid ID, Checked, BatchError, ExcelIndex (transient)
  ├─ BasePoco        → + CreateTime, CreateBy, UpdateTime, UpdateBy (審計欄位)
  │    ├─ PersistPoco → + bool IsValid (軟刪除，透過 Global Query Filter 排除)
  │    └─ FrameworkUser, FrameworkRole, ... (內建模型)
  └─ TreePoco        → + Guid? ParentId (樹狀結構)
       └─ TreePoco<T> → + T? Parent, List<T>? Children (導航屬性)
```

### 3.2 內建框架模型

| 模型 | 用途 |
|------|------|
| `FrameworkUser` | 使用者（帳號、密碼 PBKDF2、姓名、照片、租戶碼） |
| `FrameworkRole` | 角色（代碼、名稱） |
| `FrameworkUserRole` | 使用者↔角色 M:N 關聯表 |
| `FrameworkGroup` | 群組 |
| `FrameworkUserGroup` | 使用者↔群組 M:N 關聯表 |
| `FrameworkMenu` | 階層式選單（TreePoco<FrameworkMenu>） |
| `FunctionPrivilege` | 角色↔選單 功能權限 |
| `DataPrivilege` | 資料列級權限（Entity + 條件） |
| `FileAttachment` | 檔案附件（metadata + blob） |
| `ActionLog` | 審計日誌（誰、做什麼、何時、IP、結果） |
| `FrameworkTenant` | 多租戶定義 |
| `RefreshTokenEntity` | JWT Refresh Token 儲存 |

### 3.3 關鍵介面

| 介面 | 用途 | 範例 |
|------|------|------|
| `IBasePoco` | 審計欄位 | `CreateTime`, `UpdateBy` |
| `ITenant` | 多租戶 | `string? TenantCode` → Global Query Filter |
| `IPersistPoco` | 軟刪除 | `bool IsValid` → Query Filter 排除 false |
| `ISubFile` | 檔案關聯標記 | 子表附件 |

### 3.4 Model 範例

```csharp
public class Employee : BasePoco, ITenant
{
    [Display(Name = "姓名")]
    [Required(ErrorMessage = "{0}是必填項")]
    [StringLength(50)]
    public string Name { get; set; } = "";

    [Display(Name = "部門")]
    public Guid? DepartmentId { get; set; }

    [Display(Name = "部門")]
    public Department? Department { get; set; }

    [Display(Name = "薪資")]
    [Dimension(DisplayName = "部門", Hierarchy = DateHierarchy.None)]
    public decimal Salary { get; set; }

    [Display(Name = "入職日期")]
    [Dimension(Hierarchy = DateHierarchy.Month)]
    public DateTime HireDate { get; set; }

    public string? TenantCode { get; set; }
}

public class Department : BasePoco
{
    [Display(Name = "部門名稱")]
    [Required]
    public string Name { get; set; } = "";

    [Display(Name = "部門代碼")]
    [RegularExpression(@"^\d{3}$", ErrorMessage = "必須是3位數字")]
    public string Code { get; set; } = "";
}
```

### 3.5 常用 Attribute

| Attribute | 對象 | 用途 |
|-----------|------|------|
| `[Dimension]` | Property | Analysis Mode 維度（GROUP BY 候選） |
| `[Measure]` | Property | Analysis Mode 度量（聚合候選） |
| `[EnableAnalysis]` | Class (ListVM) | 啟用 Analysis Mode |
| `[CacheLookup]` | Class (Model) | 啟用 Lookup Cache |
| `[MiddleTable]` | Class | M:N 關聯表標記（自動處理） |
| `[SoftFK]` | Property | 軟外鍵（名稱對應，非導航屬性） |
| `[CanNotEdit]` | Property | 建立後唯讀 |
| `[ActionDescription]` | Method | 自訂選單/動作標籤 |
| `[Public]` | Method | 不需權限檢查 |
| `[AllRights]` | Method | 僅管理員可用 |
| `[DebugOnly]` | Method | 僅 Debug 模式可用 |
| `[NoLog]` | Method | 不寫審計日誌 |
| `[FixConnection]` | Method | 指定特定連線字串 |

---

## 4. 四種 ViewModel

### 4.1 BaseVM — 所有 VM 的共同基底

**關鍵屬性：**
```csharp
public WTMContext? Wtm { get; set; }     // DI 注入的請求上下文
public IDataContext? DC { get; set; }    // EF Core DataContext
public Dictionary<string, object> FC     // 表單資料集合
public LoginUserInfo? LoginUserInfo      // 當前登入使用者
public IDistributedCache? Cache          // 分散式快取
public IModelStateService? MSD           // 驗證錯誤服務
public IStringLocalizer? Localizer       // 多語系
```

**生命週期方法（可覆寫）：**
```csharp
protected virtual void InitVM() { }      // VM 建立後初始化（載入下拉選單等）
protected virtual void ReInitVM() { }    // 驗證失敗後重新初始化
public virtual void Validate() { }       // 自訂驗證邏輯
```

---

### 4.2 BaseCRUDVM\<T\> — 單筆實體 CRUD

**適用場景：** 新增、編輯、刪除、詳情查看單一實體。

```csharp
public class EmployeeVM : BaseCRUDVM<Employee>
{
    // 下拉選單資料
    public List<ComboSelectListItem>? AllDepartments { get; set; }

    // M:N 關聯的選取 ID
    public List<string>? SelectedSkillIds { get; set; }

    protected override void InitVM()
    {
        // 載入下拉選單
        AllDepartments = DC!.Set<Department>()
            .GetSelectListItems(Wtm!, x => x.Name);

        // 載入已選的 M:N 關聯
        if (Entity.ID != Guid.Empty)
        {
            SelectedSkillIds = DC.Set<EmployeeSkill>()
                .Where(x => x.EmployeeId == Entity.ID)
                .Select(x => x.SkillId.ToString())
                .ToList();
        }
    }

    public override void Validate()
    {
        // 自訂驗證：薪資不能為負數
        if (Entity.Salary < 0)
            MSD!.AddModelError("Entity.Salary", "薪資不能為負數");
    }

    public override void DoAdd()
    {
        base.DoAdd();
        // 新增 M:N 關聯記錄
        if (SelectedSkillIds != null)
        {
            foreach (var skillId in SelectedSkillIds)
            {
                DC!.Set<EmployeeSkill>().Add(new EmployeeSkill
                {
                    EmployeeId = Entity.ID,
                    SkillId = Guid.Parse(skillId)
                });
            }
            DC.SaveChanges();
        }
    }

    public override void DoEdit(bool updateAllFields = false)
    {
        // 重建 M:N 關聯
        DC!.Set<EmployeeSkill>()
            .Where(x => x.EmployeeId == Entity.ID)
            .ToList()
            .ForEach(x => DC.DeleteEntity(x));

        if (SelectedSkillIds != null)
        {
            foreach (var skillId in SelectedSkillIds)
            {
                DC.Set<EmployeeSkill>().Add(new EmployeeSkill
                {
                    EmployeeId = Entity.ID,
                    SkillId = Guid.Parse(skillId)
                });
            }
        }
        base.DoEdit(updateAllFields);
    }
}
```

---

### 4.3 BasePagedListVM\<TModel, TSearcher\> — 分頁列表

**適用場景：** 搜尋、分頁、匯出 Excel、Analysis Mode。

```csharp
// 搜尋條件
public class EmployeeSearcher : BaseSearcher
{
    [Display(Name = "姓名")]
    public string? Name { get; set; }

    [Display(Name = "部門")]
    public Guid? DepartmentId { get; set; }

    [Display(Name = "入職日期起")]
    public DateTime? HireDateBegin { get; set; }

    [Display(Name = "入職日期迄")]
    public DateTime? HireDateEnd { get; set; }
}

// 列表顯示用 DTO
public class Employee_View : Employee
{
    [Display(Name = "部門名稱")]
    public string? DepartmentName_view { get; set; }
}

// ListVM
[EnableAnalysis]  // 啟用 Analysis Mode
public class EmployeeListVM : BasePagedListVM<Employee_View, EmployeeSearcher>
{
    protected override IEnumerable<IGridColumn<Employee_View>> InitGridHeader()
    {
        return new List<GridColumn<Employee_View>>
        {
            this.MakeGridHeader(x => x.Name).SetWidth(150),
            this.MakeGridHeader(x => x.DepartmentName_view).SetWidth(120),
            this.MakeGridHeader(x => x.Salary).SetWidth(100)
                .SetFormat((e, v) => $"{v:N0}"),
            this.MakeGridHeader(x => x.HireDate).SetWidth(120)
                .SetFormat((e, v) => ((DateTime)v).ToString("yyyy-MM-dd")),
            this.MakeGridHeaderAction(width: 200)
        };
    }

    protected override List<GridAction> InitGridAction()
    {
        return new List<GridAction>
        {
            this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Create,  "新增", ""),
            this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Edit,     "編輯", "", GridActionParameterTypesEnum.SingleId),
            this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Delete,   "刪除", "", GridActionParameterTypesEnum.MultiIds),
            this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Details,  "詳情", "", GridActionParameterTypesEnum.SingleId),
            this.MakeStandardAction("Employee", GridActionStandardTypesEnum.BatchEdit,"批量編輯", "", GridActionParameterTypesEnum.MultiIds),
            this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Import,   "匯入", ""),
            this.MakeStandardAction("Employee", GridActionStandardTypesEnum.ExportExcel, "匯出", ""),
        };
    }

    protected override IOrderedQueryable<Employee_View> GetSearchQuery()
    {
        var query = DC!.Set<Employee>()
            .CheckContain(Searcher.Name, x => x.Name)
            .CheckEqual(Searcher.DepartmentId, x => x.DepartmentId)
            .CheckBetween(Searcher.HireDateBegin, Searcher.HireDateEnd, x => x.HireDate)
            .DPWhere(Wtm!, x => x.DepartmentId)  // 資料權限過濾
            .Select(x => new Employee_View
            {
                ID = x.ID,
                Name = x.Name,
                Salary = x.Salary,
                HireDate = x.HireDate,
                DepartmentName_view = x.Department!.Name
            })
            .OrderByDescending(x => x.HireDate);

        return query;
    }
}
```

**搜尋擴展方法：**
| 方法 | 用途 | SQL 對應 |
|------|------|---------|
| `CheckContain(val, expr)` | 模糊搜尋 | `LIKE '%val%'` |
| `CheckEqual(val, expr)` | 精確比對 | `= val` |
| `CheckBetween(begin, end, expr)` | 區間查詢 | `BETWEEN` |
| `CheckWhere(val, predicate)` | 自訂條件 | 任意 WHERE |
| `DPWhere(wtm, expr)` | 資料權限過濾 | 依 DataPrivilege 設定 |

---

### 4.4 BaseBatchVM\<TModel, TEditModel\> — 批量操作

**適用場景：** 勾選多筆後批量修改欄位或批量刪除。

```csharp
// 可編輯欄位定義
public class Employee_BatchEdit : BaseVM
{
    [Display(Name = "部門")]
    public Guid? DepartmentId { get; set; }

    [Display(Name = "薪資調整")]
    public decimal? SalaryAdjustment { get; set; }
}

public class EmployeeBatchVM : BaseBatchVM<Employee, Employee_BatchEdit>
{
    public EmployeeBatchVM()
    {
        ListVM = new EmployeeListVM();
        LinkedVM = new Employee_BatchEdit();
    }
}
```

---

### 4.5 BaseImportVM\<TTemplate, TEntity\> — Excel 匯入

**適用場景：** 從 Excel 批量匯入資料。

```csharp
public class EmployeeTemplateVM : BaseTemplateVM
{
    [Display(Name = "姓名")]
    public ExcelPropety Name_Excel = ExcelPropety.CreateProperty<Employee>(x => x.Name);

    [Display(Name = "薪資")]
    public ExcelPropety Salary_Excel = ExcelPropety.CreateProperty<Employee>(x => x.Salary);

    [Display(Name = "部門")]
    public ExcelPropety Department_Excel = ExcelPropety.CreateProperty<Employee>(x => x.DepartmentId);

    protected override void InitVM()
    {
        // 設定部門下拉選單（Excel 下拉驗證）
        Department_Excel.DataType = ColumnDataType.ComboBox;
        Department_Excel.ListItems = DC!.Set<Department>()
            .GetSelectListItems(Wtm!, x => x.Name);
    }
}

public class EmployeeImportVM : BaseImportVM<EmployeeTemplateVM, Employee>
{
}
```

---

## 5. Controller 層

### 5.1 BaseController vs BaseApiController

| 特性 | BaseController | BaseApiController |
|------|---------------|-------------------|
| 繼承 | `Controller` | `ControllerBase` |
| 回傳 | View/PartialView/FFResult | JSON |
| 驗證失敗 | 重新渲染表單 | 400 + 錯誤 JSON |
| 適用 | LayUI 前端 | SPA / API |

### 5.2 完整 CRUD Controller 範例

```csharp
[ActionDescription("員工管理")]
public class EmployeeController : BaseController
{
    // === 列表 ===
    [ActionDescription("搜尋")]
    public IActionResult Index()
    {
        var vm = Wtm.CreateVM<EmployeeListVM>();
        return PartialView(vm);
    }

    [ActionDescription("搜尋")]
    [HttpPost]
    public string Search(EmployeeSearcher searcher)
    {
        var vm = Wtm.CreateVM<EmployeeListVM>(passInit: true);
        vm.Searcher = searcher;
        return vm.GetJson(enumToString: false);
    }

    // === 新增 ===
    [ActionDescription("新增")]
    public IActionResult Create()
    {
        var vm = Wtm.CreateVM<EmployeeVM>();
        return PartialView(vm);
    }

    [ActionDescription("新增")]
    [HttpPost]
    public IActionResult Create(EmployeeVM vm)
    {
        if (!ModelState.IsValid)
            return PartialView(vm);

        vm.DoAdd();
        if (!ModelState.IsValid)
            return PartialView(vm);

        return FFResult()
            .CloseDialog()
            .RefreshGrid();
    }

    // === 編輯 ===
    [ActionDescription("修改")]
    public IActionResult Edit(string id)
    {
        var vm = Wtm.CreateVM<EmployeeVM>(id);
        return PartialView(vm);
    }

    [ActionDescription("修改")]
    [HttpPost]
    [ValidateFormItemOnly]
    public IActionResult Edit(EmployeeVM vm)
    {
        if (!ModelState.IsValid)
            return PartialView(vm);

        vm.DoEdit();
        if (!ModelState.IsValid)
            return PartialView(vm);

        return FFResult()
            .CloseDialog()
            .RefreshGridRow(vm.Entity.ID);
    }

    // === 刪除 ===
    [ActionDescription("刪除")]
    public IActionResult Delete(string id)
    {
        var vm = Wtm.CreateVM<EmployeeVM>(id);
        return PartialView(vm);
    }

    [ActionDescription("刪除")]
    [HttpPost]
    public IActionResult Delete(string id, IFormCollection noUse)
    {
        var vm = Wtm.CreateVM<EmployeeVM>(id);
        vm.DoDelete();
        if (!ModelState.IsValid)
            return PartialView(vm);

        return FFResult()
            .CloseDialog()
            .RefreshGrid()
            .Alert(Localizer["Sys.DeleteSuccess"]);
    }

    // === 匯入匯出 ===
    [ActionDescription("匯入")]
    [HttpPost]
    public IActionResult Import(EmployeeImportVM vm)
    {
        if (vm.ErrorListVM.EntityList.Count > 0 || !vm.BatchSaveData())
            return PartialView(vm);

        return FFResult().CloseDialog().RefreshGrid();
    }

    [ActionDescription("匯出")]
    [HttpPost]
    public IActionResult ExportExcel(EmployeeListVM vm)
    {
        return vm.GetExportData();
    }
}
```

### 5.3 BaseApiController 完整 CRUD 範例

**適用場景：** SPA 前端（React/Vue/Angular）或手機 App 對接 API。

```csharp
[ActionDescription("員工管理API")]
[ApiController]
[Route("api/[controller]")]
public class EmployeeApiController : BaseApiController
{
    [ActionDescription("搜尋")]
    [HttpPost("search")]
    public IActionResult Search([FromBody] EmployeeSearcher searcher)
    {
        var vm = Wtm.CreateVM<EmployeeListVM>(passInit: true);
        vm.Searcher = searcher;
        return Content(vm.GetJson(), "application/json");
    }

    [ActionDescription("詳情")]
    [HttpGet("{id}")]
    public IActionResult Get(Guid id)
    {
        var vm = Wtm.CreateVM<EmployeeVM>(id.ToString());
        if (vm.Entity == null)
            return NotFound(new { Code = 404, Msg = "員工不存在" });
        return Ok(new { Code = 200, Msg = "success", Data = vm.Entity });
    }

    [ActionDescription("新增")]
    [HttpPost]
    public IActionResult Create([FromBody] EmployeeVM vm)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState.GetErrorJson());

        vm.DoAdd();
        if (!ModelState.IsValid)
            return BadRequest(ModelState.GetErrorJson());

        return Ok(new { Code = 200, Msg = "新增成功", Data = new { vm.Entity.ID } });
    }

    [ActionDescription("修改")]
    [HttpPut("{id}")]
    public IActionResult Edit(Guid id, [FromBody] EmployeeVM vm)
    {
        if (vm.Entity.ID != id)
            return BadRequest(new { Code = 400, Msg = "ID 不一致" });

        if (!ModelState.IsValid)
            return BadRequest(ModelState.GetErrorJson());

        vm.DoEdit();
        if (!ModelState.IsValid)
            return BadRequest(ModelState.GetErrorJson());

        return Ok(new { Code = 200, Msg = "修改成功" });
    }

    [ActionDescription("刪除")]
    [HttpDelete("{id}")]
    public IActionResult Delete(Guid id)
    {
        var vm = Wtm.CreateVM<EmployeeVM>(id.ToString());
        vm.DoDelete();
        if (!ModelState.IsValid)
            return BadRequest(ModelState.GetErrorJson());

        return Ok(new { Code = 200, Msg = "刪除成功" });
    }

    [ActionDescription("批量刪除")]
    [HttpPost("batch-delete")]
    public IActionResult BatchDelete([FromBody] Guid[] ids)
    {
        foreach (var id in ids)
        {
            var vm = Wtm.CreateVM<EmployeeVM>(id.ToString());
            vm.DoDelete();
        }
        return Ok(new { Code = 200, Msg = $"已刪除 {ids.Length} 筆" });
    }

    [ActionDescription("匯出")]
    [HttpPost("export")]
    public IActionResult Export([FromBody] EmployeeSearcher searcher)
    {
        var vm = Wtm.CreateVM<EmployeeListVM>(passInit: true);
        vm.Searcher = searcher;
        vm.SearcherMode = ListVMSearchModeEnum.Export;
        var data = vm.GenerateExcel();
        return File(data, "application/vnd.ms-excel",
            $"Employee_{DateTime.Now:yyyyMMdd}.xlsx");
    }
}
```

**API 與 MVC Controller 的關鍵差異：**

| 差異 | MVC (BaseController) | API (BaseApiController) |
|------|---------------------|------------------------|
| 回傳格式 | View / PartialView / FFResult | JSON (Ok / BadRequest / NotFound) |
| 驗證失敗 | `return PartialView(vm)` 重新渲染表單 | `return BadRequest(ModelState.GetErrorJson())` |
| 路由 | `[ActionDescription]` + 選單 | `[Route("api/[controller]")]` RESTful |
| 認證 | Cookie + Session | JWT Bearer Token |
| 典型前端 | LayUI（server-rendered） | React / Vue / 手機 App |

### 5.4 FFResultJson 流暢 API（推薦，10.3.0+）

FFResultJson 是 WTM 的 **CSP 安全** JSON 動作分派機制，告訴 LayUI 執行動作後該做什麼。Server 回傳 `X-WTM-Action: application/json` 標頭 + JSON body，client `ff.DispatchAction` 走白名單 switch，無任何動態代碼執行。

```csharp
FFResultJson()
    .CloseDialog()                  // 關閉對話框
    .RefreshGrid()                  // 重新載入列表
    .RefreshGridRow(id)             // 等同 RefreshGrid（layui 無單行刷新）
    .Alert("成功")                   // 對話框提示
    .Message("已儲存")               // 輕量 toast 訊息
    .Redirect("/Home/Index")        // 跳轉（僅接受相對路徑，阻擋 open-redirect）
    .Reload()                        // 重新載入當前頁面
    .RefreshPage();                  // 重新渲染 LayUI 主面板
```

**常用組合場景：**
```csharp
// 場景：新增成功後關閉對話框並刷新列表
return FFResultJson().CloseDialog().RefreshGrid();

// 場景：編輯成功後刷新
return FFResultJson().CloseDialog().RefreshGridRow(vm.Entity.ID);

// 場景：刪除成功後刷新並提示
return FFResultJson().CloseDialog().RefreshGrid()
    .Alert(Localizer["Sys.DeleteSuccess"]);
```

**安全契約**：
- `Alert` / `Message` 的 `msg` / `title` 參數由 dispatcher 透過 `ff.EscapeText`（jQuery `.text()/.html()` idiom）自動轉義，**視為純文字**（避免 layui `layer.alert` 的 innerHTML XSS）
- `Redirect(url)` 僅接受 `/`, `~/`, `#`, `?` 開頭的相對路徑；絕對 URL / protocol-relative / `javascript:` 一律 `ArgumentException`
- `Type` 以 `WtmActionType` enum 型別保證（wire 格式為 `"alert"`、`"closeDialog"` 等 camelCase，client 已鎖死）

**Action 清單**：

| 方法 | 客戶端行為 |
|------|-----------|
| `CloseDialog()` | `ff.CloseDialog()` |
| `Alert(msg, title?)` | `layer.alert(escape(msg), { title })` |
| `Message(msg, title?)` | `layer.msg(escape(msg))` |
| `RefreshGrid(winId?, index?)` | `ff.RefreshGrid(winId \|\| 'LAY_app_body', index \|\| 0)` |
| `RefreshGridRow(id, winId?)` | 同 `RefreshGrid`（layui 無單行刷新） |
| `RefreshPage()` | `layui.index.render()` |
| `Reload()` | `location.reload()` |
| `Redirect(url)` | `location.href = url`（僅相對路徑） |

### 5.4.1 FFResult 遺留 API（已 `[Obsolete]`）

舊版 `FFResult()` 透過 `IsScript: true` header + 回傳 JavaScript 字串由 client `eval()` 執行。**10.3.0 起標記 `[Obsolete(DiagnosticId = "WTM789")]`**，編譯期警告 downstream 遷移至 `FFResultJson()`。

```csharp
// 舊寫法（仍可用，會產生 WTM789 編譯警告）
return FFResult().CloseDialog().RefreshGrid();

// 新寫法
return FFResultJson().CloseDialog().RefreshGrid();
```

**抑制警告（暫緩遷移期間）：** 在 `.csproj` 加 `<NoWarn>WTM789</NoWarn>`。

**Client 端 legacy 路徑**：`ff.DispatchAction` 先檢查 `X-WTM-Action` header，若非 JSON 再走 `IsScript` → `ff._legacyScriptEval` → `eval()`（集中於單一 helper，僅剩 1 個 `eval(` 在 `framework_layui.js`）。下次 major release 將移除此 fallback。

**常用組合場景：**
```csharp
// 場景：新增成功後關閉對話框並刷新列表
return FFResult().CloseDialog().RefreshGrid();

// 場景：編輯成功後只更新該行（避免翻回第一頁）
return FFResult().CloseDialog().RefreshGridRow(vm.Entity.ID);

// 場景：刪除成功後刷新並提示
return FFResult().CloseDialog().RefreshGrid()
    .Alert(Localizer["Sys.DeleteSuccess"]);

// 場景：匯入完成後刷新並跳轉
return FFResult().CloseDialog().RefreshGrid()
    .RedirectTo("/Employee/Index");
```

### 5.5 檔案上傳與下載

**上傳（由 _FrameworkController 統一處理）：**
```
POST /_Framework/Upload → 回傳 { Id, Name }
POST /_Framework/UploadImage → 回傳 { Id, Name }（支援自動縮圖 width/height）
```

**在 Controller 中使用已上傳的檔案：**
```csharp
// Model 中定義附件關聯
public class Employee : BasePoco
{
    public Guid? PhotoId { get; set; }
    [Display(Name = "照片")]
    public FileAttachment? Photo { get; set; }
}

// View 中使用 upload TagHelper
// <wt:upload field="Entity.PhotoId" upload-type="ImageFile" />

// 下載（由 _FrameworkController 統一處理）
// GET /_Framework/GetFile?id={fileId}           → 下載
// GET /_Framework/GetFile?id={fileId}&stream=true → 在瀏覽器內顯示
// GET /_Framework/GetFile?id={fileId}&width=200&height=200 → 縮圖
```

### 5.6 常用 Action Attribute

| Attribute | 用途 | 範例 |
|-----------|------|------|
| `[ActionDescription("名稱")]` | 定義選單/功能標籤（權限管理用） | `[ActionDescription("員工管理")]` |
| `[Public]` | 跳過權限檢查（任何人可用） | 登入頁、驗證碼 |
| `[AllRights]` | 任何已登入使用者可用 | 個人設定、首頁 |
| `[DebugOnly]` | 僅 Debug 模式可用 | 代碼生成器 |
| `[NoLog]` | 不寫 ActionLog 審計日誌 | 高頻查詢（避免日誌膨脹） |
| `[FixConnection("cskey")]` | 指定特定連線字串 | 跨資料庫查詢 |
| `[ValidateFormItemOnly]` | 僅驗證表單送出的欄位 | 編輯時忽略未送出的必填欄位 |

### 5.7 Framework Controller 路由表

| 路由 | 方法 | 用途 |
|------|------|------|
| `/_Framework/Selector` | POST | 彈出選擇器 |
| `/_Framework/GetPagingData` | POST | 分頁資料 |
| `/_Framework/Upload` | POST | 檔案上傳 |
| `/_Framework/GetFile?id=` | GET | 檔案下載/串流 |
| `/_Framework/GetExportExcel` | POST | 匯出 Excel |
| `/_Framework/Menu` | GET | 選單 JSON |
| `/_Framework/GetVerifyCode` | GET | 驗證碼圖片 |
| `/_Analysis/meta` | GET | Analysis 欄位中繼資料 |
| `/_Analysis/query` | POST | Analysis 聚合查詢 |
| `/_Analysis/export` | POST | Analysis 匯出 |
| `/_Dashboard/list` | GET | Dashboard 列表 |
| `/_Dashboard/{id}` | GET/PUT/DELETE | Dashboard CRUD |
| `/api/_account/refreshtoken` | POST | JWT Token 刷新 |

---

## 6. LayUI TagHelper

### 6.1 命名空間

```cshtml
@addTagHelper *, WalkingTec.Mvvm.TagHelpers.LayUI
```

### 6.2 佈局

```cshtml
@* 響應式行列 *@
<wt:row items-per-row="Three" items-per-row-sm="One" space="8">
    <wt:textbox field="Entity.Name" />
    <wt:combobox field="Entity.DepartmentId" items="@Model.AllDepartments" />
    <wt:datetime field="Entity.HireDate" type="Date" />
</wt:row>
```

### 6.3 表單完整範例

```cshtml
@model EmployeeVM

<wt:form vm="@Model">
    <wt:row items-per-row="Two">
        <wt:textbox field="Entity.Name" />
        <wt:textbox field="Entity.Salary" />
    </wt:row>
    <wt:row items-per-row="Two">
        <wt:combobox field="Entity.DepartmentId"
                     items="@Model.AllDepartments"
                     empty-text="請選擇部門" />
        <wt:datetime field="Entity.HireDate"
                     type="Date"
                     max="@DateTime.Today.ToString("yyyy-MM-dd")" />
    </wt:row>
    <wt:row items-per-row="One">
        <wt:checkbox field="SelectedSkillIds"
                     items="@Model.AllSkills" />
    </wt:row>
    <wt:row align="Right">
        <wt:submitbutton text="儲存" theme="Primary" />
        <wt:closebutton />
    </wt:row>
</wt:form>
```

### 6.4 列表 + 搜尋面板

```cshtml
@model EmployeeListVM

<wt:searchpanel vm="@Model">
    <wt:row items-per-row="Three">
        <wt:textbox field="Searcher.Name" />
        <wt:combobox field="Searcher.DepartmentId"
                     items="@ViewBag.AllDepartments" />
        <wt:datetime field="Searcher.HireDateBegin"
                     type="Date" />
    </wt:row>
</wt:searchpanel>

<wt:grid vm="@Model"
         url="/Employee/Search"
         height="-200"
         enable-analysis="true" />
```

### 6.5 主要 TagHelper 速查表

**佈局（6 個）：** `container`, `row`, `card`, `panel`, `tab`, `treecontent`

**表單（2 個）：** `form`, `searchpanel`

**輸入欄位（17 個）：**

| TagHelper | 用途 | 關鍵屬性 |
|-----------|------|---------|
| `wt:textbox` | 文字輸入 | `field`, `empty-text`, `is-password`, `search-url` |
| `wt:textarea` | 多行文字 | `field`, `empty-text` |
| `wt:combobox` | 下拉選單 | `field`, `items`, `multi-select`, `enable-search` |
| `wt:tree` | 樹狀選擇 | `field`, `items`, `show-line` |
| `wt:datetime` | 日期時間 | `field`, `type` (date/time/datetime), `min`, `max` |
| `wt:checkbox` | 多選框 | `field`, `items` |
| `wt:radio` | 單選框 | `field`, `items`, `yes-text`, `no-text` |
| `wt:switch` | 開關 | `field`, `lay-text` (ON\|OFF) |
| `wt:selector` | 彈窗選擇 | `field`, `list-vm`, `text-bind`, `multi-select` |
| `wt:upload` | 檔案上傳 | `field`, `upload-type`, `file-size` |
| `wt:hidden` | 隱藏欄位 | `field` |
| `wt:display` | 唯讀顯示 | `field` |
| `wt:slider` | 滑桿 | `field`, `min`, `max`, `step` |
| `wt:transfer` | 穿梭框 | `field`, `items` |
| `wt:colorpicker` | 色彩選擇 | `field` |
| `wt:richtextbox` | 富文字 | `field` |

**按鈕（6 個）：** `button`, `submitbutton`, `resetbutton`, `closebutton`, `linkbutton`, `downloadtemplatebutton`

**資料顯示（2 個）：** `grid`, `chart`

### 6.6 聯動下拉（Cascade）

**場景：省市區三級聯動**

```cshtml
@* 選擇省份後自動載入城市，選擇城市後載入區域 *@
<wt:row items-per-row="Three">
    <wt:combobox field="Entity.ProvinceId"
                 items="@Model.AllProvinces"
                 empty-text="請選擇省份"
                 link-field="Entity.CityId"
                 trigger-url="/Address/GetCities" />

    <wt:combobox field="Entity.CityId"
                 empty-text="請先選省份"
                 link-field="Entity.DistrictId"
                 trigger-url="/Address/GetDistricts" />

    <wt:combobox field="Entity.DistrictId"
                 empty-text="請先選城市" />
</wt:row>
```

Controller 端：
```csharp
[HttpGet]
public IActionResult GetCities(Guid id)
{
    var cities = DC.Set<City>()
        .Where(x => x.ProvinceId == id)
        .GetSelectListItems(Wtm, x => x.Name);
    return JsonMore(cities);
}

[HttpGet]
public IActionResult GetDistricts(Guid id)
{
    var districts = DC.Set<District>()
        .Where(x => x.CityId == id)
        .GetSelectListItems(Wtm, x => x.Name);
    return JsonMore(districts);
}
```

### 6.7 彈窗選擇器（Selector）

**場景：從員工列表中選擇審批人**

```cshtml
@* 單選 — 選擇一位審批人 *@
<wt:selector field="Entity.ApproverId"
             list-vm="@typeof(EmployeeListVM)"
             text-bind="Name"
             window-title="選擇審批人"
             window-width="800"
             window-height="500" />

@* 多選 — 選擇多位參與者（field 為 List 類型時自動多選） *@
<wt:selector field="SelectedParticipantIds"
             list-vm="@typeof(EmployeeListVM)"
             text-bind="Name"
             multi-select="true"
             window-title="選擇參與者" />
```

**運作機制：**
1. 點擊輸入框右側按鈕 → 開啟 `/_Framework/Selector` 彈窗
2. 彈窗內載入指定 ListVM 的搜尋 + 列表頁面
3. 使用者勾選後點確認 → 選中的 ID 寫回 `field`，顯示名稱寫回 `text-bind`
4. 多選時以逗號分隔

### 6.8 檔案上傳（Upload）

**場景 1：上傳員工大頭照（圖片，限 2MB）**
```cshtml
<wt:upload field="Entity.PhotoId"
           upload-type="ImageFile"
           file-size="2048"
           show-preview="true"
           thumb-width="120"
           thumb-height="120" />
```

**場景 2：上傳合約文件（PDF/Word，限 10MB）**
```cshtml
<wt:upload field="Entity.ContractFileId"
           upload-type="AllFiles"
           file-size="10240" />
```

**場景 3：上傳 Excel 匯入檔**
```cshtml
<wt:upload field="UploadFileId"
           upload-type="ExcelFile"
           file-size="5120" />
```

**upload-type 選項：**

| 類型 | 說明 | 允許的副檔名 |
|------|------|------------|
| `AllFiles` | 所有檔案 | 不限 |
| `ImageFile` | 圖片 | jpg, jpeg, png, bmp, gif |
| `ZipFile` | 壓縮檔 | zip, rar, 7z |
| `ExcelFile` | Excel | xlsx, xls |
| `WordFile` | Word | docx, doc |
| `PDFFile` | PDF | pdf |
| `TextFile` | 文字 | txt, csv |

### 6.9 對話框（Dialog）

**場景：在列表頁彈出編輯對話框**

Dialog 不是 TagHelper，而是透過 Grid Action 自動觸發。當 GridAction 的 `ShowInRow` 為 `true` 時，點擊列表行按鈕會開啟對話框：

```csharp
// ListVM 中定義 Action
protected override List<GridAction> InitGridAction()
{
    return new List<GridAction>
    {
        // 標準 CRUD 按鈕 — 自動開啟對話框
        this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Create, "新增", ""),
        this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Edit, "編輯", "",
            GridActionParameterTypesEnum.SingleId),
        this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Details, "詳情", "",
            GridActionParameterTypesEnum.SingleId),

        // 自訂按鈕 — 指定 URL 和對話框大小
        this.MakeAction("Employee", "Approve", "審批", "審批",
            GridActionParameterTypesEnum.SingleId)
            .SetDialogTitle("員工審批")
            .SetWidth(600).SetHeight(400),

        // 無對話框 — 直接發送 AJAX（例如「啟用/停用」切換）
        this.MakeAction("Employee", "ToggleStatus", "切換狀態", "",
            GridActionParameterTypesEnum.SingleId)
            .SetIsRedirect(false)
            .SetOnClickScript("toggleEmployeeStatus"),
    };
}
```

**手動開啟對話框（JavaScript）：**
```javascript
// 在 LayUI 中手動開啟 iframe 對話框
layui.layer.open({
    type: 2,
    title: '自訂對話框',
    area: ['800px', '600px'],
    content: '/Employee/Edit?id=' + employeeId
});
```

### 6.10 Grid 進階功能

**固定列 + 行內按鈕 + 自訂排序：**
```csharp
protected override IEnumerable<IGridColumn<Employee_View>> InitGridHeader()
{
    return new List<GridColumn<Employee_View>>
    {
        // 固定左側
        this.MakeGridHeader(x => x.Name).SetWidth(150).SetFixed(GridColumnFixedEnum.Left),

        // 自訂格式化
        this.MakeGridHeader(x => x.Salary).SetWidth(120)
            .SetFormat((entity, val) => $"<span style='color:green'>NT${val:N0}</span>"),

        // 可排序
        this.MakeGridHeader(x => x.HireDate).SetWidth(120).SetSort(true)
            .SetFormat((e, v) => ((DateTime)v).ToString("yyyy-MM-dd")),

        // 狀態欄（用顏色標籤）
        this.MakeGridHeader(x => x.Status).SetWidth(80)
            .SetFormat((e, v) => v?.ToString() == "Active"
                ? "<span class='layui-badge layui-bg-green'>在職</span>"
                : "<span class='layui-badge'>離職</span>"),

        // 操作按鈕列（固定右側）
        this.MakeGridHeaderAction(width: 200).SetFixed(GridColumnFixedEnum.Right)
    };
}
```

**Grid 屬性速查：**

| 屬性 | 用途 | 範例值 |
|------|------|--------|
| `url` | 資料來源 URL | `/Employee/Search` |
| `height` | 表格高度（負值=距底部距離） | `-200` |
| `enable-analysis` | 啟用 Analysis Mode 按鈕 | `true` |
| `multi-select` | 多選（出現 checkbox） | `true`（預設） |
| `auto-search` | 頁面載入時自動搜尋 | `true`（預設） |

---

## 7. Analysis Mode 分析模式

Analysis Mode 讓任何列表頁面變成即時分析工具：使用者可以自由選擇維度（GROUP BY）和度量（SUM/AVG/COUNT 等），產生圖表和樞紐表，並匯出 Excel/CSV。開發者只需加 3 個 Attribute，零 JavaScript。

### 7.1 啟用方式（三步驟）

**步驟 1：Model 上標記維度和度量**
```csharp
public class SaleRecord : BasePoco
{
    [Dimension(DisplayName = "地區")]
    public string Region { get; set; } = "";

    [Dimension(DisplayName = "業務員")]
    public string SalesRep { get; set; } = "";

    [Dimension(DisplayName = "日期", Hierarchy = DateHierarchy.Month)]
    public DateTime SaleDate { get; set; }

    [Measure]
    public decimal Amount { get; set; }

    [Measure]
    public int Quantity { get; set; }

    [Measure]
    public decimal Discount { get; set; }
}
```

**步驟 2：ListVM 上標記 [EnableAnalysis]**
```csharp
[EnableAnalysis]
public class SaleRecordListVM : BasePagedListVM<SaleRecord, SaleRecordSearcher>
{
    protected override IOrderedQueryable<SaleRecord> GetSearchQuery()
    {
        return DC!.Set<SaleRecord>()
            .CheckContain(Searcher.Region, x => x.Region)
            .CheckBetween(Searcher.DateBegin, Searcher.DateEnd, x => x.SaleDate)
            .DPWhere(Wtm!, x => x.Region)  // 資料權限也在 Analysis 中生效
            .OrderByDescending(x => x.SaleDate);
    }
}
```

**步驟 3：View 的 Grid 啟用**
```cshtml
<wt:grid vm="@Model" enable-analysis="true" />
```

使用者在列表頁面會看到「分析模式」按鈕，點擊後進入互動式分析介面。

### 7.2 核心元件

| 元件 | 用途 |
|------|------|
| `AnalysisVmRegistry` | 啟動時掃描 `[EnableAnalysis]` VM，建立白名單 |
| `AnalysisFieldScanner` | 反射 Model 產生 `AnalysisFieldMeta` 列表 |
| `AnalysisQueryEngine` | 驗證欄位 → Expression Tree 過濾 → `Take(50_000)` → 記憶體內 GroupBy → 截斷至 10,000 行 |
| `AnalysisPivotEngine` | 樞紐分析（交叉表） |
| `AnalysisExcelExporter` | 匯出 XLSX |
| `_AnalysisController` | REST API（meta/query/pivot/export） |
| `framework_analysis.js` | 前端 UI（維度/度量選擇、圖表自動偵測、匯出按鈕） |

### 7.3 Dimension Attribute 詳解

```csharp
[Dimension(
    DisplayName = "訂單日期",        // UI 顯示名稱（預設用 [Display] 的 Name）
    Hierarchy = DateHierarchy.Month  // 日期層級（僅 DateTime 欄位有效）
)]
```

**DateHierarchy 選項：**

| 層級 | 說明 | 分組範例 |
|------|------|---------|
| `None` | 非日期欄位（預設） | 原值 |
| `Year` | 年 | 2026 |
| `Quarter` | 季 | 2026-Q1 |
| `Month` | 月 | 2026-03 |
| `Week` | 週 | 2026-W10 |
| `Day` | 日 | 2026-03-12 |
| `Hour` | 時 | 2026-03-12 14:00 |

使用者可在前端切換層級（例如從「月」切換到「季」），不需重新開發。

### 7.4 AggregateFunc 選項

| 函數 | 說明 | 適用類型 |
|------|------|---------|
| `Sum` | 加總 | 數值 |
| `Avg` | 平均 | 數值 |
| `Min` | 最小值 | 數值、日期 |
| `Max` | 最大值 | 數值、日期 |
| `Count` | 計數 | 任何 |
| `DistinctCount` | 不重複計數 | 任何 |
| `Variance` | 變異數 | 數值 |
| `StdDev` | 標準差 | 數值 |

### 7.5 API 端點

| 端點 | 方法 | 用途 |
|------|------|------|
| `/_analysis/meta?listVmType=` | GET | 取得可用維度/度量欄位清單 |
| `/_analysis/query` | POST | 執行聚合查詢 |
| `/_analysis/pivot` | POST | 樞紐分析（交叉表） |
| `/_analysis/export?format=xlsx\|csv` | POST | 匯出查詢結果 |
| `/_analysis/pivot/export?format=xlsx\|csv` | POST | 匯出樞紐結果 |

### 7.6 查詢請求與回應

**請求：**
```json
{
  "listVmType": "MyApp.ViewModels.SaleRecordListVM, MyApp",
  "dimensions": ["Region", "SaleDate"],
  "measures": [
    { "field": "Amount", "func": "Sum" },
    { "field": "Quantity", "func": "Count" }
  ],
  "dimensionHierarchies": { "SaleDate": "Month" },
  "filters": [
    { "field": "SaleDate", "operator": ">=", "value": "2026-01-01" }
  ],
  "searcherFormData": { "Region": "北區" }
}
```

**回應：**
```json
{
  "columns": ["Region", "SaleDate", "Amount_Sum", "Quantity_Count"],
  "rows": [
    { "Region": "北區", "SaleDate": "2026-01", "Amount_Sum": 150000, "Quantity_Count": 42 },
    { "Region": "北區", "SaleDate": "2026-02", "Amount_Sum": 180000, "Quantity_Count": 55 }
  ],
  "totalCount": 2,
  "truncated": false
}
```

### 7.7 使用場景範例

#### 場景 1：HR 部門分析員工薪資分佈

**Model：**
```csharp
public class Employee : BasePoco
{
    [Dimension(DisplayName = "部門")]
    public string Department { get; set; } = "";

    [Dimension(DisplayName = "職級")]
    public string Level { get; set; } = "";

    [Dimension(DisplayName = "入職日期", Hierarchy = DateHierarchy.Year)]
    public DateTime HireDate { get; set; }

    [Measure]
    public decimal Salary { get; set; }
}
```

**可能的分析操作（使用者在前端自由組合）：**
- 維度: 部門 → 度量: Salary(Avg) → 看各部門平均薪資
- 維度: 部門 + 職級 → 度量: Salary(Min, Max) → 看各部門各職級薪資範圍
- 維度: 入職日期(Year) → 度量: Salary(Avg) → 看薪資隨年資的趨勢

#### 場景 2：電商訂單多維分析

```csharp
public class OrderRecord : BasePoco
{
    [Dimension(DisplayName = "商品類別")]
    public string Category { get; set; } = "";

    [Dimension(DisplayName = "付款方式")]
    public string PaymentMethod { get; set; } = "";

    [Dimension(DisplayName = "訂單日期", Hierarchy = DateHierarchy.Month)]
    public DateTime OrderDate { get; set; }

    [Measure]
    public decimal OrderAmount { get; set; }

    [Measure]
    public decimal ShippingCost { get; set; }

    [Measure]
    public int ItemCount { get; set; }
}
```

**樞紐分析範例：** 行 = 商品類別，列 = 付款方式，值 = OrderAmount(Sum)
→ 產生「類別 × 付款方式」的交叉表。

### 7.8 自訂欄位存取控制

```csharp
// 實作 IAnalysisFieldPolicy 限制不同角色看到的欄位
public class MyFieldPolicy : IAnalysisFieldPolicy
{
    public IEnumerable<AnalysisFieldMeta> Filter(
        IEnumerable<AnalysisFieldMeta> fields,
        LoginUserInfo? user)
    {
        // 非管理員不能看到薪資欄位
        if (user?.Roles?.Any(r => r.RoleCode == "admin") != true)
            return fields.Where(f => f.FieldName != "Salary");
        return fields;
    }
}

// 註冊
services.AddSingleton<IAnalysisFieldPolicy, MyFieldPolicy>();
```

### 7.9 安全限制

| 限制 | 值 | 說明 |
|------|-----|------|
| 維度上限 | 3 個 | 防止過度分組 |
| 度量上限 | 3 個 | 防止過度聚合 |
| 資料取樣 | 50,000 筆 | `Take(50_000)` 後在記憶體內 GroupBy |
| 結果行數 | 10,000 筆 | 超過截斷，回應標記 `truncated: true` |
| CSV 防護 | 前置 tab | `=+@-\t\r` 開頭的儲存格加 tab，防止公式注入 |
| 欄位白名單 | 啟動時掃描 | 未標記 `[Dimension]`/`[Measure]` 的欄位無法查詢 |
| 資料權限 | DPWhere 生效 | `GetSearchQuery()` 中的資料權限過濾在 Analysis 中同樣生效 |

### 7.10 前端自動圖表選擇

`framework_analysis.js` 根據維度和度量組合自動選擇最佳圖表類型：

| 維度數 | 度量數 | 自動選擇 |
|--------|--------|---------|
| 0 | 1+ | KPI 卡片 |
| 1 (非日期) | 1 | 長條圖 |
| 1 (日期) | 1 | 折線圖 |
| 1 | 2+ | 堆疊長條圖 |
| 2+ | 1+ | 分組長條圖 |

使用者可手動切換圖表類型，不受自動偵測限制。

---

## 8. ETL 模組

### 8.1 架構

```
管理 UI (_EtlJobController)
  → EtlSchedulerService (啟用/暫停/觸發)
  → Quartz Scheduler
  → EtlQuartzJob [DisallowConcurrentExecution]
  → EtlPipelineExecutor
     1. EnsureStagingTable → 自動建 staging 表
     2. TruncateStaging
     3. Extract (IEtlSource) → Transform (optional) → BulkLoad (IBulkLoader)
     4. Merge staging → target (MERGE INTO)
     5. Commit/Discard Watermark
  → EtlRunLog (寫入執行紀錄)
  → EtlProgressTracker (即時進度)
```

### 8.2 啟用方式

```csharp
// Program.cs / Startup.cs
services.AddWtmEtl();

// DataContext.OnModelCreating
modelBuilder.ApplyEtlModels();
```

### 8.3 Job 定義範例

```csharp
var job = new EtlJobDefinition
{
    Name = "同步訂單",
    Description = "每小時從 MSSQL 來源同步訂單到本地",
    CronExpression = "0 0 * * * ?",       // 每小時
    SourceDbType = DBTypeEnum.SqlServer,
    SourceConnectionKey = "SourceMssql",
    TargetConnectionKey = "default",
    QueryTemplate = "SELECT * FROM Orders WHERE UpdatedAt > @watermark",
    TargetTableName = "Orders",
    MergeKeyColumn = "OrderNo",
    WatermarkType = EtlWatermarkType.Timestamp,
    WatermarkColumn = "UpdatedAt",
    BatchSize = 50000,
    TimeoutMinutes = 30,
    Status = EtlJobStatus.Enabled
};
```

### 8.4 支援的資料庫

| 來源/目標 | Extract | BulkLoad | Merge |
|-----------|---------|----------|-------|
| **SQL Server** | SqlCommand + SequentialAccess | SqlBulkCopy | MERGE...WHEN MATCHED/NOT MATCHED |
| **Oracle** | OracleCommand + SequentialAccess | Array Binding | MERGE INTO...USING |

### 8.5 水印策略（Watermark）

水印是增量同步的核心 — 記錄「上次同步到哪裡」，下次只抓新的資料。

| 類型 | 說明 | WHERE 子句 | 適用場景 |
|------|------|-----------|---------|
| `FullLoad` | 全量載入，每次抓全部 | `1=1` | 小資料表、維度表 |
| `Timestamp` | 時間戳增量 | `column > @watermark` | 有 UpdatedAt 欄位的交易表 |
| `Identity` | 自增 ID 增量 | `column > @watermark` | 僅新增不修改的日誌表 |

**水印生命週期：**
```
Job 開始 → 讀取 LastWatermarkValue
  → 每批次更新 PendingValue（取 batch 中的最大值）
  → 全部完成 → CommitPendingValue() → 存回 DB
  → 若失敗 → DiscardPendingValue() → 不更新，下次重跑
```

**時區處理：** 水印值一律以 UTC 儲存。若來源資料庫使用本地時間，在 `WatermarkTimeZone` 欄位指定時區（如 `"Asia/Taipei"`），系統自動在查詢時轉換。

### 8.6 使用場景範例

#### 場景 1：每小時從 MSSQL ERP 同步訂單（Timestamp 增量）

```csharp
// 1. 定義 Job
var job = new EtlJobDefinition
{
    Name = "sync-orders",
    Description = "每小時從 ERP 同步訂單到報表庫",
    CronExpression = "0 0 * * * ?",           // 每小時整點
    SourceDbType = DBTypeEnum.SqlServer,
    SourceConnectionKey = "ErpSource",         // appsettings.json 中的連線
    TargetConnectionKey = "default",
    QueryTemplate = @"
        SELECT OrderNo, CustomerName, Amount, Status, Region,
               OrderDate, UpdatedAt
        FROM dbo.Orders
        WHERE UpdatedAt > @watermark
        ORDER BY UpdatedAt",
    TargetTableName = "Orders",
    MergeKeyColumn = "OrderNo",               // MERGE ON 的 key
    WatermarkType = EtlWatermarkType.Timestamp,
    WatermarkColumn = "UpdatedAt",
    WatermarkTimeZone = "Asia/Taipei",
    InitialWatermarkValue = "2026-01-01T00:00:00",  // 首次同步起點
    BatchSize = 50000,
    TimeoutMinutes = 30,
    Status = EtlJobStatus.Enabled
};
```

**Pipeline 執行流程：**
```
1. EnsureStagingTable → 自動建立 Orders_Staging（與查詢結果同結構）
2. TruncateStaging → 清空 staging
3. 迴圈：
   a. Extract：SELECT ... WHERE UpdatedAt > '2026-03-12 09:00:00' (上次水印)
   b. Transform：（此例無自訂轉換）
   c. BulkLoad：SqlBulkCopy 寫入 Orders_Staging（每批 50,000 筆）
   d. 更新 PendingWatermark = batch 中最大的 UpdatedAt
4. Merge：
   MERGE INTO Orders AS target
   USING Orders_Staging AS source ON target.OrderNo = source.OrderNo
   WHEN MATCHED THEN UPDATE SET ...
   WHEN NOT MATCHED THEN INSERT ...
5. 成功 → Commit Watermark → 寫入 RunLog(Success)
   失敗 → Discard Watermark → 寫入 RunLog(Failed) + LastError
```

#### 場景 2：每天從 Oracle 同步客戶資料（FullLoad）

```csharp
var job = new EtlJobDefinition
{
    Name = "sync-customers",
    Description = "每天凌晨 2 點全量同步客戶主檔",
    CronExpression = "0 0 2 * * ?",           // 每天 02:00
    SourceDbType = DBTypeEnum.Oracle,
    SourceConnectionKey = "OracleHR",
    TargetConnectionKey = "default",
    QueryTemplate = @"
        SELECT CUSTOMER_ID, CUSTOMER_NAME, PHONE, EMAIL, REGION,
               CREDIT_LIMIT, STATUS
        FROM HR.CUSTOMERS
        WHERE STATUS = 'ACTIVE'",
    TargetTableName = "Customers",
    MergeKeyColumn = "CUSTOMER_ID",
    WatermarkType = EtlWatermarkType.FullLoad,  // 每次全量
    BatchSize = 10000,
    TimeoutMinutes = 60,
    Status = EtlJobStatus.Enabled
};
```

**Oracle 與 MSSQL Loader 差異：**

| 特性 | MssqlBulkLoader | OracleBulkLoader |
|------|----------------|-----------------|
| 寫入方式 | `SqlBulkCopy`（原生 binary copy） | Array Binding（參數化 INSERT） |
| 速度 | 極快（10 萬筆/秒） | 快（5 萬筆/秒） |
| Schema 探索 | `INFORMATION_SCHEMA.COLUMNS` | `USER_TAB_COLUMNS` |
| 表名命名 | 大小寫敏感（加 `[]` 括號） | 強制大寫 |
| MERGE 語法 | `MERGE ... WHEN MATCHED/NOT MATCHED` | `MERGE INTO ... USING` |

#### 場景 3：帶自訂轉換的 Pipeline

```csharp
// 在 EtlPipelineConfig 中設定 TransformFunc
var config = new EtlPipelineConfig
{
    // ... 基本設定 ...
    TransformFunc = batch =>
    {
        // 新增計算欄位
        batch.Columns.Add("FullName", typeof(string));
        batch.Columns.Add("AmountWithTax", typeof(decimal));

        foreach (DataRow row in batch.Rows)
        {
            row["FullName"] = $"{row["LastName"]}, {row["FirstName"]}";
            row["AmountWithTax"] = (decimal)row["Amount"] * 1.05m;
        }
        return batch;
    }
};
```

### 8.7 管理 UI 與操作

ETL 模組內建完整管理介面（`_EtlJobController`），支援 CRUD 和即時操作：

| 操作 | 端點 | 說明 |
|------|------|------|
| 列表/搜尋 | `/_EtlJob/Index`, `Search` | Job 清單（含狀態、下次執行時間） |
| 新增/編輯/刪除 | `/_EtlJob/Create`, `Edit`, `Delete` | 標準 CRUD |
| 手動觸發 | `POST /_EtlJob/TriggerNow?id=` | 立即執行一次（不影響排程） |
| 預覽執行（Dry-Run）| `POST /_EtlJob/DryRun?id=&sampleSize=10` | 只抓第一批 + transform，**不寫目標表**（10.4.0+，#834） |
| 暫停/恢復 | `POST /_EtlJob/Pause`, `Resume` | Quartz 排程暫停/恢復 |
| 中斷執行 | `POST /_EtlJob/Abort?id=` | 中斷正在執行的 Job |
| 跳過下次 | `POST /_EtlJob/SkipNext?id=` | 設定 SkipCount++ |
| 重新排程 | `POST /_EtlJob/Reschedule?id=&newCron=` | 更新 Cron 並重排 |

#### 8.7.1 Dry-Run 預覽模式（10.4.0+，#834）

新增 Job 或改 `QueryTemplate` / `MergeKeyColumn` 之後，**先 Dry-Run 再 TriggerNow** 可以在不動目標表的前提下驗證：

- SQL 語法、權限、連線字串是否可行（Extract 會真的執行）
- `MergeKeyColumn` 是否存在於 source columns
- Transform mapper 輸出長什麼樣
- 下次真跑時 watermark 會 commit 成什麼值
- source 有沒有資料可抓（空 source 會出警告）

**與 TriggerNow 的差異：**

| | TriggerNow | DryRun |
|-|-----------|--------|
| Extract | ✓ 全部 | ✓ 只第一批 |
| Transform | ✓ | ✓ |
| EnsureStagingTable / Truncate / BulkLoad / Merge | ✓ | ✗ |
| Watermark commit | ✓ | ✗（計算但不寫回） |
| 寫入 RunLog | ✓ | ✗（不污染 audit trail）|

**Response payload：**

```json
{
  "success": true,
  "isDryRun": true,
  "extractedRows": 1000,
  "elapsedMs": 412,
  "newWatermarkValue": "2026-04-18T05:30:00",
  "previewRows": [ { "OrderNo": "ORD-00000001", "Amount": 100, "UpdatedAt": "..." } ],
  "validationWarnings": [ "MergeKeyColumn 'bogus' not found in source columns: [...]" ],
  "errorMessage": null
}
```

`sampleSize` query param 控制 `previewRows` 長度（預設 10，上限 100）。

### 8.8 監控 API

```
GET /_EtlMonitor/Running       → 所有執行中 job 及進度
GET /_EtlMonitor/Progress?jobId=X → 單一 job 即時進度
```

**進度回應格式：**
```json
{
    "jobId": "3fa85f64-...",
    "jobName": "sync-orders",
    "phase": "BulkLoad",
    "processedRows": 125000,
    "totalRows": null,
    "rowsPerSecond": 48000,
    "startedAt": "2026-03-12T10:00:00Z"
}
```

**執行歷史：**
```
GET /_EtlRunLog/Index?jobId=X → 該 Job 的所有執行紀錄
POST /_EtlRunLog/Rerun?runLogId=X → 從某次執行的水印快照重跑
```

**EtlRunLog 記錄的資訊：**

| 欄位 | 說明 |
|------|------|
| `Trigger` | Scheduled / Manual / Retry |
| `Result` | Success / Failed / Aborted / Skipped |
| `ExtractedRows` | 抽取的總筆數 |
| `LoadedRows` | 寫入的總筆數 |
| `ErrorRows` | 錯誤筆數 |
| `ElapsedMs` | 執行耗時（毫秒） |
| `ErrorMessage` | 錯誤訊息（最長 4000 字元） |
| `WatermarkSnapshot` | 執行時的水印值（可用於重跑） |

### 8.9 Cron 表達式速查

| 表達式 | 說明 |
|--------|------|
| `0 0 * * * ?` | 每小時整點 |
| `0 0/30 * * * ?` | 每 30 分鐘 |
| `0 0 2 * * ?` | 每天凌晨 2 點 |
| `0 0 0 ? * MON-FRI` | 週一到週五午夜 |
| `0 0 8,12,18 * * ?` | 每天 8:00、12:00、18:00 |

### 8.10 並發控制與錯誤處理

- **`[DisallowConcurrentExecution]`**：同一 Job 不會同時執行兩個實例
- **失敗處理**：水印不更新（`DiscardPendingValue`），下次自動從上次成功點重跑
- **`OperationCanceledException`**：標記為 `Aborted`（手動中斷或逾時）
- **重跑機制**：每次 RunLog 保存 `WatermarkSnapshot`，可從任何歷史時間點重跑

---

## 9. Dashboard 模組

Dashboard 提供可拖拽、可設定的即時數據看板，支援 6 種 Widget 類型、自訂資料來源、Analysis Mode 整合、多租戶隔離與角色共享。

### 9.1 啟用方式

```csharp
// Program.cs / Startup.cs
services.AddWtmDashboard(opts =>
{
    opts.DashboardDirectory = "App_Data/dashboards";  // JSON 檔案儲存路徑
    opts.DefaultRefreshInterval = 60;                  // 秒
    opts.EnableEditing = true;                         // 允許前端編輯
    opts.AllowIframeSameOrigin = false;                // Embed widget 安全設定
});

// 註冊自訂資料來源
services.AddWidgetDataSource<SalesKpiDataSource>();
services.AddWidgetDataSource<InventoryTableSource>();
// 或批量掃描 assembly
services.AddWidgetDataSourcesFromAssembly(typeof(Startup).Assembly);
```

### 9.2 核心模型

**DashboardDefinition** — 一個看板的完整定義：
```csharp
{
    "id": "sales-overview",
    "title": "銷售總覽",
    "owner": "admin",
    "tenantId": null,
    "sharing": { "mode": "public", "roles": null },
    "refreshInterval": 30,
    "layout": [
        { "id": "w1", "x": 0, "y": 0, "w": 4, "h": 2 },
        { "id": "w2", "x": 4, "y": 0, "w": 8, "h": 4 },
        { "id": "w3", "x": 0, "y": 2, "w": 4, "h": 4 }
    ],
    "widgets": {
        "w1": {
            "type": "kpi",
            "title": "本月營收",
            "source": { "kind": "custom", "name": "sales-kpi" },
            "config": { "format": "currency", "prefix": "NT$" }
        },
        "w2": {
            "type": "chart",
            "title": "月度趨勢",
            "source": {
                "kind": "analysis",
                "listVmType": "MyApp.ViewModels.SaleRecordListVM, MyApp",
                "dimensions": [{ "field": "SaleDate", "hierarchy": "Month" }],
                "measures": [{ "field": "Amount", "func": "Sum" }]
            },
            "config": { "chartType": "line" }
        },
        "w3": {
            "type": "table",
            "title": "待處理訂單",
            "source": { "kind": "custom", "name": "pending-orders" },
            "config": {}
        }
    },
    "filters": [
        { "field": "region", "label": "地區", "type": "select", "options": ["北區","南區","中區"] }
    ],
    "links": [
        { "source": "w1", "target": "w2", "event": "click", "param": "region" }
    ]
}
```

### 9.3 六種 Widget 類型

| 類型 | 用途 | 資料格式 | 使用場景 |
|------|------|---------|---------|
| **kpi** | 單一數值 + 趨勢箭頭 | `Value`, `PreviousValue` | 營收、訂單數、轉換率 |
| **chart** | ECharts 圖表（bar/line/pie 自動偵測） | `Rows[]`, `Columns[]` | 趨勢分析、分佈圖 |
| **table** | 可排序表格 | `Rows[]`, `Columns[]` | 明細清單、排行榜 |
| **progress** | 進度條 + 百分比 | `Value` (0~1 或 0~100) | 目標達成率、專案進度 |
| **list** | 項目列表（含狀態標籤） | `Rows[{label, status, url}]` | 待辦事項、告警清單 |
| **embed** | iframe 嵌入 | `config.url` | 外部報表、Grafana |

### 9.4 自訂資料來源（Custom DataSource）

**介面定義：**
```csharp
public interface IWidgetDataSource
{
    string Name { get; }
    WidgetDataSourceKind Kind { get; }
    Task<WidgetDataResult> GetDataAsync(
        WidgetDataRequest request, CancellationToken ct = default);
}
```

**完整範例 — 銷售 KPI：**
```csharp
public class SalesKpiDataSource : IWidgetDataSource
{
    public string Name => "sales-kpi";
    public WidgetDataSourceKind Kind => WidgetDataSourceKind.Custom;

    private readonly DataContext _db;
    public SalesKpiDataSource(DataContext db) => _db = db;

    public async Task<WidgetDataResult> GetDataAsync(
        WidgetDataRequest request, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var thisMonth = await _db.Set<Order>()
            .Where(o => o.OrderDate >= new DateTime(now.Year, now.Month, 1))
            .SumAsync(o => o.Amount, ct);

        var lastMonth = await _db.Set<Order>()
            .Where(o => o.OrderDate >= new DateTime(now.Year, now.Month - 1, 1)
                     && o.OrderDate < new DateTime(now.Year, now.Month, 1))
            .SumAsync(o => o.Amount, ct);

        return new WidgetDataResult
        {
            Value = thisMonth,
            PreviousValue = lastMonth  // 前端自動計算趨勢 ↑↓
        };
    }
}
```

**完整範例 — 待處理訂單表格：**
```csharp
public class PendingOrdersDataSource : IWidgetDataSource
{
    public string Name => "pending-orders";
    public WidgetDataSourceKind Kind => WidgetDataSourceKind.Custom;

    private readonly DataContext _db;
    public PendingOrdersDataSource(DataContext db) => _db = db;

    public async Task<WidgetDataResult> GetDataAsync(
        WidgetDataRequest request, CancellationToken ct = default)
    {
        // 支援 Dashboard 級 filter
        var region = request.Parameters.GetValueOrDefault("region");

        var query = _db.Set<Order>().Where(o => o.Status == "Pending");
        if (!string.IsNullOrEmpty(region))
            query = query.Where(o => o.Region == region);

        var rows = await query
            .OrderByDescending(o => o.OrderDate)
            .Take(20)
            .Select(o => new Dictionary<string, object?>
            {
                ["訂單號"] = o.OrderNo,
                ["客戶"] = o.CustomerName,
                ["金額"] = o.Amount,
                ["日期"] = o.OrderDate.ToString("yyyy-MM-dd")
            })
            .ToListAsync(ct);

        return new WidgetDataResult
        {
            Columns = new List<string> { "訂單號", "客戶", "金額", "日期" },
            Rows = rows,
            Metadata = new Dictionary<string, object?> { ["totalCount"] = rows.Count }
        };
    }
}
```

**完整範例 — 進度條：**
```csharp
public class QuarterTargetDataSource : IWidgetDataSource
{
    public string Name => "quarter-target";
    public WidgetDataSourceKind Kind => WidgetDataSourceKind.Custom;

    private readonly DataContext _db;
    public QuarterTargetDataSource(DataContext db) => _db = db;

    public async Task<WidgetDataResult> GetDataAsync(
        WidgetDataRequest request, CancellationToken ct = default)
    {
        var actual = await _db.Set<Order>()
            .Where(o => o.OrderDate >= GetQuarterStart())
            .SumAsync(o => o.Amount, ct);

        var target = 1_000_000m;  // 季度目標

        return new WidgetDataResult
        {
            Value = actual / target,  // 0~1 的比率
            Metadata = new Dictionary<string, object?>
            {
                ["max"] = target,
                ["label"] = $"NT${actual:N0} / NT${target:N0}"
            }
        };
    }
}
```

### 9.4.1 REST 資料來源（10.4.0+，無需 C#）

**用途**：直接從外部 HTTP endpoint 取 JSON 資料，不需要寫 `IWidgetDataSource` 實作；admin 在 Dashboard JSON config 填 URL + headers + `jsonPath` 即可。

**Dashboard config：**

```json
{
  "id": "weather-taipei",
  "widget": "kpi",
  "dataSource": {
    "kind": "Rest",
    "options": {
      "url": "https://api.weather.gov/points/25.03,121.56",
      "method": "GET",
      "headers": { "Accept": "application/json" },
      "jsonPath": "$.properties.forecast",
      "cacheTtlSeconds": 300,
      "timeoutSeconds": 10,
      "maxResponseBytes": 1048576,
      "allowPrivateNetwork": false,
      "allowHttp": false
    }
  }
}
```

**`jsonPath` 支援**：僅簡單 dot-path（`$`、`$.data`、`$.result.items`）。不支援 wildcard、array filter；進階需求用 `Custom` DataSource。

**回應型別映射：**

| JSON 形狀 | `WidgetDataResult` | 適合 widget |
|-----------|-------------------|-------------|
| scalar (`42`, `"hi"`, `true`) | `Value` | `kpi`, `progress` |
| array of objects | `Rows[]` + `Columns[]` | `table`, `chart` |
| array of primitives | `Rows[{value}]` + `Columns=["value"]` | `chart` |
| single object | `Rows[ { ...obj } ]` + `Columns[]` | `kpi`, `list` |

**SSRF 防護（預設 on）：**

Admin-supplied URL 是 classic SSRF 攻擊面。預設禁以下 IP 範圍（hostname resolve 後檢查）：

| 範圍 | 例子 |
|------|------|
| RFC 1918 私網 | `10.*.*.*`, `172.16-31.*.*`, `192.168.*.*` |
| Loopback | `127.*.*.*`, `::1` |
| Link-local | `169.254.*.*`（含 AWS IMDS `169.254.169.254`） |
| Multicast | `224.0.0.0/4` |
| IPv6 ULA / link-local / multicast | `fc00::/7`, `fe80::/10`, `ff00::/8` |

opt-in 內部 endpoint：設 `allowPrivateNetwork: true`（搭配 `allowHttp: true` 若用 plain HTTP）。

**安全與性能上限：**

- Response body 上限 `maxResponseBytes`（預設 1 MiB）— 防 memory 爆炸
- 請求 timeout `timeoutSeconds`（預設 10s）
- 回應以 `cacheTtlSeconds`（預設 60s）快取於 `IMemoryCache`，key = `method + url + body + jsonPath`；設 0 停用快取
- URL 僅接受 `https://`（預設）；`http://` 需 `allowHttp: true` opt-in

**不支援的範圍**：無 OAuth2 flow、無 retry/backoff、無 streaming、無 outbound webhook 接收（這些請用 `Custom` DataSource）。

### 9.5 Analysis Mode 資料來源

Widget 可直接使用任何 `[EnableAnalysis]` 的 ListVM 作為資料來源，無需寫自訂 DataSource：

```json
{
    "type": "chart",
    "title": "部門薪資分佈",
    "source": {
        "kind": "analysis",
        "listVmType": "MyApp.ViewModels.EmployeeListVM, MyApp",
        "dimensions": [{ "field": "DepartmentName_view" }],
        "measures": [
            { "field": "Salary", "func": "Sum" },
            { "field": "Salary", "func": "Avg" }
        ],
        "filters": [
            { "field": "HireDate", "operator": ">=", "value": "2025-01-01" }
        ]
    }
}
```

安全性：所有欄位透過 `AnalysisVmRegistry` 白名單驗證，與 Analysis Mode API 相同。

### 9.6 API 端點

| 端點 | 方法 | 認證 | 用途 |
|------|------|------|------|
| `/_dashboard/list` | GET | 所有已認證使用者 | 列出可見的 Dashboard（依 owner/role/tenant 過濾） |
| `/_dashboard/{id}` | GET | 檢視權限 | 取得完整 Dashboard 定義（layout + widgets + filters） |
| `/_dashboard` | POST | 所有已認證使用者 | 建立新 Dashboard（server 端設定 owner + tenant） |
| `/_dashboard/{id}` | PUT | 編輯權限 | 更新 Dashboard（owner 不可改、tenant 鎖定） |
| `/_dashboard/{id}` | DELETE | 編輯權限 | 刪除 Dashboard（不可回復） |
| `/_dashboard/{id}/widget/{wid}/data` | GET/POST | 檢視權限 | 取得 Widget 資料（支援 query string 或 POST body 傳入 filter） |
| `/_dashboard/datasources` | GET | 所有已認證使用者 | 列出可用資料來源（custom + Analysis Mode ListVM） |

### 9.7 前端 JavaScript API

```html
<!-- 引入內嵌資源 -->
<script src="/_js/framework_dashboard.js"></script>
<link rel="stylesheet" href="/_js/framework_dashboard.css" />
```

**初始化 Dashboard：**
```javascript
// 載入並渲染 Dashboard
const dashboard = await WtmDashboard.DashboardManager.init('container', 'sales-overview');
// 自動開始定時刷新（依 refreshInterval 設定）

// 手動控制刷新
WtmDashboard.DashboardManager.stopRefresh();
WtmDashboard.DashboardManager.startRefresh(30);  // 30 秒
```

**編輯模式：**
```javascript
// 切換拖拽編輯模式（暫停刷新）
WtmDashboard.DashboardEditor.toggleEditMode();

// 新增 Widget
WtmDashboard.DashboardEditor.addWidget('w-new', {
    type: 'kpi',
    title: '新指標',
    source: { kind: 'custom', name: 'my-source' },
    config: { format: 'percent' }
}, { x: 0, y: 6, w: 4, h: 2 });

// 儲存（PUT /_dashboard/{id}）
await WtmDashboard.DashboardEditor.saveDashboard();
```

**Dashboard Filter 聯動：**
```javascript
// FilterBar 控制 → 所有 Widget 重新載入（帶 filter 參數）
WtmDashboard.FilterBar.onChange(filters => {
    // filters = { region: "北區", dateRange: "2026-Q1" }
    // 自動傳入每個 widget 的 data 請求
});
```

**Widget 間聯動（EventBus）：**
```javascript
// Widget w1 (KPI) 點擊時通知 w2 (Chart) 過濾
WtmDashboard.EventBus.on('click', 'w2', data => {
    // data = { region: "北區" }
    // w2 重新載入，帶入 region 參數
});
```

### 9.8 存取控制

| 身分 | 列表 | 檢視 | 建立 | 編輯 | 刪除 |
|------|:----:|:----:|:----:|:----:|:----:|
| **Owner** | 自己的 | ✅ | ✅ | ✅ | ✅ |
| **Admin** | 全部 | ✅ | ✅ | ✅ | ✅ |
| **角色共享** | 共享的 | ✅ | — | — | — |
| **公開** | 公開的 | ✅ | — | — | — |

**共享設定：**
```json
// 私有（僅 owner + admin）
{ "mode": "private" }

// 公開（所有已認證使用者）
{ "mode": "public" }

// 角色共享（指定角色 + owner + admin）
{ "mode": "private", "roles": ["manager", "analyst"] }
```

### 9.9 使用場景範例

#### 場景 1：主管營運看板

**需求：** 主管需要即時看到營收 KPI、月度趨勢、待處理訂單。

```csharp
// 1. 建立 3 個 DataSource
services.AddWidgetDataSource<SalesKpiDataSource>();       // KPI: 本月 vs 上月
services.AddWidgetDataSource<PendingOrdersDataSource>();  // Table: 待處理訂單
// Chart: 直接用 Analysis Mode，不需自訂 DataSource
```

Dashboard JSON:
```json
{
    "title": "營運看板",
    "sharing": { "mode": "private", "roles": ["manager"] },
    "refreshInterval": 30,
    "layout": [
        { "id": "revenue",  "x": 0, "y": 0, "w": 3, "h": 2 },
        { "id": "orders",   "x": 3, "y": 0, "w": 3, "h": 2 },
        { "id": "target",   "x": 6, "y": 0, "w": 3, "h": 2 },
        { "id": "trend",    "x": 0, "y": 2, "w": 6, "h": 4 },
        { "id": "pending",  "x": 6, "y": 2, "w": 6, "h": 4 }
    ],
    "widgets": {
        "revenue": { "type": "kpi", "title": "本月營收", "source": { "kind": "custom", "name": "sales-kpi" }, "config": { "format": "currency", "prefix": "NT$" } },
        "orders":  { "type": "kpi", "title": "訂單數", "source": { "kind": "custom", "name": "order-count-kpi" }, "config": {} },
        "target":  { "type": "progress", "title": "季度目標", "source": { "kind": "custom", "name": "quarter-target" }, "config": { "color": "#1890ff" } },
        "trend":   { "type": "chart", "title": "月度營收趨勢", "source": { "kind": "analysis", "listVmType": "MyApp.SaleRecordListVM, MyApp", "dimensions": [{"field":"SaleDate","hierarchy":"Month"}], "measures": [{"field":"Amount","func":"Sum"}] }, "config": {"chartType":"line"} },
        "pending": { "type": "table", "title": "待處理訂單 TOP 20", "source": { "kind": "custom", "name": "pending-orders" }, "config": {} }
    },
    "filters": [
        { "field": "region", "label": "地區", "type": "select", "options": ["全部","北區","南區","中區"] }
    ]
}
```

#### 場景 2：IT 監控面板

```json
{
    "title": "系統監控",
    "sharing": { "mode": "private", "roles": ["admin", "devops"] },
    "refreshInterval": 10,
    "widgets": {
        "cpu":     { "type": "progress", "title": "CPU 使用率", "source": { "kind": "custom", "name": "system-cpu" }, "config": { "color": "#52c41a" } },
        "memory":  { "type": "progress", "title": "記憶體", "source": { "kind": "custom", "name": "system-memory" }, "config": { "color": "#faad14" } },
        "alerts":  { "type": "list", "title": "近期告警", "source": { "kind": "custom", "name": "recent-alerts" }, "config": {} },
        "grafana": { "type": "embed", "title": "Grafana", "source": { "kind": "custom", "name": "empty" }, "config": { "url": "/grafana/d/api-latency" } }
    }
}
```

#### 場景 3：多租戶 SaaS — 每個租戶有自己的看板

```csharp
// Dashboard 自動隔離 — 建立時自動帶入 TenantId
// POST /_dashboard → server 端從 LoginUserInfo.TenantCode 注入 tenantId
// 租戶 A 看不到租戶 B 的 Dashboard，即使知道 ID 也返回 403
```

### 9.10 配置選項

```csharp
public class DashboardOptions
{
    public string DashboardDirectory { get; set; } = "App_Data/dashboards";
    public bool EnableEditing { get; set; } = true;
    public int DefaultRefreshInterval { get; set; } = 60;
    public bool AllowIframeSameOrigin { get; set; } = false;  // Embed 安全
}
```

### 9.11 儲存結構

Dashboard 以 JSON 檔案儲存（非資料庫）：
```
App_Data/dashboards/
├── _default/              ← 無租戶
│   ├── sales-overview.json
│   └── system-monitor.json
├── tenant-a/              ← 租戶 A
│   └── custom-board.json
└── tenant-b/              ← 租戶 B
    └── operations.json
```

### 9.12 安全注意事項

- **Embed Widget**：iframe 預設 `sandbox="allow-scripts"`（無 `allow-same-origin`），`javascript:`, `data:`, `vbscript:` URL 被封鎖
- **Analysis 資料來源**：所有欄位經 `AnalysisVmRegistry` 白名單驗證，無 SQL 注入風險
- **多租戶**：Server 端強制 tenant 隔離，非同租戶的 GET/PUT/DELETE 返回 403
- **編輯權限**：僅 owner 和 admin 可修改/刪除

---

## 10. 安全機制

### 10.1 密碼雜湊

**演算法：** ASP.NET Core Identity 的 PBKDF2 實作（SHA-256, 100,000 iterations, 128-bit salt）

```csharp
// 雜湊密碼（用於註冊、重設密碼）
string hash = PasswordHashHelper.HashPassword("MyP@ssw0rd");
// 輸出格式：Base64 編碼的 Identity V3 格式（包含 salt + iterations + hash）

// 驗證密碼（用於登入）
var result = PasswordHashHelper.VerifyPassword(storedHash, "MyP@ssw0rd");
// result: Success | Failed | SuccessRehashNeeded
```

**三種驗證結果：**

| 結果 | 說明 | 後續動作 |
|------|------|---------|
| `Success` | 密碼正確，hash 格式最新 | 直接放行 |
| `SuccessRehashNeeded` | 密碼正確，但 hash 需要升級 | 放行 + 背景重新 hash |
| `Failed` | 密碼錯誤 | 拒絕登入 |

**Legacy MD5 自動遷移流程：**
```
使用者登入 → IsLegacyMD5Hash(storedHash)?
  → 是：用 MD5 驗證 → 正確 → 自動 rehash 為 PBKDF2 → 更新 DB → 登入成功
  → 否：用 PBKDF2 驗證 → 正常流程
```

`IsLegacyMD5Hash()` 判斷方式：hash 為 32 字元十六進位字串（MD5 格式）。

### 10.2 JWT 認證流程

**完整流程圖：**
```
Client                          Server
  │                                │
  │  POST /api/_account/login      │
  │  { ITCode, Password }         │
  │──────────────────────────────→│ 驗證密碼
  │                                │ 產生 AccessToken (15 min)
  │  { access_token, refresh_token │ 產生 RefreshToken (7 days)
  │    expires_in: 900 }           │ 存入 RefreshTokenEntity 表
  │←──────────────────────────────│
  │                                │
  │  GET /api/data                 │
  │  Authorization: Bearer {AT}    │
  │──────────────────────────────→│ 正常存取
  │←──────────────────────────────│
  │                                │
  │  ... 15 分鐘後 AT 過期 ...      │
  │                                │
  │  POST /api/_account/refreshtoken│
  │  { refreshToken: "old_RT" }    │
  │──────────────────────────────→│ 驗證 old_RT 有效
  │                                │ 標記 old_RT 為 Revoked
  │  { access_token: "new_AT",     │ 產生 new_RT（Token 旋轉）
  │    refresh_token: "new_RT" }   │ old_RT.ReplacedByToken = new_RT
  │←──────────────────────────────│
  │                                │
  │  POST /api/_account/revoketoken│
  │  { refreshToken: "new_RT" }    │
  │──────────────────────────────→│ 登出（撤銷所有 token chain）
  │←──────────────────────────────│
```

**Access Token Claims：**

| Claim | 說明 |
|-------|------|
| `jti` | 唯一 Token ID（`Guid.NewGuid().ToString("N")`） |
| `sub` | 使用者帳號（ITCode） |
| `TenantCode` | 租戶代碼（多租戶場景） |
| `exp` | 過期時間（預設 900 秒 = 15 分鐘） |

**Refresh Token 安全機制：**
- 64 bytes 隨機數（Base64 編碼）
- 存入 `RefreshTokenEntity` 資料表（含 `CreatedByIp`、`RevokedByIp`）
- **Token 旋轉**：每次 refresh 都產生全新的 token pair，舊 token 立即作廢
- **鏈式撤銷**：若被撤銷的 token 有 `ReplacedByToken`，整條 chain 全部撤銷（防止 token 被盜用後的重播攻擊）

**在前端使用：**
```javascript
// 登入
const res = await fetch('/api/_account/login', {
    method: 'POST',
    body: JSON.stringify({ ITCode: 'admin', Password: 'xxx' })
});
const { access_token, refresh_token } = await res.json();

// 帶 Token 請求
fetch('/api/employee', {
    headers: { 'Authorization': `Bearer ${access_token}` }
});

// Token 過期後刷新
const refreshRes = await fetch('/api/_account/refreshtoken', {
    method: 'POST',
    body: JSON.stringify({ refreshToken: refresh_token })
});
const newTokens = await refreshRes.json();
```

### 10.3 權限模型

WTM 採用 **RBAC（角色存取控制）** + **列級資料權限** 雙層模型：

```
FrameworkUser
  ├── FrameworkUserRole ──→ FrameworkRole
  │                           ├── FunctionPrivilege ──→ FrameworkMenu (URL)
  │                           └── DataPrivilege (列級過濾)
  └── FrameworkUserGroup ──→ FrameworkGroup
                              └── DataPrivilege (列級過濾)
```

#### 功能權限（FunctionPrivilege）

控制「使用者能存取哪些頁面/功能」：

```
角色「業務經理」 → 可存取 /Employee/Index, /Employee/Create, /Employee/Edit
角色「業務員」   → 可存取 /Employee/Index（只能看，不能改）
```

**PrivilegeFilter 運作流程（每個 HTTP 請求）：**
```
收到請求 → 取得請求 URL
  → 是否標記 [Public]? → 是 → 放行
  → 是否標記 [AllRights]? → 是 → 已登入就放行
  → Wtm.IsAccessable(url) 檢查 → 該角色有此 URL 的 FunctionPrivilege? → 放行/拒絕
```

#### 資料權限（DataPrivilege）

控制「使用者能看到哪些資料列」，在查詢層面自動過濾：

**場景：業務員只能看自己負責區域的客戶**

```csharp
// 1. 在 Admin 後台設定 DataPrivilege：
//    使用者「張三」→ 表「Customer」→ 欄位「Region」→ 值「北區」
//    意思：張三只能看到 Region = "北區" 的客戶

// 2. 在 ListVM 的 GetSearchQuery 中使用 DPWhere
protected override IOrderedQueryable<Customer> GetSearchQuery()
{
    return DC!.Set<Customer>()
        .DPWhere(Wtm!, x => x.Region)   // 自動根據 DataPrivilege 設定過濾
        .OrderBy(x => x.Name);
}
// 張三查詢時，SQL 自動加上 WHERE Region = '北區'
// 管理員則沒有限制（看到全部資料）
```

**DPWhere 內部機制：**
1. 從 `Wtm.LoginUserInfo` 取得當前使用者
2. 查詢 `DataPrivilege` 表：此使用者（或其所屬群組）對此 Table + Column 有哪些限制
3. 動態建構 Expression Tree 加入 WHERE 條件
4. 無 DataPrivilege 記錄 = 無限制（看全部）

### 10.4 Analysis Mode 安全

Analysis Mode 有多層安全防護：

| 層級 | 機制 | 說明 |
|------|------|------|
| VM 白名單 | `AnalysisVmRegistry` | 只有標記 `[EnableAnalysis]` 的 ListVM 才能被查詢 |
| 欄位白名單 | `AnalysisFieldScanner` | 只有標記 `[Dimension]`/`[Measure]` 的欄位才能作為維度/度量 |
| VM 層角色控制 | `[EnableAnalysis(AllowedRoles = "...")]` | 限制可存取此 VM 的角色（值為 **RoleCode**，逗號分隔） |
| 欄位層角色控制 | `[Dimension/Measure(AllowedRoles = "...")]` | 限制可見特定欄位的角色（值同為 **RoleCode**） |
| 欄位存取策略 | `IAnalysisFieldPolicy` | 框架呼叫 `Filter(fields, ClaimsPrincipal)` 過濾可用欄位；`ClaimsPrincipal` 以 `RoleCode` 建構 role claims |
| 資料權限 | `DPWhere` | `GetSearchQuery()` 中的資料權限在 Analysis 中同樣生效 |
| Expression 防注入 | 白名單比對 | 欄位名稱必須完全匹配白名單，無法注入任意表達式 |
| 結果截斷 | `Take(50_000)` + 10,000 行 | 防止大量資料洩露 |
| CSV 防公式注入 | 前置 tab | `=+@-\t\r` 開頭的儲存格加 tab |

**角色識別符規則**：`AllowedRoles` 填入的值必須是 `RoleCode`（資料庫 `FrameworkRole.RoleCode` 欄位），**不是** `RoleName`。`RoleCode` 是穩定的機器識別符；`RoleName` 可能在 UI 中被修改。

```csharp
// ✅ 正確：使用 RoleCode
[EnableAnalysis(AllowedRoles = "analyst,finance_mgr")]
public class SalesListVM : BasePagedListVM<Sales, SalesSearcher>

[Measure(AllowedRoles = "finance_mgr")]
public decimal Salary { get; set; }

// ❌ 錯誤：不要使用 RoleName（顯示名稱）
[EnableAnalysis(AllowedRoles = "財務主管")]  // RoleName 可變，不應依賴
```

`RoleCode = "Admin"` 的使用者永遠繞過欄位層限制（完整存取）。

### 10.5 跨模組 RBAC 統一規範

WTM 三個進階模組（Analysis、Dashboard、ETL）使用相同的角色識別符（`RoleCode`），但各自有不同的設定層次：

| 模組 | RBAC 設定位置 | 角色識別符 | Admin 快速路徑 |
|------|--------------|-----------|--------------|
| **Analysis** | `[EnableAnalysis(AllowedRoles)]`（VM 層） + `[Dimension/Measure(AllowedRoles)]`（欄位層） | `RoleCode` | `RoleCode = "Admin"` 繞過所有限制 |
| **Dashboard** | `DashboardDefinition.Sharing.Roles`（分享設定，陣列） | `RoleCode` | `DashboardOptions.AdminRoles`（預設 `["Admin"]`）可存取全部 Dashboard |
| **ETL** | WTM 標準 `[ActionDescription]` + Admin 後台功能權限授予 | `FunctionPrivilege`（URL 為鍵） | 同一般 Controller 機制 |

#### Analysis RBAC 設定範例

```csharp
// VM 層：限制哪些角色可以查詢這個 VM
[EnableAnalysis(AllowedRoles = "analyst,bi_viewer")]
[ActionDescription("銷售分析")]
public class SalesListVM : BasePagedListVM<Sales, SalesSearcher>
{
    // 欄位層：限制哪些角色可見此欄位
    [Measure(AllowedFuncs = AggregateFunc.Sum, AllowedRoles = "finance_mgr")]
    public decimal GrossProfit { get; set; }

    [Dimension]            // 無 AllowedRoles = 所有已授權使用者都能看到
    public string Region { get; set; }
}
```

#### Dashboard RBAC 設定範例

```json
{
  "id": "sales-overview",
  "title": "Sales Overview",
  "owner": "alice",
  "sharing": {
    "mode": "roles",
    "roles": ["analyst", "sales_mgr"]
  }
}
```

- `mode: "private"` — 僅 owner 可見
- `mode: "public"` — 所有已登入使用者可見
- `mode: "roles"` — `roles` 陣列中列出的 `RoleCode` 可見（加上 owner 和 Admin）

`roles` 陣列的值必須是 `RoleCode`，與 Analysis 的 `AllowedRoles` 規則一致。

#### ETL RBAC

ETL Controller 使用標準 WTM `[AllRights]`（已登入即可）和 `[ActionDescription]`（每個 action 一條 URL 功能權限）。透過 Admin 後台的「角色管理 → 功能授權」頁面為角色授予或撤銷各個 ETL 操作的存取權限，不需要修改程式碼。

```
_EtlController.Index  →  URL: /_etl/index  →  在 Admin 後台為角色 "etl_operator" 授權
_EtlController.Trigger →  URL: /_etl/trigger/{id}
_EtlController.Pause   →  URL: /_etl/pause/{id}
```

#### 不得混用 RoleCode 與 RoleName

所有三個模組的角色設定都必須使用 `RoleCode`，禁止使用 `RoleName`：

| ❌ 錯誤 | ✅ 正確 |
|--------|--------|
| `AllowedRoles = "財務主管"` | `AllowedRoles = "finance_mgr"` |
| `roles: ["業務分析師"]` | `roles: ["analyst"]` |

`RoleName` 是 UI 顯示名稱，由管理員可以在後台修改；`RoleCode` 是不可變的機器識別符，是安全邊界的正確依據。

### 10.6 審計日誌（ActionLog）

所有 Controller Action（除標記 `[NoLog]` 者）自動寫入 `ActionLog` 表：

| 欄位 | 內容 |
|------|------|
| `ITCode` | 操作者帳號 |
| `ActionUrl` | 請求 URL |
| `ActionName` | `[ActionDescription]` 的值 |
| `ActionTime` | 操作時間 |
| `IP` | 客戶端 IP |
| `Duration` | 執行耗時（毫秒） |
| `Remark` | 額外資訊（如錯誤訊息） |
| `LogType` | Normal / Exception / Debug |

**在 `FrameworkFilter.OnResultExecuted` 中寫入**，所以即使 Action 拋出例外也會被記錄。

#### 10.6.1 ActionLog 保留政策（opt-in 背景服務，10.4.0+，#832）

`ActionLog` 會隨流量線性成長（典型每日 1K–10K 筆），半年就會累積百萬筆，造成：

- 後台 ActionLog 列表查詢分頁變慢
- 資料庫儲存 / 備份成本增加
- 金融、醫療等合規場景要求「保留期限」—原生 WTM 無此機制
- 單次 `DELETE FROM ActionLogs WHERE ActionTime < ...` 是地雷—大交易鎖表

`AddWtmActionLogRetention(configure)` 是 opt-in 的 `IHostedService`，每日排定時間跑一次批次刪除：

```csharp
// Program.cs
services.AddWtmActionLogRetention(opt =>
{
    opt.Enabled = true;          // master switch
    opt.RunAtLocalHour = 3;       // 03:00 local
    opt.NormalDays = 90;
    opt.ExceptionDays = 365;      // 例外留久一點，方便事後 debug
    opt.DebugDays = 30;
    opt.JobDays = 90;
    opt.BatchSize = 5000;         // 每次 DELETE 最多 5000 列，避免大交易鎖表
});
```

**關鍵設計決策：**

- **per-`LogType` TTL**：Normal/Exception/Debug/Job 各自獨立天數；設 `0` 或負數等於保留無限（該類別不刪）。
- **批次迴圈**：使用 EF Core 7+ `ExecuteDeleteAsync`（`DELETE ... WHERE ActionTime < @cutoff AND LogType = @t`），搭配 `OrderBy(ActionTime).Take(BatchSize)` 分批刪；每輪迴圈直到該類別當日配額耗盡才收工，避免一次大交易導致鎖表。
- **容錯**：任何單輪失敗都只記 Warning（不 crash hosting），下一個排程還會重試。
- **結構化日誌**：每輪結束寫一筆 `ActionLogRetention: sweep complete. Normal=... Exception=... total=...`。
- **opt-out**：`opt.Enabled = false` 則服務閒置（不刪任何東西），方便某些金融客戶以外部 archival pipeline 管理保留。

**手動觸發**：測試或 admin API 可直接呼叫 `svc.RunRetentionOnceAsync(options, ct)`，不必等到排程時間。

### 10.7 Content Security Policy（opt-in 中介軟體，10.3.0+）

`UseWtmContentSecurityPolicy()` 為可選 CSP 中介軟體，在 response 掛 `Content-Security-Policy` 標頭，封鎖 `'unsafe-eval'`（issue #789 六階段 `framework_layui.js` eval 清除的收益落地）。

**預設策略（不含 `'unsafe-eval'`）：**

```
default-src 'self';
script-src  'self' 'unsafe-inline';
style-src   'self' 'unsafe-inline' https://fonts.googleapis.com;
img-src     'self' data: https:;
font-src    'self' data: https://fonts.gstatic.com;
connect-src 'self';
frame-src   'self';
object-src  'none';
base-uri    'self';
form-action 'self';
```

**啟用：**

```csharp
// Startup.Configure
app.UseWtmContentSecurityPolicy();                         // 預設策略

app.UseWtmContentSecurityPolicy(o => o.ReportOnly = true); // 監控模式（不封鎖、只報告）

app.UseWtmContentSecurityPolicy(o =>
{
    o.ScriptSrc = "'self'";               // 嚴格：禁 inline scripts（需配合 TagHelper 重構）
    o.ReportUri = "/csp-report";
});
```

**關鍵取捨：**

| 指令 | 預設 | 原因 |
|------|------|------|
| `'unsafe-eval'` | **omitted** | #789 六階段清除 eval，框架自身無需此例外 |
| `'unsafe-inline'` | **kept** | LayUI TagHelper 大量輸出 inline `<script>` 與 `style=""`；移除需重構所有 TagHelper（#807 epic 追蹤） |
| `img-src data: https:` | 寬鬆 | 支援上傳圖片 base64 + 外部 CDN |

**行為：**
- **Opt-in**：app 未呼叫 → 不加 header（零 breaking change）
- **First-writer-wins**：若上游中介軟體或反向代理已設過 `Content-Security-Policy`，本中介軟體不覆蓋
- **ReportOnly 模式**：emit `Content-Security-Policy-Report-Only` 而非 enforcement

**遷移須知：** 啟用前先搜尋 app 自有 JS 的 `" + "eval(" + "` 呼叫，全部改用 JSON 或 `Function` 等 CSP 相容寫法。

### 10.8 Cookie SecurePolicy（10.3.0+）

`CookieOption.SecurePolicy` 控制 session 與認證 cookie 的 `Secure` flag 設定策略，影響 `AddWtmSession` 與 `AddWtmAuthentication` 兩條路徑：

| 值 | 行為 | 場景 |
|---|------|------|
| `SameAsRequest`（預設） | 請求為 HTTPS 時設 `Secure`，HTTP 則不設 | 本地 HTTP dev、direct-HTTPS production |
| `Always` | **永遠**設 `Secure` flag | Production 建議，特別是 TLS-terminating reverse proxy（nginx、Azure App Service、IIS ARR、Cloudflare）後面 |
| `None` | 永不設 `Secure` flag | 僅用於 HTTP-only 特殊場景（非常不建議） |

**反向代理場景的陷阱：**

```
Client ──HTTPS──▶ nginx ──HTTP──▶ WTM app
                                    │
                                    └── 看到 HTTP → SameAsRequest 不加 Secure flag
                                        → cookie 可被瀏覽器送往任意 HTTP endpoint
                                        → session hijack 風險
```

**Production 設定：**

```json
// appsettings.Production.json
{
  "CookieOptions": {
    "SecurePolicy": "Always",
    "LoginPath": "/Login/Login",
    "Expires": 3600
  }
}
```

**預設保持 `SameAsRequest`**：upgrade WTM 不會突然中斷本地 HTTP dev 或現有 direct-HTTPS 部署。只有明確 opt-in `Always` 才獲得強化行為。

### 10.9 Per-endpoint Rate Limit `[WtmRateLimit]`（10.4.0+）

`[WtmRateLimit(permits, windowSeconds)]` 為單一 controller action 或整個 controller 加上**較嚴格的 per-IP quota**，疊加在 `AddWtmRateLimiting` 裝的全域 limiter 之上。專為 brute-force-sensitive 端點設計（登入 / 密碼重設 / 忘記密碼）。

```csharp
[HttpPost("/Login/Login")]
[WtmRateLimit(5, 60)]    // 每 IP 每 60 秒最多 5 次
public IActionResult Login(LoginDTO vm) { ... }

[HttpPost("/Account/ResetPassword")]
[WtmRateLimit(3, 300)]   // 每 IP 每 5 分鐘最多 3 次
public IActionResult ResetPassword(ResetPasswordDTO vm) { ... }

// 也可貼在 Controller 類別上（action 層覆寫優先）
[WtmRateLimit(10, 60)]
public class SensitiveController : ControllerBase { ... }
```

**設計要點**：
- **Per-IP per-endpoint 獨立 bucket** — 攻擊者在 `/Login` 耗光 quota 不影響 `/ResetPassword`
- **Policy dedup**：多個 actions 共用相同 `(permits, window, queue)` → 共用單一 registered policy（避免 policy 爆炸）
- **疊加全域 limiter**：per-endpoint 不會取消全域；兩者皆檢查，先命中的拒絕
- **預設 queue=0**：超過限額立即 429（brute-force 場景中 queue 不幫攻擊者）
- **啟動時 assembly 掃描**：`AddWtmRateLimiting` 掃描所有已載入 assembly，找出所有 `[WtmRateLimit]` 的 `(permits, window, queue)` unique 組合，動態 `AddPolicy` 註冊
- **ASP.NET Core 集成**：`IActionModelConvention` 於 model-build 階段把 `WtmRateLimitAttribute` 映射成 `EnableRateLimitingAttribute` 放進 endpoint metadata；runtime 由內建 rate-limit middleware 接手
- **未啟用則無效**：必須同時 `services.AddWtmRateLimiting()` + `app.UseWtmRateLimiting()` 才生效

**拒絕回應**：HTTP 429 Too Many Requests，Serilog 會記錄一筆 `Rate limit exceeded` warning（`ClientIp` + `Path` + `Method`）— 跟全域 limiter 的 `OnRejected` 同一 logger。

**驗證範例**：

```csharp
// 在測試用 TestServer 裡
var r1 = await client.GetAsync("/Login");  // 200
var r2 = await client.GetAsync("/Login");  // 200 (第 2 次)
var r3 = await client.GetAsync("/Login");  // 200 (第 3 次)
var r4 = await client.GetAsync("/Login");  // 200 (第 4 次)
var r5 = await client.GetAsync("/Login");  // 200 (第 5 次)
var r6 = await client.GetAsync("/Login");  // 429 ⛔ quota 用罄
```

**限制**：
- 只支援 fixed-window（非 sliding / token-bucket）— 簡單、低延遲、足夠擋 brute-force
- Per-IP 分區；未支援 per-user / per-tenant 分區（後者可透過 `WtmRateLimitingOptions.CustomConfig` 自行加 policy）
- Controller 類別與 action 同時貼 `[WtmRateLimit]` 時 **action 層優先**

### 10.10 X-Correlation-Id middleware（opt-in，10.4.0+）

`UseWtmCorrelationId()` 中介軟體讓跨服務 trace ID 可從 upstream → WTM → log 連貫穿透，解決 SRE/on-call 查問題要手動比對 timestamp+IP 的痛點。

```csharp
// Startup.Configure
app.UseWtmCorrelationId();                                     // 預設 X-Correlation-Id
app.UseWtmCorrelationId(o => o.HeaderName = "X-Request-Id");   // Heroku/Rails 風格
app.UseWtmCorrelationId(o => o.AdoptInbound = false);          // 不信任 upstream，永遠自產
```

**行為**：
1. **Inbound**：讀請求 header（default `X-Correlation-Id`），通過 sanitization 則採用為此請求的 trace ID
2. **Fallback**：缺少 / 不合格 → 自產 `Guid.NewGuid("N")` 32-hex UUID
3. **Propagate**：覆寫 `HttpContext.TraceIdentifier` → Serilog scope / `Activity.Current.Id` / `WtmProblemDetails.traceId` **自動** 使用同一 ID
4. **Outbound**：回寫 response header（可 opt-out）

**Sanitization 規則（防 log injection）**：
- 空值 / 超過 `MaxLength`（default 128） → 拒絕
- 只允許 `[A-Za-z0-9\-_.]` 字符 — 涵蓋 UUID / W3C Trace Context / slug / 點分命名；拒絕 CR/LF、分號、逗號、空格、control char、非 ASCII
- 不合格 → 轉為 fallback auto-gen（不是錯誤，是 silent replace）

**Options**：

| 欄位 | 預設 | 說明 |
|------|------|------|
| `HeaderName` | `"X-Correlation-Id"` | Request 讀 / Response 寫 的 header 名 |
| `MaxLength` | `128` | 允許 inbound ID 最大長度 |
| `AdoptInbound` | `true` | 是否採用 upstream 傳進的 ID；zero-trust 環境可設 `false` |
| `EmitOutbound` | `true` | 是否在 response 回寫 header；純內部服務可設 `false` |

**驗證範例**：

```
→ GET /api/orders/42
  X-Correlation-Id: caller-abc-123

← 200 OK
  X-Correlation-Id: caller-abc-123            ← 原樣回寫
  ProblemDetails traceId = "caller-abc-123"   ← 錯誤響應自動用新 ID
  Serilog log: TraceIdentifier = "caller-abc-123"  ← scope 自動關聯
```

**與 OpenTelemetry 整合**：WTM 的 `AddWtmOpenTelemetry()` 已設 `Activity.Current` 串接；本 middleware 覆寫 `TraceIdentifier` 後，OpenTelemetry propagator（W3C Trace Context）可自行銜接，或 app 用標準 `propagation.extract` / `inject` pattern。

**不做**：
- 不自動整合 W3C `traceparent` / `tracestate` 解析 — 用標準 `System.Diagnostics.Activity` propagator 即可
- 不支援多 header fallback chain（一個主 header 即可）
- 不加 HMAC / 簽章（correlation ID 非安全 token）

---

## 11. 多租戶

WTM 的多租戶透過 EF Core Global Query Filter 在資料庫查詢層面自動隔離，開發者幾乎不需要額外寫程式碼。

### 11.1 啟用方式

**步驟 1：Model 實作 `ITenant`**
```csharp
public class Employee : BasePoco, ITenant
{
    public string? TenantCode { get; set; }

    [Display(Name = "姓名")]
    public string Name { get; set; } = "";
    // ...
}
```

**步驟 2：確認 DataContext 繼承 FrameworkContext**

`FrameworkContext.OnModelCreating` 會自動掃描所有實作 `ITenant` 的 Model，加入 Global Query Filter。不需要手動設定。

**步驟 3：設定租戶資料**

在 Admin 後台的「租戶管理」建立租戶：
```
FrameworkTenant:
  - TCode: "ACME",   TName: "ACME 公司"
  - TCode: "STARK",  TName: "Stark 工業"
```

使用者在 `FrameworkUser.TenantCode` 欄位設定所屬租戶。

### 11.2 運作機制（Global Query Filter）

`FrameworkContext.OnModelCreating` 中的核心邏輯：

```csharp
// 虛擬碼 — 實際實作透過 Expression Tree 動態建構
foreach (var entityType in allModels)
{
    if (typeof(ITenant).IsAssignableFrom(entityType))
    {
        // 自動加入：WHERE TenantCode = DataContext.TenantCode
        builder.Entity(entityType)
            .HasQueryFilter(e => e.TenantCode == this.TenantCode);
    }

    if (typeof(IPersistPoco).IsAssignableFrom(entityType))
    {
        // 自動加入：WHERE IsValid = true（軟刪除過濾）
        builder.Entity(entityType)
            .HasQueryFilter(e => e.IsValid == true);
    }
}
```

**效果：** 所有 LINQ 查詢自動帶上租戶過濾，開發者寫 `DC.Set<Employee>().ToList()` 就只會拿到當前租戶的資料。

### 11.3 使用場景

#### 場景 1：SaaS 多租戶應用

```csharp
// 情境：兩家公司共用同一套系統
// ACME 的使用者登入後，TenantCode = "ACME"
// 以下查詢自動只返回 ACME 的員工：
var employees = DC.Set<Employee>().ToList();
// SQL: SELECT * FROM Employee WHERE TenantCode = 'ACME' AND IsValid = 1

// STARK 的使用者登入後，TenantCode = "STARK"
// 同樣的程式碼，自動只返回 STARK 的員工
```

#### 場景 2：跨租戶查詢（管理後台）

```csharp
// 系統管理員需要看到所有租戶的資料（例如：統計報表）
// 必須明確使用 IgnoreQueryFilters()
var allEmployees = DC.Set<Employee>()
    .IgnoreQueryFilters()  // 移除 TenantCode + IsValid 過濾
    .Where(x => x.IsValid)  // 手動加回軟刪除過濾（需要的話）
    .ToList();

// ⚠️ 注意：IgnoreQueryFilters 會同時移除所有 Global Query Filter
//          包括 ITenant 和 IPersistPoco 的，需要手動補回需要的條件
```

#### 場景 3：租戶切換（超級管理員）

```
POST /_Framework/SetTenant?tenant=ACME
→ 更新 Session + ClaimsPrincipal 的 TenantCode
→ 後續所有查詢自動切換到 ACME 的資料
```

### 11.4 多租戶注意事項

| 事項 | 說明 |
|------|------|
| 新增資料 | `TenantCode` 由 `FrameworkFilter` 在 `DoAdd()` 前自動設定，不需手動填 |
| 唯一索引 | 考慮是否需要加上 `TenantCode`（例如：員工工號在每個租戶內唯一） |
| 資料遷移 | 遷移既有單租戶系統時，需要先為現有資料填入 TenantCode |
| Dashboard | 自動隔離 — 每個租戶只看到自己的 Dashboard |
| Analysis | `GetSearchQuery()` 的租戶過濾自動生效 |
| Lookup Cache | 快取 key 包含 tenantId，每個租戶獨立快取 |

---

## 12. Lookup Cache

Lookup Cache 專為「少量、低頻變動、頻繁讀取」的資料設計（如部門、職稱、狀態碼），一行 Attribute 就能啟用。

### 12.1 啟用方式

```csharp
// 加上 [CacheLookup] 即可
[CacheLookup(WarmOnStartup = true)]  // WarmOnStartup: 應用啟動時預載
public class Department : BasePoco
{
    [Display(Name = "部門名稱")]
    public string Name { get; set; } = "";

    [Display(Name = "部門代碼")]
    public string Code { get; set; } = "";
}
```

### 12.2 使用方式

```csharp
// 方式 1：透過 DI 注入
public class MyService
{
    private readonly ILookupCacheService _cache;
    public MyService(ILookupCacheService cache) => _cache = cache;

    public string GetDepartmentName(Guid id)
    {
        var dept = _cache.Get<Department>(id);
        return dept?.Name ?? "未知部門";
    }

    public IReadOnlyList<Department> GetAllDepartments()
    {
        return _cache.GetAll<Department>();
    }
}

// 方式 2：在 ViewModel 中使用（透過 Wtm.ServiceProvider）
protected override void InitVM()
{
    var cache = Wtm!.ServiceProvider.GetRequiredService<ILookupCacheService>();
    var departments = cache.GetAll<Department>();
    AllDepartments = departments
        .Select(d => new ComboSelectListItem { Value = d.ID.ToString(), Text = d.Name })
        .ToList();
}

// 方式 3：Async 版本（推薦用於 API Controller）
public async Task<IActionResult> GetDepartments()
{
    var cache = HttpContext.RequestServices.GetRequiredService<ILookupCacheService>();
    var departments = await cache.GetAllAsync<Department>(DC as DbContext);
    return Ok(departments);
}
```

### 12.3 手動失效

```csharp
// 單一類型失效
cache.Invalidate<Department>();

// 指定租戶失效
cache.Invalidate<Department>(tenantId: "ACME");

// 類型級失效（所有租戶）
cache.InvalidateType(typeof(Department));
```

### 12.4 Stampede Protection（防快取雪崩）

當快取過期時，如果 100 個請求同時到達，不做保護的話會有 100 次 DB 查詢。WTM 的 Lookup Cache 使用 **Per-Key SemaphoreSlim + Double-Check Locking**：

```
100 個請求同時到達，快取為空：
  → 請求 1：取得 SemaphoreSlim 鎖 → 查 DB → 寫入快取 → 釋放鎖
  → 請求 2~100：等待鎖（最多 10 秒）→ 取得鎖後 double-check → 快取已有 → 直接返回

結果：只有 1 次 DB 查詢，其餘 99 次直接用快取
```

**快取 Key 結構：** `wtm:lookup:{Type.FullName}:{tenantId}`
- 例如：`wtm:lookup:MyApp.Models.Department:ACME`
- 無租戶時 tenantId = `"_"`

### 12.5 自動失效機制

```
DC.Set<Department>().Add(new Department { ... });
DC.SaveChanges();
  → FrameworkContext.SaveChanges() 內部：
    → 掃描 ChangeTracker 中的變更實體
    → 是否有 [CacheLookup] 標記？
    → 是 → 呼叫 InvalidateType(typeof(Department))
    → 下次 GetAll<Department>() 重新從 DB 載入
```

**觸發失效的操作：** Add、Update、Delete（任何透過 `SaveChanges()` 的變更）。

### 12.6 多資料庫支援

每個連線字串（`CS`）獨立維護快取。如果你的應用連接了多個資料庫，同一個 `Department` 類型在不同資料庫中有不同的快取：

```csharp
// 從 default DB 取得
var depts = cache.GetAll<Department>(defaultDc);

// 從 secondary DB 取得（不同快取）
var depts2 = cache.GetAll<Department>(secondaryDc);
```

### 12.7 使用場景

#### 場景 1：下拉選單加速

```csharp
// 傳統做法：每次開啟表單都查 DB
AllDepartments = DC.Set<Department>().GetSelectListItems(Wtm!, x => x.Name);

// 使用 Lookup Cache：從記憶體取，零 DB 查詢
var cache = Wtm!.ServiceProvider.GetRequiredService<ILookupCacheService>();
AllDepartments = cache.GetAll<Department>()
    .Select(d => new ComboSelectListItem { Value = d.ID.ToString(), Text = d.Name })
    .ToList();
```

#### 場景 2：資料驗證（不查 DB）

```csharp
public override void Validate()
{
    var cache = Wtm!.ServiceProvider.GetRequiredService<ILookupCacheService>();
    var dept = cache.Get<Department>(Entity.DepartmentId);
    if (dept == null)
        MSD!.AddModelError("Entity.DepartmentId", "部門不存在");
}
```

#### 場景 3：適合 vs 不適合快取的資料

| 適合 | 不適合 |
|------|--------|
| 部門（幾十筆，少變動） | 訂單（百萬筆，頻繁變動） |
| 職稱、等級 | 使用者列表（可能很大） |
| 狀態碼、類別 | 任何超過 1000 筆的表 |
| 設定參數 | 含有大型欄位（BLOB）的表 |

**經驗法則：** 資料量 < 1000 筆 且 變動頻率 < 每分鐘 1 次 → 適合用 Lookup Cache。

### 12.8 Stats / Health API（10.4.0+）

`ILookupCacheService.GetStats()` 公開內部統計，讓 admin / SRE 判斷 cache 是否 warm、invalidate 是否生效、hit rate 是否合理。

```csharp
// 取得所有已註冊型別的統計
var all = _lookupCacheService.GetStats();
foreach (var s in all)
{
    Console.WriteLine($"{s.EntityTypeName}: hits={s.Hits} misses={s.Misses} " +
        $"cached-keys={s.CurrentlyCachedTenantKeys} " +
        $"last-warm={s.LastWarmAt:o} last-invalidate={s.LastInvalidatedAt:o}");
}

// 取得單一型別
var cityStats = _lookupCacheService.GetStats(typeof(CityCode));
if (cityStats == null)
{
    // 該型別未標 [CacheLookup]
}
```

**`LookupCacheStats` 欄位：**

| 欄位 | 意義 |
|------|------|
| `EntityTypeName` | 型別 FullName（string） |
| `Hits` | Cache-hit read 次數（fast-path） |
| `Misses` | Cache-miss read 次數（DB load path，含首次載入與 TTL 到期後重載） |
| `InvalidateCount` | `Invalidate<T>` / `InvalidateType` 呼叫次數 |
| `CurrentlyCachedTenantKeys` | 目前記憶體中持有的 tenant key 數；多租戶場景每 tenant 獨立 key |
| `LastAccessAt` | 最後一次 `GetAll`（hit or miss）UTC 時間；從未存取則為 null |
| `LastWarmAt` | 最後一次 SetCache（`WarmOnStartup` 啟動載入 或 `RefreshAsync` 完成）UTC 時間 |
| `LastInvalidatedAt` | 最後一次 invalidate UTC 時間 |
| `TtlMinutesConfigured` | 對應 `CacheLookupAttribute.TtlMinutes` |
| `WarmOnStartup` | 對應 `CacheLookupAttribute.WarmOnStartup` |

**範疇與限制：**
- 計數器為 **process-lifetime** — 重啟歸零，非持久化
- 統計查詢本身（`GetStats` 呼叫）**不**計入 Hits / Misses
- 聚合粒度為 per-type（跨 tenant 合併）；tenant 分層維度刻意不做（記憶體膨脹 vs 效益不符）
- 未暴露 Prometheus / OpenTelemetry exporter — app 可自行在 `/metrics` endpoint 或 background hosted service 取 stats 轉換為想要的監控格式
- 框架不內建 HTTP endpoint；app 可寫 5 行 controller 包：

```csharp
[ApiController, Route("admin/cache"), Authorize(Roles = "Admin")]
public class CacheStatsController : ControllerBase
{
    private readonly ILookupCacheService _cache;
    public CacheStatsController(ILookupCacheService cache) => _cache = cache;

    [HttpGet("stats")]
    public ActionResult<IReadOnlyList<LookupCacheStats>> Stats() => Ok(_cache.GetStats());
}
```

**運維場景範例：**

```
scenario：用戶回報「我改了資料，前端還看到舊值」
  1. admin hit /admin/cache/stats
  2. 看對應 type 的 LastInvalidatedAt：
     - null / 很久以前 → cache 沒失效，確認 Invalidate 呼叫邏輯
     - 剛剛 → cache 已失效，問題在 DB replication / app 層
  3. 看 CurrentlyCachedTenantKeys：失效後應為 0；大於 0 則是其他 tenant 的 key（不影響當前用戶）

scenario：部署後啟動 warmup 是否成功
  1. admin hit /admin/cache/stats
  2. 看 WarmOnStartup=true 的 type：LastWarmAt 應為啟動時間（幾分鐘內）
     - null → warmup service 沒跑 或 DB 查詢拋例外；查 Serilog
     - 遠早於啟動 → warmup 失敗且 fallback 到 lazy load
```

---

## 13. 測試指南

### 13.1 Mock 基礎設施

```csharp
// 建立 WTMContext（不需資料庫）
var wtm = MockWtmContext.CreateWtmContext();

// 建立 WTMContext（帶 DataContext）
var wtm = MockWtmContext.CreateWtmContext(dataContext, "testuser");

// 建立 MVC Controller
var controller = MockController.CreateController<EmployeeController>(dataContext, "user");

// 建立 API Controller
var controller = MockController.CreateApi<EmployeeApiController>(dataContext, "user");
```

### 13.2 Controller 測試範例

```csharp
[TestClass]
public class EmployeeControllerTest
{
    private EmployeeController _controller = null!;
    private string _seed = null!;

    [TestInitialize]
    public void Setup()
    {
        _seed = Guid.NewGuid().ToString();
        _controller = MockController.CreateController<EmployeeController>(
            new DataContext(_seed, DBTypeEnum.Memory), "testuser");
    }

    [TestMethod]
    public void Create_ValidEmployee_SucceedsAndPersists()
    {
        var vm = _controller.Wtm.CreateVM<EmployeeVM>();
        vm.Entity = new Employee { Name = "張三", Salary = 50000 };

        _controller.Create(vm);

        using var ctx = new DataContext(_seed, DBTypeEnum.Memory);
        var employee = ctx.Set<Employee>().FirstOrDefault();
        Assert.IsNotNull(employee);
        Assert.AreEqual("張三", employee.Name);
    }
}
```

### 13.3 Analysis 測試範例（靜態資料注入）

```csharp
[EnableAnalysis]
private class TestListVM : BasePagedListVM<SaleRecord, BaseSearcher>
{
    public override IOrderedQueryable<SaleRecord> GetSearchQuery()
        => _testData.AsQueryable().OrderByDescending(x => x.ID);
}

private static IList<SaleRecord> _testData = new List<SaleRecord>();

[TestInitialize]
public void Setup()
{
    _testData = new List<SaleRecord>
    {
        new() { Region = "北區", Amount = 100 },
        new() { Region = "南區", Amount = 200 },
    };

    var registry = new AnalysisVmRegistry();
    registry.Build(new[] { typeof(MyTest).Assembly });

    _controller = new _AnalysisController(registry);
    _controller.Wtm = MockWtmContext.CreateWtmContext();
}
```

### 13.4 SQLite In-Memory 測試

```csharp
private SqliteConnection _conn = null!;
private DataContext _ctx = null!;

[TestInitialize]
public void Setup()
{
    _conn = new SqliteConnection("DataSource=:memory:");
    _conn.Open();  // 必須保持開啟

    var opts = new DbContextOptionsBuilder<DataContext>()
        .UseSqlite(_conn).Options;
    _ctx = new DataContext(opts);
    _ctx.Database.EnsureCreated();
}

[TestCleanup]
public void Cleanup()
{
    _ctx.Dispose();
    _conn.Dispose();  // 必須在 context 之後 dispose
}
```

### 13.5 JS 測試（Jest）

```javascript
const vm = require('vm');
const fs = require('fs');

function makeEnv() {
    const ctx = vm.createContext({
        window: {},
        document: { getElementById: jest.fn(), querySelectorAll: jest.fn(() => []) },
        fetch: jest.fn(),
    });
    const src = fs.readFileSync('src/.../framework_analysis.js', 'utf8');
    new vm.Script(src).runInContext(ctx);
    return ctx.wtmAnalysis;
}

test('detectChartType returns bar for single dim + single measure', () => {
    const wa = makeEnv();
    expect(wa.detectChartType(['Region'], ['Amount'])).toBe('bar');
});
```

**重要：** 每個 DOM 測試必須用 `makeEnv()` 建立新 context，因為 `_state` 是 IIFE 模組級變數。

---

## 14. TimeProvider 時間抽象

自 10.0.1 起，WTM 全面採用 .NET 8+ 的 `TimeProvider` 抽象取代直接呼叫 `DateTime.Now` / `DateTime.UtcNow`。這使得所有時間相關邏輯皆可在測試中精確控制。

### 14.1 取得當前時間

在 ViewModel 或 Service 中，透過 `WTMContext.TimeProvider` 取得：

```csharp
// 取代 DateTime.Now
var now = Wtm.TimeProvider.GetLocalNow();

// 取代 DateTime.UtcNow
var utcNow = Wtm.TimeProvider.GetUtcNow();
```

### 14.2 受影響範圍

以下區域的 `DateTime.Now` / `DateTime.UtcNow` 已全部遷移至 `TimeProvider`：

- **WTMContext** — `TimeProvider` 屬性，透過 DI 注入
- **TokenService** — JWT access/refresh token 過期計算
- **DataContext** — `CreateTime`、`UpdateTime` 自動填充
- **Dashboard 模組** — 日期範圍計算
- **ETL 模組** — 排程與執行時間
- **DateRange** — `Today()`、`ThisMonth()` 等 factory methods 接受 `TimeProvider` 參數

### 14.3 測試中使用 FakeTimeProvider

```csharp
using Microsoft.Extensions.Time.Testing;

var fakeTime = new FakeTimeProvider(
    new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.FromHours(8)));

// 注入到 WTMContext
var wtm = MockWtmContext.CreateWtmContext(timeProvider: fakeTime);

// 推進時間
fakeTime.Advance(TimeSpan.FromHours(2));
```

`FakeTimeProvider` 來自 `Microsoft.Extensions.TimeProvider.Testing` 套件，測試專案已包含此依賴。

---

## 15. Integration Tests 整合測試

WTM 支援對真實資料庫執行整合測試，以驗證 EF Core 查詢、Migration 和 DB-specific 行為。

### 15.1 執行整合測試

**選項 1：Docker（推薦，10.3.0+）**

專案根目錄備有 `docker-compose.yml`，一鍵啟動與 CI 相同版本的 SQL Server 2022：

```bash
# 啟動 SQL Server（同 CI 使用的映像）
docker compose up -d mssql

# 等 ~15s 冷啟動完成，執行整合測試
dotnet test test/WalkingTec.Mvvm.Integration.Test -c Release

# 用完清除
docker compose down -v
```

**選項 2：連線現有 SQL Server**

```bash
export WTM_TEST_MSSQL="Server=localhost;Database=WtmTest;User Id=sa;Password=YourPassword;TrustServerCertificate=True"
dotnet test --filter "TestCategory=Integration" -c Release
```

**選項 3：跳過整合測試**

```bash
dotnet test WalkingTec.Mvvm.sln --filter "TestCategory!=Integration" -c Release
# 或使用 CI 專用的 solution filter
dotnet test ci.slnf -c Release
```

**無 SQL 時的行為（10.3.0+）**：若本機未跑 SQL Server，`IntegrationTestBase.EnsureSqlServerAvailable()` 會透過 `Lazy<T>` 快取的 2 秒 TCP 探測偵測，每個整合測試改報 `AssertInconclusive`（黃色略過）而非 `Failed`（紅色），並附多行設定指引訊息。CI 上的 service container 能連 → 探測回傳 null → 正常執行。

### 15.2 撰寫整合測試

```csharp
[TestClass]
[TestCategory("Integration")]
public class StudentIntegrationTests
{
    [TestMethod]
    public void Create_and_query_student()
    {
        var connStr = Environment.GetEnvironmentVariable("WTM_TEST_MSSQL");
        if (string.IsNullOrEmpty(connStr))
        {
            Assert.Inconclusive("WTM_TEST_MSSQL not set — skipping integration test");
        }

        // 使用真實 DB 執行測試...
    }
}
```

### 15.3 CI 整合

GitHub Actions 中，整合測試需在 `services` 區塊啟動資料庫容器，並將連線字串透過 `env` 傳入。預設 CI 只執行單元測試（不含 `TestCategory=Integration`）。

---

## 16. 代碼生成器

代碼生成器是 WTM 的核心生產力工具 — 選擇 Model，點幾下按鈕，就能生成完整的 CRUD 功能（Controller + ViewModel + View），省去 80% 的重複編碼工作。

### 16.1 存取方式

```
瀏覽器開啟：http://localhost:5000/_CodeGen/Index
```

- 僅在 `Debug` 模式可用（標記 `[DebugOnly]`）
- 生產環境自動隱藏，無安全風險

### 16.2 使用步驟

**步驟 1：準備 Model**

確保 Model 滿足以下條件：
```csharp
// ✅ 繼承 TopBasePoco（或其子類 BasePoco、PersistPoco）
public class Product : BasePoco
{
    [Display(Name = "商品名稱")]
    [Required]
    [StringLength(100)]
    public string Name { get; set; } = "";

    [Display(Name = "價格")]
    public decimal Price { get; set; }

    [Display(Name = "分類")]
    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }

    [Display(Name = "上架")]
    public bool IsActive { get; set; } = true;
}

// ✅ 在 DataContext 中註冊 DbSet
public class DataContext : FrameworkContext
{
    public DbSet<Product> Products { get; set; } = null!;
    public DbSet<Category> Categories { get; set; } = null!;
}
```

**步驟 2：開啟代碼生成器頁面**

1. 在下拉選單中選擇 Model（如 `Product`）
2. 系統自動掃描 Model 的所有屬性

**步驟 3：設定生成選項**

| 設定項 | 選項 | 說明 |
|--------|------|------|
| Controller 類型 | MVC / API | MVC 生成 View，API 生成 RESTful 端點 |
| 功能模組 | CRUD、列表、匯入、匯出、批量 | 勾選需要的功能 |
| 前端框架 | Razor / React / Vue 3 / Blazor | 僅 MVC 類型可選 |
| 列表欄位 | 勾選要顯示的欄位 | 自動生成 GridHeader |
| 搜尋欄位 | 勾選可搜尋的欄位 | 自動生成 Searcher |
| 匯入欄位 | 勾選可匯入的欄位 | 自動生成 TemplateVM |

**步驟 4：生成代碼**

點擊「生成」後，自動建立以下檔案：

```
Areas/
└── {Module}/
    ├── Controllers/
    │   └── ProductController.cs        ← CRUD Controller（含搜尋/匯入/匯出）
    ├── ViewModels/
    │   └── ProductVMs/
    │       ├── ProductVM.cs            ← CRUD ViewModel
    │       ├── ProductListVM.cs        ← 列表 ViewModel + Searcher
    │       ├── ProductBatchVM.cs       ← 批量操作 ViewModel
    │       └── ProductImportVM.cs      ← 匯入 ViewModel + Template
    └── Views/
        └── Product/
            ├── Index.cshtml            ← 列表頁（搜尋面板 + Grid）
            ├── Create.cshtml           ← 新增表單
            ├── Edit.cshtml             ← 編輯表單
            ├── Delete.cshtml           ← 刪除確認
            ├── Details.cshtml          ← 詳情檢視
            ├── Import.cshtml           ← 匯入頁面
            └── BatchEdit.cshtml        ← 批量編輯頁面
```

### 16.3 生成後的自訂

生成的程式碼是完整可運行的，但通常需要自訂以下部分：

```csharp
// ProductVM.cs — 最常需要自訂的地方
public class ProductVM : BaseCRUDVM<Product>
{
    // 1. 加入下拉選單資料
    public List<ComboSelectListItem>? AllCategories { get; set; }

    protected override void InitVM()
    {
        // 2. 載入下拉選單（生成器會自動為 FK 生成）
        AllCategories = DC!.Set<Category>()
            .GetSelectListItems(Wtm!, x => x.Name);
    }

    public override void Validate()
    {
        // 3. 加入自訂驗證（生成器不會生成，需手動加）
        if (Entity.Price <= 0)
            MSD!.AddModelError("Entity.Price", "價格必須大於 0");
    }
}
```

### 16.4 Analysis Mode 整合

如果 Model 上有 `[Dimension]` 和 `[Measure]` Attribute，生成器會自動：
- 在 ListVM 加上 `[EnableAnalysis]`
- 在 View 的 Grid 加上 `enable-analysis="true"`

### 16.5 使用限制

| 限制 | 說明 |
|------|------|
| 繼承要求 | Model 必須繼承 `TopBasePoco` 或其子類 |
| DbSet 註冊 | Model 必須在 DataContext 中有 `DbSet<T>` |
| Debug only | `[DebugOnly]` 標記，Release 模式不可存取 |
| 覆蓋風險 | 重複生成會覆蓋已有檔案 — 先備份再生成 |

**最佳實踐：** 用代碼生成器建立骨架，然後手動加入業務邏輯。不要重複生成已自訂過的檔案。

---

## 17. 配置參考

### 17.1 appsettings.json 完整結構

```json
{
  "Connections": [
    { "Key": "default", "Value": "Data Source=app.db", "DbType": "SQLite" },
    { "Key": "secondary", "Value": "Server=...;Database=...;", "DbType": "SqlServer" }
  ],
  "CookiePre": "WTM",
  "IsQuickDebug": true,
  "EnableTenant": false,
  "UIOptions": {
    "DataTable": {
      "RPP": 30,
      "ShowPrint": false,
      "ShowFilter": true
    },
    "ComboBox": { "DefaultEnableSearch": true },
    "DateTime": { "DefaultReadonly": true },
    "SearchPanel": { "DefaultExpand": true }
  },
  "FileUploadOptions": {
    "UploadLimit": 20971520,
    "SaveFileMode": "Local"
  },
  "JwtOptions": {
    "Issuer": "WTM",
    "Audience": "WTM",
    "Expires": 900,
    "RefreshExpires": 604800,
    "SecurityKey": "your-256-bit-secret-key-here-min-32-chars!",
    "LoginPath": "/Login/Login"
  },
  "CorsOptions": {
    "EnableAll": false,
    "Policy": [
      { "Name": "default", "Domain": "http://localhost:3000" }
    ]
  }
}
```

### 17.2 連線字串格式

| DbType | 連線字串範例 |
|--------|------------|
| `SQLite` | `Data Source=app.db` |
| `SqlServer` | `Server=localhost;Database=MyDb;User Id=sa;Password=xxx;TrustServerCertificate=True` |
| `MySql` | `Server=localhost;Port=3306;Database=MyDb;Uid=root;Pwd=xxx;` |
| `PgSql` | `Host=localhost;Port=5432;Database=MyDb;Username=postgres;Password=xxx;` |
| `Oracle` | `Data Source=//localhost:1521/ORCL;User Id=HR;Password=xxx;` |
| `Memory` | （任意字串作為 DB 名）`"testdb"` |

### 17.3 關鍵配置說明

| 配置項 | 預設值 | 說明 |
|--------|--------|------|
| `CookiePre` | `"WTM"` | Cookie 前綴（多站部署時避免衝突） |
| `IsQuickDebug` | `true` | 快速調試模式（跳過部分權限檢查） |
| `EnableTenant` | `false` | 啟用多租戶 |
| `UIOptions.DataTable.RPP` | `20` | Grid 每頁預設行數 |
| `FileUploadOptions.UploadLimit` | `20971520` | 上傳限制（bytes，預設 20MB） |
| `FileUploadOptions.SaveFileMode` | `"Local"` | 檔案儲存方式（Local / Database） |
| `JwtOptions.Expires` | `3600` | Access Token 有效期（秒） |
| `JwtOptions.RefreshExpires` | `604800` | Refresh Token 有效期（秒，預設 7 天） |
| `JwtOptions.SecurityKey` | — | JWT 簽名金鑰（至少 32 字元，**必須修改**） |
| `CookieOptions.SecurePolicy` | `"SameAsRequest"` | Cookie `Secure` flag 策略（`SameAsRequest` / `Always` / `None`；production 建議 `Always`，詳見 §10.8） |
| `CookieOptions.Expires` | `3600` | Cookie 驗證有效期（秒） |
| `CookieOptions.LoginPath` | `"/Login/Login"` | 未登入時重導向的登入路徑 |

### 17.4 version.props

```xml
<Project>
  <PropertyGroup>
    <VersionPrefix>10.3.0</VersionPrefix>
  </PropertyGroup>
</Project>
```

所有 NuGet 套件共用此版本號。修改此檔案後，所有 `dotnet pack` 產出的套件自動使用新版本。

### 17.5 多環境配置

```
appsettings.json              ← 基礎配置（所有環境共用）
appsettings.Development.json  ← 開發環境覆蓋（IsQuickDebug: true）
appsettings.Production.json   ← 生產環境覆蓋（連線字串、JWT Key）
```

**生產環境必改項目：**

| 項目 | 原因 |
|------|------|
| `Connections[].Value` | 使用正式資料庫連線 |
| `JwtOptions.SecurityKey` | 換成高強度隨機金鑰 |
| `IsQuickDebug` | 設為 `false`（啟用完整權限檢查） |
| `CookiePre` | 如有多站部署，確保不同前綴 |

---

## 18. 常見問題

### Q1: FrameworkContext 測試時出現 SQLite Error 1（重複欄名）
**原因：** `base.OnModelCreating()` 會掃描所有載入的 assembly，造成 `MajorId` 等欄位衝突。
**解法：** 測試用的 `TestFwContext : FrameworkContext` 必須 override `OnModelCreating` **不呼叫** `base`。EF Core 透過 `DbSet<>` 屬性自動探索實體。

### Q2: method.Invoke() 的例外被包裝
**原因：** 反射呼叫（如 Analysis 的 `ExecuteDynamic`）會把內部例外包在 `TargetInvocationException` 裡。
**解法：** catch 時 unwrap：`catch (TargetInvocationException ex) { throw ex.InnerException!; }`

### Q3: Nullable 遷移策略
- 新檔案：完全 nullable-annotated，不加 `#nullable disable`
- catch block：用 `?.` + fallback，不用 `!`
- 現有 156 個 `#nullable disable` 檔案，按依賴順序逐批遷移

### Q4: 多資料庫切換
**方式 1：Attribute 固定指定**
```csharp
[FixConnection("secondary")]  // 此 Action 固定使用 secondary 連線
public IActionResult Report()
{
    var vm = Wtm.CreateVM<ReportListVM>();
    return PartialView(vm);
}
```

**方式 2：動態切換（同一 Action 依條件選擇）**
```csharp
// 在 Controller 中手動切換
Wtm.CurrentCS = "secondary";
var vm = Wtm.CreateVM<EmployeeListVM>();

// 或在 ViewModel 中切換
protected override void InitVM()
{
    DC = Wtm!.CreateDC(cskey: "secondary");
}
```

### Q5: Console 應用使用 WTM
```csharp
var services = new ServiceCollection();
services.AddWtmContextForConsole(new ConfigurationBuilder()
    .AddJsonFile("appsettings.json").Build());

var provider = services.BuildServiceProvider();
var wtm = provider.GetRequiredService<WTMContext>();
var vm = wtm.CreateVM<EmployeeListVM>();
vm.DoSearch();
Console.WriteLine($"共 {vm.Searcher.Count} 筆");
```

### Q6: 如何自訂登入頁面？
```csharp
// 1. 建立自己的 LoginController
[Public]
public class LoginController : BaseController
{
    [ActionDescription("登入")]
    public IActionResult Login() => View();

    [HttpPost]
    [ActionDescription("登入")]
    public async Task<IActionResult> Login(LoginVM vm)
    {
        var user = await vm.DoLoginAsync();
        if (user == null)
            return View(vm);  // 驗證失敗，重新顯示表單

        return Redirect("/Home/Index");
    }
}

// 2. 在 appsettings.json 設定登入路徑
// "JwtOptions": { "LoginPath": "/Login/Login" }
```

### Q7: Grid 列表怎麼加自訂按鈕？
```csharp
protected override List<GridAction> InitGridAction()
{
    return new List<GridAction>
    {
        // 標準按鈕
        this.MakeStandardAction("Employee", GridActionStandardTypesEnum.Create, "新增", ""),

        // 自訂按鈕 — 開啟對話框
        this.MakeAction("Employee", "Approve", "審批", "審批",
            GridActionParameterTypesEnum.SingleId)
            .SetDialogTitle("審批").SetWidth(600),

        // 自訂按鈕 — 直接執行 AJAX（不開對話框）
        this.MakeAction("Employee", "Export", "匯出報表", "",
            GridActionParameterTypesEnum.NoId)
            .SetIsRedirect(false)
            .SetOnClickScript("doExport"),
    };
}
```

### Q8: 怎麼處理 M:N 多對多關聯？
```csharp
// 1. 建立中間表（標記 [MiddleTable]）
[MiddleTable]
public class EmployeeSkill : TopBasePoco
{
    public Guid EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public Guid SkillId { get; set; }
    public Skill? Skill { get; set; }
}

// 2. 在 CRUD VM 中加入 SelectedSkillIds
public List<string>? SelectedSkillIds { get; set; }

// 3. InitVM 中載入已選項
SelectedSkillIds = DC.Set<EmployeeSkill>()
    .Where(x => x.EmployeeId == Entity.ID)
    .Select(x => x.SkillId.ToString()).ToList();

// 4. View 中使用 checkbox
// <wt:checkbox field="SelectedSkillIds" items="@Model.AllSkills" />

// 5. DoAdd/DoEdit 中手動處理關聯（見 Section 4.2 範例）
```

### Q9: 如何實現軟刪除？
```csharp
// Model 繼承 PersistPoco（自帶 IsValid 欄位）
public class Employee : PersistPoco
{
    public string Name { get; set; } = "";
}

// 效果：
// - 刪除時 IsValid 設為 false（不是真刪除）
// - 所有查詢自動加 WHERE IsValid = true（Global Query Filter）
// - 需要看到已刪除資料：.IgnoreQueryFilters()
```

### Q10: 部署到生產環境的檢查清單

| 項目 | 動作 |
|------|------|
| `IsQuickDebug` | 設為 `false` |
| `JwtOptions.SecurityKey` | 替換為高強度隨機金鑰（≥32 字元） |
| 連線字串 | 使用正式 DB，不用 SQLite/Memory |
| HTTPS | 啟用 HTTPS + HSTS |
| 檔案上傳 | 設定合理的 `UploadLimit` |
| 密碼 | 確認所有管理員帳號已改密碼 |
| 代碼生成器 | Release 模式自動隱藏（`[DebugOnly]`） |
| 日誌 | 設定 Serilog/NLog 寫入持久化儲存 |
| 資料庫遷移 | `dotnet ef database update` 確認 schema 最新 |

---

## 附錄

### A. 檔案路徑速查

| 路徑 | 用途 |
|------|------|
| `src/WalkingTec.Mvvm.Core/BaseVM.cs` | ViewModel 基底 |
| `src/WalkingTec.Mvvm.Core/BaseCRUDVM.cs` | CRUD VM |
| `src/WalkingTec.Mvvm.Core/BasePagedListVM.cs` | 列表 VM |
| `src/WalkingTec.Mvvm.Core/WTMContext.cs` | 請求上下文 |
| `src/WalkingTec.Mvvm.Core/DataContext.cs` | EF Core 封裝 |
| `src/WalkingTec.Mvvm.Core/Analysis/` | Analysis 引擎 |
| `src/WalkingTec.Mvvm.Mvc/FrameworkServiceExtension.cs` | 啟動註冊 |
| `src/WalkingTec.Mvvm.Mvc/_AnalysisController.cs` | Analysis API |
| `src/WalkingTec.Mvvm.Etl/` | ETL 模組 |
| `src/WalkingTec.Mvvm.Etl/Pipeline/` | ETL Pipeline 核心（Executor, Loaders, Watermark） |
| `src/WalkingTec.Mvvm.Etl/Scheduling/` | Quartz 排程（SchedulerService, ProgressTracker） |
| `src/WalkingTec.Mvvm.Etl/Controllers/` | ETL 管理 UI（Job CRUD, Monitor, RunLog） |
| `src/WalkingTec.Mvvm.Etl/Models/` | ETL 資料模型（JobDefinition, RunLog, Progress） |
| `src/WalkingTec.Mvvm.Core/Auth/` | JWT Token 服務 |
| `src/WalkingTec.Mvvm.Core/Cache/` | Lookup Cache 服務 |
| `src/WalkingTec.Mvvm.Core/PasswordHashHelper.cs` | 密碼雜湊（PBKDF2） |
| `test/WalkingTec.Mvvm.Test.Mock/` | 測試 Mock |
| `docs/` | 文件 |
| `version.props` | 版本號 |
| `CHANGELOG.md` | 變更日誌 |

### B. 版本歷史

詳見 `CHANGELOG.md`。
