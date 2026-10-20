# Reproducible carver benchmarks

`tests/ReTrace.Benchmarks` is an optional, dependency-free console benchmark. It
creates four deterministic fixtures in a new temporary directory, warms each
path, measures repeated runs and prints one JSON report. It checks source and
candidate SHA-256 values, candidate counts, exact lengths and coverage before
accepting a measurement. The temporary fixtures are removed at the end.

Run on the same otherwise idle machine, with the same SDK and runtime, in
Release mode. Disable tiered compilation so JIT tier transitions do not become
an apparent before/after improvement:

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_TieredCompilation = "0"
dotnet run --project tests/ReTrace.Benchmarks/ReTrace.Benchmarks.csproj -c Release
```

The synthetic core paths also run on Linux with a .NET 10 SDK:

```bash
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_TieredCompilation=0 \
  dotnet run --project tests/ReTrace.Benchmarks/ReTrace.Benchmarks.csproj -c Release
```

Build/restore messages can precede the JSON on the first run. Build once and
use `--no-build` for subsequent measurements if clean JSON output is required.
The JSON includes raw timings, medians, allocation counters, exact fixture
hashes and source sizes, result evidence, runtime/OS/architecture, processor
count, GC mode and available vector acceleration.

To compare another source checkout with the same benchmark harness, set
`-p:ReTraceCoreProject=<checkout>/src/ReTrace.Core/ReTrace.Core.csproj`. Supply
an absolute path when the checkout is elsewhere. Do not use `--no-build` when
switching the referenced core project. The reference baseline for the results
below is `ac3ba91793f648a74301594545248dddc09de7e3`.

## What each case measures

| Case | Fixture and expected result | Warmups / timed runs | Allocation counter |
| --- | --- | --- | --- |
| `jpeg_entropy_16MiB` | A structural JPEG with a 16 MiB non-marker entropy span; one exact complete structural candidate | 2 / 5 | Current managed thread |
| `png_crc_16MiB` | A CRC-valid PNG chunk stream with a 16 MiB synthetic IDAT payload; one exact complete structural candidate | 2 / 5 | Current managed thread |
| `2000_rejected_png_signatures` | 36,000 bytes containing 2,000 malformed PNG headers; zero candidates and a complete source hash | 1 / 3 | Whole managed process |
| `capped_video_64MiB` | A 64 MiB image with `ftyp` and an open-ended `mdat`; one partial candidate capped to 128 bytes and a complete source hash | 2 / 5 | Whole managed process |

The first two cases call `PhotoCarver.AssessFile`; they measure structural
parsing and candidate hashing. The other two call the asynchronous source
scanner and include sequential full-source hashing. The video case verifies
that applying a fragment cap does not change the retained bytes or their hash.
It exposes the cost of hashing an entire discarded media span before hashing
its much smaller retained prefix.

These are synthetic structural fixtures. PNG compressed pixels and JPEG/video
frames are not decoded, and the payloads are not offered as genuine photographs
or playable videos. The long JPEG entropy run intentionally isolates marker
search throughput. Results describe warm-cache parser and hashing work, not
whole-product speed, physical-drive throughput or recovery completeness.

Allocation counts include the measured operation and small measurement/result
overheads. Process-wide counters can also include thread-pool or runtime work;
compare the medians and inspect the raw samples. They measure managed bytes
allocated, not peak resident memory or disk cache. No timing or allocation
threshold is used as a correctness gate, and this project is deliberately
outside the regular CI test run.

## Observed comparison, 2026-10-20

An isolated comparison used the original baseline source and then replaced only
`PhotoCarver.cs` and `VideoCarver.cs` with the reviewed implementation. It used
.NET SDK 10.0.402 / runtime 10.0.13 on Linux x64, Release mode,
`DOTNET_TieredCompilation=0`, and the fixtures and warmup/sample counts above.
The figures are medians from that local experiment; repeat runs and different
hardware will vary.

The exact original comparison samples, synthetic hashes and code blob
identities are retained in
[2026-10-20-carvers.json](benchmark-results/2026-10-20-carvers.json).

| Case | Baseline median | Updated median | Observed ratio |
| --- | ---: | ---: | ---: |
| 16 MiB JPEG assessment | 56.4091 ms | 13.8564 ms | 4.07× faster |
| 16 MiB PNG CRC assessment | 100.7125 ms | 53.2914 ms | 1.89× faster |
| 2,000 rejected PNG signatures | 8.5271 ms | 3.1485 ms | 2.71× faster |
| Shared 64 MiB video scan, 128-byte retained fragment | 87.9838 ms | 43.8695 ms | 2.01× faster |

For the 2,000 rejected signatures, median managed allocations changed from
**131,560,360 bytes to 439,912 bytes**, a reduction of approximately **99.67%**.
Pooling the parser buffer removes the former 64 KiB allocation for each
candidate assessment. Bulk PNG copies and vectorized JPEG marker search reduce
per-byte overhead. Video scans hash only the selected, retained span after
fragment caps and the hash budget have been applied.

All four performance cases retained the same candidate counts, states, exact
byte lengths and source/candidate hashes. The separate correctness suite also
covers stuffed JPEG entropy, parser and streaming boundaries, bounded partial
files, AVI-only selection, cancellation and exact exports. Performance
measurements do not replace those correctness checks or native Windows tests.
