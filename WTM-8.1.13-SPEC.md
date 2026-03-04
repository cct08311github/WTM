# WTM 8.1.13 Security Patches - Claude CLI Execution Spec

## Overview

This document is the **single source of truth** for implementing P0/P1 security and quality fixes on the WTM fork (`cct08311github/WTM`, `dotnet8` branch). Designed for Claude CLI automated execution.

**Repository:** https://github.com/cct08311github/WTM
**Branch:** `dotnet8` (base), create `feature/8.1.13-security` for work
**Current version:** WTM 8.1.12, targeting net8.0

---

## Execution Order (7 tasks, each = 1 git commit)

| Task | ID | Priority | Type | Risk |
|------|----|----------|------|------|
| 1 | P1-1 | HIGH | Dependency removal | Low |
| 2 | P1-2 | HIGH | Dependency replacement | Medium |
| 3 | P0-1a | CRITICAL | New file | Low |
| 4 | P0-1b | CRITICAL | Code modification (multiple files) | Medium |
| 5 | P0-2 | CRITICAL | New files + code modification | Medium |
| 6 | P1-3 | HIGH | Code modification | Medium |
| 7 | CI | INFRA | New file | Low |

Before starting: `git checkout dotnet8 && git checkout -b feature/8.1.13-security`

---
## GitHub Issues to Create (5 total, create ALL before starting code changes)

### Issue #1
```
Title: [P1-1] Remove legacy .NET Core 2.1.x dependencies
Labels: security, dependencies, P1
```
Body: Three NuGet packages reference .NET Core 2.1.x (EOL Aug 2021): Microsoft.AspNetCore.Http 2.1.34 (Core.csproj), Microsoft.AspNetCore.Mvc.ViewFeatures 2.1.3 (TagHelpers.LayUI.csproj), Microsoft.AspNetCore.Razor.Runtime 2.1.2 (TagHelpers.LayUI.csproj), System.IO.FileSystem 4.3.0 (unnecessary polyfill). All types included in .NET 8 shared framework. Replace with FrameworkReference. Risk: Low | Effort: 0.5 day

### Issue #2
```
Title: [P1-2] Replace abandoned DotNetCore.NPOI with official NPOI 2.7.6
Labels: security, dependencies, P1
```
Body: DotNetCore.NPOI 1.2.3 is abandoned fork with no security patches. Official NPOI actively maintained at 2.7.x. API compatible, same namespaces. Risk: Medium | Effort: 1-2 days

### Issue #3
```
Title: [P0-1][CRITICAL] Replace MD5 password hashing with PBKDF2
Labels: security, critical, P0
```
Body: Login uses bare Utils.GetMD5String(password) - unsalted MD5. Vulnerable to rainbow table attacks. Fails CISSP/SOC2 compliance. Fix: Add PasswordHashHelper with PBKDF2, transparent verify-and-upgrade migration. DB migration: ALTER TABLE FrameworkUser ALTER COLUMN Password NVARCHAR(256). Risk: Medium | Effort: 2-3 days

### Issue #4
```
Title: [P0-2][CRITICAL] Implement JWT refresh token with rotation
Labels: security, critical, P0
```
Body: TokenService.cs returns RefreshToken = "". No refresh mechanism. Fix: Add RefreshTokenEntity, implement rotation with reuse detection, expand ITokenService, add API endpoints. DB migration: CREATE TABLE FrameworkRefreshTokens. Risk: Low (additive) | Effort: 2-3 days

### Issue #5
```
Title: [P1-3] Fix sync-over-async patterns (ThreadPool starvation)
Labels: performance, P1
```
Body: ~20+ .Result/.Wait() on async methods. Highest risk: DoLogin() on every auth request. Key locations: WTMContext.cs DoLogin (6x), _WorkflowApiController.cs (5x), ApproveActivity.cs (5x). Fix: Add DoLoginAsync, keep sync wrapper [Obsolete]. Risk: Medium | Effort: 2-3 days

---
## Task 1: Remove Legacy .NET Core 2.1.x Dependencies [P1-1]

