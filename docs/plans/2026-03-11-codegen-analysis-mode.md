# Code Generator Analysis Mode 整合 — 實作計畫

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 讓 Code Generator 自動產生 Analysis Mode 所需的 `[EnableAnalysis]`、`[Dimension]`、`[Measure]` attribute

**Architecture:** 在現有 Code Generator UI 表格新增 IsDimension/IsMeasure checkbox，ListVM 模板加入 `[EnableAnalysis]`，生成後自動在 Model source file 插入 `[Dimension]`/`[Measure]` attribute

**Tech Stack:** C# / ASP.NET Core 8 / WTM Framework / MSTest

**Branch:** `issue-156-codegen-analysis` from `dotnet8`（單一分支，多個 commit 對應 #156–#159）

**Spec:** `docs/specs/2026-03-11-codegen-analysis-mode-design.md`

---

## Task 1: 資料模型 — FieldInfo + CodeGenListView + CodeGenVM 屬性 (#156)

**Files:**
- Modify: `src/WalkingTec.Mvvm.Mvc/CodeGenVM.cs:3119-3212` (FieldInfo class)
- Modify: `src/WalkingTec.Mvvm.Mvc/CodeGenVM.cs:27-63` (CodeGenVM properties)
- Modify: `src/WalkingTec.Mvvm.Mvc/CodeGenListVM.cs:236-271` (CodeGenListView class)

- [ ] **Step 1: Add IsDimensionField/IsMeasureField to FieldInfo**

In `CodeGenVM.cs`, find the `FieldInfo` class (line 3119). Add two properties after `IsBatchField` (line 3131):

```csharp
public bool IsDimensionField { get; set; }
public bool IsMeasureField { get; set; }
```

- [ ] **Step 2: Add IsDimensionField/IsMeasureField to CodeGenListView**

In `CodeGenListVM.cs`, find `CodeGenListView` class (line 236). Add after `IsBatchField` (line 264):

```csharp
[Display(Name = "Codegen.IsDimensionField")]
public bool IsDimensionField { get; set; }

[Display(Name = "Codegen.IsMeasureField")]
public bool IsMeasureField { get; set; }
```

- [ ] **Step 3: Add EnableAnalysis to CodeGenVM**

In `CodeGenVM.cs`, after `IsApi` property (line 38), add:

```csharp
[Display(Name = "Codegen.EnableAnalysis")]
public bool EnableAnalysis { get; set; }
```

- [ ] **Step 4: Build to verify compilation**

Run: `dotnet build src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj -c Release`
Expected: Build succeeded, 0 errors

- [ ] **Step 5: Commit**

```bash
git add src/WalkingTec.Mvvm.Mvc/CodeGenVM.cs src/WalkingTec.Mvvm.Mvc/CodeGenListVM.cs
git commit -m "feat(codegen): add Analysis Mode data model — EnableAnalysis, IsDimension, IsMeasure properties (Closes #156)"
```

---

## Task 2: UI 表格 — InitGridHeader + 智慧預設 (#156 續)

**Files:**
- Modify: `src/WalkingTec.Mvvm.Mvc/CodeGenListVM.cs:22-35` (InitGridHeader)
- Modify: `src/WalkingTec.Mvvm.Mvc/CodeGenListVM.cs:76-200` (GetSearchQuery smart defaults)

- [ ] **Step 1: Add IsDimension/IsMeasure columns to InitGridHeader**

In `CodeGenListVM.cs` `InitGridHeader()` (line 22), add two columns after `IsBatchField` (line 33):

```csharp
this.MakeGridHeader(x=>x.IsDimensionField,150).SetFormat((entity,val)=>{return getCheckBox($"FieldInfos[{entity.Index}].IsDimensionField",entity.IsDimensionField); }),
this.MakeGridHeader(x=>x.IsMeasureField,150).SetFormat((entity,val)=>{return getCheckBox($"FieldInfos[{entity.Index}].IsMeasureField",entity.IsMeasureField); }),
```

- [ ] **Step 2: Add smart defaults in GetSearchQuery**

In `CodeGenListVM.cs` `GetSearchQuery()`, find where `view.IsFormField = true` / `view.IsListField = true` / `view.IsImportField = true` are set (around line 134-136). After those lines, add the smart default logic:

