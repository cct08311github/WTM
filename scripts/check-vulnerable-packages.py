#!/usr/bin/env python3
"""#925 cross-vendor review finding 1: the release gate's local vulnerability scan
previously matched a text marker via `echo "$VULN_OUTPUT" | grep -q "..."` under
`set -o pipefail` (.github/workflows/publish-nuget.yml's "Local vulnerability scan"
step). `grep -q` exits the instant it finds the marker and closes its end of the pipe;
if the buffered VULN_OUTPUT is larger than the pipe can drain before grep exits
(reproduced locally with ~3MB of synthetic output -- see the PR description for the
exact repro), `echo` receives SIGPIPE (exit 141) writing the remainder, and bash's
pipefail rule ("the pipeline's exit status is the value of the LAST command to exit
non-zero, or zero if all succeeded") makes the overall pipeline exit 141 even though
grep DID match -- so the `if` guarding the abort path evaluates false and the gate
prints "clean" on a dependency graph that is NOT clean. docs/ci-operations.md pitfall
#3 already documented this exact class of trap (a different manifestation, `grep | head`)
before this bug shipped; this is the same idiom biting a second call site.

FIX SHAPE: no text, no pipe. `dotnet list package --vulnerable --format json` is
redirected straight to a file (a real file descriptor, not a pipe a reader can
prematurely close out from under the writer), and this script parses the JSON
structurally instead of grepping rendered text.

Schema (verified empirically against the dotnet 10.0.x SDK actually installed in this
environment -- both a clean run of WalkingTec.Mvvm.sln and a deliberately vulnerable
scratch project referencing Newtonsoft.Json 9.0.1; see the PR description for the
exact commands used to capture both fixtures):
  {"version": 1, "parameters": ..., "sources": [...], "projects": [
    {"path": ..., "frameworks": [        # "frameworks" key is ABSENT entirely when
                                          # the project has zero vulnerable packages --
                                          # verified, not assumed.
      {"framework": ..., "topLevelPackages": [
        {"id": ..., "resolvedVersion": ..., "vulnerabilities": [
          {"severity": ..., "advisoryurl": ...}, ...
        ]}, ...
      ], "transitivePackages": [ ... same shape ... ]}
    ]}
  ]}

Exit codes:
  0  clean -- zero vulnerabilities found across every project/framework/package.
  1  one or more REAL vulnerabilities found (each printed above the summary line).
  2  fail-closed -- the file could not be read/parsed as JSON, or its shape does not
     match what is documented above (missing/wrong-typed 'projects', a project entry
     that isn't an object, 'frameworks' present but not a list, a non-empty top-level
     'problems'/'errors' field some dotnet SDK versions use to report a scan that did
     not actually complete, etc.). 2 is DELIBERATELY distinct from 0: an unrecognised
     shape must never be reported as clean -- that is the exact failure mode (a false
     "clean") this script exists to replace.
"""
from __future__ import annotations

import json
import sys


def fail_closed(reason: str) -> None:
    print(f"UNRECOGNISED OUTPUT -- treating as a gate FAILURE, not clean: {reason}", file=sys.stderr)
    sys.exit(2)


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: check-vulnerable-packages.py <path-to-dotnet-list-package-json>", file=sys.stderr)
        return 2

    path = sys.argv[1]
    try:
        with open(path, "r", encoding="utf-8") as f:
            raw = f.read()
    except OSError as exc:
        fail_closed(f"could not read {path}: {exc}")

    if not raw.strip():
        fail_closed(f"{path} is empty -- 'dotnet list package --vulnerable' produced no output")

    try:
        data = json.loads(raw)
    except json.JSONDecodeError as exc:
        fail_closed(f"{path} is not valid JSON: {exc}")
        return 2  # unreachable -- fail_closed always exits; keeps type-checkers happy

    if not isinstance(data, dict):
        fail_closed(f"top-level JSON value in {path} is not an object")

    # Some dotnet SDK versions surface restore/resolution problems (e.g. an
    # unreachable source, an unresolved framework) via a top-level 'problems' array.
    # A non-empty one means the scan did not run cleanly against the full graph --
    # never treat that as "no vulnerabilities found".
    problems = data.get("problems")
    if problems:
        fail_closed(f"'problems' field in {path} is non-empty: {problems!r}")

    projects = data.get("projects")
    if not isinstance(projects, list):
        fail_closed(f"'projects' in {path} is missing or not a list")

    findings: list[tuple[object, object, object, object, object]] = []
    for proj in projects:
        if not isinstance(proj, dict):
            fail_closed(f"a 'projects' entry in {path} is not an object: {proj!r}")
        proj_path = proj.get("path")
        frameworks = proj.get("frameworks")
        if frameworks is None:
            continue  # documented absence: no vulnerable packages for this project
        if not isinstance(frameworks, list):
            fail_closed(f"'frameworks' for project {proj_path!r} is not a list")
        for fw in frameworks:
            if not isinstance(fw, dict):
                fail_closed(f"a 'frameworks' entry for project {proj_path!r} is not an object")
            for key in ("topLevelPackages", "transitivePackages"):
                pkgs = fw.get(key, [])
                if not isinstance(pkgs, list):
                    fail_closed(f"'{key}' for project {proj_path!r} is not a list")
                for pkg in pkgs:
                    if not isinstance(pkg, dict):
                        fail_closed(f"a '{key}' entry for project {proj_path!r} is not an object")
                    vulns = pkg.get("vulnerabilities")
                    if not vulns:
                        continue
                    if not isinstance(vulns, list):
                        fail_closed(f"'vulnerabilities' for package {pkg.get('id')!r} is not a list")
                    for v in vulns:
                        v = v or {}
                        findings.append(
                            (proj_path, pkg.get("id"), pkg.get("resolvedVersion"),
                             v.get("severity"), v.get("advisoryurl"))
                        )

    if findings:
        print(f"Vulnerable package(s) found in the dependency graph at this exact commit ({len(findings)} finding(s)):")
        for proj_path, pkg_id, version, severity, url in findings:
            print(f"  - {pkg_id} {version} [{severity}] {url}  (project: {proj_path})")
        return 1

    print("Local vulnerability scan: clean (0 findings across all projects/frameworks/packages).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
