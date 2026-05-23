# .NET 10 Package Upgrade & Test Stabilization Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Align all NuGet package versions across the solution after the .NET 10 migration. Ensure Microsoft packages are at 10.0.4, fix third-party version inconsistencies, centralize version variables in MSBuild props, and validate via CI.

**Main Branch:** `dotnet8` (target framework is .NET 10 despite the branch name)

**Constraints:**
- Local SDK is .NET 8.0.418; `global.json` locks to .NET 10 SDK. **All build/test validation must go through CI (GitHub Actions).**
- Only patch/minor upgrades. No major version bumps without explicit justification.
- DB providers (Oracle, MySQL, PostgreSQL) are conservative — skip unless a security patch exists.
- FluentAssertions stays at 6.x (7.x has breaking changes).

**Files involved:**

| File | Role |
|------|------|
| `common.props` | Shared MSBuild props for src projects (currently has NO package version variables) |
| `test/Directory.Build.props` | Test package version variables (already has `$(TestSdkVersion)`, `$(MSTestVersion)`, etc.) |
| `src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj` | Core package references |
| `src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj` | Mvc package references |
| `src/WalkingTec.Mvvm.TagHelpers.LayUI/WalkingTec.Mvvm.TagHelpers.LayUI.csproj` | TagHelpers (no direct packages) |
| `version.props` | Framework version (`VersionPrefix`) — not changed in this plan |
| `CHANGELOG.md` | Version history |

---

## Task 0: Create GitHub Issue & Branch

- [ ] **Step 1: Create issue**

```bash
gh issue create --repo cct08311github/WalkingTec.Mvvm \
  --title "chore(deps): .NET 10 package version alignment & upgrade" \
  --body "$(cat <<'EOF'
## Summary
After the .NET 10 migration, package versions are inconsistent:
- Microsoft.AspNetCore.* packages in Mvc.csproj are at 10.0.3 (10.0.4 available)
- Swashbuckle.AspNetCore.SwaggerUI is 10.1.4 while Swagger/SwaggerGen are 10.1.5
- System.Text.Json is 10.0.0 (10.0.4 available)
- No centralized MSBuild variables for shared package versions in common.props

## Tasks
1. Add MSBuild version variables to common.props
2. Upgrade Microsoft.AspNetCore.* 10.0.3 → 10.0.4
3. Fix Swashbuckle version inconsistency (10.1.4 → 10.1.5)
4. Upgrade System.Text.Json 10.0.0 → 10.0.4
5. Evaluate and upgrade other third-party packages where safe
6. CI validation & CHANGELOG update

## Acceptance Criteria
- All Microsoft packages at 10.0.4
- All Swashbuckle packages at 10.1.5
- common.props has version variables for shared packages
- CI green (build + all tests pass)
EOF
)" \
  --label "chore"
```

Record the issue number (e.g., `#NNN`). Use it for branch name and PR references throughout.

- [ ] **Step 2: Mark issue in-progress**

```bash
gh issue edit NNN --repo cct08311github/WalkingTec.Mvvm --add-label "in-progress"
```

- [ ] **Step 3: Create feature branch**

```bash
cd ~/.openclaw/shared/projects/WTM
git checkout dotnet8
git pull origin dotnet8
git checkout -b chore/issue-NNN-dotnet10-package-upgrade
```

Replace `NNN` with the actual issue number.

---

## Task 1: Centralize Package Version Variables in `common.props`

**Rationale:** `common.props` is imported by all three src projects. Adding MSBuild variables here allows version changes in one place. The test projects already use `test/Directory.Build.props` for this purpose.

- [ ] **Step 1: Add version variables to `common.props`**

**File:** `common.props`

Add a new `<PropertyGroup>` before the closing `</Project>` tag:

```xml
  <!--
    Centralized package versions for src projects.
    Microsoft packages follow the .NET 10 release cadence.
    Third-party packages are pinned to latest stable.
  -->
  <PropertyGroup>
    <!-- Microsoft .NET 10 packages -->
    <MicrosoftExtensionsVersion>10.0.4</MicrosoftExtensionsVersion>
    <EntityFrameworkCoreVersion>10.0.4</EntityFrameworkCoreVersion>
    <AspNetCoreVersion>10.0.4</AspNetCoreVersion>
    <SystemTextJsonVersion>10.0.4</SystemTextJsonVersion>

    <!-- Third-party packages -->
    <SwashbuckleVersion>10.1.5</SwashbuckleVersion>
    <OpenTelemetryVersion>1.15.0</OpenTelemetryVersion>
    <SerilogAspNetCoreVersion>10.0.0</SerilogAspNetCoreVersion>
    <QuartzVersion>3.16.0</QuartzVersion>
    <NPOIVersion>2.7.6</NPOIVersion>
    <ImageSharpVersion>3.1.12</ImageSharpVersion>
    <ImageSharpDrawingVersion>2.1.7</ImageSharpDrawingVersion>
    <AspVersioningVersion>8.1.1</AspVersioningVersion>
  </PropertyGroup>
```

