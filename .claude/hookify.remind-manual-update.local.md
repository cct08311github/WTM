---
name: remind-manual-update
enabled: true
event: file
pattern: VersionPrefix
action: warn
---

**Reminder: version.props changed — update the developer manual**

When bumping the version number, the developer manual must also be updated:
1. Run `/wtm-manual-update` to auto-update `docs/wtm-developer-manual.md`
2. Or manually update the version header and relevant sections
3. `/wtm-release-check` will verify this before release
