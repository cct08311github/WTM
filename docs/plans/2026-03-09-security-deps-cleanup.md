# Security Dependencies & Cleanup Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix all known .NET dependency vulnerabilities and clean up npm demo app alerts.

**Architecture:** Three independent issues, each with its own branch/PR. No code logic changes — only dependency version bumps and `npm audit fix`.

**Tech Stack:** .NET 8, NuGet, npm

---

## Task 1: Fix .NET dependency vulnerabilities (P0)

**Issue title:** `fix(deps): upgrade System.Text.Json and ImageSharp to fix High vulnerabilities`

**Context:**
- `System.Text.Json 8.0.0` — 2× High (GHSA-hh2w-p6rv-4g7w, GHSA-8g4q-xg66-9fp4), transitive from EF Core/ASP.NET Core
- `SixLabors.ImageSharp 3.1.3` — 2× High + 4× Moderate, transitive from `DUWENINK.Captcha 0.7.0`
- `DUWENINK.Captcha` only supports .NET 8 up to v0.8.0 (0.9.0+ requires .NET 9/10)

**Files:**
- Modify: `src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj`
- Modify: `src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj`

**Step 1: Create GitHub issue**

```bash
gh issue create --title "fix(deps): upgrade System.Text.Json and ImageSharp to fix High vulnerabilities" \
  --body "## Problem
- System.Text.Json 8.0.0: 2× High CVEs (transitive from EF Core)
- SixLabors.ImageSharp 3.1.3: 2× High + 4× Moderate CVEs (transitive from DUWENINK.Captcha 0.7.0)

## Fix
- Add direct \`System.Text.Json 8.0.6\` to Core csproj (overrides transitive 8.0.0)
- Upgrade \`DUWENINK.Captcha\` 0.7.0 → 0.8.0 (last .NET 8 compatible version)
- Add direct \`SixLabors.ImageSharp 3.1.12\` to Mvc csproj (overrides transitive 3.1.5 from Captcha 0.8.0)

## Risk
Low — only version bumps, no API changes. Full test suite validates." \
  --label "security"
```

**Step 2: Mark in-progress, create branch**

```bash
gh issue edit <N> --add-label "in-progress"
git checkout -b issue-<N>-fix-dotnet-deps dotnet8
```

**Step 3: Add System.Text.Json 8.0.6 to Core csproj**

In `src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj`, add inside `<ItemGroup>` with other PackageReferences:
```xml
<PackageReference Include="System.Text.Json" Version="8.0.6" />
```

**Step 4: Upgrade Captcha + pin ImageSharp in Mvc csproj**

In `src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj`:
- Change `DUWENINK.Captcha` from `0.7.0` to `0.8.0`
- Add direct references to override transitives:
```xml
<PackageReference Include="SixLabors.ImageSharp" Version="3.1.12" />
<PackageReference Include="SixLabors.ImageSharp.Drawing" Version="2.1.7" />
```

**Step 5: Restore and build**

```bash
dotnet restore WalkingTec.Mvvm.sln
dotnet build WalkingTec.Mvvm.sln -c Release
```

Expected: Build succeeds with no errors.

**Step 6: Run vulnerability scan to verify fixes**

```bash
dotnet list WalkingTec.Mvvm.sln package --vulnerable --include-transitive
```

Expected: No vulnerable packages reported for src projects.

**Step 7: Run full test suite**

```bash
dotnet test WalkingTec.Mvvm.sln -c Release --verbosity normal
```

Expected: All tests pass.

**Step 8: Commit**

```bash
git add src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj
git commit -m "fix(deps): upgrade System.Text.Json 8.0.6 and ImageSharp 3.1.12

- System.Text.Json 8.0.0→8.0.6: fixes GHSA-hh2w-p6rv-4g7w, GHSA-8g4q-xg66-9fp4
- DUWENINK.Captcha 0.7.0→0.8.0 (last .NET 8 compatible)
- SixLabors.ImageSharp 3.1.3→3.1.12: fixes 6 CVEs
- SixLabors.ImageSharp.Drawing 2.1.2→2.1.7

Closes #<N>"
```

**Step 9: Push, PR, CI, merge**

Use git-ship skill.

---

## Task 2: Fix npm demo app vulnerabilities (P1)

**Issue title:** `chore(deps): fix npm vulnerabilities in demo apps`

**Context:**
- 176 Dependabot alerts, all in demo app `package-lock.json` files
- 3 demo apps: ReactDemo, VueDemo, Vue3Demo
- These are example/demo apps, not shipped as part of the framework

**Files:**
- Modify: `demo/WalkingTec.Mvvm.ReactDemo/ClientApp/package-lock.json`
- Modify: `demo/WalkingTec.Mvvm.VueDemo/ClientApp/package-lock.json`
- Modify: `demo/WalkingTec.Mvvm.Vue3Demo/ClientApp/package-lock.json`
- Possibly modify: corresponding `package.json` files if major version bumps needed

**Step 1: Create GitHub issue**

```bash
gh issue create --title "chore(deps): fix npm vulnerabilities in demo apps" \
  --body "## Problem
176 Dependabot alerts (12 critical, 70 high) across 3 demo apps:
- ReactDemo, VueDemo, Vue3Demo

All are npm ecosystem vulnerabilities in demo/example apps.

## Fix
Run npm audit fix in each demo app. For critical/high that require major bumps, evaluate case-by-case.

## Risk
Low — demo apps only, not shipped as framework packages." \
  --label "security,chore"
```

**Step 2: Mark in-progress, create branch**

```bash
gh issue edit <N> --add-label "in-progress"
git checkout -b issue-<N>-fix-npm-deps dotnet8
```

**Step 3: Run npm audit fix in each demo app**

```bash
cd demo/WalkingTec.Mvvm.VueDemo/ClientApp && npm ci && npm audit fix && cd -
cd demo/WalkingTec.Mvvm.Vue3Demo/ClientApp && npm ci && npm audit fix && cd -
cd demo/WalkingTec.Mvvm.ReactDemo/ClientApp && npm ci && npm audit fix && cd -
```

**Step 4: Check remaining vulnerabilities**

```bash
cd demo/WalkingTec.Mvvm.VueDemo/ClientApp && npm audit 2>&1 | tail -5 && cd -
cd demo/WalkingTec.Mvvm.Vue3Demo/ClientApp && npm audit 2>&1 | tail -5 && cd -
cd demo/WalkingTec.Mvvm.ReactDemo/ClientApp && npm audit 2>&1 | tail -5 && cd -
```

If critical/high remain, evaluate `npm audit fix --force` or manual package.json updates.

**Step 5: Commit**

```bash
git add demo/
git commit -m "chore(deps): fix npm vulnerabilities in demo apps

Run npm audit fix across ReactDemo, VueDemo, Vue3Demo.

Closes #<N>"
```

**Step 6: Push, PR, CI, merge**

Use git-ship skill.

---

## Task 3: Remove BaseImportVM #nullable disable (P2)

**Note:** Investigation revealed `BaseImportVM.cs` already has `#nullable enable` at line 1. This task may already be complete. Verify by checking if there are any remaining `#nullable disable` files in src/.

**Step 1: Verify**

```bash
grep -rl '#nullable disable' src/
```

If empty → no issue needed, update MEMORY.md to reflect completion.
If files remain → create issue and fix.

**Step 2: Update MEMORY.md**

Remove the "Nullable 現代化（進行中）" section or mark as complete if all src/ files are clean.
