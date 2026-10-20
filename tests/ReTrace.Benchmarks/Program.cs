using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReTrace.Core;

// Optional diagnostic measurements, deliberately outside the correctness runner.
// Timings never determine success; fixture bytes, coverage and hashes do.
string root = Path.Combine(Path.GetTempPath(), "retrace-benchmarks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string jpeg = Path.Combine(root, "entropy.jpg");
    using (var stream = new FileStream(jpeg, FileMode.CreateNew))
    {
        stream.Write([255, 216, 255, 224, 0, 4, 65, 65, 255, 192, 0, 11, 8, 0, 1, 0, 1, 1, 1, 0x11, 0,
            255, 218, 0, 8, 1, 1, 0, 0, 63, 0]);
        stream.Write(new byte[16 * 1024 * 1024]); stream.Write([255, 217]);
    }
    string png = Path.Combine(root, "bulk.png");
    using (var stream = new FileStream(png, FileMode.CreateNew))
    {
        stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] ihdr = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(ihdr, 1);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 1); ihdr[8] = 8; ihdr[9] = 2;
        Chunk(stream, "IHDR", ihdr); Chunk(stream, "IDAT", new byte[16 * 1024 * 1024]); Chunk(stream, "IEND", []);
    }
    string signatures = Path.Combine(root, "signature-flood.img");
    using (var stream = new FileStream(signatures, FileMode.CreateNew))
        for (int i = 0; i < 2000; i++) stream.Write([137, 80, 78, 71, 13, 10, 26, 10, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2]);
    string video = Path.Combine(root, "partial-video.img");
    using (var stream = new FileStream(video, FileMode.CreateNew))
    {
        // Supported ftyp followed by an open-ended mdat, deliberately partial.
        stream.Write([0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109, 0, 0, 0, 0, 105, 115, 111, 109, 109, 112, 52, 49,
            0, 0, 0, 0, 109, 100, 97, 116]);
        stream.SetLength(64 * 1024 * 1024);
    }
    List<CaseResult> cases =
    [
        Assess("jpeg_entropy_16MiB", jpeg, "jpeg", "c462ee281a143f109c27202f18aff10ae27a955e759bd302a0ae73844599a2fb"),
        Assess("png_crc_16MiB", png, "png", "30a400dbd0f7ba3a04dcb25fd38a2a2c2de0db619e922e8a6a64d477569e16a3"),
        await Scan("2000_rejected_png_signatures", signatures,
            "034c7396ff7f787bfcb32441db4219f1b05a9bb06d27e269d6f9f5d648662a5e", false),
        await Scan("capped_video_64MiB", video,
            "bb44a6ddc4ca31454074436dbdd9258238f67daafcc60e9b86774808d13a9d6d", true)
    ];
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        schema = "retrace.benchmarks.v1",
        recordedUtc = DateTimeOffset.UtcNow,
        context = new
        {
#if DEBUG
            configuration = "Debug",
#else
            configuration = "Release",
#endif
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            logicalProcessors = Environment.ProcessorCount,
            serverGc = GCSettings.IsServerGC,
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime default",
            vector128 = Vector128.IsHardwareAccelerated,
            vector256 = Vector256.IsHardwareAccelerated,
            vector512 = Vector512.IsHardwareAccelerated,
            stopwatchFrequency = Stopwatch.Frequency
        },
        limitations = new[]
        {
            "Synthetic structural fixtures and warm filesystem cache; compressed pixels/frames are not decoded.",
            "These measurements isolate parser work, allocations and retained-range hashing, not whole-product or physical-drive recovery speed.",
            "Thread allocations cover synchronous assessments; process-wide allocations cover asynchronous scans and may include background runtime work.",
            "Run Release with DOTNET_TieredCompilation=0 on the same otherwise idle machine for before/after comparisons. Timings have no pass/fail threshold."
        },
        cases
    }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally { Directory.Delete(root, true); }

static CaseResult Assess(string name, string path, string kind, string expectedHash)
{
    long length = new FileInfo(path).Length;
    VerifySource(path, expectedHash);
    for (int i = 0; i < 2; i++) Verify(PhotoCarver.AssessFile(path, kind));
    List<Sample> runs = [];
    for (int i = 0; i < 5; i++)
    {
        long before = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
        var candidate = PhotoCarver.AssessFile(path, kind); timer.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Verify(candidate);
        runs.Add(new(timer.Elapsed.TotalMilliseconds, allocated, "assessment", 1, 0, "", Evidence(candidate!)));
    }
    VerifySource(path, expectedHash);
    return Summarize(name, "Single contiguous structural assessment and candidate SHA-256", path, expectedHash, 2, "current_thread", runs);

    void Verify(RecoveryCandidate? candidate)
    {
        if (candidate is null || candidate.Kind != kind || candidate.State != "complete" || candidate.Offset != 0 ||
            candidate.Length != length || candidate.Sha256 != expectedHash)
            throw new InvalidOperationException($"{name}: structural result or exact candidate hash changed.");
    }
}