- [ ] **Step 2: Update `WalkingTec.Mvvm.Core.csproj` to use variables**

**File:** `src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj`

Replace hardcoded versions with MSBuild variables:

```xml
  <ItemGroup>
    <PackageReference Include="Aliyun.OSS.SDK.NetCore" Version="2.13.0" />
    <PackageReference Include="BCrypt.Net-Next" Version="4.1.0" />
    <PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="$(MicrosoftExtensionsVersion)" />
    <PackageReference Include="Microsoft.Extensions.Identity.Core" Version="$(AspNetCoreVersion)" />
    <PackageReference Include="NPOI" Version="$(NPOIVersion)" />
    <PackageReference Include="Fare" Version="2.2.1" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="$(EntityFrameworkCoreVersion)" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" Version="$(EntityFrameworkCoreVersion)" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="$(EntityFrameworkCoreVersion)" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="$(EntityFrameworkCoreVersion)" />
    <PackageReference Include="Microsoft.Extensions.Configuration.EnvironmentVariables" Version="$(MicrosoftExtensionsVersion)" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="$(MicrosoftExtensionsVersion)" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="$(MicrosoftExtensionsVersion)" />
    <PackageReference Include="Microsoft.Extensions.Http" Version="$(MicrosoftExtensionsVersion)" />
    <PackageReference Include="Microsoft.Extensions.Localization" Version="$(MicrosoftExtensionsVersion)" />
    <PackageReference Include="Microsoft.Extensions.Logging.Configuration" Version="$(MicrosoftExtensionsVersion)" />
    <PackageReference Include="Microsoft.Extensions.Logging.Console" Version="$(MicrosoftExtensionsVersion)" />
    <PackageReference Include="Microsoft.Extensions.Logging.Debug" Version="$(MicrosoftExtensionsVersion)" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.1" />
    <PackageReference Include="Oracle.EntityFrameworkCore" Version="10.23.60" />
    <PackageReference Include="MySql.EntityFrameworkCore" Version="10.0.1" />
    <PackageReference Include="Quartz" Version="$(QuartzVersion)" />
    <PackageReference Include="System.Text.Json" Version="$(SystemTextJsonVersion)" />
  </ItemGroup>
```

**Note:** DB providers (Npgsql, Oracle, MySql) keep hardcoded versions — they follow their own release cadence and need individual attention.

- [ ] **Step 3: Update `WalkingTec.Mvvm.Mvc.csproj` to use variables**

**File:** `src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj`

Replace hardcoded versions:

```xml
  <ItemGroup>
    <PackageReference Include="DUWENINK.Captcha" Version="0.8.0" />
    <PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="$(OpenTelemetryVersion)" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="$(OpenTelemetryVersion)" />
    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.15.1" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="$(OpenTelemetryVersion)" />
    <PackageReference Include="SixLabors.ImageSharp" Version="$(ImageSharpVersion)" />
    <PackageReference Include="SixLabors.ImageSharp.Drawing" Version="$(ImageSharpDrawingVersion)" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.NewtonsoftJson" Version="$(AspNetCoreVersion)" />
    <PackageReference Include="Asp.Versioning.Mvc" Version="$(AspVersioningVersion)" />
    <PackageReference Include="Asp.Versioning.Mvc.ApiExplorer" Version="$(AspVersioningVersion)" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="$(AspNetCoreVersion)" />
    <PackageReference Include="Microsoft.AspNetCore.DataProtection" Version="$(AspNetCoreVersion)" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation" Version="$(AspNetCoreVersion)" />
    <PackageReference Include="Microsoft.AspNetCore.SpaServices.Extensions" Version="$(AspNetCoreVersion)" />
    <PackageReference Include="Swashbuckle.AspNetCore.Swagger" Version="$(SwashbuckleVersion)" />
    <PackageReference Include="Swashbuckle.AspNetCore.SwaggerGen" Version="$(SwashbuckleVersion)" />
    <PackageReference Include="Swashbuckle.AspNetCore.SwaggerUI" Version="$(SwashbuckleVersion)" />
    <PackageReference Include="VueCliMiddleware" Version="6.0.0" />
    <PackageReference Include="Serilog.AspNetCore" Version="$(SerilogAspNetCoreVersion)" />
  </ItemGroup>
```

