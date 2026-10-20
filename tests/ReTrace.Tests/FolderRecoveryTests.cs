using System.Security.Cryptography;
using ReTrace.Core;

namespace ReTrace.Tests;

internal static class FolderRecoveryTests
{
    private sealed class InlineProgress(Action<RecoveryProgress> action) : IProgress<RecoveryProgress>
    {
        public void Report(RecoveryProgress value) => action(value);
    }

    public static async Task<int> RunAsync()
    {
        int assertions = 0;
        void Assert(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
        string root = Path.Combine(Path.GetTempPath(), "retrace-folder-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "chosen");
            Directory.CreateDirectory(source);
            string hidden = Path.Combine(source, "hidden-cache.bin");
            byte[] png = CarverFixtures.Png();
            File.WriteAllBytes(hidden, [..new byte[100], ..png, ..new byte[33]]);
            File.SetAttributes(hidden, FileAttributes.Hidden);
            string backups = Path.Combine(source, "backups");
            Directory.CreateDirectory(backups);
            string backup = Path.Combine(backups, "older-copy.bak");
            File.WriteAllBytes(backup, [..new byte[20], ..CarverFixtures.StructuralJpeg()]);
            File.WriteAllText(Path.Combine(source, "ordinary.txt"), "Not an image; no execution or extension assumptions.");
            byte[] original = File.ReadAllBytes(hidden);
            var found = await FolderRecovery.ScanAsync(source);
            Assert(found.Schema == "retrace.folder-search.v1" && found.Status == "complete_within_profile", "Ordinary chosen folder completes within its profile.");
            Assert(found.ScannedFiles == 3 && found.Containers.Count == 2, "Hidden containers and nested backup copies are examined.");
            Assert(found.Containers.Single(c => c.Recovery.Source.Path == hidden).Recovery.Candidates.Single().Offset == 100, "Embedded image bytes have exact container offsets.");
            Assert(found.Containers.SelectMany(c => c.Recovery.Candidates).All(c => c.DeletionStatus == "unknown"), "Copies or thumbnails never establish deletion.");
            Assert(!found.Exhaustive && found.Diagnostics.Any(x => x.Contains("unallocated")), "Folder coverage cannot claim absent free-space sectors.");
            Assert(found.ScannedBytes == Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length), "Actual source byte counts cover the examined containers.");
            var container = found.Containers.Single(c => c.Recovery.Source.Path == hidden);
            string selected = container.Id + ":" + container.Recovery.Candidates[0].Id;
            var exported = await FolderRecovery.RestoreAsync(found, [selected], Path.Combine(root, "recovered"));
            Assert(exported.Status == "complete" && exported.Files.Count == 1, "Selected folder hit exports through its bound native source.");
            Assert(File.ReadAllBytes(exported.Files[0].Path).SequenceEqual(png), "Embedded recovery exports only the exact PNG bytes.");
            Assert(exported.Files[0].CandidateId == selected && exported.Files[0].Sha256 == CarverFixtures.Hash(png), "Composite IDs and written hashes remain bound.");
            Assert(File.ReadAllBytes(hidden).SequenceEqual(original), "Folder scanning and export preserve original container bytes.");
            bool duplicate = false;
            try { await FolderRecovery.RestoreAsync(found, [selected], exported.Destination); } catch (IOException) { duplicate = true; }
            Assert(duplicate, "Existing destination directories are never overwritten.");
            string reportPath = Path.Combine(root, "report.json");
            await FolderRecovery.SaveReportAsync(found, reportPath);
            Assert(File.ReadAllText(reportPath).Contains("retrace.folder-search.v1"), "Metadata report can be passed to TraceLedger.");
            bool reportOverwrite = false;
            try { await FolderRecovery.SaveReportAsync(found, reportPath); } catch (IOException) { reportOverwrite = true; }
            Assert(reportOverwrite, "An existing report is never replaced.");
            bool sourceOutput = false;
            try { await FolderRecovery.RestoreAsync(found, [selected], Path.Combine(source, "new-output")); } catch (IOException) { sourceOutput = true; }
            Assert(sourceOutput && !Directory.Exists(Path.Combine(source, "new-output")), "Recovery cannot create new files inside the investigated source tree.");
            bool sourceReport = false;
            try { await FolderRecovery.SaveReportAsync(found, Path.Combine(source, "new-report.json")); } catch (IOException) { sourceReport = true; }
            Assert(sourceReport && !File.Exists(Path.Combine(source, "new-report.json")), "Metadata export cannot write inside the investigated source tree.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var interrupted = await FolderRecovery.ScanAsync(source, cancellationToken: cancelled.Token);
            Assert(interrupted.Status == "cancelled_partial" && interrupted.ScannedFiles == 0, "Pre-cancelled folder search returns explicit partial coverage.");
            var shallow = await FolderRecovery.ScanAsync(source, new() { MaximumDepth = 0 });
            Assert(shallow.Status == "partial_profile_limits" && shallow.Containers.Count == 1, "Depth-limited backup coverage is explicit.");
            var capped = await FolderRecovery.ScanAsync(source, new() { MaximumFiles = 1 });
            Assert(capped.Status == "partial_profile_limits" && capped.ScannedFiles == 1, "Maximum file count is a real bound.");
            var entryCap = await FolderRecovery.ScanAsync(source, new() { MaximumEntries = 1 });
            Assert(entryCap.Status == "partial_profile_limits" && entryCap.VisitedEntries == 1, "Enumeration has a separate bounded entry count.");
            var byteCap = await FolderRecovery.ScanAsync(source, new() { MaximumSourceBytes = 1 });
            Assert(byteCap.Status == "partial_profile_limits" && byteCap.ScannedBytes == 0, "A read budget skips files that cannot fit; it is not silently exceeded.");
            var containerCap = await FolderRecovery.ScanAsync(source, new() { MaximumContainerBytes = 1 });
            Assert(containerCap.Status == "partial_profile_limits" && containerCap.ScannedFiles == 0, "Per-container size limit is real.");
            byte[] changed = File.ReadAllBytes(hidden); changed[120] ^= 1;
            File.SetAttributes(hidden, FileAttributes.Normal);
            File.WriteAllBytes(hidden, changed);
            var altered = await FolderRecovery.RestoreAsync(found, [selected], Path.Combine(root, "changed"));
            Assert(altered.Status == "failed_partial" && altered.Files.Count == 0, "Modified source bytes are never published as verified recovery.");
            bool unknown = false;
            try { await FolderRecovery.RestoreAsync(found, ["unknown"], Path.Combine(root, "unknown")); } catch (IOException) { unknown = true; }
            Assert(unknown && !Directory.Exists(Path.Combine(root, "unknown")), "Unknown candidate IDs fail before output creation.");
            bool badOptions = false;
            try { await FolderRecovery.ScanAsync(source, new() { MaximumFiles = 0 }); } catch (ArgumentOutOfRangeException) { badOptions = true; }
            Assert(badOptions, "Unsupported folder budgets are rejected.");
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            CarverFixtures.Cleanup(root);
        }
        return assertions;
    }
}
