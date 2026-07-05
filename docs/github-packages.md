# GitHub Packages 安裝與發佈指南

本文件說明 WTM 套件如何發佈到 GitHub Packages，以及其他專案如何從 GitHub Packages 安裝。

目前本 repo 的 NuGet package 來源為：

- Registry: `https://nuget.pkg.github.com/cct08311github/index.json`
- Repository: `https://github.com/cct08311github/WTM`

---

## 1. 套件列表

WTM 目前主要發佈以下三個 package：

- `WalkingTec.Mvvm.Core`
- `WalkingTec.Mvvm.Mvc`
- `WalkingTec.Mvvm.TagHelpers.LayUI`

版本號統一由 [version.props](~/.openclaw/shared/projects/WTM/version.props) 的 `VersionPrefix` 控制。

---

## 2. 安裝前準備

若套件是 private，安裝端必須先準備一個具有 `read:packages` 權限的 GitHub PAT。

### 新增 GitHub Packages source

```bash
dotnet nuget add source \
  --username <GITHUB_USERNAME> \
  --password <PAT_WITH_READ_PACKAGES> \
  --store-password-in-clear-text \
  --name github-wtm \
  "https://nuget.pkg.github.com/cct08311github/index.json"
```

也可以在專案或使用者層級建立 `NuGet.Config`：

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="github-wtm" value="https://nuget.pkg.github.com/cct08311github/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <github-wtm>
      <add key="Username" value="YOUR_GITHUB_USERNAME" />
      <add key="ClearTextPassword" value="YOUR_PAT_WITH_READ_PACKAGES" />
    </github-wtm>
  </packageSourceCredentials>
</configuration>
```

---

## 3. 安裝套件

安裝指定版本：

```bash
dotnet add package WalkingTec.Mvvm.Core --version 8.2.1
dotnet add package WalkingTec.Mvvm.Mvc --version 8.2.1
dotnet add package WalkingTec.Mvvm.TagHelpers.LayUI --version 8.2.1
```

如果你只依賴 MVC 入口，通常會由 `WalkingTec.Mvvm.Mvc` 連帶帶入其他相依；是否單獨安裝 `Core` 或 `LayUI`，取決於你的使用方式。

---

## 4. 本機打包

正式版建議先做完整建置與測試：

```bash
dotnet build WalkingTec.Mvvm.sln -c Release
dotnet test WalkingTec.Mvvm.sln -c Release
```

然後打包：

```bash
dotnet pack src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj -c Release -o nupkgs
dotnet pack src/WalkingTec.Mvvm.TagHelpers.LayUI/WalkingTec.Mvvm.TagHelpers.LayUI.csproj -c Release -o nupkgs
dotnet pack src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj -c Release -o nupkgs
```

輸出會在 `nupkgs/`：

- `WalkingTec.Mvvm.Core.<version>.nupkg`
- `WalkingTec.Mvvm.TagHelpers.LayUI.<version>.nupkg`
- `WalkingTec.Mvvm.Mvc.<version>.nupkg`

---

## 5. 版本策略

### 穩定版

直接修改 [version.props](~/.openclaw/shared/projects/WTM/version.props)：

```xml
<VersionPrefix>8.2.1</VersionPrefix>
```

Release 組態下，最終 package version 會直接等於 `VersionPrefix`。

### 預發布版

workflow dispatch 時可帶 `version_suffix`，例如：

- `alpha.20260306.1`
- `beta.1`
- `rc.1`

組合後會變成：

- `8.2.1-alpha.20260306.1`
- `8.2.1-beta.1`
- `8.2.1-rc.1`

---

## 6. 透過 GitHub Actions 發佈

本 repo 已內建 workflow：

- [`.github/workflows/publish-nuget.yml`](~/.openclaw/shared/projects/WTM/.github/workflows/publish-nuget.yml)

它具備以下行為：

- 可手動觸發 `workflow_dispatch`
- 可在 push tag `v*` 時自動觸發
- 使用 `GITHUB_TOKEN` 發佈到 GitHub Packages
- 目標 source 為 `https://nuget.pkg.github.com/cct08311github/index.json`

### 一鍵發佈腳本

repo 內提供：

- [`scripts/release-github-package.sh`](~/.openclaw/shared/projects/WTM/scripts/release-github-package.sh)

穩定版：

```bash
./scripts/release-github-package.sh 8.2.2
```

預發布版：

```bash
./scripts/release-github-package.sh 8.2.2 beta.1
```

預覽但不執行：

```bash
./scripts/release-github-package.sh --dry-run 8.2.2
```

這個腳本會：

1. 修改 [version.props](~/.openclaw/shared/projects/WTM/version.props) 的 `VersionPrefix`
2. 建立 release commit
3. push 到 `origin/dotnet8`
4. 觸發 `publish-nuget.yml`

補充：

- 若 `VersionPrefix` 已經等於目標版本，腳本不會重複 commit，只會直接觸發發佈 workflow
- 腳本會先檢查 `gh auth` 是否已登入
- `--dry-run` 可用於確認版本、commit 與 workflow 參數，不會修改檔案

### 手動發佈穩定版

1. 先把 `VersionPrefix` 改到目標版本，例如 `8.2.1`
2. push 到遠端分支
3. 在 GitHub Actions 手動執行 `Publish NuGet to GitHub Packages`
4. `version_suffix` 留空

### 手動發佈預發布版

1. 保持 `VersionPrefix` 為基礎版本，例如 `8.2.1`
2. 在 workflow dispatch 時填入 `version_suffix`
3. workflow 會自動組合最終版本號

### 以 tag 發佈

也可以使用 tag 觸發：

```bash
git tag v8.2.1
git push origin v8.2.1
```

注意：這個 workflow 目前只用 repo 內的 `VersionPrefix` 與 `version_suffix` 來決定版本號，tag 名稱本身不會覆寫 package version。

---

## 7. 權限需求

### 發佈端

- GitHub Actions：使用 `GITHUB_TOKEN`，workflow 需有 `packages: write`
- 本機發佈：需要具有 `write:packages` 的 PAT

### 安裝端

- private package：需要具有 `read:packages` 的 PAT

---

## 8. 本機直接推送到 GitHub Packages

若要不用 Actions，直接從本機推送：

```bash
dotnet nuget add source \
  --username <GITHUB_USERNAME> \
  --password <PAT_WITH_WRITE_PACKAGES> \
  --store-password-in-clear-text \
  --name github-wtm \
  "https://nuget.pkg.github.com/cct08311github/index.json"
```

```bash
dotnet nuget push "nupkgs/*.nupkg" \
  --source github-wtm \
  --api-key <PAT_WITH_WRITE_PACKAGES> \
  --skip-duplicate
```

---

## 9. 常見問題

### 找不到 package

先確認：

- source 是否已加到 `NuGet.Config`
- PAT 是否有 `read:packages`
- package 是否授權給目前 repo 或使用者

### 已存在相同版本，無法覆蓋

NuGet package version 應視為不可變。若已發過：

- 穩定版請升 patch，例如 `8.2.2`
- 預發布版請改 suffix，例如 `alpha.20260306.2`

### 為什麼 workflow 用 tag 觸發，但版本不是 tag 名稱

因為目前 workflow 的版本來源是 [version.props](~/.openclaw/shared/projects/WTM/version.props) 與 `version_suffix`，不是從 tag 名稱解析。
