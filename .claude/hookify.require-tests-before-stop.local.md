---
name: require-tests-before-stop
enabled: true
event: stop
pattern: .*
action: warn
---

**Before finishing: did you run tests?**

Checklist:
- [ ] `dotnet build WalkingTec.Mvvm.sln -c Release` passed?
- [ ] `dotnet test WalkingTec.Mvvm.sln -c Release` all green?
- [ ] If JS changes: `cd test/WalkingTec.Mvvm.Js.Tests && npm test` passed?
- [ ] CHANGELOG.md updated (if version bump)?

If tests were already run and passed, proceed. Otherwise, run them first.
