# Sed rules applied to remaining text files during Gitea -> GitHub sync,
# AFTER excludes are removed and replaces are applied.
# Run as: git grep -zIl -e '' -- . | xargs -0 sed -i -f .sync/github-sanitize.sed
# (single-process, NUL-safe enumeration — see publish-nuget.yml's
# "Sync manifest — apply github-sanitize.sed and commit" step for why a
# two-hop `git ls-files -z | xargs -0 grep -Il` | `xargs sed -i` pipeline is
# NOT NUL-safe end-to-end and breaks on tracked filenames with spaces.)
# Coverage is tree-wide (every tracked text file, binary-safe via `git grep
# -I`), not a fixed extension allowlist (#659) — a new file type is
# sanitized too.
#
# Targets: internal hostname, tailnet name, macOS username, internal token names.
# Replacement strings are intentionally generic / clearly-placeholder.

s|mac-mini\.tailde842d\.ts\.net|internal.registry.invalid|g
s|tailde842d|internal-tailnet|g
s|/Users/openclaw/|~/|g
s|~/\.gitea-token|.local-token-file|g
s|\bGITEA_TOKEN\b|REGISTRY_TOKEN|g
s|\bGITEA_HOST\b|REGISTRY_HOST|g
s|\bGITEA_USER\b|REGISTRY_USER|g
s|\bGitea Actions\b|internal CI|g
s|\bGitea Packages\b|internal package registry|g
s|\bGitea NuGet\b|internal NuGet|g
s|\bGitea registry\b|internal registry|g
s|\bGitea release\b|release|g
s|\bGitea\b|internal infrastructure|g
s|\bgitea-wtm\b|private-feed|g
