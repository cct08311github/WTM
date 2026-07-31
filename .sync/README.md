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

The sync workflow (`.github/workflows/publish-nuget.yml` -> `Sync to GitHub` job) runs all three in order and only then pushes the resulting tree to GitHub. It fires only on a release-tag push (`if: startsWith(github.ref, 'refs/tags/')` on every step in that job) — there is no cron/scheduled sync, and a `workflow_dispatch` run (used to cut a Gitea-only pre-release) never reaches these steps at all.

## Invariants (#659)

These properties are enforced by the workflow and must not regress:

- **`github-replace.txt` is linted before it's used.** A dedicated `Sync manifest —
  lint github-replace.txt` step fails the job if any non-comment line is not
  exactly two TAB-separated fields. The replace step's `cut -f1`/`cut -f2` +
  whitespace-fallback parsing means a line accidentally edited with a space
  instead of a tab silently no-ops (matches nothing, replaces nothing) — the
  lint turns that into a hard CI failure instead of a quiet mirror drift.
- **The leak-gate scan is unconditional and runs before ANY push to GitHub.**
  It executes once as its own step (`Sync manifest — leak-gate scan`)
  immediately after the sanitize+commit step and before the first `git push`
  attempt — covering the fast-forward path, which previously had no final
  scan at all. It runs a second time, via the same shared script, after the
  merge-fallback conflict resolution, since a non-conflicting file add from
  `github/dotnet10` can introduce content the pre-push scan never saw. Both
  invocations run the identical script (materialized once into `$RUNNER_TEMP`
  during the "create temp branch" step) so the check can't drift between the
  two call sites.
- **Sanitize and leak-gate coverage is tree-wide, not extension-limited.**
  The sanitize step enumerates every git-tracked file in the push tree with
  a single `git grep -zIl -e '' -- .` call — NUL-terminated (`-z`, safe for
  the two tracked ClientApp demo filenames that contain spaces) and
  filtered to text files (`-I`, binary-safe, mirrors the leak-gate's own
  `git grep -I`) — instead of a fixed extension allowlist. A brand-new file
  *extension* (`.ts`, `.txt`, …) is sanitized and scanned the same as
  `.md`/`.yml`/`.cs` — it can never silently bypass either pass just
  because nobody added its extension to a list. (An earlier revision piped
  `git ls-files -z` through a separate `xargs -0 grep -Il` hop, which
  degraded to newline-delimited, unquoted output and broke on those
  space-containing filenames; the current single `git grep` call keeps the
  whole enumeration NUL-safe and avoids that hop's batching entirely.)
  **Caveat:** this is extension-agnostic, not encoding-agnostic. Both
  passes classify "text" the same way `grep -I` does — by sniffing for a
  NUL byte in the first chunk of the file. A UTF-16-encoded text file
  (which embeds NUL bytes between ASCII characters) or a 0-byte file is
  invisible to both passes regardless of extension — the same blind spot
  the old extension-based sed already had for non-UTF-8 text. Keep new
  internal-only text content UTF-8 (or ASCII) if it needs either pass to
  see it.

## Maintenance

- When adding a new internal-only file, list it in `github-excludes.txt`.
- When a file needs a separate GitHub variant, create `<filename>.github.<ext>` and add a line to `github-replace.txt` — the source and destination MUST be separated by a literal TAB character (see Invariants above; the lint step will reject anything else).
- When a new internal hostname / token name needs scrubbing, add a rule to `github-sanitize.sed`. No need to also update a file-extension list — sanitize coverage is tree-wide (see Invariants above).

## Why `go-forward` instead of history scrub

Older commits on GitHub `dotnet10` already contain internal hostnames and `Gitea` mentions (from the pre-divergence sync that landed before this manifest existed). We don't force-push to scrub history because:

1. Force-pushing to `dotnet10` violates the global hard rule.
2. The exposed info is private DNS + a user name, not a credential / secret — low actual risk.
3. Force-push on a public repo is visible to watchers and looks suspicious.

The manifest enforces "no NEW exposure" from the date it lands. The one-time cleanup commit (the same PR that lands this manifest) removes the legacy exposure on the current `dotnet10` tip without rewriting history.