### File: `src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj`

DELETE this line:
```xml
    <PackageReference Include="Microsoft.AspNetCore.Http" Version="2.1.34" />
```

ADD this ItemGroup (after existing PackageReference ItemGroup):
```xml
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
```

### File: `src/WalkingTec.Mvvm.TagHelpers.LayUI/WalkingTec.Mvvm.TagHelpers.LayUI.csproj`

DELETE these 3 lines:
```xml
    <PackageReference Include="Microsoft.AspNetCore.Mvc.ViewFeatures" Version="2.1.3" />
    <PackageReference Include="Microsoft.AspNetCore.Razor.Runtime" Version="2.1.2" />
    <PackageReference Include="System.IO.FileSystem" Version="4.3.0" />
```

REPLACE the now-empty ItemGroup with:
```xml
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
```

### Verify: `dotnet build WalkingTec.Mvvm.sln -c Release`

### Commit message:
```
fix(deps): remove legacy .NET Core 2.1.x dependencies [P1-1]

- Remove Microsoft.AspNetCore.Http 2.1.34 from Core.csproj
- Remove Mvc.ViewFeatures 2.1.3, Razor.Runtime 2.1.2, System.IO.FileSystem 4.3.0 from TagHelpers.LayUI.csproj
- Add FrameworkReference to Microsoft.AspNetCore.App (both projects)
Closes #1
```

---

## Task 2: Replace DotNetCore.NPOI [P1-2]

### File: `src/WalkingTec.Mvvm.Core/WalkingTec.Mvvm.Core.csproj`

REPLACE:
```xml
    <PackageReference Include="DotNetCore.NPOI" Version="1.2.3" />
```
WITH:
```xml
    <PackageReference Include="NPOI" Version="2.7.6" />
```

Also check: `grep -rn "using DotNetCore" src/ --include="*.cs"` - if any found, change to `using NPOI`.

### Verify: `dotnet build WalkingTec.Mvvm.sln -c Release`

### Commit message:
```
fix(deps): replace abandoned DotNetCore.NPOI with official NPOI 2.7.6 [P1-2]
Closes #2
```

---
## Task 3: Add PasswordHashHelper [P0-1a]

### NEW FILE: `src/WalkingTec.Mvvm.Core/PasswordHashHelper.cs`

```csharp
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;

namespace WalkingTec.Mvvm.Core
{
    public static class PasswordHashHelper
    {
        private static readonly PasswordHasher<string> _hasher = new();
        private static readonly Regex _md5Pattern =
            new(@"^[0-9A-F]{32}$", RegexOptions.Compiled);

        public static string HashPassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return string.Empty;
            return _hasher.HashPassword(string.Empty, password);
        }

        public static PasswordVerifyResult VerifyPassword(
            string storedHash, string password)
        {
            if (string.IsNullOrEmpty(storedHash) ||
                string.IsNullOrEmpty(password))
                return PasswordVerifyResult.Failed;

            if (IsLegacyMD5Hash(storedHash))
            {
                var md5 = ComputeMD5(password);
                return string.Equals(storedHash, md5, StringComparison.Ordinal)
                    ? PasswordVerifyResult.SuccessRehashNeeded
                    : PasswordVerifyResult.Failed;
            }

            var result = _hasher.VerifyHashedPassword(
                string.Empty, storedHash, password);
            return result switch
            {
                PasswordVerificationResult.Success
                    => PasswordVerifyResult.Success,
                PasswordVerificationResult.SuccessRehashNeeded
                    => PasswordVerifyResult.SuccessRehashNeeded,
                _ => PasswordVerifyResult.Failed
            };
        }

        public static bool IsLegacyMD5Hash(string hash)
        {
            if (string.IsNullOrEmpty(hash) || hash.Length != 32) return false;
            return _md5Pattern.IsMatch(hash);
        }

        internal static string ComputeMD5(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            byte[] buffer = Encoding.UTF8.GetBytes(input);
            byte[] hash = MD5.HashData(buffer);
            var sb = new StringBuilder(32);
            foreach (byte b in hash) sb.Append(b.ToString("X2"));
            return sb.ToString();
        }
    }

    public enum PasswordVerifyResult
    {
        Failed = 0,
        Success = 1,
        SuccessRehashNeeded = 2
    }
}
```

