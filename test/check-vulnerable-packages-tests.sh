#!/usr/bin/env bash
# #925 cross-vendor review finding 1: local test harness for
# scripts/check-vulnerable-packages.py -- the JSON-based vulnerability-scan gate that
# replaced the `echo "$VULN_OUTPUT" | grep -q "..."` pipeline in publish-nuget.yml's
# "Local vulnerability scan" step (that pipeline SIGPIPE-false-greened under
# `set -o pipefail` on large output; see the script's own header comment and the PR
# description for the exact repro). This proves the replacement's parsing logic against
# fabricated `dotnet list package --vulnerable --format json` fixtures, including a
# large (~3MB) adversarial one reproducing the exact input class that broke the old code
# -- output captured to a file rather than piped into `grep -q`
# (docs/ci-operations.md pitfall #3).

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT_UNDER_TEST="$ROOT_DIR/scripts/check-vulnerable-packages.py"

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

run_case() {
  local name="$1"
  local fixture="$2"
  local expected_exit="$3"
  echo "[case] $name"
  local status=0
  python3 "$SCRIPT_UNDER_TEST" "$fixture" > "$TMP_DIR/out.txt" 2>&1 || status=$?
  cat "$TMP_DIR/out.txt"
  if [[ "$status" -ne "$expected_exit" ]]; then
    echo "FAIL: $name -- expected exit $expected_exit, got $status" >&2
    exit 1
  fi
  cp "$TMP_DIR/out.txt" "$TMP_DIR/last_out.txt"
}

# ── Case 1: clean scan (real shape captured from a clean `dotnet list ... --vulnerable
# --format json` run -- no project has a 'frameworks' key at all when nothing is
# vulnerable, verified empirically against the dotnet 10.0.x SDK) ───────────────────
cat > "$TMP_DIR/clean.json" <<'EOF'
{
  "version": 1,
  "parameters": "--vulnerable --include-transitive",
  "sources": ["https://api.nuget.org/v3/index.json"],
  "projects": [
    {"path": "/repo/src/A/A.csproj"},
    {"path": "/repo/src/B/B.csproj"}
  ]
}
EOF
run_case "clean scan" "$TMP_DIR/clean.json" 0
grep -q "clean (0 findings" "$TMP_DIR/last_out.txt"

# ── Case 2: a single real vulnerability (shape captured from a scratch project
# referencing Newtonsoft.Json 9.0.1, a known GHSA-5crp-9r3c-p9vr hit) ────────────────
cat > "$TMP_DIR/vulnerable.json" <<'EOF'
{
  "version": 1,
  "parameters": "--vulnerable --include-transitive",
  "sources": ["https://api.nuget.org/v3/index.json"],
  "projects": [
    {
      "path": "/repo/probe.csproj",
      "frameworks": [
        {
          "framework": "net10.0",
          "topLevelPackages": [
            {
              "id": "Newtonsoft.Json",
              "requestedVersion": "9.0.1",
              "resolvedVersion": "9.0.1",
              "vulnerabilities": [
                {"severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-5crp-9r3c-p9vr"}
              ]
            }
          ]
        }
      ]
    }
  ]
}
EOF
run_case "one real vulnerability" "$TMP_DIR/vulnerable.json" 1
grep -q "Newtonsoft.Json 9.0.1" "$TMP_DIR/last_out.txt"

# ── Case 3: large adversarial output (~3MB) with a real vulnerability -- the exact
# input class that SIGPIPE-false-greened the old `echo | grep -q` pipeline. Must still
# be caught (exit 1), proving the new file-based, no-pipe approach is not vulnerable to
# the same class of defect regardless of payload size. ──────────────────────────────
python3 - "$TMP_DIR/large-adversarial.json" <<'PY'
import json
import sys

data = {
    "version": 1,
    "parameters": "--vulnerable --include-transitive",
    "sources": ["https://api.nuget.org/v3/index.json"],
    "projects": [
        {
            "path": "/repo/big/project.csproj",
            "frameworks": [
                {
                    "framework": "net10.0",
                    "topLevelPackages": [
                        {
                            "id": "Some.Padding.Package",
                            "resolvedVersion": "1.0.0",
                            # ~3MB of padding to simulate a very large real-world report.
                            "padding": "x" * (3 * 1024 * 1024),
                        },
                        {
                            "id": "Really.Vulnerable.Pkg",
                            "resolvedVersion": "2.0.0",
                            "vulnerabilities": [
                                {"severity": "High", "advisoryurl": "https://example/adv"}
                            ],
                        },
                    ],
                }
            ],
        }
    ],
}
with open(sys.argv[1], "w") as f:
    json.dump(data, f)
PY
FIXTURE_SIZE=$(wc -c < "$TMP_DIR/large-adversarial.json" | tr -d ' ')
echo "large-adversarial.json is ${FIXTURE_SIZE} bytes"
if [[ "$FIXTURE_SIZE" -lt 3000000 ]]; then
  echo "FAIL: fixture is smaller than expected (~3MB) -- test setup is broken" >&2
  exit 1
fi
run_case "large adversarial output still caught (SIGPIPE-false-green repro input)" "$TMP_DIR/large-adversarial.json" 1
grep -q "Really.Vulnerable.Pkg" "$TMP_DIR/last_out.txt"

# ── Case 4: malformed JSON -- fail closed (exit 2, distinct from 1) ──────────────────
printf '{"projects": [' > "$TMP_DIR/malformed.json"
run_case "malformed JSON fails closed" "$TMP_DIR/malformed.json" 2
grep -q "UNRECOGNISED OUTPUT" "$TMP_DIR/last_out.txt"

# ── Case 5: empty file -- fail closed ────────────────────────────────────────────────
: > "$TMP_DIR/empty.json"
run_case "empty file fails closed" "$TMP_DIR/empty.json" 2

# ── Case 6: valid JSON but missing 'projects' -- fail closed ────────────────────────
echo '{"version": 1}' > "$TMP_DIR/noprojects.json"
run_case "missing 'projects' key fails closed" "$TMP_DIR/noprojects.json" 2

# ── Case 7: non-empty top-level 'problems' -- fail closed even with an empty
# 'projects' list (a scan that did not complete must never read as clean) ───────────
echo '{"version": 1, "problems": ["restore failed"], "projects": []}' > "$TMP_DIR/problems.json"
run_case "non-empty 'problems' field fails closed" "$TMP_DIR/problems.json" 2

# ── Case 8: missing file argument entirely -- fail closed, not a crash ──────────────
echo "[case] missing file argument"
status=0
python3 "$SCRIPT_UNDER_TEST" "$TMP_DIR/does-not-exist.json" > "$TMP_DIR/out8.txt" 2>&1 || status=$?
cat "$TMP_DIR/out8.txt"
if [[ "$status" -ne 2 ]]; then
  echo "FAIL: missing file argument -- expected exit 2, got $status" >&2
  exit 1
fi

echo "check-vulnerable-packages-tests: PASS"
