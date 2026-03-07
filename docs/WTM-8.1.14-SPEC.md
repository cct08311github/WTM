# WTM 8.1.14 Technical Debt & Stabilization - Claude CLI Execution Spec

## Overview

This document is the **single source of truth** for v8.1.14 technical debt cleanup on the WTM fork (`cct08311github/WTM`, `dotnet8` branch). Designed for Claude CLI automated execution.

**Repository:** https://github.com/cct08311github/WTM
**Branch:** `dotnet8` (base after v8.1.13 merged), create `feature/8.1.14-stabilize` for work
**Prerequisite:** v8.1.13 must be merged into `dotnet8` first
**Current version:** WTM 8.1.13, targeting net8.0

---

## Execution Order (6 tasks, each = 1 git commit)

| Task | ID | Priority | Type | Risk |
|------|----|----------|------|------|
| 1 | ASYNC-1 | HIGH | Code modification (multiple files) | Medium |
| 2 | DEPS-1 | HIGH | Dependency updates | Medium |
| 3 | QUALITY-1 | MEDIUM | New file (.editorconfig) | Low |
| 4 | QUALITY-2 | MEDIUM | csproj modifications | Low-Medium |
| 5 | TEST-1 | MEDIUM | New project + test files | Low |
| 6 | CI-UPDATE | LOW | CI config update | Low |

Before starting:
```bash
git checkout dotnet8 && git pull origin dotnet8
git checkout -b feature/8.1.14-stabilize
dotnet build WalkingTec.Mvvm.sln -c Release   # confirm baseline
```

---
## GitHub Issues to Create (4 total, create ALL before starting code changes)

### Issue #6
```
Title: [P1-3 cont.] Complete sync-over-async fixes across codebase
Labels: performance, P1
```
Body:
v8.1.13 fixed DoLogin. Remaining ~15 `.Result/.Wait()` calls cause ThreadPool starvation risk under load.

Locations:
- `_WorkflowApiController.cs`: 5x `Request.RedirectCall(...).Result`
- `ApproveActivity.cs`: 5x `CallAPI(...).Result`
- `FrameworkServiceExtension.cs`: `DataInit(...).Wait()`
- `SessionExtension.cs`: `CommitAsync().Wait()`
- Any others found via `grep -rn "\.Result[^s]" src/ --include="*.cs"` and `grep -rn "\.Wait()" src/ --include="*.cs"`

Fix: Convert to async/await. Keep sync wrappers marked [Obsolete] where public API.
Risk: Medium | Effort: 2-3 days

### Issue #7
```
Title: [DEPS] Full dependency audit and update
Labels: dependencies, security
```
Body:
Run `dotnet list WalkingTec.Mvvm.sln package --outdated` and upgrade all packages to latest stable within same major version. Do NOT cross major version boundaries without explicit approval.

Special attention:
- EF Core: upgrade to latest 8.0.x patch
- System.Text.Json: check version alignment
- Any package with known CVEs (check `dotnet list package --vulnerable`)

Risk: Medium | Effort: 1 day

### Issue #8
```
Title: [QUALITY] Add .editorconfig and enable Nullable Reference Types
Labels: quality, code-health
```
Body:
1. Add `.editorconfig` with consistent C# coding standards
2. Enable `<Nullable>enable</Nullable>` starting with Core project
3. Fix resulting warnings (nullable annotations)

Risk: Low-Medium | Effort: 2-3 days

### Issue #9
```
Title: [TEST] Establish unit test project and baseline coverage
Labels: testing, quality
```
Body:
Create xUnit test project targeting critical security code:
- PasswordHashHelper (hash, verify, MD5 migration)
- TokenService (issue, refresh, revoke, reuse detection)
- RefreshTokenEntity (computed properties)

Goal: baseline test coverage on security-critical paths.
Risk: Low | Effort: 2 days

---
## Task 1: Complete Sync-over-Async Fixes [ASYNC-1]

### Step 1: Full scan of remaining violations

