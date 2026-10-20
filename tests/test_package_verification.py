"""Exercise release-inventory acceptance and rejection using owned tiny fixtures."""
import hashlib
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from verify_package import verify


class PackageVerificationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="retrace-package-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "package"
        self.root.mkdir()
        self.manifest = {"schema": "retrace.package.v1", "version": "test", "files": []}
        self.add_member("payload.bin", b"owned fixture")

    def write_manifest(self):
        (self.root / "retrace-package.json").write_text(json.dumps(self.manifest), encoding="utf-8")

    def add_member(self, name, contents):
        target = self.root / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(contents)
        self.manifest["files"].append({"path": name, "bytes": len(contents),
                                        "sha256": hashlib.sha256(contents).hexdigest()})
        self.write_manifest()

    def make_link(self, link, target, *, directory=False):
        try:
            link.symlink_to(target, target_is_directory=directory)
        except (OSError, NotImplementedError) as error:
            self.skipTest("Symbolic links are unavailable: " + str(error))

    def test_exact_inventory_passes(self):
        self.assertEqual(verify(self.root), {"version": "test", "files_verified": 1, "all_checks_passed": True})

    def test_same_length_tampering_fails(self):
        (self.root / "payload.bin").write_bytes(b"other fixture")
        with self.assertRaisesRegex(ValueError, "mismatch"):
            verify(self.root)

    def test_size_change_fails(self):
        (self.root / "payload.bin").write_bytes(b"short")
        with self.assertRaisesRegex(ValueError, "mismatch"):
            verify(self.root)

    def test_missing_member_fails(self):
        (self.root / "payload.bin").unlink()
        with self.assertRaisesRegex(ValueError, "missing or unlisted"):
            verify(self.root)

    def test_unlisted_member_fails(self):
        (self.root / "extra.bin").write_bytes(b"extra")
        with self.assertRaisesRegex(ValueError, "missing or unlisted"):
            verify(self.root)

    def test_unlisted_nested_inventory_is_not_silently_ignored(self):
        nested = self.root / "nested" / "retrace-package.json"
        nested.parent.mkdir()
        nested.write_bytes(b"unlisted content")
        with self.assertRaisesRegex(ValueError, "missing or unlisted"):
            verify(self.root)

    def test_listed_nested_inventory_is_hashed_normally(self):
        self.add_member("nested/retrace-package.json", b"listed content")
        self.assertEqual(verify(self.root)["files_verified"], 2)
        (self.root / "nested/retrace-package.json").write_bytes(b"edited content")
        with self.assertRaisesRegex(ValueError, "mismatch"):
            verify(self.root)

    def test_noncanonical_and_unsafe_names_fail(self):
        row = self.manifest["files"][0]
        for name in ("", "../outside", "/absolute", "a/./b", "a//b", "a\\b", "C:payload",
                     "payload.bin/", "retrace-package.json", "RETRACE-PACKAGE.JSON"):
            with self.subTest(name=name):
                row["path"] = name
                self.write_manifest()
                with self.assertRaisesRegex(ValueError, "Unsafe"):
                    verify(self.root)

    def test_duplicate_windows_alias_fails(self):
        self.manifest["files"].append({**self.manifest["files"][0], "path": "PAYLOAD.BIN"})
        self.write_manifest()
        with self.assertRaisesRegex(ValueError, "duplicate"):
            verify(self.root)

    def test_invalid_member_metadata_fails(self):
        original = self.manifest["files"][0].copy()
        for key, value in (("bytes", -1), ("bytes", True), ("bytes", "13"), ("sha256", "invalid"), ("sha256", 42)):
            with self.subTest(key=key, value=value):
                self.manifest["files"][0] = {**original, key: value}
                self.write_manifest()
                with self.assertRaisesRegex(ValueError, "Invalid inventory"):
                    verify(self.root)

    def test_linked_inventory_fails(self):
        path = self.root / "retrace-package.json"
        outside = Path(self.temporary.name) / "outside.json"
        path.rename(outside)
        self.make_link(path, outside)
        with self.assertRaisesRegex(ValueError, "Linked"):
            verify(self.root)

    def test_linked_payload_fails(self):
        path = self.root / "payload.bin"
        outside = Path(self.temporary.name) / "outside.bin"
        path.rename(outside)
        self.make_link(path, outside)
        with self.assertRaisesRegex(ValueError, "Linked"):
            verify(self.root)

    def test_unlisted_linked_directory_fails(self):
        outside = Path(self.temporary.name) / "outside"
        outside.mkdir()
        self.make_link(self.root / "linked", outside, directory=True)
        with self.assertRaisesRegex(ValueError, "Linked"):
            verify(self.root)

    @unittest.skipUnless(hasattr(os, "mkfifo"), "FIFO fixtures require POSIX")
    def test_special_file_is_rejected_without_opening_it(self):
        path = self.root / "payload.bin"
        path.unlink()
        os.mkfifo(path)
        with self.assertRaisesRegex(ValueError, "ordinary files"):
            verify(self.root)


if __name__ == "__main__":
    unittest.main()
