"""Check source, core tests and an actual offline native package in a NEW workspace."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

from verify_package import verify

ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def check(package, workspace):
    package, workspace = Path(package).absolute(), Path(workspace).absolute()
    for path in (workspace, *workspace.parents):
        if path.is_symlink() or path.exists() and getattr(path.lstat(), "st_file_attributes", 0) & 0x400:
            raise ValueError("Select a new ordinary local verification workspace.")
    if workspace.exists() or not workspace.parent.is_dir():
        raise ValueError("Select a NEW workspace under an existing parent.")
    verified = verify(package)
    workspace.mkdir()

    def run(name, arguments, *, env=None):
        result = subprocess.run(arguments, cwd=ROOT, env=env, capture_output=True, timeout=180)
        (workspace / (name + ".log")).write_bytes(result.stdout + result.stderr)
        if result.returncode:
            raise RuntimeError("Release check failed: " + name + "; see its log.")
        return result.stdout

    run("source", [sys.executable, str(ROOT / "scripts/source_manifest.py"), "--verify"])
    test_project = str(ROOT / "tests/ReTrace.Tests/ReTrace.Tests.csproj")
    raw = run("core", ["dotnet", "run", "--project", test_project, "-c", "Release"])
    core = json.loads(raw.decode("utf-8-sig").strip().splitlines()[-1])
    if core.get("status") != "passed" or core.get("assertions", 0) < 405:
        raise ValueError("Unexpected core verification result.")
    image, video = workspace / "owned-photos.img", workspace / "owned-video.img"
    for name, argument, path in (("photo-fixture", "--write-fixture", image),
                                  ("video-fixture", "--write-video-fixture", video)):
        run(name, ["dotnet", "run", "--no-build", "--project", test_project,
                   "-c", "Release", "--", argument, str(path)])
    env = os.environ.copy()
    env["PATH"] = str(Path(os.environ["SystemRoot"]) / "System32")
    env["DOTNET_ROOT"] = str(workspace / "no-global-dotnet")
    native = []
    for name, option, source in (("photos", "--verify-workbench", image),
                                  ("videos", "--verify-video-workbench", video),
                                  ("saved-case", "--verify-session-workbench", image)):
        before = digest(source)
        proof, preview = workspace / (name + ".json"), workspace / (name + ".png")
        run(name, [str(package / "ReTrace.exe"), "--source", str(source), option,
                   str(proof), "--render-preview", str(preview)], env=env)
        if not proof.is_file() or not preview.is_file() or digest(source) != before:
            raise ValueError("Missing native proof or modified source: " + name)
        data = json.loads(proof.read_text(encoding="utf-8-sig"))
        exports = data.get("export", {}).get("Files", [])
        for row in exports:
            path = Path(row["Path"])
            if path.stat().st_size != row["Bytes"] or digest(path) != row["Sha256"]:
                raise ValueError("Export verification failed: " + name)
        native.append({"name": name, "proof_sha256": digest(proof),
                       "source_unchanged": True, "exports_verified": len(exports),
                       "global_runtime_unavailable": True})
    verify(package)
    result = {"schema": "retrace.release-check.v1", "version": verified["version"],
              "package_files_verified": verified["files_verified"],
              "package_manifest_sha256": digest(package / "retrace-package.json"),
              "source_manifest_sha256": digest(ROOT / "SHA256SUMS.txt"),
              "core": core, "native": native, "all_checks_passed": True}
    (workspace / "release-check.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--work-directory", required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(check(args.package, args.work_directory), indent=2))
