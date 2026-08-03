#!/usr/bin/env python3
"""#925 cross-vendor review finding 3: publish-nuget.yml pushes all six packages in one
`dotnet nuget push "nupkgs/*.nupkg" --skip-duplicate` call, but NuGet servers accept
each package in the glob independently -- there is no server-side transaction across
them. If package N of 6 fails mid-push (a network blip, a internal infrastructure hiccup, a runner
restart), packages 1..N-1 are already live and IMMUTABLE. A later re-run of the same
version on a DIFFERENT commit (a fix landed in between) would then "top up" only the
missing packages with --skip-duplicate -- silently mixing artifacts built from two
different commits under one version number, with nothing in the registry to show that
happened.

This script queries the registry via the standard NuGet V3 protocol (the
PackageBaseAddress resource, discovered from the service index -- not a hardcoded
internal infrastructure- or GitHub-Packages-specific URL shape, so the identical script and call site
works against both registries this workflow publishes to) for each of the package IDs
given, at the exact version about to be published, and reports which already exist.

Exit codes:
  0  safe to proceed -- either NONE of the packages exist yet at this version (a clean
     first publish) or ALL of them do (a pure idempotent re-run, where the subsequent
     `--skip-duplicate` push is a true no-op for every package).
  1  a PARTIAL/mixed cohort was found -- some but not all of the packages already
     exist at this version. This is the exact "N-1 published, package N missing"
     scenario described above; the caller must investigate before publishing.
  2  fail-closed -- the registry's service index could not be fetched or does not
     advertise a PackageBaseAddress resource, or a per-package existence check
     errored for a reason other than "not found" (e.g. auth failure, timeout, 5xx).
     An unreachable registry must NEVER be silently treated as "0 exist, safe to
     publish" -- that is indistinguishable from a genuine partial-cohort race.

NOTE: this script performs live HTTP calls against the registry named on argv[1] and
is exercised in this repository's CI as part of a real release run.

Verification status (#967): the live internal-host internal registry has been exercised
end-to-end -- a GET against the PackageBaseAddress flat-container URL returns 200 for
a version that already exists and 404 for one that does not, while a HEAD against the
identical URL returns 405 Method Not Allowed in BOTH cases (internal infrastructure rejects the method
outright rather than routing it based on existence), which is why package_exists()
below issues GET, not HEAD. GitHub Packages' nuget.pkg.github.com was confirmed,
unauthenticated, to reject HEAD the same way (405) on both its service index and a
plausible flat-container download URL, so the same fix is expected to apply there;
its authenticated GET exists-vs-404 distinction was NOT exercised in this session (no
PAT available) and remains unverified -- treat that half of the GitHub Packages path
as a known gap, not a confirmed behavior, until spot-checked against a real
`workflow_dispatch` run. The PackageBaseAddress resource and its flat-container URL
layout are part of the standardized NuGet V3 protocol, not a internal infrastructure- or GitHub-specific
shape, which is why the identical script and call site works against both registries
this workflow publishes to.
"""
from __future__ import annotations

import sys
import urllib.error
import urllib.request


def fetch(url: str, token: str | None, method: str = "GET") -> bytes:
    req = urllib.request.Request(url, method=method)
    if token:
        req.add_header("Authorization", f"token {token}")
    with urllib.request.urlopen(req, timeout=30) as resp:  # noqa: S310 -- fixed, non-user-controlled scheme (http/https only, caller-supplied service index URL)
        return resp.read()


def resolve_package_base_address(index_url: str, token: str | None) -> str:
    import json

    raw = fetch(index_url, token)
    data = json.loads(raw)
    for res in data.get("resources", []):
        rtype = res.get("@type", "")
        if isinstance(rtype, str) and rtype.startswith("PackageBaseAddress/"):
            base = res["@id"]
            return base if base.endswith("/") else base + "/"
    raise RuntimeError(f"service index {index_url} advertises no PackageBaseAddress resource")


def package_exists(base: str, package_id: str, version: str, token: str | None) -> bool:
    pid = package_id.lower()
    ver = version.lower()
    url = f"{base}{pid}/{ver}/{pid}.{ver}.nupkg"
    # GET, not HEAD (#967): internal infrastructure's NuGet flat-container endpoint returns 405 Method
    # Not Allowed for HEAD regardless of whether the version exists, and GitHub
    # Packages' nuget.pkg.github.com rejects HEAD the same way -- see the module
    # docstring's verification-status paragraph. GET is the verb both registries
    # answer correctly (200/404) for this resource. Deliberately does NOT reuse the
    # fetch() helper above, which buffers the full response body via resp.read() --
    # that is fine for the small JSON service index but would pull an entire,
    # potentially multi-MB, .nupkg into memory just to check existence. Instead this
    # opens the connection directly, lets a non-2xx status raise HTTPError (same as
    # the 404 branch below already expects), and reads at most one byte off a 2xx
    # response before the `with` block closes the connection.
    req = urllib.request.Request(url, method="GET")
    if token:
        req.add_header("Authorization", f"token {token}")
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:  # noqa: S310 -- fixed, non-user-controlled scheme (http/https only, caller-supplied service index URL)
            resp.read(1)
        return True
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return False
        raise
    except urllib.error.URLError as exc:
        raise RuntimeError(f"could not reach {url}: {exc}") from exc


def main() -> int:
    args = sys.argv[1:]
    token = None
    if "--token" in args:
        i = args.index("--token")
        token = args[i + 1]
        args = args[:i] + args[i + 2:]

    if len(args) < 3:
        print(
            "usage: check-package-cohort.py <service-index-url> <version> <package-id...> [--token <token>]",
            file=sys.stderr,
        )
        return 2

    index_url, version, *package_ids = args
    if not package_ids:
        print("no package ids given", file=sys.stderr)
        return 2

    try:
        base = resolve_package_base_address(index_url, token)
    except Exception as exc:  # noqa: BLE001 -- fail closed on ANY resolution problem
        print(f"ERROR: could not resolve PackageBaseAddress from {index_url}: {exc}", file=sys.stderr)
        return 2

    present: list[str] = []
    missing: list[str] = []
    for pid in package_ids:
        try:
            exists = package_exists(base, pid, version, token)
        except Exception as exc:  # noqa: BLE001 -- fail closed on ANY check problem
            print(f"ERROR: could not check existence of {pid} {version}: {exc}", file=sys.stderr)
            return 2
        (present if exists else missing).append(pid)

    print(f"Version cohort check for {version}: {len(present)}/{len(package_ids)} already published.")
    if present:
        print("  already published: " + ", ".join(present))
    if missing:
        print("  not yet published: " + ", ".join(missing))

    if present and missing:
        print(
            f"ERROR: MIXED cohort -- {len(present)} of {len(package_ids)} packages for version {version} "
            "already exist in this registry and the rest do not. This looks like a previous publish run "
            "failed partway through. Publishing now would top up the missing packages from THIS commit, "
            "potentially mixing artifacts built from two different commits under one version number. "
            "Refusing -- investigate the registry state and either delete the partial cohort or cut a "
            "new version.",
            file=sys.stderr,
        )
        return 1

    print("Cohort check passed (clean first publish, or a pure idempotent re-run).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
