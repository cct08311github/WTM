---
name: warn-nullable-disable
enabled: true
event: file
pattern: "#nullable disable"
action: warn
---

**Warning: #nullable disable detected in new/edited code**

WTM policy: new files must be fully nullable-annotated. Do not add `#nullable disable` to new code. If editing an existing file that already has it, plan to remove it as part of the nullable modernization effort.
