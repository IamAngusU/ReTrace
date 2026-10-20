# ReTrace

<p align="center"><strong>A second chance for your files.</strong><br>A local Windows workbench for finding, inspecting and saving file, photo and video data.</p>

<p align="center"><a href="README.de.md"><img src="docs/assets/readme-language-de.svg" height="40" alt="Diese README auf Deutsch lesen"></a></p>
<p align="center">
  <a href="https://github.com/IamAngusU/ReTrace/releases/tag/v0.1.0-alpha.6"><img src="docs/assets/readme/repo-release.svg" height="40" alt="Preview 0.1.0-alpha.6"></a>
  <a href="docs/release.md"><img src="docs/assets/readme/repo-platforms.svg" height="40" alt="Windows 10 and 11 x64"></a>
  <a href="THIRD-PARTY-NOTICES.md"><img src="docs/assets/readme/repo-license.svg" height="40" alt="MIT application with separately licensed GPL components"></a>
</p>

For people recovering missing files on Windows who want to inspect findings before
saving them. Native C#/WPF interface, dark/light themes, local search and bundled
PhotoRec. **An early preview with explicit limits.**

![ReTrace: real native scan and image preview using synthetic test data](docs/assets/workbench.png)

<sub>The running Windows app with a generated test image. The app UI is currently German. This is not evidence of a real-world recovery.</sub>

## Start in three steps

1. [Download the complete Windows ZIP](https://github.com/IamAngusU/ReTrace/releases/tag/v0.1.0-alpha.6) and extract the whole folder.
2. Open `ReTrace.exe`, choose a source and explicitly start the appropriate scan.
3. Inspect findings and use **Auswahl sichern** (save selection) onto a **different physical drive**.

**Never save recovered files, working folders or disk images onto the drive you
are recovering from.** Downloading and extracting software there can also overwrite
deleted data. A different drive letter on the same disk is not enough. SSD TRIM
and overwritten bytes can make recovery impossible.

The portable package includes its runtime and PhotoRec. No Python, SDK, account
or online service is required. Administrator rights are requested only for volume access.

## Choose your source

- **Recycle Bin:** Remaining files and `$I` metadata, saved as new SHA-256-verified
  copies. No emptying the bin or writing to original paths. Directory entries
  are not yet recovered recursively.
- **Folders, backups and cache:** Regular and hidden files, including embedded
  media. No deleted free sectors, automatic cloud-bin queries or backup discovery.
- **Raw disk images:** Native JPEG, PNG, MP4/MOV and AVI search, plus additional
  format families through pinned PhotoRec 7.2.
- **Capture a volume:** Read-only acquisition into a new image on another recognized
  physical disk. Unknown, virtual, RAID and Storage Spaces layouts are not
  automatically approved. Active volumes may change during acquisition.

Findings stay bound to their source and exact byte range. Exports create a new
folder and independently verify copies with SHA-256. Existing files are not
replaced. A readable preview establishes neither originality, completeness nor
that a file was previously deleted. Missing bytes are never invented.

## Why not Recuva, PhotoRec or Windows File Recovery?

[Recuva](https://www.ccleaner.com/recuva) provides an established Windows recovery UI.
[Windows File Recovery](https://support.microsoft.com/en-us/windows/experience/backup-recovery/windows-file-recovery)
is Microsoft's command-line recovery tool.
[PhotoRec](https://www.cgsecurity.org/testdisk_doc/photorec.html) is the independent
signature recovery engine bundled unchanged with ReTrace.

Choose ReTrace for a native workbench with bounded previews, inspectable byte
ranges, saved investigations and verified new copies. It is younger and narrower
in scope; there is no evidence that it recovers more files than these tools.
Its PhotoRec mode uses PhotoRec's engine.

## Photos, videos and limits

Native video scanning checks container boundaries, nested video-track headers,
media ranges and supported chunk addresses. **Alle Medien** (all media) uses one
source pass; JPEG frames inside MJPEG AVI may also appear as images. The video
view never executes a finding. A complete container does not prove that every
frame decodes or the original recording is complete.

Older MOVs without `ftyp` require consistent movie, track, sample and chunk
structures; an isolated `mdat` block is insufficient. Missing headers, fragmented
MP4s and truncated media remain partial. PhotoRec profiles select actual upstream
families, which can include related audio formats. Its `mov/mdat` option preserves
fragments without blindly joining them to unrelated headers.

Limits are reported: up to 10,000 native signature checks, 64 MiB per complete
photo candidate and 4 MiB per fragment. Videos use 256 MiB per finding, extendable
to 1 GiB. Folder defaults are 256 files, 512 MiB source data, 128 MiB per container
and eight directory levels. Links, alternate streams and archives are not followed
or unpacked. Limits and skipped ranges make results explicitly partial.

PhotoRec defaults to one hour, 10,000 files, 64 MiB per finding and 4 GiB total.
Limits are explicitly adjustable; external output may briefly exceed them before
the process stops. A new working folder contains tools, findings and the report.
Cancellation does not delete it. A finished profile does not guarantee an exhaustive scan.

## Resume an investigation

**Untersuchung speichern** (save investigation), **Öffnen** (open) and
**Gespeicherte** (saved) manage bounded JSON reports. Reopening does not rerun
scans. Source binding and ranges are checked; saved PhotoRec findings are fully
hashed. Native bytes are rechecked before export. A missing or changed source
keeps the report readable and blocks recovery. Removing saved metadata does not
delete sources, findings or recovered copies.

## Develop and verify

Windows 10/11 x64, .NET 10 SDK and Python 3.11+ for build scripts:

```powershell
dotnet run --project tests/ReTrace.Tests/ReTrace.Tests.csproj -c Release
dotnet build src/ReTrace.App/ReTrace.App.csproj -c Release
python scripts/fetch_components.py .tools
python scripts/publish.py D:\ReTrace\new-package --assets .tools
```

[Release verification](docs/release.md) · [Validation and limits](docs/validation.md) ·
[CI runs](https://github.com/angusu-de/ReTrace/actions) ·
[Build provenance](https://github.com/IamAngusU/IamAngusU/blob/main/ci.md) ·
[Third-party licenses and included sources](THIRD-PARTY-NOTICES.md).

<p align="center"><a href="https://github.com/IamAngusU/icon-marquee"><img src="docs/assets/stack-marquee.svg" width="760" alt="C#, .NET, Windows and Python"></a></p>

ReTrace grew out of TraceLedger's recovery workflow and runs as an independent product.
