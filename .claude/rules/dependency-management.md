# Dependency & Version Management

## Dependency Upgrades

- Upgrade for security patches and bug fixes — not for new features alone
- Pin to stable releases; avoid preview/RC packages in release branches
- After upgrading: run full test suite, check for behavioural changes in changelogs

## .NET Version Strategy

- Current target: **.NET 8** (LTS)
- Upgrade to next LTS (.NET 10) only after it reaches GA and ecosystem stabilizes
- Multi-target only if there is a concrete user need

## Package Versioning

- All NuGet package versions centralized in `Directory.Build.props` (MSBuild variables)
- Framework version defined in `version.props` (`VersionPrefix`)
- All packages share `common.props`