Run these commands to find ALL remaining sync-over-async:
```bash
# Find .Result calls (excluding comments, string literals, test files)
grep -rn "\.Result[^s\"]" src/ --include="*.cs" | grep -v "//.*\.Result" | grep -v "/obj/"

# Find .Wait() calls
grep -rn "\.Wait()" src/ --include="*.cs" | grep -v "//.*\.Wait" | grep -v "/obj/"

# Find .GetAwaiter().GetResult() calls (these are OK in [Obsolete] wrappers only)
grep -rn "GetAwaiter().GetResult()" src/ --include="*.cs" | grep -v "/obj/"
```

### Step 2: Fix `_WorkflowApiController`

File: Search with `find src/ -name "*Workflow*" -not -path "*/obj/*"`
(Note: This might be a .txt template file that generates controllers)

For each occurrence of `.Result`:
```csharp
// FIND pattern:
var result = someAsyncCall(...).Result;
// REPLACE WITH:
var result = await someAsyncCall(...);
```

Ensure the containing method signature is changed to `async Task<IActionResult>` if not already.

### Step 3: Fix `ApproveActivity.cs`

File: Search with `find src/ -name "ApproveActivity.cs" -not -path "*/obj/*"`

Same pattern: replace all `.Result` with `await`, change method signatures to async.

### Step 4: Fix `FrameworkServiceExtension.cs`

File: Search with `find src/ -name "FrameworkServiceExtension.cs" -not -path "*/obj/*"`

```csharp
// FIND pattern:
DataInit(...).Wait();
// REPLACE WITH:
await DataInit(...);
```

If `DataInit` is called from a synchronous context (like `Configure`), use this pattern instead:
```csharp
// If in IApplicationBuilder pipeline (sync context), keep as-is but add comment:
// TODO: Convert to IHostedService for proper async startup in future version
DataInit(...).GetAwaiter().GetResult();
```

### Step 5: Fix `SessionExtension.cs`

File: Search with `find src/ -name "SessionExtension.cs" -not -path "*/obj/*"`

```csharp
// FIND pattern:
CommitAsync().Wait();
// REPLACE WITH (if method can be made async):
await CommitAsync();
// OR (if sync context is unavoidable):
// Add [Obsolete] and provide async alternative
```

### Step 6: Fix any remaining violations found in Step 1

Apply the same patterns. For each file:
1. If the caller can be made async -> use await
2. If the caller is in a sync-only context (startup, interface constraint) -> use `.GetAwaiter().GetResult()` with a `// TODO` comment
3. Never use `.Result` or `.Wait()` - these can deadlock

### Verification
```bash
dotnet build WalkingTec.Mvvm.sln -c Release

# Verify no remaining .Result/.Wait() (except [Obsolete] wrappers and startup code):
grep -rn "\.Result[^s\"]" src/ --include="*.cs" | grep -v "/obj/" | grep -v "Obsolete" | grep -v "TODO"
grep -rn "\.Wait()" src/ --include="*.cs" | grep -v "/obj/" | grep -v "Obsolete" | grep -v "TODO"
```

### Commit message:
```
perf: complete sync-over-async fixes across codebase [ASYNC-1]

- WorkflowApiController: convert RedirectCall().Result to await
- ApproveActivity: convert CallAPI().Result to await
- FrameworkServiceExtension: fix DataInit().Wait()
- SessionExtension: fix CommitAsync().Wait()
- Mark unavoidable sync wrappers with TODO for future refactor

Closes #6
```

---
## Task 2: Full Dependency Audit and Update [DEPS-1]

### Step 1: Audit current state

```bash
# List all outdated packages
dotnet list WalkingTec.Mvvm.sln package --outdated

# Check for known vulnerabilities
dotnet list WalkingTec.Mvvm.sln package --vulnerable --include-transitive
```

### Step 2: Upgrade rules

**DO upgrade:**
- All packages within same major version to latest patch/minor
- EF Core 8.x -> latest 8.0.x
- Microsoft.Extensions.* -> latest 8.x
- Any package with CVE advisories

**DO NOT upgrade without asking the project owner:**
- Any package crossing a major version boundary (e.g., 7.x -> 8.x, 2.x -> 3.x)
- Newtonsoft.Json (deep integration, needs separate migration plan)

**DO NOT upgrade at all:**
- Packages that would require .NET 9 (skip per project owner decision)

### Step 3: Apply upgrades

For each `.csproj` file with outdated packages, update the version number.

After EACH package group upgrade, run:
```bash
dotnet build WalkingTec.Mvvm.sln -c Release
```