```csharp
// Analysis Mode smart defaults
if (checktype == typeof(string) || checktype.IsEnum())
{
    view.IsDimensionField = true;
}
else if (checktype == typeof(decimal) || checktype == typeof(int) || checktype == typeof(double)
    || checktype == typeof(float) || checktype == typeof(long) || checktype == typeof(short))
{
    view.IsMeasureField = true;
}
else if (checktype == typeof(DateTime))
{
    view.IsDimensionField = true;
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj -c Release`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/WalkingTec.Mvvm.Mvc/CodeGenListVM.cs
git commit -m "feat(codegen): add IsDimension/IsMeasure UI columns with smart defaults (#156)"
```

---

## Task 3: ListVM 模板 — [EnableAnalysis] 生成 (#157)

**Files:**
- Modify: `src/WalkingTec.Mvvm.Mvc/GeneratorFiles/ListVM.txt`
- Modify: `src/WalkingTec.Mvvm.Mvc/CodeGenVM.cs:868` (ListVM replacement chain)

- [ ] **Step 1: Add placeholders to ListVM.txt**

Current template starts with:
```
using System;
...
using $modelnamespace$;
$othernamespace$

namespace $vmnamespace$
{
    public partial class $classname$ListVM : BasePagedListVM<$classname$_View, $classname$Searcher>
```

Change to:
```
using System;
...
using $modelnamespace$;
$othernamespace$$analysisusing$

namespace $vmnamespace$
{
    $analysisattr$public partial class $classname$ListVM : BasePagedListVM<$classname$_View, $classname$Searcher>
```

Note: `$analysisusing$` on same line as `$othernamespace$` to avoid blank line when empty. `$analysisattr$` before class declaration with newline included in the replacement value.

- [ ] **Step 2: Handle replacements in CodeGenVM.GenerateVM**

In `CodeGenVM.cs`, find the ListVM replacement chain at line 868:
```csharp
rv = rv.Replace("$headers$", headerstring)...
```

After line 868, add the analysis attribute handling:

```csharp
if (EnableAnalysis)
{
    rv = rv.Replace("$analysisusing$", "\nusing WalkingTec.Mvvm.Core.Analysis;\n");
    rv = rv.Replace("$analysisattr$", "[EnableAnalysis]\n    ");
}
else
{
    rv = rv.Replace("$analysisusing$", "");
    rv = rv.Replace("$analysisattr$", "");
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj -c Release`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/WalkingTec.Mvvm.Mvc/GeneratorFiles/ListVM.txt src/WalkingTec.Mvvm.Mvc/CodeGenVM.cs
git commit -m "feat(codegen): ListVM template adds [EnableAnalysis] when analysis enabled (Closes #157)"
```

---

## Task 4: Model 檔案插入 — InjectAnalysisAttributes (#158)

**Files:**
- Modify: `src/WalkingTec.Mvvm.Mvc/CodeGenVM.cs` (new method + call from DoGen)

- [ ] **Step 1: Add InjectAnalysisAttributes method**

In `CodeGenVM.cs`, add this new method before `DoGen()` (before line 389):

```csharp
/// <summary>
/// 在 Model source file 中自動插入 [Dimension] / [Measure] attribute
/// </summary>
public string InjectAnalysisAttributes()
{
    if (!EnableAnalysis) return "";

    var analysisFields = FieldInfos?.Where(x => x.IsDimensionField || x.IsMeasureField).ToList();
    if (analysisFields == null || analysisFields.Count == 0) return "";

    // Find Model source file
    Type modelType = Type.GetType(SelectedModel);
    if (modelType == null) return "Error: Cannot resolve model type.";

    string modelFileName = modelType.Name + ".cs";
    string modelFilePath = FindModelFile(MainDir, modelFileName);
    if (modelFilePath == null)
    {
        return $"Warning: Cannot find {modelFileName}. Please manually add [Dimension]/[Measure] attributes.";
    }

    string content = File.ReadAllText(modelFilePath, Encoding.UTF8);
    string originalContent = content;
    bool modified = false;

    // Add using if missing
    if (!content.Contains("using WalkingTec.Mvvm.Core.Analysis;"))
    {
        // Find last using statement and insert after it
        var lastUsingMatch = System.Text.RegularExpressions.Regex.Match(
            content,
            @"^using [^;]+;\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.RightToLeft);
        if (lastUsingMatch.Success)
        {
            int insertPos = lastUsingMatch.Index + lastUsingMatch.Length;
            content = content.Insert(insertPos, "\nusing WalkingTec.Mvvm.Core.Analysis;");
            modified = true;
        }
    }

    foreach (var field in analysisFields)
    {
        string attrName = field.IsDimensionField ? "Dimension" : "Measure";

        // Skip if attribute already exists on this property
        var alreadyHasAttr = System.Text.RegularExpressions.Regex.IsMatch(
            content,
            @"\[" + attrName + @"[(\]].*\n\s*public\s+\S+\??\s+" + System.Text.RegularExpressions.Regex.Escape(field.FieldName) + @"\s",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        if (alreadyHasAttr) continue;

        // Build attribute string
        string attrStr;
        if (field.IsDimensionField)
        {
            var propType = modelType.GetSingleProperty(field.FieldName)?.PropertyType;
            var underlying = Nullable.GetUnderlyingType(propType) ?? propType;
            if (underlying == typeof(DateTime))
            {
                attrStr = "[Dimension(Hierarchy = DateHierarchy.Month)]";
            }
            else
            {
                attrStr = "[Dimension]";
            }
        }
        else
        {
            attrStr = "[Measure]";
        }

        // Find property declaration and insert attribute before it
        var propPattern = @"(\n)((\s*)\[[\s\S]*?)?((\s*)(public\s+\S+\??\s+" + System.Text.RegularExpressions.Regex.Escape(field.FieldName) + @"\s*\{))";
        var propMatch = System.Text.RegularExpressions.Regex.Match(content, propPattern);
        if (propMatch.Success)
        {
            // Insert attribute on line before property declaration
            string indent = propMatch.Groups[5].Value;
            if (string.IsNullOrEmpty(indent)) indent = propMatch.Groups[3].Value;
            if (string.IsNullOrEmpty(indent)) indent = "        ";
            string insertion = indent + attrStr + "\n";
            int insertPos = propMatch.Groups[4].Index;
            content = content.Insert(insertPos, insertion);
            modified = true;
        }
    }

    if (modified && content != originalContent)
    {
        File.WriteAllText(modelFilePath, content, Encoding.UTF8);
        return $"Analysis attributes injected into {modelFilePath}";
    }

    return "";
}

private string FindModelFile(string startDir, string fileName)
{
    // Search up from MainDir looking for the file
    var dir = new DirectoryInfo(startDir);
    while (dir != null)
    {
        var files = dir.GetFiles(fileName, SearchOption.AllDirectories);
        // Exclude bin/obj directories
        var match = files.FirstOrDefault(f =>
            !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
            !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        if (match != null) return match.FullName;

        dir = dir.Parent;
        // Stop at solution root (don't go above 3 levels)
        if (dir?.Parent?.Parent == null) break;
    }
    return null;
}
```

- [ ] **Step 2: Call InjectAnalysisAttributes from DoGen**

In `CodeGenVM.cs` `DoGen()` method, after the test generation block (line 558, before the closing `}` of DoGen), add:

```csharp
// Inject Analysis Mode attributes into Model source file
if (EnableAnalysis)
{
    InjectAnalysisAttributes();
}
```

- [ ] **Step 3: Add System.Text.RegularExpressions using if missing**

Check if `CodeGenVM.cs` already has `using System.Text.RegularExpressions;`. If not, add it at the top.

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/WalkingTec.Mvvm.Mvc/WalkingTec.Mvvm.Mvc.csproj -c Release`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/WalkingTec.Mvvm.Mvc/CodeGenVM.cs
git commit -m "feat(codegen): auto-inject [Dimension]/[Measure] into Model source file (Closes #158)"
```

---

## Task 5: 測試 (#159)

**Files:**
- Create: `test/WalkingTec.Mvvm.Core.Test/CodeGen/CodeGenAnalysisTests.cs`

- [ ] **Step 1: Create test file with InjectAnalysisAttributes tests**

```csharp
using System;
using System.IO;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WalkingTec.Mvvm.Mvc;

namespace WalkingTec.Mvvm.Core.Test.CodeGen
{
    [TestClass]
    public class CodeGenAnalysisTests
    {
        private string _tempDir;

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "wtm_codegen_test_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }

        [TestMethod]
        public void InjectAnalysisAttributes_Adds_Dimension_To_String_Property()
        {
            // Arrange
            var modelContent = @"using System;
namespace TestApp.Models
{
    public class Student : BasePoco
    {
        public string Name { get; set; }
        public decimal Score { get; set; }
    }
}";
            var modelPath = Path.Combine(_tempDir, "Student.cs");
            File.WriteAllText(modelPath, modelContent);

            var vm = CreateCodeGenVM("Student", _tempDir, new List<FieldInfo>
            {
                new FieldInfo { FieldName = "Name", IsDimensionField = true },
                new FieldInfo { FieldName = "Score", IsMeasureField = true }
            });

            // Act
            vm.InjectAnalysisAttributes();

            // Assert
            var result = File.ReadAllText(modelPath);
            Assert.IsTrue(result.Contains("[Dimension]"));
            Assert.IsTrue(result.Contains("[Measure]"));
            Assert.IsTrue(result.Contains("using WalkingTec.Mvvm.Core.Analysis;"));
        }

        [TestMethod]
        public void InjectAnalysisAttributes_Idempotent_No_Duplicate()
        {
            // Arrange
            var modelContent = @"using System;
using WalkingTec.Mvvm.Core.Analysis;
namespace TestApp.Models
{
    public class Student : BasePoco
    {
        [Dimension]
        public string Name { get; set; }
    }
}";
            var modelPath = Path.Combine(_tempDir, "Student.cs");
            File.WriteAllText(modelPath, modelContent);

            var vm = CreateCodeGenVM("Student", _tempDir, new List<FieldInfo>
            {
                new FieldInfo { FieldName = "Name", IsDimensionField = true }
            });

            // Act
            vm.InjectAnalysisAttributes();

            // Assert
            var result = File.ReadAllText(modelPath);
            var count = result.Split("[Dimension]").Length - 1;
            Assert.AreEqual(1, count, "Should not duplicate [Dimension]");
        }

        [TestMethod]
        public void InjectAnalysisAttributes_EnableAnalysis_False_Does_Nothing()
        {
            var vm = new CodeGenVM { EnableAnalysis = false };
            var result = vm.InjectAnalysisAttributes();
            Assert.AreEqual("", result);
        }

        [TestMethod]
        public void InjectAnalysisAttributes_Missing_File_Returns_Warning()
        {
            var vm = CreateCodeGenVM("NonExistent", _tempDir, new List<FieldInfo>
            {
                new FieldInfo { FieldName = "Name", IsDimensionField = true }
            });

            var result = vm.InjectAnalysisAttributes();
            Assert.IsTrue(result.Contains("Warning") || result.Contains("Cannot find"));
        }

        private CodeGenVM CreateCodeGenVM(string modelName, string mainDir, List<FieldInfo> fields)
        {
            return new CodeGenVM
            {
                EnableAnalysis = true,
                FieldInfos = fields,
                MainDir = mainDir,
                // SelectedModel needs to be set but for file-finding tests we use MainDir directly
                SelectedModel = $"TestApp.Models.{modelName}, TestAssembly"
            };
        }
    }
}
```

Note: These tests may need adjustment based on the actual `InjectAnalysisAttributes` implementation's dependency on `Type.GetType()`. If `Type.GetType()` fails for test types, the test should verify the warning path. The core regex logic can be tested by directly manipulating files.

- [ ] **Step 2: Run tests**

Run: `dotnet test test/WalkingTec.Mvvm.Core.Test/WalkingTec.Mvvm.Core.Test.csproj --filter "FullyQualifiedName~CodeGenAnalysisTests" -c Release --verbosity normal`
Expected: All tests pass

- [ ] **Step 3: Update docs/analysis-mode.md**

Add a section at the end:

```markdown
## Code Generator 整合

WTM Code Generator 支援自動產生 Analysis Mode 所需的 attribute。

### 使用方式

1. 在 Code Generator 頁面勾選 **Enable Analysis**
2. 表格會顯示 **IsDimension** 和 **IsMeasure** 兩欄
3. 系統根據屬性型別自動推薦（string/enum→Dimension、數值→Measure、DateTime→Dimension）
4. 開發者可覆寫預設值
5. 點擊生成後，`[EnableAnalysis]` 自動加到 ListVM，`[Dimension]`/`[Measure]` 自動插入 Model 檔案

### 智慧預設規則

| 屬性型別 | 預設 |
|----------|------|
| `string`, `enum` | Dimension |
| `decimal`, `int`, `double`, `float`, `long` | Measure |
| `DateTime` | Dimension (Hierarchy = Month) |
| 其他 | 無 |
```

- [ ] **Step 4: Run full test suite**

Run: `dotnet test WalkingTec.Mvvm.sln -c Release --verbosity minimal`
Expected: All 425+ tests pass

- [ ] **Step 5: Commit**

```bash
git add test/WalkingTec.Mvvm.Core.Test/CodeGen/CodeGenAnalysisTests.cs docs/analysis-mode.md
git commit -m "test(codegen): add Analysis Mode code gen tests + update docs (Closes #159)"
```

---

## Task 6: Push + PR

- [ ] **Step 1: Push branch**

```bash
git push origin issue-156-codegen-analysis
```

- [ ] **Step 2: Create PR**

```bash
gh pr create --title "feat(codegen): Code Generator Analysis Mode integration" --base dotnet8 --body "..."
```

Link issues: Closes #156, #157, #158, #159

- [ ] **Step 3: Wait for CI, merge, close issues**
