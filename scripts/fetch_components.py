"""Fetch unchanged official PhotoRec binary/source archives with immutable pins."""
import argparse
import hashlib
from pathlib import Path
import urllib.request

ASSETS = {
    "testdisk-7.2.win64.zip": (27354549, "e97e203ce77b6b1a3a37d01beccf069dc6c4632b579ffbb82ae739cdda229f38"),
    "testdisk-7.2.tar.bz2": (855781, "f8343be20cb4001c5d91a2e3bcd918398f00ae6d8310894a5a9f2feb813c283f"),
}

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    target = args.destination.absolute()
    for path in (target, *target.parents):
        if path.is_symlink() or path.exists() and getattr(path.lstat(), "st_file_attributes", 0) & 0x400:
            raise ValueError("Select a component directory without links.")
    target.mkdir(parents=False, exist_ok=True)
    for name, (size, sha) in ASSETS.items():
        path = target / name
        if path.exists():
            with path.open("rb") as stream:
                if path.stat().st_size != size or hashlib.file_digest(stream, "sha256").hexdigest() != sha:
                    raise ValueError("Existing component does not match its official pin: " + name)
            continue
        url = "https://www.cgsecurity.org/" + name
        with urllib.request.urlopen(url, timeout=60) as response:
            raw = response.read(size + 1)
        if len(raw) != size or hashlib.sha256(raw).hexdigest() != sha:
            raise ValueError("Official component size/hash mismatch: " + name)
        with path.open("xb") as output:
            output.write(raw)
        print("Verified " + name)