If build fails after a specific upgrade, revert that one and note it as a known issue.

### Step 4: Verify no new vulnerabilities
```bash
dotnet list WalkingTec.Mvvm.sln package --vulnerable --include-transitive
```

### Commit message:
```
fix(deps): full dependency audit and update to latest stable [DEPS-1]

- Upgraded [list each package: name X.Y.Z -> X.Y.Z]
- No major version boundary crossings
- Verified zero known CVEs in direct dependencies

Closes #7
```

---
## Task 3: Add .editorconfig [QUALITY-1]

### NEW FILE: `.editorconfig` (in repo root)

```ini
# EditorConfig: https://editorconfig.org
root = true

[*]
indent_style = space
indent_size = 4
end_of_line = lf
charset = utf-8
trim_trailing_whitespace = true
insert_final_newline = true

[*.md]
trim_trailing_whitespace = false

[*.{json,yml,yaml}]
indent_size = 2

[*.{csproj,props,targets}]
indent_size = 2

[*.cs]
# Namespace
dotnet_style_namespace_match_folder = true

# Using directives
dotnet_sort_system_directives_first = true
dotnet_separate_import_directive_groups = false

# this. qualification
dotnet_style_qualification_for_field = false:suggestion
dotnet_style_qualification_for_property = false:suggestion
dotnet_style_qualification_for_method = false:suggestion
dotnet_style_qualification_for_event = false:suggestion

# Language keywords vs BCL types
dotnet_style_predefined_type_for_locals_parameters_members = true:suggestion
dotnet_style_predefined_type_for_member_access = true:suggestion

# var preferences
csharp_style_var_for_built_in_types = false:suggestion
csharp_style_var_when_type_is_apparent = true:suggestion
csharp_style_var_elsewhere = false:suggestion

# Expression-level preferences
csharp_style_expression_bodied_methods = when_on_single_line:suggestion
csharp_style_expression_bodied_constructors = false:suggestion
csharp_style_expression_bodied_properties = true:suggestion
csharp_style_expression_bodied_accessors = true:suggestion

# Pattern matching
csharp_style_pattern_matching_over_is_with_cast_check = true:suggestion
csharp_style_pattern_matching_over_as_with_null_check = true:suggestion

# Null checking
csharp_style_throw_expression = true:suggestion
csharp_style_conditional_delegate_call = true:suggestion
dotnet_style_coalesce_expression = true:suggestion
dotnet_style_null_propagation = true:suggestion

# Formatting
csharp_new_line_before_open_brace = all
csharp_new_line_before_else = true
csharp_new_line_before_catch = true
csharp_new_line_before_finally = true
csharp_indent_case_contents = true
csharp_indent_switch_labels = true
csharp_space_after_cast = false
csharp_space_after_keywords_in_control_flow_statements = true

# Naming conventions
dotnet_naming_rule.private_fields_should_be_camel_case.severity = suggestion
dotnet_naming_rule.private_fields_should_be_camel_case.symbols = private_fields
dotnet_naming_rule.private_fields_should_be_camel_case.style = camel_case_prefix

dotnet_naming_symbols.private_fields.applicable_kinds = field
dotnet_naming_symbols.private_fields.applicable_accessibilities = private

dotnet_naming_style.camel_case_prefix.capitalization = camel_case
dotnet_naming_style.camel_case_prefix.required_prefix = _

# Severity overrides - don't break build for style
dotnet_analyzer_diagnostic.category-Style.severity = suggestion
```

### Verification
```bash
dotnet build WalkingTec.Mvvm.sln -c Release
# Should produce no new errors (style rules are suggestion level only)
```

### Commit message:
```
chore: add .editorconfig with C# coding standards [QUALITY-1]

- Consistent indentation, charset, line endings
- C# style rules as suggestions (non-breaking)
- Naming conventions for private fields (_camelCase)

Part of #8
```

---
## Task 4: Enable Nullable Reference Types [QUALITY-2]

### Strategy: Incremental enablement

Enable Nullable in this order (least to most dependents):
1. `WalkingTec.Mvvm.Core` - the foundation
2. Only Core for v8.1.14. Other projects in future versions.

### Step 1: Enable in Core.csproj

File: `src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj`

ADD inside `<PropertyGroup>`:
```xml
    <Nullable>enable</Nullable>
```

