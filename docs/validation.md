# Validation scope, 2026-10-19

This is an independently runnable alpha.2 preview, not a promise of exhaustive
recovery or a completed data-recovery certification.

The dependency-free C# test runner checks native JPEG/PNG parsing, PNG CRCs,
truncated/damaged candidates, signature boundaries, larger streaming images,
overlap budgets, cancellation, Windows source sharing/file IDs/hardlinks,
path/ADS/reparse rejection, existing output refusal, selected-range hashes,
independent output verification and stored-candidate export after source
disconnection. Synthetic acquisition tests verify copied prefixes, output hash
readback, early EOF, cancellation manifests and same-device rejection logic.
PhotoRec tests check literal structured arguments, all-format selection,
numbered output directories, aggregate quotas, unsafe entries, cancellation
and both archive-size and same-size/wrong-hash rejection.

Actual native WPF runs use a controlled 65-MiB image containing a decoded PNG,
a complete JPEG, a damaged PNG and a truncated JPEG. They exercise the real
core, format/state filters, multiple selection, independently verified export,
source preservation and bounded WIC preview. Both complete and partial image
previews decoded in the test. Dark and light renders were inspected. A real
UI cancellation stops with partial coverage and releases controls.

The pinned Windows PhotoRec 7.2 executable was actually run against a valid
64-MiB FAT16 deletion fixture: the known deleted JPEG and visible PNG matched
their expected hashes. A separate 16-MiB fixture produced whole JPEG/PNG,
partial JPEG, exact ZIP and PDF output. All-format scanning also produced
false candidates, which remain unverified. Actual process cancellation,
one-second timeout and a small output quota produced cancelled/incomplete
results. Worker output can exceed a polled quota before termination; that
limitation is recorded rather than hidden.

Windows volume metadata enumeration was exercised read-only on this machine.
No real user files were deleted, no real volume contents were acquired, and
no live source recovery or privileged raw-volume capture was performed.
Therefore those operations must not be described as validated end-to-end on
arbitrary physical devices. Source handles request GENERIC_READ only, direct
partition reads are bounded by checked length, and separate recognized direct
disk identities are required before output creation. Unknown, virtual,
spanned, RAID or Storage-Spaces backing is refused. The stream copy/verification
loop and preflight guards are covered independently with synthetic inputs.

Private integration fixtures and proofs are kept outside source publication.
The portable package inventories all binaries, matching .NET notices and the
unchanged pinned PhotoRec binary/source archives. Hashes detect byte changes;
they are not a publisher signature. Hosted CI is a separate result from local
Windows validation and must not be claimed successful unless it actually ran.

## Alpha.2 improvements

The shared source bound is 16 TiB. A synthetic sparse file larger than 4 TiB
tests 64-bit source lengths, bounded early cancellation, an exact selected
export above the 4-TiB offset, and a deliberately smaller caller budget.
This is not a full multi-terabyte scan or a physical-drive recovery benchmark.
The test marks its newly created file sparse before extending it; a sparse
filesystem failure never falls back to allocating terabytes.

Saved investigations have strict bounded JSON validation, duplicate-key and
candidate-range checks, metadata-only library management and explicit
unavailable/changed-source states. Native source reopening checks identity
metadata; selected export independently hashes the candidate again. Saved
specialist output is checked against its recorded hashes, including when the
original image is disconnected. A modified metadata file is not authenticated
by a publisher signature.

Native session tests scan, save, clear, reload without rescanning, preview and
export with independent output hashes and source-preservation checks. Advanced
PhotoRec limits and selected format groups are actual engine options. They do
not turn a timed-out or quota-limited scan into complete coverage.

IamAngusU/ReTrace is the canonical private source repository; angusu-de/ReTrace
is the CI-only mirror. Hosted runner outcomes remain separate from the local
Windows checks and do not count as passing CI.

The alpha.2 local C# run passed 193 assertions, including 71 investigation
checks. Corrupt library metadata can be explicitly removed through the exact
checked Windows file handle; a locked entry is retained. Mid-write cancellation
does not publish a case or leave its owned temporary metadata file.

The signature search skips non-header bytes with a vectorized span search.
On one mostly empty 128-MiB synthetic image, three warm scans changed from
256.96/256.94/258.43 ms to 178.58/175.28/173.69 ms with the same four candidates
and source SHA-256. BF6 was not detected during this local comparison.
This is a narrow warm-cache measurement, not a disk-speed or gaming benchmark.

## Alpha.3: real Windows Recycle Bin and folder sources

Two newly created, owned fixtures (a genuine PNG and a text file) were sent into
the real Windows Recycle Bin with the Windows Shell recycle operation. The
native core found their exact original paths and restored new copies. Both
output SHA-256 values matched the recorded originals, while the original
paths remained absent and the exact Recycle Bin metadata/payloads remained
present and unchanged. Existing personal entries were not restored or removed.
The bin was not emptied. This validates ordinary bin recovery, not recovery of
discarded sectors after permanent deletion.

