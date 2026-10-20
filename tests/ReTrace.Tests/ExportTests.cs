using ReTrace.Core;

internal static class ExportTests
{
    public static int Run()
    {
        int assertions = 0;
        void Assert(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
        void Reject(Action action, string message) { bool rejected = false; try { action(); } catch (IOException) { rejected = true; } Assert(rejected, message); }
        string root = Path.Combine(Path.GetTempPath(), "retrace-export-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string volumeRoot = Path.GetPathRoot(root)!;
            Assert(PathSafety.CheckPath(volumeRoot) == volumeRoot, "An ordinary filesystem root passes ancestor validation, including the one-character POSIX root.");
            byte[] png = CarverFixtures.Png();
            string source = Path.Combine(root, "source.img"); File.WriteAllBytes(source, [0, 0, 0, ..png, 0, 0, 0]);
            var report = PhotoCarver.ScanAsync(source).GetAwaiter().GetResult();
            var candidate = report.Candidates.Single();
            ExportReport Export(RecoveryReport value, string name, IProgress<RecoveryProgress>? progress = null, CancellationToken token = default)
                => RecoveryExporter.ExportAsync(value, value.Candidates.Select(x => x.Id), Path.Combine(root, name), progress, token).GetAwaiter().GetResult();
            var copied = Export(report, "recovered");
            Assert(copied.Status == "complete" && copied.Files.Count == 1, "Selected PNG export succeeds.");
            Assert(File.ReadAllBytes(copied.Files[0].Path).SequenceEqual(png), "Recovery is byte-exact.");
            Assert(copied.Files[0].Sha256 == CarverFixtures.Hash(png), "Export is independently SHA-256 verified.");
            Assert(File.Exists(Path.Combine(copied.Destination, "recovery.json")), "Recovery receipt exists.");
            Assert(File.ReadAllBytes(source).SequenceEqual(new byte[] { 0, 0, 0 }.Concat(png).Concat(new byte[] { 0, 0, 0 })), "Recovery does not alter source image.");
            Reject(() => Export(report, "recovered"), "An existing destination is never reused.");

            var renamed = report with { Candidates = [candidate with { Name = "../../outside.png", Id = "../../escape" }] };
            var safe = Export(renamed, "generated-names");
            Assert(safe.Status == "complete" && Path.GetFileName(safe.Files.Single().Path).StartsWith("recovered_00001_"), "Untrusted original names and IDs never become output paths.");
            Assert(!File.Exists(Path.Combine(root, "outside.png")), "Path-escape name cannot escape recovery folder.");

            var badOffset = report with { Candidates = [candidate with { Offset = -1 }] };
            Reject(() => Export(badOffset, "bad-offset"), "Negative source mapping is rejected before destination creation.");
            Assert(!Directory.Exists(Path.Combine(root, "bad-offset")), "Invalid mappings leave no destination.");
            var badHash = report with { Candidates = [candidate with { Sha256 = new string('0', 64) }] };
            var mismatched = Export(badHash, "bad-hash");
            Assert(mismatched.Status == "failed_partial" && mismatched.Files.Count == 0, "Candidate hash mismatch yields no verified recovery.");
            Assert(Directory.GetFiles(mismatched.Destination).Select(Path.GetFileName).SequenceEqual(new[] { "recovery.json" }), "Hash mismatch is detected before candidate output is written.");

            string spool = Path.Combine(root, "specialist-run"); Directory.CreateDirectory(spool);
            string stored = Path.Combine(spool, "photorec.png"); File.WriteAllBytes(stored, png);
            var specialist = report with { RunDirectory = spool, Candidates = [candidate with { Offset = -1, Mapping = "external_reconstruction", StoredPath = stored, Length = png.Length }] };
            var specialistCopy = Export(specialist, "specialist-export");
            Assert(specialistCopy.Status == "complete" && File.ReadAllBytes(specialistCopy.Files.Single().Path).SequenceEqual(png), "PhotoRec-style reconstruction is exported by stored SHA without inventing source offset.");
            var escaped = specialist with { Candidates = [specialist.Candidates[0] with { StoredPath = source }] };
            Reject(() => Export(escaped, "outside-spool"), "Stored specialist candidate must remain inside its run.");
            Reject(() => RecoveryExporter.ExportAsync(specialist, [candidate.Id], Path.Combine(spool, "nested-export")).GetAwaiter().GetResult(), "Recovery output cannot be placed inside the specialist spool.");

            using var cancel = new CancellationTokenSource();
            var cancelled = Export(report, "cancelled", new CarverTests.InlineProgress(_ => cancel.Cancel()), cancel.Token);
            Assert(cancelled.Status == "cancelled_partial" && cancelled.Files.Count == 0, "Interrupted export cannot publish an unverified candidate.");
            Assert(File.Exists(Path.Combine(cancelled.Destination, "recovery.json")), "Cancellation preserves an explicit receipt.");
            Assert(Directory.GetFiles(cancelled.Destination).Any(x => x.EndsWith(".png")), "Interrupted output bytes are retained for inspection.");

            long timestamp = File.GetLastWriteTimeUtc(source).Ticks;
            byte[] changed = File.ReadAllBytes(source); changed[0] = 1; File.WriteAllBytes(source, changed); File.SetLastWriteTimeUtc(source, new DateTime(timestamp + TimeSpan.TicksPerSecond, DateTimeKind.Utc));
            Reject(() => Export(report, "changed-source"), "Source metadata mutation invalidates report identity.");
            Assert(!Directory.Exists(Path.Combine(root, "changed-source")), "Source mutation is rejected before output directory creation.");

            var rescanned = PhotoCarver.ScanAsync(source).GetAwaiter().GetResult();
            long preserved = File.GetLastWriteTimeUtc(source).Ticks;
            byte[] tampered = File.ReadAllBytes(source); tampered[(int)rescanned.Candidates[0].Offset + 20] ^= 1; File.WriteAllBytes(source, tampered); File.SetLastWriteTimeUtc(source, new DateTime(preserved, DateTimeKind.Utc));
            var hashBound = Export(rescanned, "changed-bytes");
            Assert(hashBound.Status == "failed_partial" && hashBound.Files.Count == 0, "Selected-byte SHA detects mutation even if size and timestamp are restored.");
            File.Delete(source);
            var disconnected = Export(specialist, "disconnected-source");
            Assert(disconnected.Status == "complete" && File.ReadAllBytes(disconnected.Files.Single().Path).SequenceEqual(png), "Verified specialist spool remains recoverable after original image disconnects.");
            Assert(disconnected.Diagnostics.Any(x => x.Contains("not reopened")), "Disconnected original identity is not falsely claimed as revalidated.");
            var document = specialist with { Candidates = [specialist.Candidates[0] with { Kind = "docx" }] };
            Assert(Export(document, "document-extension").Files.Single().Path.EndsWith(".docx"), "Safe specialist extensions are preserved.");
            var unsafeKind = specialist with { Candidates = [specialist.Candidates[0] with { Kind = "../exe" }] };
            Assert(Export(unsafeKind, "unsafe-extension").Files.Single().Path.EndsWith(".bin"), "Untrusted extension text falls back to bin.");
        }
        finally { CarverFixtures.Cleanup(root); }
        return assertions;
    }
}
