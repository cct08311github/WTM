# WTM 架構評估與 8.3 路線圖

本文件將目前對 WTM 的整體評估正式化，並整理為可執行的優化路線圖與工作任務。

---

## 1. 系統評價

### 總體判斷

WTM 已經具備企業級快速開發框架的骨架與可用性，不是單純的 helper 集合，而是一套整合：

- ViewModel 開發模型
- 權限與多租戶
- MVC / TagHelper / 前端整合
- 工作流
- 分析能力
- 日誌與錯誤處理
- 代碼生成
- 多資料庫支援

的完整平台。

### 優勢

- **核心抽象穩定**：`WTMContext + BaseVM + DataContext` 主幹清楚
- **功能完整**：CRUD、查詢、匯入、批次、權限、租戶、工作流、分析都已成熟
- **近版本演進正確**：安全、Analysis Mode、ProblemDetails、Structured Logging、Rate Limiting、GitHub Packages 都補到了高價值區域
- **可演進性良好**：已有 changelog、測試、CI、發版流程與文檔基礎

### 主要短板

- **歷史技術債偏重**：大量 `#nullable disable`
- **同步/非同步邊界不乾淨**：部分受框架限制，仍存在 sync-over-async 熱點
- **模組邊界仍偏厚重**：`WTMContext` 與部分 MVC/Workflow 能力耦合高
- **測試矩陣仍不完整**：多資料庫、多租戶、code generator、package 安裝驗證還不夠
- **生態與採用門檻偏高**：文檔與範例還沒完全產品化

### 評分

| 面向 | 評分 |
|------|------|
| 產品能力 | 8.5 / 10 |
| 架構一致性 | 8.0 / 10 |
| 工程現代化 | 6.5 / 10 |
| 可測試性 | 7.0 / 10 |
| 外部採用性 | 7.0 / 10 |
| 長期演進潛力 | 8.5 / 10 |

### 一句話總結

> WTM 是一套「功能成熟、抽象有價值、但工程現代化尚未完成」的企業級 .NET 快速開發框架。

---

## 2. 優化方向

### A. 核心代碼品質

- 逐步移除高頻核心路徑的 `#nullable disable`
- 收斂 sync-over-async，將受限點壓回邊界層
- 為反射式契約建立更穩定的 interface 與測試保護

### B. 模組解耦

- 進一步模組化 Analysis / Logging / Rate Limiting / Workflow
- 縮減 `WTMContext` 的責任面積
- 將 UI 特化能力與框架核心能力界線拉清

### C. 測試與驗證

- 建立多資料庫相容測試矩陣
- 補多租戶與工作流回歸
- 補 package 發佈後可見性與安裝 smoke test
- 補 code generator 產物快照測試

### D. 開發者體驗

- 標準化 release 與版本治理
- 補 onboarding / upgrade / troubleshooting 文檔
- 提供最小可行 sample 與模式選型指引

### E. 產品化

- 強化 Analysis Mode、Observability、Security、Workflow 作為核心賣點
- 讓 React / Vue / LayUI / Blazor 的支援形態更清楚可比較

---

## 3. 路線圖

## Phase 1: 穩定化與工程收斂

**目標：** 讓 8.2.x 成為穩定可消費的版本線，先把 release 與 QA 鏈條補齊。

### 主要項目

- 強化 package 發佈流程
- 增加 package install smoke test
- 清理 release 腳本邊界問題
- 補發版與安裝文檔
- 清理 JS 測試噪音

### 工作任務

- `P1-T1` 發佈後在乾淨 sample 專案執行 package restore/build smoke test
- `P1-T2` 在 publish workflow 中加入 package availability 驗證
- `P1-T3` 補 release 流程腳本的 idempotent 行為與認證檢查
- `P1-T4` 補 GitHub Packages 安裝/發佈文檔
- `P1-T5` 清理 Jest / jsdom 噪音，提升測試訊號品質

### 完成定義

- 發佈成功不代表結束，必須加上 feed 可見性與 restore smoke test
- release 腳本需支援重跑
- 文檔能支持新使用者完成安裝與發版

---

## Phase 2: 核心代碼品質提升

**目標：** 降低歷史技術債，讓後續功能開發與維護成本下降。

### 主要項目

- 核心 nullable 改造
- sync-over-async 熱點盤點與清理
- `WTMContext` 責任邊界梳理
- 反射契約測試化

### 工作任務

- `P2-T1` 優先處理 `BaseVM`、`WTMContext`、`BasePagedListVM`、`_AnalysisController`
- `P2-T2` 建立 sync-over-async 清單與優先級
- `P2-T3` 為啟動流程與 Analysis 契約補測試
- `P2-T4` 形成 `WTMContext` 拆分草案

### 完成定義

- 核心高頻路徑不再依賴大面積 `#nullable disable`
- 所有保留的同步橋接點都有明確註解與理由

---

## Phase 3: 測試矩陣與相容性驗證

**目標：** 把測試從「可跑」提升為「可信」。

### 主要項目

- 多資料庫相容測試
- 多租戶回歸測試
- code generator snapshot 測試
- package 安裝回歸驗證

### 工作任務

- `P3-T1` 建立 SQLite / SQL Server / MySQL 最小驗證矩陣
- `P3-T2` 補 tenant filter / `IgnoreQueryFilters()` 關鍵測試
- `P3-T3` 補 code generator 產物快照測試
- `P3-T4` 補 sample app restore/build/install regression workflow

### 完成定義

- 每次發版都能驗證核心 runtime、資料層、安裝層與生成層

---

## Phase 4: 框架產品化與採用提升

**目標：** 降低外部採用成本，提升生態吸引力。

### 主要項目

- 文檔入口重構
- Analysis Mode / Observability / Workflow / Security 專題化
- 前端模式選型與示例
- 升級策略與 breaking change policy

### 工作任務

- `P4-T1` 重整文檔首頁
- `P4-T2` 建立 Analysis Mode 教學與 demo
- `P4-T3` 建立 Logging / ProblemDetails / Rate Limiting 綜合 demo
- `P4-T4` 補 React / Vue / LayUI / Blazor 模式對照表
- `P4-T5` 補 upgrade guide 與 breaking change policy

---

## 4. 優先級

### 最高優先

- `P1-T1`
- `P1-T2`
- `P1-T3`
- `P1-T5`
- `P2-T1`
- `P2-T2`
- `P3-T1`
- `P3-T4`

### 第二優先

- `P2-T3`
- `P2-T4`
- `P3-T2`
- `P3-T3`
- `P4-T1`
- `P4-T2`

### 第三優先

- `P4-T3`
- `P4-T4`
- `P4-T5`

---

## 5. 本輪已落地項目

本輪已經完成的第一階段內容：

- GitHub Packages 發佈流程可用
- 一鍵 release 腳本已建立
- release 腳本已補認證檢查與重跑安全
- GitHub Packages 安裝/發佈文檔已建立
- package icon 設定已補齊
- package publish 成功發佈 `8.2.2`

本輪新增待續作項目：

- publish 後 package visibility / install smoke test 自動化
- JS 測試噪音清理
- roadmap 執行狀態持續更新

