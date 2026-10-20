"""Require public CI to test the current canonical source commit."""
import os
import subprocess

if os.environ.get("GITHUB_REPOSITORY", "").lower() == "angusu-de/retrace-ci":
    source = subprocess.check_output([
        "git", "ls-remote", "https://github.com/IamAngusU/ReTrace.git", "refs/heads/main"
    ], text=True, timeout=30).split()[0]
    if source != os.environ.get("GITHUB_SHA"):
        raise SystemExit("CI source differs from canonical main; sync before claiming evidence.")
    print("Canonical source verified:", source)
