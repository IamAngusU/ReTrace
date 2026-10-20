# Build and release verification

The current distribution is **0.1.0-alpha.5**, an explicitly limited Windows
x64 preview. A successful test run is not certification of arbitrary damaged
drives, fragmented recordings, encryption recovery or all Windows hardware.

Build from the selected Git commit on Windows with .NET 10 SDK and Python
3.11+. Use new output directories; existing output is never overwritten:

```powershell
python scripts/source_manifest.py --verify
python -m unittest discover -s tests -p 'test_*.py'
python scripts/fetch_components.py .tools
python scripts/publish.py D:\ReTrace\new-package --assets .tools
python scripts/release_check.py --package D:\ReTrace\new-package --work-directory D:\ReTrace\new-verification
```

The source inventory, pinned original PhotoRec binary/source archives and
every package member must verify. The release check runs the core assertions,
creates synthetic photo/video images, exercises the actual packaged native
application and saved investigations without a global .NET runtime on PATH,
and independently hashes exported files. It keeps its proof, logs and owned
fixtures in the new verification directory. It never empties the Recycle Bin
or accesses a physical drive. Genuine encoded/decoded media validation is
documented separately in [validation.md](validation.md).

Publish the complete portable ZIP, exact source snapshot and SHA256SUMS as
release assets. A ZIP checksum and the internal package inventory detect
accidental modification; neither is a publisher signature. Retain the exact
source commit and verification proof. Never include personal recovery data,
disk images, credentials or private case reports in a release.

The primary repository is IamAngusU/ReTrace; angusu-de/ReTrace runs CI against
the identical source. Inspect the actual CI result before claiming CI passed.
If hosted jobs cannot start, record that limitation separately from local
verification. Keep the release marked as a prerelease while the product
remains an alpha; do not rename it stable merely because packaging succeeds.
