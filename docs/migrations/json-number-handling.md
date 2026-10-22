# MVC JSON 的數字輸出：字串或 JSON number

本文件說明 `UseWtmJsonOptions()` 如何輸出數字、哪些舊版本的行為不同，以及需要時怎麼改回 JSON number。

**fork 的預設行為與 upstream 一致，本文件不改變任何預設。**

## 1. 目前的行為

`UseWtmJsonOptions()`（`src/WalkingTec.Mvvm.Mvc/Helper/MvcOptionExtension.cs`）設定：

```csharp
options.JsonSerializerOptions.NumberHandling =
    JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString;
```

因此 MVC `Json()` 與 API 回應中的數值型別（`int`、`long`、`decimal`、`double` 等），輸出時是 **JSON 字串**，例如 `{"Spread":"0"}`；讀取時，字串與數字兩種寫法都接受。

這個設定也會指定給 `CoreProgram.DefaultJsonOption`，框架內部的 cache 序列化、`WTMContext.CallAPI` 的回應反序列化等都使用同一個物件。

## 2. 不同版本的差異

這是 upstream 長期的預設值，但中間有一段時間被拿掉：

| upstream 版本 | 數字輸出 |
|---|---|
| 6.3.24 以前（2021-04 起） | 字串 |
| **6.3.25～6.3.29**（2023-06） | **JSON number** |
| 6.3.30 以後、6.4、6.5、10.0、本 fork 10.0.x | 字串 |

判斷依據是決定這個行為的套件 `WalkingTec.Mvvm.Mvc` **實際載入**的版本，而不是專案檔裡寫的版本。如果其他套件（例如 `WalkingTec.Mvvm.TagHelpers.LayUI`）依賴的是較新的 Mvc，NuGet 會解析成較高的版本。可以在 `obj/project.assets.json`，或部署目錄中 `WalkingTec.Mvvm.Mvc.dll` 的版本確認。

## 3. 會受影響的情況

從 **6.3.25～6.3.29** 升級時，回應裡的數字會從 number 變成字串，前端可能出現行為差異，例如：

- `if (resp.Spread)`：值為 `0` 時，原本判斷為 false，變成 `"0"` 之後判斷為 true
- `a + b`：變成字串串接
- `===` 比較、`typeof` 判斷

從其他版本升級時，行為沒有改變。

## 4. 改回 JSON number

在 `UseWtmJsonOptions()` **之後**把 `NumberHandling` 改回只允許從字串讀取：

```csharp
services.AddControllersWithViews() // 或 AddMvc／AddControllers，照原本的寫法
    .AddJsonOptions(o =>
    {
        o.UseWtmJsonOptions();
        o.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
    });
```

- 必須寫在 `UseWtmJsonOptions()` 之後，否則會被覆蓋。
- 不需要移除任何 converter。
- 讀取時仍然接受字串型的數字，所以前端送來 `"0"` 也能正確繫結。

### 對框架其他部分的影響

| 部分 | 影響 |
|---|---|
| LayUI grid 的資料（`GetJson`／`GetDataJson`） | 用字串組成 JSON，不經過 `NumberHandling`，**不受影響**。只有欄位值是非 System 型別的物件時，才會以 `DefaultJsonOption` 序列化，那部分的數字會改成 number |
| `FFResult()` 回傳的指令 | 輸出的是 script，不受影響 |
| distributed cache | 寫入改成 number，讀取仍接受字串，新舊 cache 內容都能讀取 |
| `WTMContext.CallAPI` | 反序列化回應時，字串與 number 都接受 |
| Vue3 等 SPA 前端 | 依各自程式碼是否假設數字為字串而定，請自行確認 |

## 5. 何時不應該改

如果應用程式的 API 有**外部呼叫端**，而且它們已經依賴目前的字串格式（例如從 6.3.24 以前或 6.3.30 以後一路沿用），就**不要**改回 number，否則外部呼叫端拿到的型別會改變。

## 6. 回滾

刪除第 4 節加上的那一行即可恢復預設（字串）。
