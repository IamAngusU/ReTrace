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
is the CI-only mirror. Hosted billing failures remain separate from the local
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
