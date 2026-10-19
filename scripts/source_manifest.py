"""Write/verify a deterministic source-only SHA-256 inventory."""
import argparse
import hashlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def inventory():
    rows = []
    for name in (".gitattributes", ".gitignore", "LICENSE", "README.md", "THIRD-PARTY-NOTICES.md", ".github", "src", "tests", "scripts", "docs"):
        base = ROOT / name
        for path in (sorted(base.rglob("*")) if base.is_dir() else [base]):
            if not path.is_file() or any(part in {"bin", "obj", "__pycache__"} for part in path.relative_to(ROOT).parts):
                continue
            if path.is_symlink():
                raise ValueError("Linked source member.")
            rows.append((path.relative_to(ROOT).as_posix(), hashlib.sha256(path.read_bytes()).hexdigest()))
    return "".join(sha + "  " + name + "\n" for name, sha in sorted(rows))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verify", action="store_true")
    args = parser.parse_args()
    expected = inventory()
    manifest = ROOT / "SHA256SUMS.txt"
    if args.verify:
        if manifest.read_text(encoding="utf-8") != expected:
            raise ValueError("Source inventory changed.")
    else:
        manifest.write_text(expected, encoding="utf-8", newline="\n")
    print("Verified" if args.verify else "Recorded", len(expected.splitlines()), "source files.")
