"""Check every member of one published ReTrace package against its inventory."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import stat

MANIFEST_NAME = "retrace-package.json"


def require_unlinked(path):
    for item in (path, *path.parents):
        info = item.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("Linked package member.")


def require_regular_file(path):
    require_unlinked(path)
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode):
        raise ValueError("Package members must be ordinary files.")
    return info


def verify(root):
    root = Path(root).absolute()
    require_unlinked(root)
    manifest_path = root / MANIFEST_NAME
    require_regular_file(manifest_path)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if (not isinstance(manifest, dict) or manifest.get("schema") != "retrace.package.v1"
            or not isinstance(manifest.get("files"), list) or not isinstance(manifest.get("version"), str)):
        raise ValueError("Unknown package inventory.")
    expected = {}
    windows_names = set()
    for row in manifest["files"]:
        if not isinstance(row, dict) or not isinstance(row.get("path"), str):
            raise ValueError("Invalid inventory member.")
        name = row["path"]
        path = PurePosixPath(name)
        if (not name or path.is_absolute() or path.as_posix() != name
                or any(p in {".", ".."} for p in name.split("/")) or ":" in name or "\\" in name
                or name.casefold() == MANIFEST_NAME or name.casefold() in windows_names):
            raise ValueError("Unsafe or duplicate inventory member.")
        if (type(row.get("bytes")) is not int or row["bytes"] < 0
                or not isinstance(row.get("sha256"), str) or not re.fullmatch(r"[0-9a-f]{64}", row["sha256"])):
            raise ValueError("Invalid inventory size or SHA-256.")
        expected[name] = row
        windows_names.add(name.casefold())

    # Only the root inventory is self-excluded. A nested file with the same
    # basename is still a package member and must be listed and verified.
    actual = set()
    for item in root.rglob("*"):
        require_unlinked(item)
        info = item.lstat()
        if stat.S_ISDIR(info.st_mode):
            continue
        if not stat.S_ISREG(info.st_mode):
            raise ValueError("Package members must be ordinary files.")
        name = item.relative_to(root).as_posix()
        if name != MANIFEST_NAME:
            actual.add(name)
    if actual != set(expected):
        raise ValueError("Package contains missing or unlisted members.")

    for name, row in expected.items():
        path = PurePosixPath(name)
        target = root.joinpath(*path.parts)
        if require_regular_file(target).st_size != row["bytes"]:
            raise ValueError("Package member mismatch: " + name)
        with target.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest()
        if digest != row["sha256"]:
            raise ValueError("Package member mismatch: " + name)
    return {"version": manifest["version"], "files_verified": len(expected), "all_checks_passed": True}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    print(json.dumps(verify(args.root), indent=2))