**Note:** `OpenTelemetry.Instrumentation.AspNetCore` stays at `1.15.1` (hardcoded) because it legitimately differs from the other OTel packages.

- [ ] **Step 4: Commit**

```bash
git add common.props \
  src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj \
  src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj
git commit -m "$(cat <<'EOF'
chore(deps): centralize package version variables in common.props

Move hardcoded NuGet package versions from individual .csproj files into
MSBuild variables defined in common.props. This enables single-point
version management for Microsoft, EF Core, ASP.NET Core, and major
third-party packages.

DB provider packages (Npgsql, Oracle, MySql) retain hardcoded versions
as they follow independent release cadences.

Closes #NNN (partial)

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Upgrade Microsoft.AspNetCore.* 10.0.3 to 10.0.4

**Rationale:** Five ASP.NET Core packages in Mvc.csproj are at 10.0.3 while the rest of the Microsoft ecosystem is at 10.0.4. After Task 1, these all reference `$(AspNetCoreVersion)` which is already set to `10.0.4`, so this upgrade happens automatically with Task 1's variable substitution.

**Packages affected (all in Mvc.csproj):**

| Package | Before | After |
|---------|--------|-------|
| Microsoft.AspNetCore.Mvc.NewtonsoftJson | 10.0.3 | 10.0.4 |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.3 | 10.0.4 |
| Microsoft.AspNetCore.DataProtection | 10.0.3 | 10.0.4 |
| Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation | 10.0.3 | 10.0.4 |
| Microsoft.AspNetCore.SpaServices.Extensions | 10.0.3 | 10.0.4 |

- [ ] **Step 1: Verify** — After Task 1 commit, confirm the variables resolve correctly. No additional file changes needed; the `$(AspNetCoreVersion)` variable in `common.props` already targets `10.0.4`.

- [ ] **Step 2: Push and trigger CI**

```bash
git push -u origin chore/issue-NNN-dotnet10-package-upgrade
```

Wait for CI to validate that 10.0.3 → 10.0.4 upgrade introduces no regressions:

```bash
# Get the latest workflow run
gh run list --repo cct08311github/WalkingTec.Mvvm --branch chore/issue-NNN-dotnet10-package-upgrade --limit 1
# Watch it
gh run watch <run-id> --repo cct08311github/WalkingTec.Mvvm
```

---

## Task 3: Fix Third-Party Package Inconsistencies

### 3A: Swashbuckle SwaggerUI 10.1.4 → 10.1.5

**Rationale:** `Swashbuckle.AspNetCore.SwaggerUI` is at `10.1.4` while `Swagger` and `SwaggerGen` are at `10.1.5`. After Task 1, all three use `$(SwashbuckleVersion)` = `10.1.5`, fixing the inconsistency automatically.

- [ ] **Verify** — Already handled by Task 1's variable substitution. No additional changes needed.

### 3B: System.Text.Json 10.0.0 → 10.0.4

**Rationale:** `System.Text.Json` in Core.csproj is at `10.0.0` while other Microsoft packages are at `10.0.4`. After Task 1, it uses `$(SystemTextJsonVersion)` = `10.0.4`.

- [ ] **Verify** — Already handled by Task 1's variable substitution. No additional changes needed.

### 3C: Evaluate Additional Third-Party Upgrades (Deferred)

These packages should be **evaluated** but are **not upgraded in this plan** unless a security advisory exists:

| Package | Current | Notes |
|---------|---------|-------|
| Aliyun.OSS.SDK.NetCore | 2.13.0 | Niche SDK; check for security advisories only |
| BCrypt.Net-Next | 4.1.0 | Stable, no known issues |
| NPOI | 2.7.6 | Check if 2.7.7+ exists with bug fixes |
| Fare | 2.2.1 | Stable, rarely updated |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.1 | DB provider — conservative, skip unless security patch |
| Oracle.EntityFrameworkCore | 10.23.60 | DB provider — skip |
| MySql.EntityFrameworkCore | 10.0.1 | DB provider — skip |
| Quartz | 3.16.0 | Check if 3.16.1+ exists; minor only |
| DUWENINK.Captcha | 0.8.0 | Niche; skip |
| OpenTelemetry.* | 1.15.0/1.15.1 | Check if 1.15.2+ exists; minor only |
| SixLabors.ImageSharp | 3.1.12 | Check for security patches |
| SixLabors.ImageSharp.Drawing | 2.1.7 | Follow ImageSharp |
| Asp.Versioning.Mvc | 8.1.1 | Check if 8.1.2+ exists |
| VueCliMiddleware | 6.0.0 | Likely abandoned; skip |
| Serilog.AspNetCore | 10.0.0 | Check if 10.0.1+ exists |

- [ ] **Step 1: Run vulnerability scan in CI** (add to PR description as manual check)

```bash
# This must run in CI since local SDK is .NET 8
# Add a note to the PR to manually verify:
dotnet list WalkingTec.Mvvm.sln package --vulnerable --include-transitive
```

- [ ] **Step 2: Check NuGet for available updates**

For each package above, check NuGet.org or use:
```bash
# Can run locally (doesn't need .NET 10 SDK)
curl -s "https://api.nuget.org/v3-flatcontainer/quartz/index.json" | python3 -m json.tool | tail -5
curl -s "https://api.nuget.org/v3-flatcontainer/serilog.aspnetcore/index.json" | python3 -m json.tool | tail -5
curl -s "https://api.nuget.org/v3-flatcontainer/sixlabors.imagesharp/index.json" | python3 -m json.tool | tail -5
```

- [ ] **Step 3: If safe upgrades found, update variables in `common.props`**

Only update if the new version is a patch/minor with no breaking changes. Commit separately:

```bash
git add common.props
git commit -m "$(cat <<'EOF'
chore(deps): upgrade third-party packages to latest stable

