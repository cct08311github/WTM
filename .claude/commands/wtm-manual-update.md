Update the WTM Developer Manual (`docs/wtm-developer-manual.md`) for the current version release.

## Steps

1. **Read current state:**
   - Read `version.props` for current version
   - Read `CHANGELOG.md` for recent changes since last manual update
   - Read the manual's version header line

2. **Identify what changed:**
   - `git log` since last tagged version to find modified source files
   - Categorize changes: new features, API changes, model changes, config changes, bug fixes

3. **Update the manual:**
   For each change category, update the relevant section:
   - **New Model/Attribute** → update Section 3 (Model 層) or Section 3.5 (Attribute 表)
   - **New ViewModel method** → update Section 4 (四種 ViewModel)
   - **New Controller/Route** → update Section 5.4 (路由表)
   - **New TagHelper** → update Section 6.5 (速查表)
   - **Analysis changes** → update Section 7
   - **ETL changes** → update Section 8
   - **Dashboard changes** → update Section 9
   - **Security changes** → update Section 10
   - **Config changes** → update Section 15
   - **New FAQ** → add to Section 16
   - **File path changes** → update Appendix A

4. **Update metadata:**
   - Version number in the manual header
   - "最後更新" date to today

5. **Verify:**
   - All code examples compile conceptually (no broken references)
   - No stale version numbers in examples
   - Section cross-references are valid

6. **Report:**
   - List all sections updated with brief description of changes
   - Flag any sections that may need manual review (e.g., breaking changes that affect examples)
