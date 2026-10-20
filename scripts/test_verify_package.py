"""Regression checks for incomplete but internally consistent release inventories."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from verify_package import verify


class LicenseInventoryTests(unittest.TestCase):
    def test_empty_self_consistent_inventory_is_not_a_distribution(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "retrace-package.json").write_text(json.dumps({
                "schema": "retrace.package.v1", "version": "test", "files": []}))
            with self.assertRaisesRegex(ValueError, "licenses or corresponding PhotoRec source"):
                verify(root)

    def test_license_without_corresponding_sources_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            rows = []
            for name in ("licenses/PhotoRec-COPYING.txt", "licenses/PhotoRec-AUTHORS.txt",
                         "LICENSE", "THIRD-PARTY-NOTICES.md"):
                target = root / name
                target.parent.mkdir(exist_ok=True)
                data = b"GNU GENERAL PUBLIC LICENSE\n"
                target.write_bytes(data)
                rows.append({"path": name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
            (root / "retrace-package.json").write_text(json.dumps({
                "schema": "retrace.package.v1", "version": "test", "files": rows}))
            with self.assertRaisesRegex(ValueError, "licenses or corresponding PhotoRec source"):
                verify(root)


if __name__ == "__main__":
    unittest.main()