### Verify: `dotnet build src/WalkingTec.Mvvm.Core/ -c Release`

### Commit message:
```
feat(security): add PasswordHashHelper with PBKDF2 and MD5 migration [P0-1a]
Part of #3
```

---
## Task 4: Apply Password Hashing to Existing Code [P0-1b]

### 4a. File: `src/WalkingTec.Mvvm.Core/Utils.cs` (line ~757)

Add [Obsolete] to GetMD5String:
```csharp
// FIND:
        public static string GetMD5String(string str)
// REPLACE WITH:
        [Obsolete("Use PasswordHashHelper.HashPassword(). MD5 is broken for passwords.")]
        public static string GetMD5String(string str)
```

Fix GetMD5Stream memory bomb (line ~775):
```csharp
// FIND entire method:
        public static string GetMD5Stream(Stream stream)
        {
            byte[] buffer = new byte[stream.Length];
            stream.Read(buffer, 0, buffer.Length);
            return MD5String(buffer);
        }
// REPLACE WITH:
        public static string GetMD5Stream(Stream stream)
        {
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(stream);
            var sb = new StringBuilder(32);
            foreach (byte b in hash)
                sb.Append(b.ToString("X2"));
            return sb.ToString();
        }
```

### 4b. File: `src/WalkingTec.Mvvm.Core/WTMContext.cs` (line ~444-449)

FIND (in else branch where IsAuthenticated is false):
```csharp
                else
                {
                    exist = BaseUserQuery.IgnoreQueryFilters().Any(x => x.ITCode == username && x.Password == Utils.GetMD5String(password) && x.TenantCode == tenant && x.IsValid==true);
                }
```

REPLACE WITH:
```csharp
                else
                {
                    var userEntity = BaseUserQuery.IgnoreQueryFilters()
                        .Where(x => x.ITCode == username
                                 && x.TenantCode == tenant
                                 && x.IsValid == true)
                        .Select(x => new { x.ID, x.Password })
                        .FirstOrDefault();

                    if (userEntity != null)
                    {
                        var verifyResult = PasswordHashHelper.VerifyPassword(
                            userEntity.Password, password);

                        if (verifyResult == PasswordVerifyResult.Failed)
                        {
                            exist = false;
                        }
                        else
                        {
                            exist = true;
                            if (verifyResult == PasswordVerifyResult.SuccessRehashNeeded)
                            {
                                try
                                {
                                    var newHash = PasswordHashHelper.HashPassword(password);
                                    var dbUser = DC.Set<FrameworkUser>()
                                        .IgnoreQueryFilters()
                                        .FirstOrDefault(x => x.ID == userEntity.ID);
                                    if (dbUser != null)
                                    {
                                        dbUser.Password = newHash;
                                        DC.SaveChanges();
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    else
                    {
                        exist = false;
                    }
                }
```

### 4c. Demo ViewModel Changes

Run: `grep -rn "Utils.GetMD5String" demo/ --include="*.cs"` to find ALL occurrences.

Standard replacement (apply to ALL demo projects - BlazorDemo, Demo, ReactDemo, Vue3Demo, VueDemo):

| File pattern | Before | After |
|---|---|---|
| AccountController.cs | `Utils.GetMD5String(regInfo.Password)` | `PasswordHashHelper.HashPassword(regInfo.Password)` |
| DataContext.cs | `Utils.GetMD5String("000000")` | `PasswordHashHelper.HashPassword("000000")` |
| FrameworkTenantVM.cs | `Utils.GetMD5String("000000")` | `PasswordHashHelper.HashPassword("000000")` |
| FrameworkUserImportVM.cs | `Utils.GetMD5String(item.Password)` | `PasswordHashHelper.HashPassword(item.Password)` |
| FrameworkUserVM.cs (2x) | `Utils.GetMD5String(Entity.Password)` | `PasswordHashHelper.HashPassword(Entity.Password)` |

