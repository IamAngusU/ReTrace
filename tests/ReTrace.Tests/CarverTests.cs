using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using ReTrace.Core;

internal static class CarverTests
{
    public static int Run()
    {
        int assertions = 0;
        void Assert(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
        string root = Path.Combine(Path.GetTempPath(), "retrace-carver-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string Save(string name, byte[] bytes) { string path = Path.Combine(root, name); File.WriteAllBytes(path, bytes); return path; }
            RecoveryReport Scan(string path, RecoveryOptions? options = null, IProgress<RecoveryProgress>? progress = null, CancellationToken token = default)
                => PhotoCarver.ScanAsync(path, options, progress, token).GetAwaiter().GetResult();
            byte[] png = CarverFixtures.Png();
            var exact = Scan(Save("intact-png.img", png));
            Assert(exact.Status == "complete_within_profile" && exact.ScannedBytes == png.Length, "Complete PNG scan status.");
            Assert(exact.Candidates.Count == 1 && exact.Candidates[0].State == "complete", "Intact PNG format structure.");
            Assert(exact.Candidates[0].Offset == 0 && exact.Candidates[0].Length == png.Length, "PNG exact byte span.");
            Assert(exact.Candidates[0].Sha256 == CarverFixtures.Hash(png) && exact.Source.Sha256 == CarverFixtures.Hash(png), "PNG and full-source SHA-256.");
            Assert(!exact.Exhaustive && exact.Candidates[0].DeletionStatus == "unknown", "Carving cannot assert deletion or exhaustive recovery.");
            Assert(exact.Source.FileId.Length > 0 || !OperatingSystem.IsWindows(), "Stable Windows source file ID.");

            byte[] damaged = (byte[])png.Clone(); damaged[^1] ^= 1;
            var badCrc = Scan(Save("bad-crc.img", damaged));
            Assert(badCrc.Candidates.Single().State == "damaged", "PNG CRC mismatch is not complete.");
            Assert(badCrc.Candidates[0].Diagnostics.Any(x => x.Contains("CRC")), "PNG CRC diagnostic.");
            var truncated = Scan(Save("truncated.img", png[..^6]));
            Assert(truncated.Candidates.Single().State == "partial", "Truncated PNG retained as partial.");
            Assert(truncated.Candidates[0].Length == png.Length - 6, "Partial PNG is never padded or repaired.");
            Assert(Scan(Save("no-fragments.img", png[..^6]), new() { IncludeFragments = false }).Candidates.Count == 0, "Fragments can be disabled.");

            byte[] invalid = (byte[])png.Clone(); invalid.AsSpan(16, 4).Clear();
            Assert(Scan(Save("zero-width.img", invalid)).Candidates.Count == 0, "Impossible PNG dimensions rejected.");
            byte[] falseHeader = [137, 80, 78, 71, 13, 10, 26, 10, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2];
            Assert(Scan(Save("false-header.img", falseHeader)).Candidates.Count == 0, "PNG magic alone is insufficient.");

            byte[] jpeg = CarverFixtures.StructuralJpeg();
            var jpg = Scan(Save("jpeg.img", jpeg));
            Assert(jpg.Candidates.Single().Kind == "jpeg" && jpg.Candidates[0].State == "complete", "JPEG frame/scan/EOI structure.");
            Assert(jpg.Candidates[0].Length == jpeg.Length, "JPEG ends at EOI.");
            Assert(Scan(Save("jpeg-partial.img", jpeg[..^2])).Candidates.Single().State == "partial", "JPEG with missing EOI remains partial.");
            Assert(Scan(Save("jpeg-false.img", [255, 216, 255, 224, 0, 4, 65, 65, 255, 217])).Candidates.Count == 0, "SOI and APP alone do not qualify as JPEG.");

            byte[] boundary = new byte[2 * 1024 * 1024 + 512];
            png.CopyTo(boundary, 1024 * 1024 - 4);
            var across = Scan(Save("boundary.img", boundary));
            Assert(across.Candidates.Single().Offset == 1024 * 1024 - 4, "Signature crossing a 1 MiB boundary is found once.");
            Assert(across.Candidates[0].Sha256 == CarverFixtures.Hash(png), "Boundary span remains exact.");

            byte[] large = new byte[33 * 1024 * 1024 + jpeg.Length];
            jpeg.CopyTo(large, 33 * 1024 * 1024);
            var beyond = Scan(Save("beyond-32MiB.img", large));
            Assert(beyond.ScannedBytes == large.Length && beyond.Candidates.Single().Offset == 33 * 1024 * 1024, "Streaming scan reaches beyond the former 32 MiB profile.");
            Assert(beyond.Source.Sha256 == CarverFixtures.Hash(large), "Large image source SHA covers all bytes.");

            byte[] fragment = CarverFixtures.OversizedTruncatedPng(5 * 1024 * 1024);
            var capped = Scan(Save("fragment-cap.img", fragment));
            Assert(capped.Candidates.Single().State == "partial" && capped.Candidates[0].Length == 4 * 1024 * 1024, "Partial PNG cap is enforced.");
            Assert(capped.Candidates[0].Diagnostics.Any(x => x.Contains("capped")), "Fragment truncation is explicit.");

            byte[] multi = new byte[2 * 1024 * 1024]; png.CopyTo(multi, 15);
            using var cancel = new CancellationTokenSource();
            var cancelled = Scan(Save("cancel.img", multi), progress: new InlineProgress(p => { if (p.BytesDone >= 1024 * 1024) cancel.Cancel(); }), token: cancel.Token);
            Assert(cancelled.Status == "cancelled_partial" && cancelled.Source.Sha256 == "", "Cancellation never produces a full-source hash.");
            Assert(cancelled.Candidates.Count == 1 && cancelled.Candidates[0].Sha256 == CarverFixtures.Hash(png), "Cancellation preserves previously verified candidates.");

            string locked = Save("locked.img", png);
            bool denied = false;
            Scan(locked, progress: new InlineProgress(p =>
            {
                if (p.Stage != "scanning" || p.BytesDone != 0) return;
                try { using var writer = new FileStream(locked, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
                catch (IOException) { denied = true; }
            }));
            Assert(denied || !OperatingSystem.IsWindows(), "Active image scan denies concurrent writers on Windows.");
            bool invalidOptions = false;
            try { Scan(locked, new() { MaximumCandidates = 0 }); } catch (ArgumentOutOfRangeException) { invalidOptions = true; }
            Assert(invalidOptions, "Invalid scan limits rejected.");

            var bounded = Scan(Save("signature-budget.img", [..png, ..jpeg]), new() { MaximumCandidates = 1 });
            Assert(bounded.Status == "partial_signature_limit" && bounded.Candidates.Count == 1 && bounded.Source.Sha256 == "", "Signature budget is visibly partial.");
            Assert(PhotoCarver.AssessFile(locked, "png")?.State == "complete", "Standalone specialist assessment shares structural checks.");
            byte[] overlap = new byte[2 * 1024 * 1024];
            byte[] unfinishedHeader = CarverFixtures.OversizedTruncatedPng(5 * 1024 * 1024)[..41];
            for (int at = 0; at < overlap.Length - unfinishedHeader.Length; at += 8192) unfinishedHeader.CopyTo(overlap, at);
            var overlapBounded = Scan(Save("overlap-budget.img", overlap));
            Assert(overlapBounded.Status == "partial_validation_budget" && overlapBounded.Candidates.Count < 256, "Nested partial signatures cannot trigger unbounded repeated scanning.");
            Assert(overlapBounded.Source.Sha256 == "" && overlapBounded.Diagnostics.Any(x => x.Contains("budget")), "Overlapping validation quota is explicit and never falsely complete.");
            foreach (string unsafePath in new[] { "relative.img", @"\\.\PhysicalDrive0", locked + ":alternate-stream" })
            {
                bool rejected = false;
                try { using var ignored = PathSafety.OpenRead(unsafePath); } catch (IOException) { rejected = true; }
                Assert(rejected, "Relative, device and alternate-stream source paths are rejected.");
            }
            if (OperatingSystem.IsWindows())
            {
                string alias = Path.Combine(root, "hard-linked.img");
                Assert(CreateHardLink(alias, locked, IntPtr.Zero), "Synthetic hard-link fixture could not be created.");
                bool deniedAlias = false;
                try { using var ignored = PathSafety.OpenRead(alias); } catch (IOException) { deniedAlias = true; }
                Assert(deniedAlias, "Hard-linked source images are rejected.");
            }
        }
        finally { CarverFixtures.Cleanup(root); }
        return assertions;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string alias, string existing, IntPtr attributes);

    internal sealed class InlineProgress(Action<RecoveryProgress> action) : IProgress<RecoveryProgress>
    { public void Report(RecoveryProgress value) => action(value); }
}

internal static class CarverFixtures
{
    public static void Cleanup(string root)
    {
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(root);
        if (!target.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("retrace-", StringComparison.Ordinal))
            throw new IOException("Synthetic test cleanup escaped its named temporary directory.");
        Directory.Delete(target, true);
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static byte[] Png()
    {
        using var stream = new MemoryStream();
        stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(header, 1); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1); header[8] = 8; header[9] = 2;
        Chunk(stream, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true)) zlib.Write([0, 255, 127, 0]);
        Chunk(stream, "IDAT", compressed.ToArray()); Chunk(stream, "IEND", []);
        return stream.ToArray();
    }
    public static byte[] OversizedTruncatedPng(int bytes)
    {
        byte[] prefix = Png()[..33];
        byte[] result = new byte[bytes + 41]; prefix.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(33), (uint)(bytes + 1024)); "IDAT"u8.CopyTo(result.AsSpan(37));
        return result;
    }
    // A controlled marker fixture, deliberately not represented as a decoded JPEG photograph.
    public static byte[] StructuralJpeg() => [255, 216, 255, 224, 0, 4, 65, 65, 255, 192, 0, 11, 8, 0, 1, 0, 1, 1, 1, 0x11, 0, 255, 218, 0, 8, 1, 1, 0, 0, 63, 0, 0x12, 0x34, 255, 0, 0x56, 255, 209, 0x78, 255, 217];
    private static void Chunk(Stream stream, string name, byte[] bytes)
    {
        byte[] type = System.Text.Encoding.ASCII.GetBytes(name);
        Span<byte> integer = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(integer, (uint)bytes.Length); stream.Write(integer); stream.Write(type); stream.Write(bytes);
        uint crc = uint.MaxValue;
        foreach (byte b in type.Concat(bytes)) { crc ^= b; for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1; }
        BinaryPrimitives.WriteUInt32BigEndian(integer, crc ^ uint.MaxValue); stream.Write(integer);
    }
}