Actual native dark/light UI runs selected only the owned PNG entry and repeated
the verified copy operation. Separate owned hidden-cache, hidden-backup and
temporary-file fixtures produced four image candidates across three containers.
Multiple selected whole and partial candidates were exported byte-exactly;
source hashes were unchanged. The original image workflow was rechecked too.

Folder search examines allocated file bytes within the chosen root; it cannot
read unallocated disk sectors. Thumbnail/cache candidates are potential derived
copies, never asserted to be full original files. A live physical-volume scan,
permanent deletion from a real host bin, VSS acquisition and arbitrary fragmented
real-device recovery have not been newly certified by these tests.

The bounded native folder profile was also exercised on the current user's
actual Windows Explorer cache folder. It read 22 accessible ordinary files
(6,308,242 bytes) and found 38 structurally complete JPEG/PNG candidates across
three containers. No image contents were exported or rendered. Locked entries
and the deliberately zero-depth profile produced an explicit partial report.
These can be derived icon/thumbnail images, not recovered original photographs.

## Alpha.4: video containers and shared media recovery

Owned one-second 96x64 videos were genuinely encoded as H.264 MP4, MPEG-4 MOV,
MJPEG AVI, fragmented MP4 and VP9 WebM. Each original independently decoded
successfully with FFmpeg 7.1 from a private imageio-ffmpeg 0.6.0 test installation.
FFmpeg is a validation tool here, not a new app requirement or bundled decoder.

The native MP4/MOV/AVI parser recovered the exact known byte spans and SHA-256
values from a separate 8-MiB image, including MP4 and MOV headers crossing source
read-block boundaries. Ordinary MP4/MOV/AVI reached supported structural
completion. Fragmented MP4 retained its exact bytes but stayed partial because
its fragment addressing is not reconstructed. An EOF-truncated MOV stayed partial
with the exact retained-prefix hash. All Media additionally found the ten JPEG
frames embedded in the MJPEG AVI. The source hash matched the independently
recorded image. This does not certify arbitrary real-device fragment recovery.

The shared media scanner reads photos and videos in one streaming source pass;
random-access candidate validation and hashing remain separately bounded.
Deterministic core tests cover selected formats, boundary offsets, cancellation,
aggregate candidate quotas, exact export, saved video investigations and folder
video profiles. Structural fixtures in CI do not claim decodable compressed
video samples; genuine encoded/decoded fixtures are a separate local check.

The specialist video preset uses PhotoRec 7.2's actual `mov`, `riff`, `mkv`,
`asf` and `mpg` format families, verified against its pinned source. Optional
`mov/mdat` preserves separate media-data fragments. There is no automatic header
pairing or claim that isolated mdat data forms a playable original recording.

## Legacy QuickTime recovery

The native video/shared-media scanner now also recognizes QuickTime-compatible
containers without `ftyp`, which Apple's format documentation explicitly makes
optional. Entry atoms are limited to `moov`, `mdat` and `wide`; real nested movie
and video-track headers, a visual sample description, chunk addressing and media
data must agree before a legacy candidate is retained. Bare media atoms, audio
tracks and handler-like strings are insufficient. Recognized branded-container
extents suppress duplicate legacy interpretations of their internal atoms,
including AVIF/HEIF-like inputs rejected by the native video profile.

The full local C# suite passed 405 assertions. New cases cover both atom orders,
initial wide padding, 32-/64-bit sample addresses, adjacent legacy movies,
malformed/orphan/audio inputs, truncation, fragment selection/caps, shared scan
boundaries and full-source hashes, hidden folder containers, exact exports and
saved-investigation reopening. The existing format, source and export guards
remain enabled; the report schema did not change.

Four owned one-second 96x64 MJPEG MOV videos were genuinely encoded with FFmpeg
7.1: movie metadata first and media data first, each with and without initial
wide padding. Their ftyp atoms were removed and sample offsets rebased, then
the resulting files were independently decoded. All four were embedded at known
offsets in a separate 6-MiB image, including read-block boundaries. Native recovery
and saved-investigation export preserved their exact lengths and SHA-256 values.
FFmpeg's strict error mode independently decoded all four exported movies and
an additional hidden-folder export with 12 frames each. A genuinely truncated
movie remained partial and exported only its exact available bytes; no playback
claim was made for that candidate. The source image remained unchanged.

These are contiguous legacy spans, not reconstructed fragment chains. A legacy
candidate stops once the observed movie/media atom group supplies the recorded
tracks. Preceding unrelated/padding atoms, trailing optional metadata, original
brand and exact original file completeness are not established. Partial or
unsupported enclosing video containers can suppress ambiguous inner legacy
interpretations; the profile remains explicitly non-exhaustive.

## README screenshot

The screenshot was captured from the actual WPF workbench using
`--verify-workbench` and `--render-preview`. The source contains an original
generated landscape PNG, a deliberately truncated copy and zero padding.
No personal media or physical device was used. The native scan, preview and
independently hashed new-file export ran before capture. This demonstrates the
UI and the bounded fixture workflow, not real-world recovery effectiveness.
