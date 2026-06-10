# Dependency Management

> **適用版本**：10.5.1+
> **最後更新**：2026-05-14
> **對應 .claude/rules**：本檔是 `.claude/rules/dependency-management.md` 的 public-facing 對應版本

WTM 的套件版本管理政策、升級流程、與 .NET 10 帶來的 `NU1510` 雙意義警告陷阱。

---

## 版本號規則 (X.Y.Z)

`version.props` 中的 `<VersionPrefix>` 遵循以下規則：

| 欄位 | 意義 | 何時遞增 |
|------|------|---------|
| **X** | .NET Core 主版本 | 僅在升至下一個 .NET 主版本時（如 .NET 10 → 11）；當前固定為 `10` |
| **Y** | 主功能升級 | 新增模組或重大新功能（例：WorkFlow 引擎 → `10.9.0`） |
| **Z** | 次要優化 | Bug 修復、patch、小優化（例：hotfix → `10.9.1`） |

**範例**：`10.9.0` = .NET 10、第 9 次主功能升級（WorkFlow 引擎）、初始釋出。  
**注意**：X 只在 .NET 主版本升級時才動，不反映 SDK patch 版本（如 10.0.300 vs 10.0.201）。

---

## 版本宣告檔分工

| 檔案 | 用途 |
|------|------|
| `version.props` | 框架自身版本（`<VersionPrefix>10.9.0</VersionPrefix>`） — release 時 bump（遵循上方 X.Y.Z 規則） |
| `common.props` | 集中所有 src 專案的套件版本（MSBuild 變數：`MicrosoftExtensionsVersion`、`EntityFrameworkCoreVersion`、`OpenTelemetryVersion`、`SerilogAspNetCoreVersion`、`NPOIVersion`、`SystemSecurityCryptographyXmlVersion` 等 12+ 個） |
| `test/Directory.Build.props` | 測試專案專用套件版本（MSTest、FluentAssertions、Moq 等） |

**DB providers** 的 EF Core provider 套件版本（MSSQL/MySQL/PostgreSQL/Oracle/SQLite）**硬編碼在各 csproj**、不走 `common.props` — 因為這些 provider 版本演進節奏不同步，不應綁同一個變數。

---

## 升級 Checklist

升級任何 NuGet 套件前後：

1. 改 `common.props` 中對應的版本變數（不要在個別 csproj 內覆寫，除非有明確理由）
2. `dotnet restore` 確認無 NU1605 / NU1107 conflict
3. **`dotnet list package --vulnerable --include-transitive`** 通過（**0 NU1903** 是 release gate）
4. `dotnet build WalkingTec.Mvvm.sln -c Release` 0 errors
5. `dotnet test WalkingTec.Mvvm.sln -c Release` 全 pass
6. 若是 security fix：**`CHANGELOG.md` 必須有條目**

---

## NU1510 雙意義警告（.NET 10 起）

> ⚠️ **重要陷阱**：.NET 10 SDK 引入的 `NU1510` 警告有兩種完全相反的意義，**單看警告本身分辨不出**。
> 誤判可能造成安全漏洞復活（本 repo Issue #13 / PR #14 有 post-mortem 紀錄）。

### 兩種意義

| 情境 | 觸發條件 | 正確動作 |
|------|----------|----------|
| **A. 真冗餘** | 顯式 `<PackageReference>` 一個 framework 已含的套件，且該套件**沒有作為 transitive override** 角色 | 移除 PackageReference |
| **B. Security override** | 顯式 reference 是為了用較新版本**蓋掉**某 transitive dep 拉的有漏洞舊版 | **保留**。NU1510 是「override 生效」的正向訊號，**不是**「該清」的訊號 |

### 本 repo 已知情境 B（不可移除）

`src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj` 中緊接 NPOI 之後的：

```xml
<!--
  Issue #816 — pin vulnerable transitive from NPOI.
  NPOI's transitive pull of System.Security.Cryptography.Xml 8.0.2 has HIGH-severity
  CVEs (GHSA-37gx-xxp4-5rgx, GHSA-w3x6-4m5h-cxqf). This direct PackageReference at
  10.0.6 overrides the transitive across the whole solution.
  .NET 10 will emit NU1510 ("framework already provides this package") on this line.
  DO NOT remove based on NU1510 — that signal is the EXPECTED positive side-effect
  of the override working. See commit 7b63587b and Issue #13 post-mortem.
-->
<PackageReference Include="System.Security.Cryptography.Xml" Version="$(SystemSecurityCryptographyXmlVersion)" />
```

**背景**：NPOI 2.7.6 與 2.8.0（最新）都 transitive pin `System.Security.Cryptography.Xml 8.0.2`，該版本有兩個 high-severity CVE。本 repo 用「直接 reference 蓋過 transitive」模式，把 solution-wide Crypto.Xml 鎖到 10.0.6 安全版本。

