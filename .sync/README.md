# `.sync/` — Gitea -> GitHub mirror divergence config

> **Gitea-only.** This directory is in `github-excludes.txt` and never appears on the public GitHub mirror.

This repo lives primarily on Gitea (internal). The same `dotnet10` branch is also published to a public GitHub mirror (`github.com/cct08311github/WTM`) so external users can install via GitHub Packages. The two surfaces must look different:

- **Gitea (internal)** — full content with internal hostname, internal registry instructions, AI tooling, sprint planning docs, etc.
- **GitHub (public)** — no internal hostnames, no internal tooling, no planning history, GitHub-Packages-flavored README.

The three files in this directory drive the divergence:

| File | Purpose | Order |
|---|---|---|
| `github-replace.txt` | File swaps. Format: `<source>\t<destination>`. The sync copies `<source>` over `<destination>`, then removes `<source>`. Use for fully-divergent files like the README. | 1 (first) |
| `github-excludes.txt` | Paths to delete from the push tree before pushing to GitHub. Trailing slash = directory. | 2 |
| `github-sanitize.sed` | Sed rules applied to remaining text files. Strips internal hostnames / token names / brand-name references. | 3 (last) |

The sync workflow (`.github/workflows/publish-nuget.yml` -> `Sync to GitHub` job) runs all three in order and only then pushes the resulting tree to GitHub.

## Maintenance

- When adding a new internal-only file, list it in `github-excludes.txt`.
- When a file needs a separate GitHub variant, create `<filename>.github.<ext>` and add a line to `github-replace.txt`.
- When a new internal hostname / token name needs scrubbing, add a rule to `github-sanitize.sed`.

## Why `go-forward` instead of history scrub

Older commits on GitHub `dotnet10` already contain internal hostnames and `Gitea` mentions (from the pre-divergence sync that landed before this manifest existed). We don't force-push to scrub history because:

1. Force-pushing to `dotnet10` violates the global hard rule.
2. The exposed info is private DNS + a user name, not a credential / secret — low actual risk.
3. Force-push on a public repo is visible to watchers and looks suspicious.

The manifest enforces "no NEW exposure" from the date it lands. The one-time cleanup commit (the same PR that lands this manifest) removes the legacy exposure on the current `dotnet10` tip without rewriting history.
