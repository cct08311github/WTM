# Sed rules applied to remaining text files during Gitea -> GitHub sync,
# AFTER excludes are removed and replaces are applied.
# Run as: find <push-tree> -type f \( -name '*.md' -o -name '*.yml' -o -name '*.props' -o -name '*.csproj' -o -name '*.json' -o -name '*.cs' \) -exec sed -i -f .sync/github-sanitize.sed {} +
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
