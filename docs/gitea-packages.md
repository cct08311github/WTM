# Gitea Packages 安裝與發佈指南

本文件說明 WTM 套件如何發佈到 Gitea NuGet registry，以及其他專案如何從 Gitea 安裝。

目前本 repo 的 NuGet package 來源為：

- Registry: `https://mac-mini.tailde842d.ts.net/api/packages/chiu0831/nuget/index.json`
- Repository: `https://mac-mini.tailde842d.ts.net/chiu0831/WTM`

> 自 v10.5.1（2026-05-13）起 GitHub Packages / `cct08311github` mirror 已完全停用。

---

## 1. 套件列表

WTM 主要發佈以下 3 個 package：

- `WalkingTec.Mvvm.Core`
- `WalkingTec.Mvvm.Mvc`
- `WalkingTec.Mvvm.TagHelpers.LayUI`

`WalkingTec.Mvvm.Etl` **不在** publish 清單（內部使用）。

版本號統一由 [version.props](../version.props) 的 `VersionPrefix` 控制。

---

## 2. 安裝前準備

Gitea NuGet 為 private — 安裝端必須先準備一個具有 `read:package` scope 的 Gitea PAT。

> 在 Gitea UI → Settings → Applications → Generate New Token 建立；只勾選 `read:package`。

### 新增 Gitea NuGet source（CLI）

```bash
dotnet nuget add source \
  --username chiu0831 \
  --password <PAT_WITH_READ_PACKAGE> \
  --store-password-in-clear-text \
  --name gitea-wtm \
  "https://mac-mini.tailde842d.ts.net/api/packages/chiu0831/nuget/index.json"
```

### 或在 `NuGet.Config` 設定

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="gitea-wtm" value="https://mac-mini.tailde842d.ts.net/api/packages/chiu0831/nuget/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <gitea-wtm>
      <add key="Username" value="chiu0831" />
      <add key="ClearTextPassword" value="YOUR_GITEA_PAT_WITH_READ_PACKAGE" />
    </gitea-wtm>
  </packageSourceCredentials>