[list specific packages and version changes here]

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 4: Push and verify CI**

```bash
git push
gh run list --repo cct08311github/WalkingTec.Mvvm --branch chore/issue-NNN-dotnet10-package-upgrade --limit 1
gh run watch <run-id> --repo cct08311github/WalkingTec.Mvvm
```

---

## Task 4: CI Validation & CHANGELOG Update

- [ ] **Step 1: Verify all CI checks pass**

```bash
gh run list --repo cct08311github/WalkingTec.Mvvm --branch chore/issue-NNN-dotnet10-package-upgrade --limit 3
```

All runs must show `completed / success`. If any fail, investigate and fix before proceeding.

- [ ] **Step 2: Update CHANGELOG.md**

**File:** `CHANGELOG.md`

Add entry under the appropriate version section (or create a new unreleased section):

```markdown
### Changed
- chore(deps): Centralize NuGet package versions in `common.props` MSBuild variables
- chore(deps): Upgrade Microsoft.AspNetCore.* packages 10.0.3 → 10.0.4
- chore(deps): Align Swashbuckle.AspNetCore.SwaggerUI 10.1.4 → 10.1.5
- chore(deps): Upgrade System.Text.Json 10.0.0 → 10.0.4
```

- [ ] **Step 3: Commit CHANGELOG**

```bash
git add CHANGELOG.md
git commit -m "$(cat <<'EOF'
docs(changelog): add .NET 10 package upgrade entries

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
git push
```

- [ ] **Step 4: Open PR**

```bash
gh pr create --repo cct08311github/WalkingTec.Mvvm \
  --base dotnet8 \
  --title "chore(deps): .NET 10 package version alignment & upgrade" \
  --body "$(cat <<'EOF'
## Summary
- Centralize NuGet package versions in `common.props` MSBuild variables for single-point management
- Upgrade Microsoft.AspNetCore.* packages from 10.0.3 to 10.0.4
- Fix Swashbuckle.AspNetCore.SwaggerUI version inconsistency (10.1.4 → 10.1.5)
- Upgrade System.Text.Json from 10.0.0 to 10.0.4

## Changes
| Area | What |
|------|------|
| `common.props` | Added 12 MSBuild version variables |
| `Core.csproj` | 15 packages now use variables |
| `Mvc.csproj` | 16 packages now use variables |
| Upgrade | 5x ASP.NET Core 10.0.3→10.0.4, 1x SwaggerUI 10.1.4→10.1.5, 1x System.Text.Json 10.0.0→10.0.4 |

## What's NOT changed (intentional)
- DB providers (Npgsql, Oracle, MySql) — independent cadence, conservative upgrade policy
- FluentAssertions — stays at 6.x (7.x has breaking changes)
- Packages with no available patch (Aliyun, BCrypt, Fare, VueCliMiddleware)
- `test/Directory.Build.props` — already centralized, no changes needed

## Test plan
- [ ] CI build passes
- [ ] All .NET tests pass
- [ ] All JS tests pass
- [ ] `dotnet list package --vulnerable` shows no new vulnerabilities

Closes #NNN

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 5: Link PR to issue**

```bash
gh issue comment NNN --repo cct08311github/WalkingTec.Mvvm \
  --body "PR: <paste PR URL here>"