Special: ChangePasswordVM.cs line ~32:
```csharp
// FIND:
            if (DC.Set<FrameworkUser>().Where(x => x.ITCode == LoginUserInfo.ITCode && x.Password == Utils.GetMD5String(OldPassword)).SingleOrDefault() == null)
// REPLACE WITH:
            var currentUser = DC.Set<FrameworkUser>().Where(x => x.ITCode == LoginUserInfo.ITCode).SingleOrDefault();
            if (currentUser == null || PasswordHashHelper.VerifyPassword(currentUser.Password, OldPassword) == PasswordVerifyResult.Failed)
```

ChangePasswordVM.cs line ~47:
```csharp
// FIND:
                user.Password = Utils.GetMD5String(NewPassword);
// REPLACE WITH:
                user.Password = PasswordHashHelper.HashPassword(NewPassword);
```

### Verify: `dotnet build WalkingTec.Mvvm.sln -c Release && dotnet test WalkingTec.Mvvm.sln -c Release`

### Commit message:
```
feat(security): apply PBKDF2 password hashing across codebase [P0-1b]
Closes #3
```

---
## Task 5: JWT Refresh Token [P0-2]

### 5a. NEW FILE: `src/WalkingTec.Mvvm.Core/Models/RefreshTokenEntity.cs`

```csharp
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WalkingTec.Mvvm.Core
{
    [Table("FrameworkRefreshTokens")]
    public class RefreshTokenEntity
    {
        [Key]
        public Guid ID { get; set; } = Guid.NewGuid();
        [Required, StringLength(256)]
        public string Token { get; set; }
        [Required, StringLength(50)]
        public string ITCode { get; set; }
        [StringLength(50)]
        public string TenantCode { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        [StringLength(50)]
        public string CreatedByIp { get; set; }
        public DateTime? RevokedUtc { get; set; }
        [StringLength(50)]
        public string RevokedByIp { get; set; }
        [StringLength(256)]
        public string ReplacedByToken { get; set; }
        [StringLength(100)]
        public string RevokeReason { get; set; }
        [NotMapped]
        public bool IsExpired => DateTime.UtcNow >= ExpiresUtc;
        [NotMapped]
        public bool IsRevoked => RevokedUtc != null;
        [NotMapped]
        public bool IsActive => !IsRevoked && !IsExpired;
    }
}
```

### 5b. REPLACE FILE: `src/WalkingTec.Mvvm.Core/Auth/ITokenService.cs`

Note: The file might be at a different path. Search: `find src -name "ITokenService.cs" -not -path "*/obj/*"`

```csharp
using System.Threading.Tasks;
using WalkingTec.Mvvm.Core.Support.Json;

namespace WalkingTec.Mvvm.Core
{
    public interface ITokenService
    {
        Task<Token> IssueTokenAsync(LoginUserInfo loginUserInfo, string ipAddress = null);
        Task<Token> RefreshTokenAsync(string refreshToken, string ipAddress = null);
        Task RevokeTokenAsync(string refreshToken, string ipAddress = null, string reason = null);
    }
}
```

### 5c. REPLACE FILE: `src/WalkingTec.Mvvm.Mvc/Auth/JwtAuth/TokenService.cs`

Full replacement file content is provided in the release package at `new-files/TokenService.cs`. The key changes are:
- Constructor adds `IServiceProvider sp` parameter
- `IssueTokenAsync` now creates and persists a RefreshTokenEntity
- New `RefreshTokenAsync` with rotation and reuse detection
- New `RevokeTokenAsync` for logout
- `RefreshToken` field now returns actual cryptographic token instead of ""

### 5d. Add DbSet to DataContext

Find DataContext files: `grep -rn "DbSet<FrameworkUser>" demo/ src/ --include="*.cs"`

In each DataContext class, add:
```csharp
        public DbSet<RefreshTokenEntity> FrameworkRefreshTokens { get; set; }
```