**移除這條 reference 的後果**：

```
13+ projects (test、demo、Etl.Test、Api.Test、Mock 等) 立刻 emit NU1903 high-severity
warning，因為 NPOI transitive 8.0.2 不再被覆蓋。release 直接失格。
```

完整 post-mortem：[Issue #13](https://internal.registry.invalid/chiu0831/WTM/issues/13)、[PR #14](https://internal.registry.invalid/chiu0831/WTM/pulls/14)、[Issue #15](https://internal.registry.invalid/chiu0831/WTM/issues/15)。

### NU1510 vs NU1903 取捨

對 production 而言，優先順序是：

1. **NU1903（vulnerable）= P0**，絕不容忍
2. **NU1510（informational）= P3 噪音**，可接受

這個 repo 接受 NU1510 噪音換取零漏洞 — 直到 NPOI 上游升 Crypto.Xml dep（追蹤於 [Issue #15](https://internal.registry.invalid/chiu0831/WTM/issues/15)）。

---

## 移除 PackageReference 的強制 SOP

在任何 PR 中移除 `<PackageReference>`（不論觸發來源是 NU1510 警告、dependency cleanup、或人工判斷冗餘）**之前必做**：

### 1. 讀 git history

```bash
git log --oneline -- <csproj-path>
```

特別找：
- `Closes #N` — 看看是否關聯某個 issue
- `fix(security)` / `chore(security)` — 安全相關修改
- `pin` / `override` 字眼 — 暗示是 transitive override

### 2. 讀 inline XML 註解

很多 security pin 會在 csproj 內留 `<!-- Issue #N: ... -->` 告示牌（Chesterton's fence 風格）。註解就是維護者留給未來自己/接手者的訊息，**讀**它。

### 3. 做 before / after vulnerable scan diff

```bash
# before：在 base branch
dotnet list WalkingTec.Mvvm.sln package --vulnerable --include-transitive \
  > /tmp/before.txt

# 移除後（你的修改）
dotnet list WalkingTec.Mvvm.sln package --vulnerable --include-transitive \
  > /tmp/after.txt

diff /tmp/before.txt /tmp/after.txt
```

若 `after.txt` 多出**任何**新的 NU1903 警告 → 該 reference 是 security override → **立即還原**並補強註解。

### 4. PR 描述明確說明

PR body 必須包含：
- 移除哪些 PackageReference
- 為何認為它們是真冗餘（情境 A）而非 security override（情境 B）
- before/after vulnerable scan diff 結果（截圖或文字）

### 5. 若派 agent 動 dep

如果你派 Sonnet/Haiku/其他 agent 執行 dep 變動，prompt 中**必須要求**對方執行步驟 3 並 report side-effect。

---

## NPOI 升級政策

NPOI（Excel 匯入/匯出）是 WTM 對外部相依的單一最大來源。當前狀態：

| 版本 | 拉的 Crypto.Xml | 圖像渲染依賴 | 建議 |
|------|----------------|--------------|------|
| 2.7.6（**目前**） | 8.0.2 ❌ | SixLabors.ImageSharp 2.1.11 | 保留 pin、暫不升 |
| 2.8.0（最新） | 8.0.2 ❌ | **SkiaSharp 3.119.2**（native binary） | 漏洞不解決，且帶來 native dep 風險 |

升級條件（任一滿足才升）：
- NPOI 自己升 Crypto.Xml 到 10.0.x → 可移除 pin（最理想）
- 業務確需 NPOI 2.8.0 才有的新功能 → 升 NPOI 但**保留** pin，並評估 SkiaSharp native binary 對 Docker image / cross-platform 的影響

否則保持現狀。追蹤於 [Issue #15](https://internal.registry.invalid/chiu0831/WTM/issues/15)。

---

## 長期方向（已評估、暫不執行）

### Central Package Management（CPM）

`Directory.Packages.props` + `<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>` 是 .NET 的「first-class transitive pin」方案。

**優點**：
- NU1510 與 NU1903 取捨從根本消失
- Solution-wide 版本宣告統一在一處
- 未來 dep 升級單點維護

**缺點**：
- 需要 touch 22+ csproj（移除每個 `Version="..."`）
- 出錯機率不小、回滾複雜
- 本 fork 走「穩定為先」，CPM 是大改動需多輪 staging

未來若有 driver（例如想加入超過 30 個專案、或想開始 OSS 對外接 PR），再評估。

---

## 相關文件

- [`docs/production-readiness.md`](./production-readiness.md) — 整體 production readiness 評估
- [`docs/ci-operations.md`](./ci-operations.md) — CI 漏洞掃描 gate 的設定
- [`docs/wtm-developer-manual.md`](./wtm-developer-manual.md) § 10 — 安全機制總覽
- [`CHANGELOG.md`](../CHANGELOG.md) — 每版 dep 變動紀錄
