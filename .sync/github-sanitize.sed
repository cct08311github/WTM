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

# Issue #678 CRITICAL (PR review, round 1): the `Microsoft.SourceLink.Gitea`
# NuGet package identifier (Directory.Packages.props PackageVersion + common.props
# PackageReference) sits on a word boundary that the catch-all `\bGitea\b` rule
# below would otherwise match — "SourceLink." (non-word char before) ... "Gitea"
# ... `"` (non-word char after). That rewrite produced the INVALID package id
# `Microsoft.SourceLink.internal infrastructure` (embedded space), which broke
# `dotnet restore`/`pack` with NU1017 for every project importing common.props on
# the sanitized tree — i.e. it bricked the public GitHub mirror's restorability on
# the very next sync, and failed the "Re-pack from sanitized source for GitHub
# Packages" release step. Protect the literal package id by swapping it out to a
# placeholder with no "Gitea" substring BEFORE the generic rule runs, then swap it
# back in immediately after. Ordered first so it always runs before line ~27.
s|Microsoft\.SourceLink\.Gitea|Microsoft.SourceLink.__SOURCELINK_PKGID_PLACEHOLDER__|g

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

# Swap-back for the package id protected above. Must run after the bare
# \bGitea\b rule (it does — sed applies -f script rules top-to-bottom per
# line) so the placeholder never survives into the committed sanitized tree.
s|Microsoft\.SourceLink\.__SOURCELINK_PKGID_PLACEHOLDER__|Microsoft.SourceLink.Gitea|g