</configuration>
```

---

## 3. 安裝套件

安裝指定版本：

```bash
dotnet add package WalkingTec.Mvvm.Core --version 10.5.1
dotnet add package WalkingTec.Mvvm.Mvc --version 10.5.1
dotnet add package WalkingTec.Mvvm.TagHelpers.LayUI --version 10.5.1
```

通常 `WalkingTec.Mvvm.Mvc` 會連帶帶入其他相依。

---

## 4. 本機打包

正式版建議先做完整建置與測試：

```bash
dotnet build WalkingTec.Mvvm.sln -c Release
dotnet test WalkingTec.Mvvm.sln -c Release --filter "FullyQualifiedName!~Integration.Test"
```

然後打包：

```bash
dotnet pack src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj -c Release -o nupkgs
dotnet pack src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj -c Release -o nupkgs
dotnet pack src/WalkingTec.Mvvm.TagHelpers.LayUI/WalkingTec.Mvvm.TagHelpers.LayUI.csproj -c Release -o nupkgs
```

輸出會在 `nupkgs/`（已 .gitignore）。

---

## 5. 版本策略

### 穩定版

直接修改 [version.props](../version.props)：

```xml
<VersionPrefix>10.5.1</VersionPrefix>
```

Release 組態下，最終 package version 直接等於 `VersionPrefix`。

### 預發布版

`scripts/publish-to-gitea.sh --suffix <pre-release>` 或 `publish-nuget.yml` workflow dispatch 帶 `version_suffix`，例如 `beta.1`、`rc.1`：

```
10.5.1-beta.1
10.5.1-rc.1
```

---

## 6. 透過 Gitea Actions 發佈

本 repo 內建 workflow：

- [`.github/workflows/publish-nuget.yml`](../.github/workflows/publish-nuget.yml)

行為：

- 可手動觸發 `workflow_dispatch`（可帶 `version_suffix`）
- push tag `v*` 時自動觸發
- 使用 repo secret `PAT_TOKEN`（需 `write:package` scope）發佈到 Gitea
- 目標 source 為 Gitea NuGet registry

> Gitea Actions 與 GitHub Actions 語法相容，故仍放在 `.github/workflows/` 目錄。

---

## 7. 本機手動發佈（已停用 —— 僅供預覽）

**`scripts/publish-to-gitea.sh` 的真實發佈路徑已停用（#925 cross-vendor review finding 4）。** 它只 pack 5 個套件中的 3 個（Core/Mvc/TagHelpers.LayUI，永遠不含 WorkFlow/Etl），也不跑 `publish-nuget.yml` 的任何 gate（smoke test、本機 vulnerability scan、version-cohort 檢查）——曾經被本文件推薦為「runner 不可用時的 fallback」，等於官方教人在跳過所有這些檢查的情況下發佈不完整的一批套件。

```bash
./scripts/publish-to-gitea.sh --dry-run                # 預覽穩定版，不執行
./scripts/publish-to-gitea.sh --dry-run --suffix beta.1  # 預覽預發布版，不執行
./scripts/publish-to-gitea.sh                           # 一律拒絕（無 --dry-run）
```

Runner 真的不可用時，優先修好 CI 觸發本身，而不是繞過它發布——見 `docs/ci-operations.md`「⚠️ 發版已知陷阱」一節的 tag-object 去重重建 SOP（`git tag -d` + 重新 `git tag -a` 幾乎都能解決）。

Token 來源（依序，`--dry-run` 仍需要能解析出 token 才會執行到列印預覽）：

1. `GITEA_TOKEN` 環境變數
2. `~/.gitea-token` 檔案 — 用 `grep -oE '[a-f0-9]{40}' ~/.gitea-token` 擷取 40 碼
   hex token，**絕不 `source`**（不論檔案內容是純 token 一行，還是
   `export GITEA_TOKEN='...'` 格式，都能正確擷取；`source` 會讓 shell 把 token
   當指令執行，`command not found: <token>` 會把明文 token 印到終端機/CI log）

需 `write:package` scope。dry-run 輸出 token 已 mask。

---

## 8. 權限需求

| 角色 | 所需 token scope |
|------|-----------------|
| 安裝端（read） | `read:package` |
| 發佈端（write） | `write:package` |
| CI（Gitea Actions） | repo secret `PAT_TOKEN` 含 `write:package` |

---

## 9. 常見問題

### 找不到 package

確認：
- `NuGet.Config` 已加 source
- PAT 有 `read:package` scope
- Tailscale 連線到 `mac-mini.tailde842d.ts.net`（Gitea host 在私網）

### 已存在相同版本，無法覆蓋

NuGet package version 應視為不可變。若已發過：
- 穩定版升 patch（例：`10.5.2`）
- 預發布版改 suffix（例：`alpha.20260513.2`）

`--skip-duplicate` 旗標讓重複 push 不報錯，但不會覆寫。

### push 收到 401 Unauthorized

最常見原因：token scope 不足。

- 安裝端需 `read:package`
- 發佈端需 `write:package`

在 Gitea UI → Settings → Applications 編輯或重新建立 token，勾選正確 scope。

### tag 觸發 publish-nuget.yml 但 workflow 顯示 failure

可能是 `PAT_TOKEN` secret 已過期或 scope 不足。改用 workflow_dispatch 重跑：

```bash
GITEA_TOKEN=$(grep -oE '[a-f0-9]{40}' "$HOME/.gitea-token")   # never `source` it
curl -X POST -H "Authorization: token $GITEA_TOKEN" -H "Content-Type: application/json" \
  -d '{"ref":"refs/tags/v<VERSION>","inputs":{}}' \
  "https://mac-mini.tailde842d.ts.net/api/v1/repos/chiu0831/WTM/actions/workflows/publish-nuget.yml/dispatches"
```

`--skip-duplicate` 會讓重跑對已存在的 packages 跳過。