### Step 2: Build and assess

```bash
dotnet build src/WalkingTec.Mvvm.Core/ -c Release 2>&1 | grep -c "warning CS86"
```

This will show the count of nullable warnings.

### Step 3: Fix warnings - priority tiers

**Tier 1 - Fix immediately (security-critical files from v8.1.13):**
- `PasswordHashHelper.cs` - add proper null annotations
- `RefreshTokenEntity.cs` - mark nullable properties with `?`
- `ITokenService.cs` - annotate return types

**Tier 2 - Fix if < 50 warnings:**
- Model classes: add `?` to optional properties, `required` to mandatory ones
- Extension methods: add null guards

**Tier 3 - Suppress if > 50 warnings remaining:**
For files that would take excessive effort, add at the top of the file:
```csharp
#nullable disable
```
This preserves the opt-in model without blocking the build.

### Step 4: Ensure zero warnings in new code

After fixing/suppressing, the build must be clean:
```bash
dotnet build WalkingTec.Mvvm.sln -c Release
# Full solution must still build. Nullable warnings are OK in non-Core projects.
# Core project should have zero nullable warnings.
```

### Important: Do NOT enable Nullable in other projects yet
Projects that depend on Core will get warnings from consuming nullable-annotated APIs. That is fine - they will be addressed in future versions.

### Commit message:
```
chore: enable Nullable Reference Types in Core project [QUALITY-2]

- Enable <Nullable>enable</Nullable> in WalkingTec.Mvvm.Core.csproj
- Add null annotations to security-critical files
- Suppress nullable in legacy files pending future cleanup
- Other projects remain nullable-oblivious for now

Closes #8
```

---
## Task 5: Establish Unit Test Project [TEST-1]

### Step 1: Create test project

```bash
cd src/
dotnet new xunit -n WalkingTec.Mvvm.Core.Tests -f net8.0
cd WalkingTec.Mvvm.Core.Tests/
dotnet add reference ../WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj
dotnet add package Moq --version 4.*
dotnet add package FluentAssertions --version 6.*
dotnet add package Microsoft.EntityFrameworkCore.InMemory --version 8.*
```

### Step 2: Add test project to solution

```bash
cd ../../   # back to repo root
dotnet sln WalkingTec.Mvvm.sln add src/WalkingTec.Mvvm.Core.Tests/WalkingTec.Mvvm.Core.Tests.csproj
```

### Step 3: Add InternalsVisibleTo in Core.csproj

File: `src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj`

ADD this ItemGroup:
```xml
  <ItemGroup>
    <InternalsVisibleTo Include="WalkingTec.Mvvm.Core.Tests" />
  </ItemGroup>
```

### Step 4: Create test files

#### NEW FILE: `src/WalkingTec.Mvvm.Core.Tests/PasswordHashHelperTests.cs`

