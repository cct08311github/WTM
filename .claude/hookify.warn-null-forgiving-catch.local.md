---
name: warn-null-forgiving-catch
enabled: true
event: file
pattern: catch\s*\([^)]*\)\s*\{[^}]*\![\.\[]
action: warn
---

**Warning: null-forgiving operator (!) detected inside catch block**

WTM convention: never use `!` in catch blocks. Use `?.` with fallback instead:
- Bad: `MSD!["key"]` or `Localizer!["msg"]`
- Good: `MSD?["key"] ?? "fallback"` or `Localizer?["msg"] ?? "error"`
