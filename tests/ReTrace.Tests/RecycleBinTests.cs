using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReTrace.Core;

namespace ReTrace.Tests;

internal static class RecycleBinTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr attributes);

    internal static byte[] Metadata(int version, string original, long bytes)
    {
        byte[] path = Encoding.Unicode.GetBytes(original + '\0');
        var result = new byte[version == 1 ? 544 : 28 + path.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(result, (ulong)version);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), (ulong)bytes);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), (ulong)new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc());
        if (version == 2) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24), (uint)path.Length / 2);
        path.CopyTo(result.AsSpan(version == 1 ? 24 : 28));
        return result;
    }

    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Assert(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        async Task Reject(Func<Task> action, string message)
        {
            bool rejected = false; try { await action(); } catch (IOException) { rejected = true; }
            Assert(rejected, message);
        }
        byte[] png = CarverFixtures.Png();
        const string original = @"C:\Users\Synthetic\Pictures\Bild-ä-😀.png";
        foreach (int version in new[] { 1, 2 })
        {
            var metadata = RecycleBinRecovery.ParseMetadata(Metadata(version, original, png.Length));
            Assert(metadata.IsValid && metadata.Version == (ulong)version, "Both modern Windows metadata versions parse.");
            Assert(metadata.OriginalPath == original && metadata.DeclaredBytes == png.Length, "Unicode original paths and declared size remain exact source claims.");
            Assert(metadata.DeletedUtc == new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero), "Deletion FILETIME is rendered in UTC.");
        }
        Assert(RecycleBinRecovery.ParseMetadata([1, 2]).Diagnostics.Contains("truncated_header"), "Truncated metadata is diagnosed.");
        byte[] unsupported = Metadata(2, original, png.Length); unsupported[0] = 3;
        Assert(RecycleBinRecovery.ParseMetadata(unsupported).Diagnostics.Contains("unsupported_metadata_version"), "Unknown metadata versions are not guessed.");
        byte[] badCount = Metadata(2, original, png.Length); BinaryPrimitives.WriteUInt32LittleEndian(badCount.AsSpan(24), uint.MaxValue);
        Assert(RecycleBinRecovery.ParseMetadata(badCount).Diagnostics.Contains("invalid_path_character_count"), "Oversized path counts are rejected before allocation.");
        byte[] shortPath = Metadata(2, original, png.Length)[..^3];
        var truncated = RecycleBinRecovery.ParseMetadata(shortPath);
        Assert(truncated.Diagnostics.Contains("truncated_path") && truncated.Diagnostics.Contains("odd_utf16_path_bytes"), "Truncated odd UTF-16 is explicitly diagnosed.");
        byte[] badTime = Metadata(2, original, png.Length); Array.Clear(badTime, 16, 8);
        Assert(RecycleBinRecovery.ParseMetadata(badTime).DeletedUtc is null, "An invalid deletion time is not invented.");
        byte[] badUtf = Metadata(2, "a", 0); badUtf[28] = 0; badUtf[29] = 0xd8;
        Assert(RecycleBinRecovery.ParseMetadata(badUtf).Diagnostics.Contains("invalid_utf16_path"), "Unpaired UTF-16 surrogate input is diagnosed.");
        byte[] padding = Metadata(1, original, png.Length); padding[^1] = 1;
        Assert(RecycleBinRecovery.ParseMetadata(padding).Diagnostics.Contains("nonzero_path_padding"), "Unexpected v1 padding is not silently discarded.");
        string root = Path.Combine(Path.GetTempPath(), "retrace-bin-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string bin = Path.Combine(root, "copied-bin"); Directory.CreateDirectory(bin);
            void Pair(string token, string name, byte[] bytes, int version = 2)
            {
                File.WriteAllBytes(Path.Combine(bin, "$I" + token), Metadata(version, name, bytes.Length));
                File.WriteAllBytes(Path.Combine(bin, "$R" + token), bytes);
            }
            Pair("first.png", original, png);
            Pair("v1.txt", @"C:\Users\Synthetic\lost.txt", Encoding.UTF8.GetBytes("synthetic only"), 1);
            Pair("empty.txt", @"C:\Users\Synthetic\empty.txt", []);
            File.WriteAllBytes(Path.Combine(bin, "$Iorphan.txt"), Metadata(2, @"C:\lost.txt", 1));
            File.WriteAllBytes(Path.Combine(bin, "$Rorphan.bin"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(bin, "$Ibad.bin"), [1, 2, 3]); File.WriteAllBytes(Path.Combine(bin, "$Rbad.bin"), [4, 5, 6]);
            File.WriteAllBytes(Path.Combine(bin, "$Idirectory"), Metadata(2, @"C:\Users\Synthetic\directory", 0));
            Directory.CreateDirectory(Path.Combine(bin, "$Rdirectory")); File.WriteAllText(Path.Combine(bin, "$Rdirectory", "file.txt"), "own test");
            File.WriteAllText(Path.Combine(bin, "desktop.ini"), "Unrelated files are never parsed as deleted payloads.");
            var report = await RecycleBinRecovery.ScanAsync(bin);
            Assert(report.Schema == "retrace.recycle.v1" && report.Entries.Count == 7, "Immediate $I/$R pairs are grouped without unrelated files.");
            Assert(report.Status == "partial", "Orphan, corrupt and unsupported entries remain visible with partial coverage.");
            var first = report.Entries.Single(x => x.PayloadName == "$Rfirst.png");
            Assert(first.CanRecover && first.Name == "Bild-ä-😀.png" && first.OriginalPath == original && first.PayloadKind == "file", "A regular paired file has inspectable original source claims.");
            Assert(first.MetadataSha256.Length == 64 && first.PayloadHashVerified && first.PayloadIdentity!.Sha256 == CarverFixtures.Hash(png), "Metadata and bounded small payload bytes have SHA-256 inventory bindings.");
            Assert(first.ContentBytes == png.Length && first.DeclaredBytes == png.Length, "Observed and declared sizes are distinct fields.");
            var again = await RecycleBinRecovery.ScanAsync(bin);
            Assert(again.Entries.Single(x => x.PayloadName == first.PayloadName).Id == first.Id, "Entry IDs remain stable across unchanged inventory runs.");
            Pair("unrelated.txt", @"C:\else.txt", [9]);
            Assert((await RecycleBinRecovery.ScanAsync(bin)).Entries.Single(x => x.PayloadName == first.PayloadName).Id == first.Id, "Unrelated bin directory timestamp changes do not invalidate a selected file ID.");
            Assert(!report.Entries.Single(x => x.MetadataName == "$Iorphan.txt").CanRecover, "Metadata without payload is not recoverable.");
            Assert(report.Entries.Single(x => x.PayloadName == "$Rorphan.bin").CanRecover, "An orphan ordinary payload can be copied with unknown original path.");
            var bad = report.Entries.Single(x => x.PayloadName == "$Rbad.bin");
            Assert(bad.CanRecover && bad.State == "metadata_invalid" && bad.OriginalPath == "", "Corrupt metadata does not hide safely bound payload bytes or invent a path.");
            var directory = report.Entries.Single(x => x.PayloadName == "$Rdirectory");
            Assert(directory.IsDirectory && !directory.CanRecover && directory.State == "directory_unsupported", "Folder payloads are explicitly unsupported rather than partially restored as complete.");

            string destination = Path.Combine(root, "recovered");
            var exported = await RecycleBinRecovery.RestoreAsync(report, first.Id, destination);
            Assert(exported.Schema == "retrace.recycle-export.v1" && exported.Status == "complete" && exported.Files.Count == 1, "Selected payload copy has a recovery receipt.");
            Assert(File.ReadAllBytes(exported.Files[0].Path).SequenceEqual(png) && exported.Files[0].Sha256 == CarverFixtures.Hash(png), "Copied PNG is byte-exact and independently SHA-256 verified.");
            Assert(File.Exists(Path.Combine(bin, first.MetadataName!)) && File.ReadAllBytes(Path.Combine(bin, first.PayloadName!)).SequenceEqual(png), "Both original metadata and payload are retained.");
            Assert(Path.GetFileName(exported.Files[0].Path).StartsWith("recovered_00001_"), "Untrusted original names never determine output paths.");
            await Reject(() => RecycleBinRecovery.RestoreAsync(report, first.Id, destination), "An existing recovery destination is never reused.");
            await Reject(() => RecycleBinRecovery.RestoreAsync(report, first.Id, Path.Combine(bin, "nested")), "Recovery cannot write into its source bin.");
            await Reject(() => RecycleBinRecovery.RestoreAsync(report, directory.Id, Path.Combine(root, "folder-unsupported")), "Unsupported folder restore is rejected before output creation.");
            await Reject(() => RecycleBinRecovery.RestoreAsync(report with { Entries = [first, first] }, first.Id, Path.Combine(root, "duplicate")), "Duplicate report entry IDs are rejected.");
            await Reject(() => RecycleBinRecovery.RestoreAsync(report with { Entries = [first with { PayloadIdentity = first.PayloadIdentity! with { Path = Path.Combine(root, "escape.png") } }] }, first.Id, Path.Combine(root, "path-escape")), "Imported source paths cannot escape their $R binding.");
            await Reject(() => RecycleBinRecovery.RestoreAsync(report with { Entries = [first with { Name = "tampered.exe" }] }, first.Id, Path.Combine(root, "name-tamper")), "Display/source claim tampering invalidates the report binding.");

            var empty = report.Entries.Single(x => x.PayloadName == "$Rempty.txt");
            var emptyExport = await RecycleBinRecovery.RestoreAsync(report, empty.Id, Path.Combine(root, "empty-output"));
            Assert(emptyExport.Status == "complete" && emptyExport.Files.Single().Bytes == 0 && new FileInfo(emptyExport.Files.Single().Path).Length == 0, "An empty deleted file is recoverable and receives its empty-byte hash.");
            var multi = await RecycleBinRecovery.ExportAsync(report, [first.Id, bad.Id], Path.Combine(root, "multiple"));
            Assert(multi.Status == "complete" && multi.Files.Count == 2, "Individually selected paired and malformed-metadata payloads can be copied together.");

            string savedPath = Path.Combine(root, "recycle-report.json"); await RecycleBinRecovery.SaveReportAsync(report, savedPath);
            var loaded = await RecycleBinRecovery.LoadReportAsync(savedPath);
            Assert(loaded.Verification == "saved_metadata_not_revalidated" && loaded.Entries.Single(x => x.Id == first.Id).PayloadIdentity == first.PayloadIdentity, "Saved report preserves bindings without claiming current-source revalidation.");
            Assert((await RecycleBinRecovery.RestoreAsync(loaded, first.Id, Path.Combine(root, "loaded-output"))).Files.Count == 1, "A reopened report actually restores through current source/hash checks.");
            string otherBin = Path.Combine(root, "unrelated-bin"); Directory.CreateDirectory(otherBin);
            string otherMeta = Path.Combine(otherBin, "$Iother.txt"), otherPayload = Path.Combine(otherBin, "$Rother.txt");
            File.WriteAllBytes(otherMeta, Metadata(2, @"F:\Synthetic\other.txt", 1)); File.WriteAllBytes(otherPayload, [8]);
            var otherReport = await RecycleBinRecovery.ScanAsync(otherBin);
            var combined = report with { Roots = [.. report.Roots, .. otherReport.Roots], Entries = [.. report.Entries, .. otherReport.Entries] };
            File.Delete(otherMeta); File.Delete(otherPayload); Directory.Delete(otherBin);
            Assert((await RecycleBinRecovery.RestoreAsync(combined, first.Id, Path.Combine(root, "unrelated-disconnected"))).Status == "complete", "A vanished unrelated source does not block recovery from the healthy selected bin.");
            await Reject(() => RecycleBinRecovery.SaveReportAsync(report, savedPath), "Saved report export never overwrites metadata.");
            await Reject(() => RecycleBinRecovery.SaveReportAsync(report, Path.Combine(bin, "report.json")), "Metadata report export does not write into the source bin.");
            string missingSchema = Path.Combine(root, "missing-schema.json"); File.WriteAllText(missingSchema, "{}");
            await Reject(() => RecycleBinRecovery.LoadReportAsync(missingSchema), "Missing JSON fields cannot silently create an empty valid report.");
            var unknown = JsonNode.Parse(File.ReadAllText(savedPath))!; unknown["Unknown"] = true;
            string unknownPath = Path.Combine(root, "unknown.json"); File.WriteAllText(unknownPath, unknown.ToJsonString());
            await Reject(() => RecycleBinRecovery.LoadReportAsync(unknownPath), "Unknown report fields are rejected.");
            string duplicate = Path.Combine(root, "duplicate.json"); File.WriteAllText(duplicate, File.ReadAllText(savedPath).Replace("\"Version\":", "\"Schema\":\"retrace.recycle.v1\",\"Version\":", StringComparison.Ordinal));
            await Reject(() => RecycleBinRecovery.LoadReportAsync(duplicate), "Duplicate JSON properties are rejected.");

            string payloadPath = Path.Combine(bin, first.PayloadName!);
            long payloadTime = File.GetLastWriteTimeUtc(payloadPath).Ticks;
            byte[] changed = png.ToArray(); changed[20] ^= 1; File.WriteAllBytes(payloadPath, changed); File.SetLastWriteTimeUtc(payloadPath, new DateTime(payloadTime, DateTimeKind.Utc));
            await Reject(() => RecycleBinRecovery.RestoreAsync(report, first.Id, Path.Combine(root, "changed-payload")), "Payload SHA detects byte changes even when length and timestamp are restored.");
            Assert(!Directory.Exists(Path.Combine(root, "changed-payload")), "Changed payload is rejected before any output directory is created.");
            File.WriteAllBytes(payloadPath, png); File.SetLastWriteTimeUtc(payloadPath, new DateTime(payloadTime, DateTimeKind.Utc));
            string metadataPath = Path.Combine(bin, first.MetadataName!); long metaTime = File.GetLastWriteTimeUtc(metadataPath).Ticks;
            byte[] metadataBytes = File.ReadAllBytes(metadataPath); byte[] metadataChanged = metadataBytes.ToArray(); metadataChanged[32] ^= 1;
            File.WriteAllBytes(metadataPath, metadataChanged); File.SetLastWriteTimeUtc(metadataPath, new DateTime(metaTime, DateTimeKind.Utc));
            await Reject(() => RecycleBinRecovery.RestoreAsync(report, first.Id, Path.Combine(root, "changed-metadata")), "Metadata SHA detects same-size, preserved-timestamp changes.");
            File.WriteAllBytes(metadataPath, metadataBytes); File.SetLastWriteTimeUtc(metadataPath, new DateTime(metaTime, DateTimeKind.Utc));

            using var preCopyCancel = new CancellationTokenSource(); bool cancelled = false;
            try { await RecycleBinRecovery.RestoreAsync(report, first.Id, Path.Combine(root, "pre-copy-cancel"), new CarverTests.InlineProgress(p => { if (p.Stage == "recycle_verifying") preCopyCancel.Cancel(); }), preCopyCancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && !Directory.Exists(Path.Combine(root, "pre-copy-cancel")), "Cancellation during initial hash verification creates no output.");
            using var copyCancel = new CancellationTokenSource();
            var interrupted = await RecycleBinRecovery.RestoreAsync(report, first.Id, Path.Combine(root, "copy-cancel"), new CarverTests.InlineProgress(p => { if (p.Stage == "recycle_copying") copyCancel.Cancel(); }), copyCancel.Token);
            Assert(interrupted.Status == "cancelled_partial" && interrupted.Files.Count == 0 && File.Exists(Path.Combine(interrupted.Destination, "recovery.json")), "Interrupted copy retains an honest receipt and never publishes an unverified file.");
            using var scanCancel = new CancellationTokenSource();
            var partial = await RecycleBinRecovery.ScanAsync(bin, new CarverTests.InlineProgress(_ => scanCancel.Cancel()), scanCancel.Token);
            Assert(partial.Status == "cancelled_partial" && partial.Entries.Count == 1, "Cancelled inventory keeps already bound entries with an explicit partial state.");
            Assert(File.ReadAllBytes(payloadPath).SequenceEqual(png), "Inventory and recovery cancellation do not mutate the original payload.");

            if (OperatingSystem.IsWindows())
            {
                string linked = Path.Combine(bin, "$Rlinked.png");
                Assert(CreateHardLinkW(linked, payloadPath, IntPtr.Zero), "The owned hard-link fixture was created.");
                var withLink = await RecycleBinRecovery.ScanAsync(bin);
                Assert(!withLink.Entries.Single(x => x.PayloadName == "$Rlinked.png").CanRecover, "A hard-linked deleted payload is rejected.");
                Assert(!withLink.Entries.Single(x => x.PayloadName == first.PayloadName).CanRecover, "The corresponding multi-link original is also rejected.");
                File.Delete(linked);
            }
            else TestCoverage.Skip("recycle_bin_hardlinks", "The native hard-link payload fixture requires Windows.");
            File.Delete(payloadPath);
            await Reject(() => RecycleBinRecovery.RestoreAsync(report, first.Id, Path.Combine(root, "missing-payload")), "Removed source payloads are rejected before recovery.");
            Assert(!Directory.Exists(Path.Combine(root, "missing-payload")), "A vanished payload creates no output directory.");
            string fullBin = Path.Combine(root, "limit-bin"); Directory.CreateDirectory(fullBin);
            byte[] emptyMetadata = Metadata(2, @"C:\Synthetic\limit.txt", 0);
            for (int index = 0; index < RecycleBinRecovery.MaximumEntries; index++)
            {
                File.WriteAllBytes(Path.Combine(fullBin, $"$I{index:D5}.txt"), emptyMetadata);
                File.WriteAllBytes(Path.Combine(fullBin, $"$R{index:D5}.txt"), []);
            }
            var atLimit = await RecycleBinRecovery.ScanAsync(fullBin);
            Assert(atLimit.Entries.Count == RecycleBinRecovery.MaximumEntries && atLimit.Entries.All(x => x.State == "paired"), "A complete exact-cap set remains bounded without requiring an extra entry to discover the limit.");
            Assert(atLimit.Status == "partial" && atLimit.Diagnostics.Contains("maximum_entries_reached"), "Reaching the global cap explicitly reports possible unexamined roots instead of complete coverage.");
        }
        finally { CarverFixtures.Cleanup(root); }
        return checks;
    }
}