```csharp
using FluentAssertions;
using WalkingTec.Mvvm.Core;
using Xunit;

namespace WalkingTec.Mvvm.Core.Tests
{
    public class PasswordHashHelperTests
    {
        [Fact]
        public void HashPassword_ReturnsNonEmptyString()
        {
            var hash = PasswordHashHelper.HashPassword("test123");
            hash.Should().NotBeNullOrEmpty();
            hash.Should().NotBe("test123");
        }

        [Fact]
        public void HashPassword_EmptyInput_ReturnsEmpty()
        {
            PasswordHashHelper.HashPassword("").Should().BeEmpty();
            PasswordHashHelper.HashPassword(null).Should().BeEmpty();
        }

        [Fact]
        public void HashPassword_DifferentCalls_ProduceDifferentHashes()
        {
            var hash1 = PasswordHashHelper.HashPassword("test123");
            var hash2 = PasswordHashHelper.HashPassword("test123");
            hash1.Should().NotBe(hash2, "PBKDF2 uses random salt");
        }

        [Fact]
        public void VerifyPassword_CorrectPassword_ReturnsSuccess()
        {
            var hash = PasswordHashHelper.HashPassword("myPassword");
            var result = PasswordHashHelper.VerifyPassword(hash, "myPassword");
            result.Should().Be(PasswordVerifyResult.Success);
        }

        [Fact]
        public void VerifyPassword_WrongPassword_ReturnsFailed()
        {
            var hash = PasswordHashHelper.HashPassword("myPassword");
            var result = PasswordHashHelper.VerifyPassword(hash, "wrongPassword");
            result.Should().Be(PasswordVerifyResult.Failed);
        }

        [Fact]
        public void VerifyPassword_NullInputs_ReturnsFailed()
        {
            PasswordHashHelper.VerifyPassword(null, "pw")
                .Should().Be(PasswordVerifyResult.Failed);
            PasswordHashHelper.VerifyPassword("hash", null)
                .Should().Be(PasswordVerifyResult.Failed);
            PasswordHashHelper.VerifyPassword(null, null)
                .Should().Be(PasswordVerifyResult.Failed);
        }

        [Fact]
        public void VerifyPassword_LegacyMD5_ReturnsSuccessRehashNeeded()
        {
            var md5Hash = PasswordHashHelper.ComputeMD5("000000");
            var result = PasswordHashHelper.VerifyPassword(md5Hash, "000000");
            result.Should().Be(PasswordVerifyResult.SuccessRehashNeeded);
        }

        [Fact]
        public void VerifyPassword_LegacyMD5_WrongPassword_ReturnsFailed()
        {
            var md5Hash = PasswordHashHelper.ComputeMD5("000000");
            var result = PasswordHashHelper.VerifyPassword(md5Hash, "111111");
            result.Should().Be(PasswordVerifyResult.Failed);
        }

        [Fact]
        public void IsLegacyMD5Hash_ValidMD5_ReturnsTrue()
        {
            PasswordHashHelper.IsLegacyMD5Hash("670B14728AD9902AECBA32E22FA4F6BD")
                .Should().BeTrue();
        }

        [Fact]
        public void IsLegacyMD5Hash_PBKDF2Hash_ReturnsFalse()
        {
            var pbkdf2 = PasswordHashHelper.HashPassword("test");
            PasswordHashHelper.IsLegacyMD5Hash(pbkdf2).Should().BeFalse();
        }

        [Fact]
        public void IsLegacyMD5Hash_NullOrEmpty_ReturnsFalse()
        {
            PasswordHashHelper.IsLegacyMD5Hash(null).Should().BeFalse();
            PasswordHashHelper.IsLegacyMD5Hash("").Should().BeFalse();
        }

        [Fact]
        public void IsLegacyMD5Hash_WrongLength_ReturnsFalse()
        {
            PasswordHashHelper.IsLegacyMD5Hash("ABC123").Should().BeFalse();
        }

        [Fact]
        public void MigrationFlow_MD5ToNewHash_Verify_Success()
        {
            // Simulate: user had MD5 password, logs in, system upgrades
            var legacyMD5 = PasswordHashHelper.ComputeMD5("secretPassword");

            // Step 1: Verify against legacy hash
            var verifyResult = PasswordHashHelper.VerifyPassword(
                legacyMD5, "secretPassword");
            verifyResult.Should().Be(PasswordVerifyResult.SuccessRehashNeeded);

            // Step 2: Generate new PBKDF2 hash
            var newHash = PasswordHashHelper.HashPassword("secretPassword");

            // Step 3: Verify against new hash
            var verifyNew = PasswordHashHelper.VerifyPassword(
                newHash, "secretPassword");
            verifyNew.Should().Be(PasswordVerifyResult.Success);
        }
    }
}
```

#### NEW FILE: `src/WalkingTec.Mvvm.Core.Tests/RefreshTokenEntityTests.cs`

