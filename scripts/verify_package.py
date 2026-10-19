"""Check every member of one published ReTrace package against its inventory."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath


def verify(root):
    root = Path(root).absolute()
    manifest = json.loads((root / "retrace-package.json").read_text(encoding="utf-8"))
    if manifest.get("schema") != "retrace.package.v1" or not isinstance(manifest.get("files"), list):
        raise ValueError("Unknown package inventory.")
    expected = {}
    for row in manifest["files"]:
        name = row["path"]
        path = PurePosixPath(name)
        if path.is_absolute() or any(p in {".", ".."} for p in name.split("/")) or ":" in name or "\\" in name or name in expected:
            raise ValueError("Unsafe or duplicate inventory member.")
        target = root.joinpath(*path.parts)
        for item in (target, *target.parents):
            if item.is_symlink() or item.exists() and getattr(item.lstat(), "st_file_attributes", 0) & 0x400:
                raise ValueError("Linked package member.")
        with target.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest()
        if target.stat().st_size != row["bytes"] or digest != row["sha256"]:
            raise ValueError("Package member mismatch: " + name)
        expected[name] = digest
    actual = {p.relative_to(root).as_posix() for p in root.rglob("*") if p.is_file() and p.name != "retrace-package.json"}
    if actual != set(expected):
        raise ValueError("Package contains missing or unlisted members.")
    return {"version": manifest["version"], "files_verified": len(expected), "all_checks_passed": True}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    print(json.dumps(verify(args.root), indent=2))
