using ReTrace.Core;

namespace ReTrace.Tests;

internal static class MediaIntegrationTests
{
    private sealed class InlineProgress(Action<RecoveryProgress> action) : IProgress<RecoveryProgress>
    {
        public void Report(RecoveryProgress value) => action(value);
    }

    public static async Task<int> RunAsync()
    {
        int assertions = 0;
        void Assert(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
        string root = Path.Combine(Path.GetTempPath(), "retrace-media-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] video = VideoCarverTests.Mp4(), png = CarverFixtures.Png();
            string source = Path.Combine(root, "mixed.img");
            foreach (int offset in new[] { 0, 1048567, 1048570, 1048574, 1048576 })
            {
                byte[] image = [..new byte[offset], ..video, ..new byte[19], ..png, ..new byte[17]];
                File.WriteAllBytes(source, image);
                var report = await MediaCarver.ScanAsync(source);
                Assert(report.Engine == "native-media-carver" && report.Status == "complete_within_profile", "Mixed media use a complete shared scan profile.");
                var found = report.Candidates.Single(c => c.Kind == "mp4");
                Assert(found.Offset == offset && found.Length == video.Length && found.Sha256 == CarverFixtures.Hash(video), "A video signature crossing the streaming boundary retains exact offsets and hashes.");
                Assert(report.Candidates.Count == 2 && report.Candidates.Single(c => c.Kind == "png").Sha256 == CarverFixtures.Hash(png), "One source profile retains both photo and video candidates.");
                Assert(report.ScannedBytes == image.Length && report.Source.Sha256 == CarverFixtures.Hash(image), "Shared scan coverage and the full source hash agree with independently known bytes.");
            }
            var videos = await MediaCarver.ScanAsync(source, types: ["mp4", "mov", "avi"]);
            Assert(videos.Candidates.Count == 1 && videos.Candidates[0].Kind == "mp4", "Videos-only excludes photos without inventing alternate video kinds.");
            var photos = await MediaCarver.ScanAsync(source, types: ["PNG", "jpg", "jpeg"]);
            Assert(photos.Engine == "native-photo-carver" && photos.Candidates.Count == 1 && photos.Candidates[0].Kind == "png", "Photo aliases retain the compatible photo engine and selected scope.");
            bool unsupported = false;
            try { await MediaCarver.ScanAsync(source, types: ["exe"]); } catch (ArgumentException) { unsupported = true; }
            Assert(unsupported, "Unsupported native type requests fail before scanning.");
            var saved = await InvestigationStore.SaveAsync(videos, Path.Combine(root, "video.retrace.json"));
            var opened = await InvestigationStore.LoadAsync(saved.Path);
            Assert(opened.CanRecover && opened.Report.Engine == "native-media-carver" && opened.Report.Candidates[0].Sha256 == CarverFixtures.Hash(video), "A saved shared video investigation reopens with exact mappings.");
            var standalone = await VideoCarver.ScanAsync(source);
            var standaloneSaved = await InvestigationStore.SaveAsync(standalone, Path.Combine(root, "standalone-video.retrace.json"));
            Assert((await InvestigationStore.LoadAsync(standaloneSaved.Path)).CanRecover, "A standalone native-video engine investigation remains recoverable.");
            var exported = await RecoveryExporter.ExportAsync(opened.Report, opened.Report.Candidates.Select(c => c.Id), Path.Combine(root, "exported"));
            Assert(exported.Status == "complete" && exported.Files.Single().Sha256 == CarverFixtures.Hash(video), "Saved video export independently verifies the written bytes.");
            Assert(File.ReadAllBytes(exported.Files.Single().Path).SequenceEqual(video), "Video export does not append padding or neighboring photo data.");
            string folder = Path.Combine(root, "chosen");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "camera-backup.bak"), [..new byte[37], ..video, ..png]);
            var folderReport = await FolderRecovery.ScanAsync(folder, new() { Types = ["MP4"] });
            Assert(folderReport.Containers.Single().Recovery.Candidates.Single().Kind == "mp4" && folderReport.Limits.Types.SequenceEqual(new[] { "mp4" }), "Folder video selection uses actual media bytes and stores its normalized limits.");
            var legacyFolder = await FolderRecovery.ScanAsync(folder);
            Assert(legacyFolder.Containers.Single().Recovery.Candidates.Single().Kind == "png", "Existing folder calls keep their compatible photos-only default.");
            var container = folderReport.Containers.Single();
            var folderExport = await FolderRecovery.RestoreAsync(folderReport, [container.Id + ":" + container.Recovery.Candidates.Single().Id], Path.Combine(root, "folder-export"));
            Assert(folderExport.Status == "complete" && folderExport.Files.Single().Sha256 == CarverFixtures.Hash(video), "Selected folder video candidates export through their bound source.");
            using var cancellation = new CancellationTokenSource();
            var interrupted = await MediaCarver.ScanAsync(source, progress: new InlineProgress(_ => cancellation.Cancel()), cancellationToken: cancellation.Token);
            Assert(interrupted.Status == "cancelled_partial" && interrupted.Source.Sha256 == "" && interrupted.Candidates.Count == 0, "Cancellation never labels incomplete coverage with a full-source hash.");
            string repeated = Path.Combine(root, "repeated.img");
            File.WriteAllBytes(repeated, [..video, ..new byte[19], ..video]);
            var limited = await MediaCarver.ScanAsync(repeated, new() { MaximumCandidates = 1 }, types: ["mp4"]);
            Assert(limited.Status == "partial_signature_limit" && limited.Candidates.Count == 1 && limited.Source.Sha256 == "", "The candidate quota applies across the shared profile and reports partial coverage.");
            string riff = Path.Combine(root, "riff-families.img");
            byte[] avi = VideoCarverTests.Avi();
            File.WriteAllBytes(riff, [.."RIFF\x04\0\0\0WAVE"u8.ToArray(), ..new byte[31], ..avi]);
            var scopedRiff = await MediaCarver.ScanAsync(riff, new() { MaximumCandidates = 1 }, types: ["avi"]);
            Assert(scopedRiff.Status == "complete_within_profile" && scopedRiff.Candidates.Single().Sha256 == CarverFixtures.Hash(avi), "RIFF audio does not consume an AVI-only signature quota.");
            bool mismatch = false;
            try { await InvestigationStore.SaveAsync(videos with { Engine = "native-photo-carver" }, Path.Combine(root, "wrong-engine.json")); } catch (IOException) { mismatch = true; }
            Assert(mismatch, "Stored engine profiles cannot silently relabel video ranges as photos.");
            byte[] legacy = VideoCarverTests.LegacyMov(padding: true);
            foreach (int offset in new[] { 0, 1048567, 1048570, 1048574, 1048576 })
            {
                byte[] mixedLegacy = [..new byte[offset], ..legacy, ..new byte[19], ..png];
                string legacyPath = Path.Combine(root, "legacy-boundary.img"); File.WriteAllBytes(legacyPath, mixedLegacy);
                var legacyReport = await MediaCarver.ScanAsync(legacyPath);
                var legacyCandidate = legacyReport.Candidates.Single(c => c.Kind == "mov");
                Assert(legacyReport.Candidates.Count == 2 && legacyCandidate.Offset == offset && legacyCandidate.Length == legacy.Length && legacyCandidate.Sha256 == CarverFixtures.Hash(legacy), "Shared scanning finds legacy MOV across block boundaries once while preserving adjacent photos.");
                Assert(legacyReport.ScannedBytes == mixedLegacy.Length && legacyReport.Source.Sha256 == CarverFixtures.Hash(mixedLegacy), "Legacy random-access validation preserves the one-pass full-source hash.");
                Assert((await MediaCarver.ScanAsync(legacyPath, types: ["mp4"])).Candidates.Count == 0, "A brandless QuickTime candidate respects MOV-only type classification.");
            }
            string legacyContainer = Path.Combine(folder, "legacy-hidden-cache.bin");
            File.WriteAllBytes(legacyContainer, [..new byte[29], ..legacy]); File.SetAttributes(legacyContainer, FileAttributes.Hidden);
            var legacyFolderReport = await FolderRecovery.ScanAsync(folder, new() { Types = ["mov"] });
            var legacyBound = legacyFolderReport.Containers.Single();
            Assert(legacyBound.Recovery.Candidates.Single().Sha256 == CarverFixtures.Hash(legacy), "Explicit folder recovery finds an unbranded MOV inside a hidden non-video container.");
            var legacyFolderExport = await FolderRecovery.RestoreAsync(legacyFolderReport, [legacyBound.Id + ":" + legacyBound.Recovery.Candidates[0].Id], Path.Combine(root, "legacy-folder-export"));
            Assert(legacyFolderExport.Status == "complete" && File.ReadAllBytes(legacyFolderExport.Files.Single().Path).SequenceEqual(legacy), "Legacy folder recovery independently verifies an exact-byte export.");
            var legacySaved = await InvestigationStore.SaveAsync(legacyBound.Recovery, Path.Combine(root, "legacy.retrace.json"));
            Assert((await InvestigationStore.LoadAsync(legacySaved.Path)).CanRecover, "The existing native investigation contract reopens legacy MOV without schema changes.");
            byte[] avifLike = VideoCarverTests.Mp4(); "avif"u8.CopyTo(avifLike.AsSpan(8));
            string avifPath = Path.Combine(root, "image-brand.img"); File.WriteAllBytes(avifPath, avifLike);
            Assert((await MediaCarver.ScanAsync(avifPath, types: ["mov", "mp4"])).Candidates.Count == 0, "Shared legacy discovery does not relabel internal media atoms of a rejected image brand as a video.");
        }
        finally { CarverFixtures.Cleanup(root); }
        return assertions;
    }
}