```

---

## Version Variable Reference (After Implementation)

### `common.props` — New Variables

| Variable | Value | Used By |
|----------|-------|---------|
| `$(MicrosoftExtensionsVersion)` | 10.0.4 | Core: Caching.Memory, Configuration.*, Hosting.*, Http, Localization, Logging.* |
| `$(EntityFrameworkCoreVersion)` | 10.0.4 | Core: EF Core InMemory, Relational, Sqlite, SqlServer |
| `$(AspNetCoreVersion)` | 10.0.4 | Core: Identity.Core; Mvc: NewtonsoftJson, JwtBearer, DataProtection, RuntimeCompilation, SpaServices |
| `$(SystemTextJsonVersion)` | 10.0.4 | Core: System.Text.Json |
| `$(SwashbuckleVersion)` | 10.1.5 | Mvc: Swagger, SwaggerGen, SwaggerUI |
| `$(OpenTelemetryVersion)` | 1.15.0 | Mvc: OTel Exporter, Extensions.Hosting, Instrumentation.Http |
| `$(SerilogAspNetCoreVersion)` | 10.0.0 | Mvc: Serilog.AspNetCore |
| `$(QuartzVersion)` | 3.16.0 | Core: Quartz |
| `$(NPOIVersion)` | 2.7.6 | Core: NPOI |
| `$(ImageSharpVersion)` | 3.1.12 | Mvc: SixLabors.ImageSharp |
| `$(ImageSharpDrawingVersion)` | 2.1.7 | Mvc: SixLabors.ImageSharp.Drawing |
| `$(AspVersioningVersion)` | 8.1.1 | Mvc: Asp.Versioning.Mvc, Asp.Versioning.Mvc.ApiExplorer |

### `test/Directory.Build.props` — Existing Variables (No Changes)

| Variable | Value |
|----------|-------|
| `$(TestSdkVersion)` | 17.12.0 |
| `$(MSTestVersion)` | 3.6.4 |
| `$(MoqVersion)` | 4.20.72 |
| `$(FluentAssertionsVersion)` | 6.12.2 |
| `$(CoverletVersion)` | 6.0.4 |

### Hardcoded Versions (Intentional — Not Centralized)

| Package | Version | Reason |
|---------|---------|--------|
| Aliyun.OSS.SDK.NetCore | 2.13.0 | Niche, single consumer |
| BCrypt.Net-Next | 4.1.0 | Single consumer, stable |
| Fare | 2.2.1 | Single consumer, stable |
| Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.1 | DB provider, independent cadence |
| Oracle.EntityFrameworkCore | 10.23.60 | DB provider, independent cadence |
| MySql.EntityFrameworkCore | 10.0.1 | DB provider, independent cadence |
| DUWENINK.Captcha | 0.8.0 | Niche, single consumer |
| OpenTelemetry.Instrumentation.AspNetCore | 1.15.1 | Intentionally differs from base OTel version |
| VueCliMiddleware | 6.0.0 | Likely EOL, single consumer |

---

## Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| ASP.NET Core 10.0.4 introduces regression | Low | Medium | CI runs full test suite; patch versions are backward-compatible |
| SwaggerUI 10.1.5 changes behavior | Low | Low | UI-only change; manual spot-check Swagger page |
| System.Text.Json 10.0.4 serialization change | Low | Medium | Covered by existing tests; STJ patch versions are bug-fix only |
| MSBuild variable resolution breaks | Very Low | High | CI catches immediately; rollback = revert one commit |
| DB provider version mismatch | N/A | N/A | Not touched in this plan |

## Rollback Plan

Each task is a separate commit. To rollback:

```bash
git revert <commit-sha>  # Revert specific task
# OR
git revert HEAD~N..HEAD  # Revert all N commits
git push
```

The MSBuild variable centralization (Task 1) is the most impactful change. If it causes unexpected behavior, revert that single commit to restore hardcoded versions.