```csharp
using System;
using FluentAssertions;
using WalkingTec.Mvvm.Core;
using Xunit;

namespace WalkingTec.Mvvm.Core.Tests
{
    public class RefreshTokenEntityTests
    {
        [Fact]
        public void NewToken_IsActive()
        {
            var token = new RefreshTokenEntity
            {
                Token = "test-token",
                ITCode = "admin",
                ExpiresUtc = DateTime.UtcNow.AddDays(7)
            };
            token.IsActive.Should().BeTrue();
            token.IsExpired.Should().BeFalse();
            token.IsRevoked.Should().BeFalse();
        }

        [Fact]
        public void ExpiredToken_IsNotActive()
        {
            var token = new RefreshTokenEntity
            {
                Token = "test-token",
                ITCode = "admin",
                ExpiresUtc = DateTime.UtcNow.AddMinutes(-1)
            };
            token.IsActive.Should().BeFalse();
            token.IsExpired.Should().BeTrue();
        }

        [Fact]
        public void RevokedToken_IsNotActive()
        {
            var token = new RefreshTokenEntity
            {
                Token = "test-token",
                ITCode = "admin",
                ExpiresUtc = DateTime.UtcNow.AddDays(7),
                RevokedUtc = DateTime.UtcNow
            };
            token.IsActive.Should().BeFalse();
            token.IsRevoked.Should().BeTrue();
        }

        [Fact]
        public void NewToken_HasGeneratedID()
        {
            var token = new RefreshTokenEntity();
            token.ID.Should().NotBe(Guid.Empty);
        }

        [Fact]
        public void NewToken_HasCreatedUtcSet()
        {
            var before = DateTime.UtcNow.AddSeconds(-1);
            var token = new RefreshTokenEntity();
            var after = DateTime.UtcNow.AddSeconds(1);
            token.CreatedUtc.Should().BeAfter(before).And.BeBefore(after);
        }
    }
}
```

### Verification
```bash
dotnet build WalkingTec.Mvvm.sln -c Release
dotnet test src/WalkingTec.Mvvm.Core.Tests/ -c Release --verbosity normal
```

All tests must pass. Expected: 17+ tests, 0 failures.

### Commit message:
```
test: add unit test project with PasswordHashHelper and RefreshToken tests [TEST-1]

- New xUnit project: WalkingTec.Mvvm.Core.Tests
- 12 tests for PasswordHashHelper (hash, verify, MD5 migration flow)
- 5 tests for RefreshTokenEntity (active/expired/revoked states)
- Dependencies: FluentAssertions, Moq, EF Core InMemory
- Added InternalsVisibleTo for testing internal methods

Closes #9
```

---
## Task 6: Update CI Pipeline [CI-UPDATE]

### File: `.github/workflows/ci-build.yml`

UPDATE the test step to include test result artifact upload.

FIND:
```yaml
    - run: dotnet test WalkingTec.Mvvm.sln --no-build -c Release --verbosity normal
```
REPLACE WITH:
```yaml
    - name: Test
      run: dotnet test WalkingTec.Mvvm.sln --no-build -c Release --verbosity normal --logger "trx;LogFileName=test-results.trx"

    - name: Upload test results
      uses: actions/upload-artifact@v4
      if: always()
      with:
        name: test-results
        path: "**/test-results.trx"
```

### Commit message:
```
ci: update workflow with test result artifacts [CI-UPDATE]
```

---

## Final Steps After All Tasks Complete

```bash
# Push feature branch
git push origin feature/8.1.14-stabilize

# Create PR
gh pr create \
  --base dotnet8 \
  --title "v8.1.14: Technical Debt Cleanup & Stabilization" \
  --body "## Changes
- Complete sync-over-async fixes (remaining .Result/.Wait() removed)
- Full dependency audit and update
- .editorconfig with C# coding standards
- Nullable Reference Types enabled in Core project
- Unit test project with baseline tests on security-critical code

Closes #6 #7 #8 #9"

# After CI passes and owner confirms, merge
gh pr merge --squash

# Tag release
git checkout dotnet8 && git pull
git tag -a v8.1.14 -m "Stabilization: async fixes, dep updates, nullable, unit tests"
git push origin v8.1.14
```

---

## Notes for Claude CLI Execution

1. **Task 1 (async)** is the most complex - search carefully. Some `.Result` calls are in template `.txt` files that generate controllers. Fix those too.
2. **Task 2 (deps)** - run the audit commands first, then TELL the project owner what you plan to upgrade before doing it. Do NOT upgrade across major versions. Do NOT upgrade to anything requiring .NET 9.
3. **Task 4 (nullable)** - if warnings exceed 100 in Core project, use `#nullable disable` at the top of the worst files and note them for future cleanup. Do NOT let nullable warnings break the build.
4. **Task 5 (tests)** - the `ComputeMD5` method is `internal`, so the test project needs `InternalsVisibleTo`. This is handled in Step 3.
5. Do NOT merge PR automatically - wait for project owner confirmation.
6. If any task fails to build, STOP and report. Do not attempt speculative fixes.
