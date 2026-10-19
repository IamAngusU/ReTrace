"""Publish one new offline Windows package with pinned upstream components."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = "0.1.0-alpha.3"
ASSETS = {
    "testdisk-7.2.win64.zip": (27354549, "e97e203ce77b6b1a3a37d01beccf069dc6c4632b579ffbb82ae739cdda229f38"),
    "testdisk-7.2.tar.bz2": (855781, "f8343be20cb4001c5d91a2e3bcd918398f00ae6d8310894a5a9f2feb813c283f"),
}


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def safe(path):
    for component in (path, *path.parents):
        if component.is_symlink() or component.exists() and getattr(component.lstat(), "st_file_attributes", 0) & 0x400:
            raise ValueError("Package paths cannot contain links or reparse points.")


def publish(destination, assets):
    destination, assets = Path(destination), Path(assets).absolute()
    if not destination.is_absolute() or ".." in destination.parts:
        raise ValueError("Select an absolute new destination.")
    safe(destination)
    if destination.exists() or not destination.parent.is_dir():
        raise ValueError("Select a new directory under an existing parent.")
    for name, (size, sha) in ASSETS.items():
        path = assets / name
        safe(path)
        if path.stat().st_size != size or digest(path) != sha:
            raise ValueError("Official component pin mismatch: " + name)
    staging = Path(tempfile.mkdtemp(prefix=".retrace-build-", dir=destination.parent))
    subprocess.run(["dotnet", "publish", str(ROOT / "src/ReTrace.App/ReTrace.App.csproj"),
                    "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-o", str(staging),
                    "-p:PublishSingleFile=false", "-p:DebugType=None", "-p:DebugSymbols=false"], cwd=ROOT, check=True)
    config = json.loads((staging / "ReTrace.runtimeconfig.json").read_text())
    frameworks = {row["name"]: row["version"] for row in config["runtimeOptions"]["includedFrameworks"]}
    cache = Path(os.environ.get("NUGET_PACKAGES") or Path.home() / ".nuget/packages").resolve(strict=True)
    licenses = staging / "licenses"
    licenses.mkdir()
    for framework, package, source, name in (
        ("Microsoft.NETCore.App", "microsoft.netcore.app.runtime.win-x64", "LICENSE.TXT", "dotnet-core-LICENSE.txt"),
        ("Microsoft.NETCore.App", "microsoft.netcore.app.runtime.win-x64", "THIRD-PARTY-NOTICES.TXT", "dotnet-core-THIRD-PARTY-NOTICES.txt"),
        ("Microsoft.WindowsDesktop.App", "microsoft.windowsdesktop.app.runtime.win-x64", "LICENSE", "dotnet-desktop-LICENSE.txt"),
    ):
        version = frameworks[framework]
        if not all(c in "0123456789." for c in version):
            raise ValueError("Unexpected .NET runtime version.")
        path = cache / package / version / source
        safe(path)
        shutil.copyfile(path, licenses / name)
    components = staging / "components"
    components.mkdir()
    for name in ASSETS:
        shutil.copyfile(assets / name, components / name)
    with zipfile.ZipFile(assets / "testdisk-7.2.win64.zip") as archive:
        (licenses / "PhotoRec-COPYING.txt").write_bytes(archive.read("testdisk-7.2/COPYING.txt"))
        (licenses / "PhotoRec-AUTHORS.txt").write_bytes(archive.read("testdisk-7.2/AUTHORS.txt"))
    for name in ("LICENSE", "README.md", "THIRD-PARTY-NOTICES.md"):
        shutil.copyfile(ROOT / name, staging / name)
    shutil.copytree(ROOT / "docs", staging / "docs")
    manifest = {"schema": "retrace.package.v1", "version": VERSION, "platform": "win-x64",
                "frameworks": frameworks, "components": ASSETS, "files": [],
                "notice": "Integrity inventory, not a publisher signature. No Python, TL, VM or online service is required."}
    for path in sorted(staging.rglob("*")):
        if path.is_file():
            manifest["files"].append({"path": path.relative_to(staging).as_posix(), "bytes": path.stat().st_size, "sha256": digest(path)})
    (staging / "retrace-package.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    subprocess.run([str(staging / "ReTrace.exe"), "--self-test"], check=True, cwd=staging)
    staging.rename(destination)
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    parser.add_argument("--assets", required=True, type=Path)
    args = parser.parse_args()
    manifest = publish(args.destination, args.assets)
    print(json.dumps({"destination": str(args.destination), "version": VERSION, "files": len(manifest["files"])}))
