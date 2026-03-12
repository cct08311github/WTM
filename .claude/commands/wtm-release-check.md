Pre-release readiness check for WTM. Run all checks and report status:

1. Read `version.props` to get current VersionPrefix
2. `dotnet build WalkingTec.Mvvm.sln -c Release` — must pass
3. `dotnet test WalkingTec.Mvvm.sln -c Release` — all green
4. Check CHANGELOG.md has an entry matching the current version
5. Check `docs/wtm-developer-manual.md` version header matches current version — if not, flag as FAIL and suggest running `/wtm-manual-update`
6. `git status` — no uncommitted changes
7. `dotnet list WalkingTec.Mvvm.sln package --vulnerable --include-transitive` — no known vulnerabilities
8. Report pass/fail for each check with a summary table
