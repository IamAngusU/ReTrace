"""Check every member of one published ReTrace package against its inventory."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
from publish import ASSETS


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
    required = {"licenses/PhotoRec-COPYING.txt", "licenses/PhotoRec-AUTHORS.txt",
                "LICENSE", "THIRD-PARTY-NOTICES.md"}
    required.update("components/" + name for name in ASSETS)
    if not required.issubset(expected):
        raise ValueError("Package is missing required licenses or corresponding PhotoRec source.")
    for name, (size, sha) in ASSETS.items():
        path = root / "components" / name
        if path.stat().st_size != size or expected["components/" + name] != sha:
            raise ValueError("Official component pin mismatch: " + name)
    if "GNU GENERAL PUBLIC LICENSE" not in (root / "licenses/PhotoRec-COPYING.txt").read_text(encoding="utf-8"):
        raise ValueError("PhotoRec GPL license text is missing.")
    return {"version": manifest["version"], "files_verified": len(expected),
            "photorec_license_and_source_verified": True, "all_checks_passed": True}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    args = parser.parse_args()
    print(json.dumps(verify(args.root), indent=2))