static async Task<CaseResult> Scan(string name, string path, string expectedHash, bool video)
{
    long length = new FileInfo(path).Length;
    VerifySource(path, expectedHash);
    var options = new RecoveryOptions { MaximumFragmentBytes = 128, MaximumFileBytes = 128 * 1024 * 1024 };
    int warmups = video ? 2 : 1, samples = video ? 5 : 3;
    Task<RecoveryReport> Run() => video ? MediaCarver.ScanAsync(path, options) : PhotoCarver.ScanAsync(path);
    for (int i = 0; i < warmups; i++) Verify(await Run());
    List<Sample> runs = [];
    for (int i = 0; i < samples; i++)
    {
        long before = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
        var report = await Run(); timer.Stop();
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        Verify(report);
        runs.Add(new(timer.Elapsed.TotalMilliseconds, allocated, report.Status, report.Candidates.Count,
            report.ScannedBytes, report.Source.Sha256, video ? Evidence(report.Candidates[0]) : null));
    }
    VerifySource(path, expectedHash);
    return Summarize(name, video ? "Shared source scan retaining one 128-byte video fragment" : "Photo source scan rejecting 2,000 malformed signatures",
        path, expectedHash, warmups, "whole_process", runs);

    void Verify(RecoveryReport report)
    {
        if (report.Status != "complete_within_profile" || report.ScannedBytes != length || report.Source.Sha256 != expectedHash ||
            report.Candidates.Count != (video ? 1 : 0))
            throw new InvalidOperationException($"{name}: scan coverage, candidate count or full-source hash changed.");
        if (video && report.Candidates[0] is var candidate && (candidate.Kind != "mp4" || candidate.State != "partial" ||
            candidate.Length != 128 || candidate.Offset != 0 || candidate.Sha256 != "8200e9a82b8d9cbba75c42d299dd2cae200000f079af35c4a692c22db600f763"))
            throw new InvalidOperationException($"{name}: retained fragment bytes or partial state changed.");
    }
}

static CaseResult Summarize(string name, string scope, string path, string hash, int warmups, string allocationScope, List<Sample> runs)
    => new(name, scope, new FileInfo(path).Length, hash, warmups, runs.Count, allocationScope,
        runs.Select(r => r.Milliseconds).Order().ElementAt(runs.Count / 2),
        runs.Select(r => r.AllocatedBytes).Order().ElementAt(runs.Count / 2), runs);

static CandidateEvidence Evidence(RecoveryCandidate candidate) => new(candidate.Kind, candidate.State, candidate.Offset, candidate.Length, candidate.Sha256);

static void VerifySource(string path, string expectedHash)
{
    using var source = File.OpenRead(path);
    if (Convert.ToHexStringLower(SHA256.HashData(source)) != expectedHash)
        throw new InvalidOperationException("Benchmark source bytes differ from the deterministic fixture.");
}

static void Chunk(Stream stream, string name, byte[] bytes)
{
    var table = new uint[256];
    for (uint n = 0; n < 256; n++)
    {
        uint value = n;
        for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
        table[n] = value;
    }
    byte[] type = Encoding.ASCII.GetBytes(name);
    Span<byte> integer = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(integer, (uint)bytes.Length);
    stream.Write(integer); stream.Write(type); stream.Write(bytes);
    uint crc = uint.MaxValue;
    foreach (byte value in type) crc = table[(crc ^ value) & 255] ^ (crc >> 8);
    foreach (byte value in bytes) crc = table[(crc ^ value) & 255] ^ (crc >> 8);
    BinaryPrimitives.WriteUInt32BigEndian(integer, crc ^ uint.MaxValue); stream.Write(integer);
}

internal sealed record CandidateEvidence(string Kind, string State, long Offset, long Length, string Sha256);
internal sealed record Sample(double Milliseconds, long AllocatedBytes, string Status, int CandidateCount, long ScannedBytes, string SourceSha256, CandidateEvidence? Candidate);
internal sealed record CaseResult(string Name, string Scope, long SourceBytes, string SourceSha256, int WarmupRuns, int TimedRuns,
    string AllocationScope, double MedianMilliseconds, long MedianAllocatedBytes, List<Sample> Runs);