### 5e. Add API endpoints to AccountController

In each demo's AccountController (find: `grep -rn "loginjwt" demo/ --include="*.cs" -l`), add RefreshToken and RevokeToken endpoints. See release package for full code.

### Verify: `dotnet build WalkingTec.Mvvm.sln -c Release`

### Commit message:
```
feat(security): implement JWT refresh token with rotation [P0-2]
Closes #4
```

---

## Task 6: Async DoLogin [P1-3]

Add `DoLoginAsync` to WTMContext.cs. It's identical to DoLogin but all `.Result` become `await` and all `.Wait()` become `await`.

Keep existing `DoLogin` as deprecated wrapper:
```csharp
        [Obsolete("Use DoLoginAsync to avoid ThreadPool starvation.")]
        public LoginUserInfo DoLogin(string username, string password, string tenant)
            => DoLoginAsync(username, password, tenant).GetAwaiter().GetResult();
```

Update all callers: `grep -rn "\.DoLogin(" demo/ src/ --include="*.cs"` - change to `await Wtm.DoLoginAsync(...)`.

### Commit message:
```
perf: convert DoLogin to async [P1-3]
Closes #5
```

---

## Task 7: CI Pipeline

### NEW FILE: `.github/workflows/ci-build.yml`

```yaml
name: WTM Build and Test
on:
  push:
    branches: [ dotnet8, "feature/**" ]
  pull_request:
    branches: [ dotnet8 ]
env:
  DOTNET_VERSION: '8.0.x'
  SOLUTION_FILE: 'WalkingTec.Mvvm.sln'
jobs:
  build-and-test:
    runs-on: ubuntu-latest
    steps:
    - uses: actions/checkout@v4
    - uses: actions/setup-dotnet@v4
      with:
        dotnet-version: 8.0.x
    - run: dotnet restore WalkingTec.Mvvm.sln
    - run: dotnet build WalkingTec.Mvvm.sln --no-restore -c Release
    - run: dotnet test WalkingTec.Mvvm.sln --no-build -c Release --verbosity normal
  security-scan:
    runs-on: ubuntu-latest
    needs: build-and-test
    steps:
    - uses: actions/checkout@v4
    - uses: actions/setup-dotnet@v4
      with:
        dotnet-version: 8.0.x
    - run: dotnet list WalkingTec.Mvvm.sln package --vulnerable --include-transitive
```

### Commit: `ci: add GitHub Actions workflow`

---

## DB Migration (run BEFORE deploying code)

```sql
ALTER TABLE [FrameworkUser] ALTER COLUMN [Password] NVARCHAR(256) NOT NULL;

CREATE TABLE [FrameworkRefreshTokens] (
    [ID] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY DEFAULT NEWID(),
    [Token] NVARCHAR(256) NOT NULL,
    [ITCode] NVARCHAR(50) NOT NULL,
    [TenantCode] NVARCHAR(50) NULL,
    [ExpiresUtc] DATETIME2 NOT NULL,
    [CreatedUtc] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [CreatedByIp] NVARCHAR(50) NULL,
    [RevokedUtc] DATETIME2 NULL,
    [RevokedByIp] NVARCHAR(50) NULL,
    [ReplacedByToken] NVARCHAR(256) NULL,
    [RevokeReason] NVARCHAR(100) NULL
);
CREATE INDEX IX_RefreshToken_Token ON [FrameworkRefreshTokens]([Token]);
CREATE INDEX IX_RefreshToken_ITCode ON [FrameworkRefreshTokens]([ITCode]);
```

## Final: Push, PR, Merge

```bash
git push origin feature/8.1.13-security
gh pr create --base dotnet8 --title "v8.1.13: P0/P1 Security Patches" --body "Closes #1 #2 #3 #4 #5"
# Wait for CI green
gh pr merge --squash
git checkout dotnet8 && git pull
git tag -a v8.1.13 -m "Security: PBKDF2 passwords, JWT refresh, legacy deps, async login"
git push origin v8.1.13
```
