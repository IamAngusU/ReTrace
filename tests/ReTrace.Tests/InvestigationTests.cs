using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReTrace.Core;

internal static class InvestigationTests
{
    public static async Task<int> RunAsync()
    {
        int assertions = 0;
        void Assert(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
        async Task Reject(Func<Task> action, string message)
        {
            bool rejected = false;
            try { await action(); } catch (IOException) { rejected = true; }
            Assert(rejected, message);
        }
        string root = Path.Combine(Path.GetTempPath(), "retrace-investigation-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] png = CarverFixtures.Png();
            string source = Path.Combine(root, "source.img");
            File.WriteAllBytes(source, [0, 0, 0, .. png, 0, 0, 0]);
            var report = await PhotoCarver.ScanAsync(source);
            var candidate = report.Candidates.Single();
            string path = Path.Combine(root, "investigation.json");
            var saved = await InvestigationStore.SaveAsync(report, path);
            Assert(saved.CaseId.StartsWith("case-") && saved.CaseId.Length == 69 && File.Exists(path), "Saved metadata has a stable SHA-256 case identifier.");
            Assert(saved.CaseId == "case-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(report))), "Streaming case-ID hashing preserves the existing compact JSON identity format.");
            Assert(saved.Verification == "not_revalidated", "Saving metadata does not falsely claim fresh byte verification.");
            Assert(JsonNode.Parse(File.ReadAllText(path))!["Schema"]!.GetValue<string>() == InvestigationStore.Schema, "Saved case has a versioned schema.");
            Assert(!Directory.EnumerateFiles(root, "*.pending").Any(), "Atomic save leaves no temporary metadata behind.");
            var opened = await InvestigationStore.LoadAsync(path);
            Assert(opened.CaseId == saved.CaseId && opened.Report.Candidates.Single().Sha256 == candidate.Sha256, "A saved native investigation reopens without losing candidate byte bindings.");
            Assert(opened.Verification == "verified_metadata_only" && opened.CanRecover, "Native reopening verifies file identity without rereading an entire image.");
            Assert(opened.Warnings.Any(x => x.Contains("not read again")), "Metadata-only verification is explicit to consumers.");
            Assert(opened.Report.Source == report.Source && opened.Report.Status == report.Status, "Original source and incomplete/complete status are preserved.");

            string second = Path.Combine(root, "second.json");
            Assert((await InvestigationStore.SaveAsync(report, second)).CaseId == saved.CaseId, "The same report saved twice retains its semantic case ID.");
            await Reject(() => InvestigationStore.SaveAsync(report, second), "Saving never overwrites an existing report.");
            await Reject(() => InvestigationStore.SaveAsync(report, source), "Saving never overwrites the original image.");
            Assert(File.ReadAllBytes(source).SequenceEqual(new byte[] { 0, 0, 0 }.Concat(png).Concat(new byte[] { 0, 0, 0 })), "Saving and reopening do not alter image bytes.");

            string raw = Path.Combine(root, "old-native-report.json");
            File.WriteAllText(raw, JsonSerializer.Serialize(report));
            Assert((await InvestigationStore.LoadAsync(raw)).CaseId == saved.CaseId, "Existing raw PascalCase native exports remain importable.");

            int mutation = 0;
            async Task RejectMutation(Action<JsonNode> change, string message)
            {
                var json = JsonNode.Parse(File.ReadAllText(path))!;
                change(json);
                string bad = Path.Combine(root, "bad-" + mutation++ + ".json");
                File.WriteAllText(bad, json.ToJsonString());
                await Reject(() => InvestigationStore.LoadAsync(bad), message);
            }
            await RejectMutation(j => j["Schema"] = "retrace.investigation.v999", "Unknown envelope schemas are rejected.");
            await RejectMutation(j => j["Unknown"] = true, "Unknown envelope fields are rejected.");
            await RejectMutation(j => j["Report"]!["Unknown"] = true, "Unknown nested report fields are rejected.");
            await RejectMutation(j => j["Report"]!["Source"]!["Unknown"] = true, "Unknown source fields are rejected.");
            await RejectMutation(j => j["Report"]!["Candidates"]![0]!["Unknown"] = true, "Unknown candidate fields are rejected.");
            await RejectMutation(j => ((JsonObject)j["Report"]!["Source"]!).Remove("Length"), "Missing source identity fields are rejected rather than defaulted.");
            await RejectMutation(j => ((JsonObject)j["Report"]!["Candidates"]![0]!).Remove("Offset"), "A missing byte mapping is never guessed as offset zero.");
            await RejectMutation(j => j["Report"]!["Candidates"]![0]!["Offset"] = -1, "Negative native offsets are rejected.");
            await RejectMutation(j => j["Report"]!["Candidates"]![0]!["Offset"] = long.MaxValue, "An overflowing or out-of-source native range is rejected.");
            await RejectMutation(j => j["Report"]!["Candidates"]![0]!["Length"] = 0, "Empty candidate mappings are rejected.");
            await RejectMutation(j => j["Report"]!["Candidates"]![0]!["Length"] = 1024L * 1024 * 1024 + 1, "Oversized candidate mappings are rejected.");
            await RejectMutation(j => j["Report"]!["Candidates"]![0]!["Sha256"] = "not-a-sha", "Invalid hash bindings are rejected.");
            await RejectMutation(j => j["Report"]!["Candidates"]![0]!["Id"] = "../../escape", "Candidate ID path escapes are rejected.");
            await RejectMutation(j => ((JsonArray)j["Report"]!["Candidates"]!).Add(j["Report"]!["Candidates"]![0]!.DeepClone()), "Duplicate candidate IDs are rejected.");
            await RejectMutation(j => j["Report"]!["Engine"] = "run-arbitrary-tool", "Unsupported engines are not executed or accepted.");
            await RejectMutation(j => j["Report"]!["Exhaustive"] = true, "Imported metadata cannot promise exhaustive recovery.");
            await RejectMutation(j => j["Report"]!["Candidates"]![0]!["Sha256"] = new string('0', 64), "Versioned metadata tampering invalidates the semantic case binding.");
            await RejectMutation(j => j["SavedUtc"] = "2026-10-19T12:00:00+02:00", "Saved timestamps require an explicit UTC offset.");
            await RejectMutation(j => j["CaseId"] = "case-" + new string('0', 64), "A substituted case identifier is rejected.");
            await RejectMutation(j => j["Report"]!["Candidates"]![0]!["StoredPath"] = source, "Native mappings cannot secretly switch to external files.");
            await RejectMutation(j => j["Report"]!["Source"]!["Path"] = "../source.img", "Relative source paths are rejected.");
            await RejectMutation(j => j["Report"]!["Source"]!["Path"] = @"\\server\share\source.img", "Network sources cannot be imported.");

            string duplicate = Path.Combine(root, "duplicate.json");
            File.WriteAllText(duplicate, File.ReadAllText(path).Replace("\"Report\": {", "\"Schema\": \"retrace.investigation.v1\",\"Report\": {", StringComparison.Ordinal));
            await Reject(() => InvestigationStore.LoadAsync(duplicate), "Duplicate envelope JSON keys are rejected.");
            string duplicateNested = Path.Combine(root, "duplicate-nested.json");
            File.WriteAllText(duplicateNested, JsonSerializer.Serialize(report).Replace("\"Length\":", "\"Length\":1,\"Length\":", StringComparison.Ordinal));
            await Reject(() => InvestigationStore.LoadAsync(duplicateNested), "Duplicate nested JSON keys are rejected.");
            string huge = Path.Combine(root, "oversized.json");
            using (var stream = new FileStream(huge, FileMode.CreateNew, FileAccess.Write)) stream.SetLength(InvestigationStore.MaximumFileBytes + 1L);
            await Reject(() => InvestigationStore.LoadAsync(huge), "Large metadata is rejected before being allocated or parsed.");
            var oversizedReport = report with
            {
                Candidates = Enumerable.Range(0, 64).Select(index => candidate with
                {
                    Id = "oversized-" + index,
                    Diagnostics = Enumerable.Repeat(new string('d', 8192), 128).ToList()
                }).ToList()
            };
            string oversizedSave = Path.Combine(root, "oversized-save.json");
            long saveAllocationStart = GC.GetTotalAllocatedBytes(precise: true);
            await Reject(() => InvestigationStore.SaveAsync(oversizedReport, oversizedSave), "Oversized in-memory reports stop serialization at the metadata byte budget.");
            long saveAllocated = GC.GetTotalAllocatedBytes(precise: true) - saveAllocationStart;
            Assert(saveAllocated < 3L * InvestigationStore.MaximumFileBytes, $"Rejecting oversized metadata must not allocate a complete oversized JSON copy (allocated {saveAllocated} bytes).");
            Assert(!File.Exists(oversizedSave) && !Directory.EnumerateFiles(root, ".retrace-*.pending").Any(), "Rejected oversized metadata publishes neither a case nor a temporary file.");
            string deep = Path.Combine(root, "deep.json");
            File.WriteAllText(deep, new string('[', 40) + "0" + new string(']', 40));
            await Reject(() => InvestigationStore.LoadAsync(deep), "Excessive JSON nesting is rejected.");

            var incomplete = report with { Status = "cancelled_partial", ScannedBytes = 0, Source = report.Source with { Sha256 = "" } };
            var incompletePath = Path.Combine(root, "incomplete.json");
            await InvestigationStore.SaveAsync(incomplete, incompletePath);
            var reopenedIncomplete = await InvestigationStore.LoadAsync(incompletePath);
            Assert(reopenedIncomplete.Report.Status == "cancelled_partial" && reopenedIncomplete.Report.Source.Sha256 == "", "A cancelled scan remains explicitly incomplete after reopening.");

            string library = Path.Combine(root, "cases");
            var libraryCase = await InvestigationStore.SaveToLibraryAsync(report, library);
            var same = await InvestigationStore.SaveToLibraryAsync(report, library);
            Assert(same.Path == libraryCase.Path && Directory.GetFiles(library).Length == 1, "Library saves are idempotent without rewriting a case.");
            var summaries = await InvestigationStore.ListAsync(library);
            Assert(summaries.Count == 1 && summaries[0].CaseId == saved.CaseId && summaries[0].CandidateCount == 1, "Local library lists saved findings and source metadata.");
            Assert(summaries[0].Verification == "not_revalidated", "Listing metadata is not misrepresented as byte verification.");
            await Reject(() => InvestigationStore.DeleteFromLibraryAsync("../" + saved.CaseId, library), "Metadata deletion cannot escape through an untrusted case ID.");
            await InvestigationStore.DeleteFromLibraryAsync(saved.CaseId, library);
            Assert((await InvestigationStore.ListAsync(library)).Count == 0 && File.Exists(source), "Removing a saved case leaves the original image untouched.");
            File.WriteAllText(libraryCase.Path, "{incomplete metadata");
            var brokenSummary = (await InvestigationStore.ListAsync(library)).Single();
            Assert(brokenSummary.Verification == "invalid_metadata" && brokenSummary.Error.Length > 0, "A corrupt saved case is shown explicitly rather than silently hidden.");
            await InvestigationStore.DeleteFromLibraryAsync(saved.CaseId, library);
            Assert(!File.Exists(libraryCase.Path) && File.Exists(source), "Explicit metadata removal also handles corrupt cases without deleting source data.");
            await InvestigationStore.SaveToLibraryAsync(report, library);
            if (OperatingSystem.IsWindows())
            {
                // FileShare.Delete is enforced by Windows. POSIX permits unlinking an open file,
                // so the local test fallback cannot exercise this native deletion guarantee.
                using (var locked = new FileStream(libraryCase.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    await Reject(() => InvestigationStore.DeleteFromLibraryAsync(saved.CaseId, library), "An in-use metadata file is not deleted through another reader's lock.");
                Assert(File.Exists(libraryCase.Path), "Rejected locked metadata removal leaves its case intact.");
            }
            else TestCoverage.Skip("investigation_delete_sharing", "Windows FileShare.Delete enforcement is unavailable on POSIX filesystems.");
            await InvestigationStore.DeleteFromLibraryAsync(saved.CaseId, library);
            Assert((await InvestigationStore.ListAsync(Path.Combine(root, "missing-library"))).Count == 0, "Listing a missing library does not create directories.");
            Assert(!Directory.Exists(Path.Combine(root, "missing-library")), "Read-only listing never writes to an absent library.");

            long originalTime = File.GetLastWriteTimeUtc(source).Ticks;
            File.SetLastWriteTimeUtc(source, new DateTime(originalTime + TimeSpan.TicksPerSecond, DateTimeKind.Utc));
            var changed = await InvestigationStore.LoadAsync(path);
            Assert(changed.Verification == "source_identity_changed" && !changed.CanRecover, "Changed source identity preserves findings but disables recovery.");
            File.SetLastWriteTimeUtc(source, new DateTime(originalTime, DateTimeKind.Utc));

            string originalSource = Path.Combine(root, "preserved-source.img");
            File.Move(source, originalSource);
            Directory.CreateDirectory(source);
            try
            {
                var unavailable = await InvestigationStore.LoadAsync(path);
                Assert(unavailable.Verification == "source_unavailable" && !unavailable.CanRecover && unavailable.Report.Candidates.Count == 1,
                    "Replacing a source with a directory keeps recorded findings readable and blocks recovery.");
            }
            finally { Directory.Delete(source); File.Move(originalSource, source); }
            if (OperatingSystem.IsWindows())
            {
                using var lockedSource = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
                var unavailable = await InvestigationStore.LoadAsync(path);
                Assert(unavailable.Verification == "source_unavailable" && !unavailable.CanRecover && unavailable.CaseId == saved.CaseId,
                    "A sharing-locked source keeps saved findings readable without enabling recovery.");
            }
            else
            {
                TestCoverage.Skip("investigation_source_sharing_lock", "Windows source sharing-lock behavior requires Windows.");
                string retainedLibrary = Path.Combine(root, "retained-cases");
                await InvestigationStore.SaveToLibraryAsync(report, retainedLibrary);
                File.Move(source, originalSource);
                File.CreateSymbolicLink(source, originalSource);
                try
                {
                    var unavailable = await InvestigationStore.LoadAsync(path);
                    Assert(unavailable.Verification == "source_unavailable" && !unavailable.CanRecover && unavailable.CaseId == saved.CaseId,
                        "A source replaced with a symlink retains metadata and is never followed for recovery.");
                    var retained = (await InvestigationStore.ListAsync(retainedLibrary)).Single();
                    Assert(retained.Verification == "not_revalidated" && retained.CandidateCount == 1,
                        "Library metadata validation does not dereference changed source bindings.");
                    string unsafeExport = Path.Combine(root, "unsafe-source-export");
                    await Reject(() => RecoveryExporter.ExportAsync(unavailable.Report, [candidate.Id], unsafeExport), "Actual recovery still refuses the symlink source.");
                    Assert(!Directory.Exists(unsafeExport), "An unsafe source leaves no recovery destination.");
                }
                finally { File.Delete(source); File.Move(originalSource, source); }
            }

            string spool = Path.Combine(root, "specialist-run");
            string recup = Path.Combine(spool, "recup.1"); Directory.CreateDirectory(recup);
            string recovered = Path.Combine(recup, "f0000001.png"); File.WriteAllBytes(recovered, png);
            var specialist = report with
            {
                Engine = "photorec-7.2-windows", RunDirectory = spool,
                Candidates = [candidate with { Id = "photorec-000001", Offset = -1, StoredPath = recovered, Mapping = "external_reconstruction", Name = "f0000001.png" }]
            };
            string specialistPath = Path.Combine(root, "photorec-investigation.json");
            var specialistSaved = await InvestigationStore.SaveAsync(specialist, specialistPath);
            var specialistOpened = await InvestigationStore.LoadAsync(specialistPath);
            Assert(specialistOpened.Verification == "verified_spool_hashes" && specialistOpened.CanRecover, "Reopening PhotoRec independently checks stored candidate SHA-256.");
            Assert(specialistOpened.Report.Candidates[0].Offset == -1 && specialistOpened.Report.Candidates[0].Mapping == "external_reconstruction", "Specialist reconstructions never acquire a fake contiguous source offset.");
            string oldPhotoRec = Path.Combine(spool, "recovery-report.json");
            File.WriteAllText(oldPhotoRec, JsonSerializer.Serialize(specialist));
            Assert((await InvestigationStore.LoadAsync(oldPhotoRec)).CaseId == specialistSaved.CaseId, "An existing PhotoRec run report can be reopened directly.");
            var manyCandidates = new List<RecoveryCandidate>();
            for (int index = 0; index < 64; index++)
            {
                string stored = Path.Combine(recup, $"small-{index:D4}.png");
                File.WriteAllBytes(stored, png);
                manyCandidates.Add(specialist.Candidates[0] with { Id = $"photorec-many-{index:D4}", StoredPath = stored });
            }
            string manyPath = Path.Combine(root, "many-photorec-investigation.json");
            await InvestigationStore.SaveAsync(specialist with { Candidates = manyCandidates }, manyPath);
            long verifyAllocationStart = GC.GetTotalAllocatedBytes(precise: true);
            var manyOpened = await InvestigationStore.LoadAsync(manyPath);
            long verifyAllocated = GC.GetTotalAllocatedBytes(precise: true) - verifyAllocationStart;
            Assert(manyOpened.Verification == "verified_spool_hashes" && manyOpened.CanRecover && manyOpened.Report.Candidates.Count == 64,
                "Every candidate in a larger saved PhotoRec collection is independently verified.");
            Assert(verifyAllocated < 16L * 1024 * 1024, $"Small-candidate verification must reuse bounded I/O memory instead of allocating 1 MiB per file (allocated {verifyAllocated} bytes).");
            await Reject(() => InvestigationStore.SaveAsync(specialist, Path.Combine(spool, "nested.json")), "Metadata saving cannot write into the specialist source run.");
            await Reject(() => InvestigationStore.SaveAsync(specialist with { Candidates = [specialist.Candidates[0] with { StoredPath = source }] }, Path.Combine(root, "outside-run.json")), "A specialist path cannot escape its recorded run.");
            await Reject(() => InvestigationStore.SaveAsync(specialist with { Candidates = [specialist.Candidates[0] with { Offset = 0 }] }, Path.Combine(root, "fake-offset.json")), "An invented specialist source offset is rejected.");
            await Reject(() => InvestigationStore.SaveAsync(specialist with { Candidates = [specialist.Candidates[0] with { StoredPath = Path.Combine(spool, "f0001.png") }] }, Path.Combine(root, "loose-spool.json")), "Specialist bytes must remain in the direct PhotoRec recup.N layout.");

            byte[] tampered = png.ToArray(); tampered[^1] ^= 1; File.WriteAllBytes(recovered, tampered);
            var changedSpecialist = await InvestigationStore.LoadAsync(specialistPath);
            Assert(changedSpecialist.Verification == "stored_output_changed" && !changedSpecialist.CanRecover && changedSpecialist.CaseId == specialistSaved.CaseId,
                "Changed saved specialist hashes retain the investigation and disable recovery even if file length is unchanged.");
            var changedSpecialistExport = await RecoveryExporter.ExportAsync(changedSpecialist.Report, [specialist.Candidates[0].Id], Path.Combine(root, "changed-specialist-export"));
            Assert(changedSpecialistExport.Status == "failed_partial" && changedSpecialistExport.Files.Count == 0,
                "The exporter independently refuses changed specialist hashes even if a caller ignores CanRecover.");
            File.WriteAllBytes(recovered, png);
            string disconnectedOutput = Path.Combine(root, "disconnected-output.png");
            File.Move(recovered, disconnectedOutput);
            try
            {
                var unavailable = await InvestigationStore.LoadAsync(specialistPath);
                Assert(unavailable.Verification == "stored_output_unavailable" && !unavailable.CanRecover && unavailable.Report.Candidates.Count == 1,
                    "A missing saved PhotoRec member retains recorded findings without enabling recovery.");
                string missingExport = Path.Combine(root, "missing-specialist-export");
                await Reject(() => RecoveryExporter.ExportAsync(unavailable.Report, [specialist.Candidates[0].Id], missingExport),
                    "The exporter independently rejects missing specialist data.");
                Assert(!Directory.Exists(missingExport), "Missing specialist data creates no recovery destination.");
            }
            finally { File.Move(disconnectedOutput, recovered); }
            if (!OperatingSystem.IsWindows())
            {
                string originalSpool = Path.Combine(root, "preserved-specialist-run");
                Directory.Move(spool, originalSpool);
                Directory.CreateSymbolicLink(spool, originalSpool);
                try
                {
                    var unavailable = await InvestigationStore.LoadAsync(specialistPath);
                    Assert(unavailable.Verification == "stored_output_unavailable" && !unavailable.CanRecover && unavailable.CaseId == specialistSaved.CaseId,
                        "A linked specialist spool is not followed, while its saved investigation remains readable.");
                    string linkedExport = Path.Combine(root, "linked-specialist-export");
                    await Reject(() => RecoveryExporter.ExportAsync(unavailable.Report, [specialist.Candidates[0].Id], linkedExport),
                        "The exporter independently rejects linked specialist storage.");
                    Assert(!Directory.Exists(linkedExport), "Linked specialist data creates no recovery destination.");
                }
                finally { Directory.Delete(spool); Directory.Move(originalSpool, spool); }
            }
            else TestCoverage.Skip("investigation_posix_source_links", "POSIX symlink fixtures run on Linux; Windows reparse coverage uses the junction fixture.");
            Assert((await InvestigationStore.LoadAsync(specialistPath)).CanRecover, "Restoring unchanged specialist bytes enables recovery again after a fresh verification.");
            await InvestigationStore.SaveToLibraryAsync(specialist, library);
            await InvestigationStore.DeleteFromLibraryAsync(specialistSaved.CaseId, library);
            Assert(File.Exists(recovered) && File.Exists(oldPhotoRec), "Deleting library metadata preserves PhotoRec candidate bytes and its original report.");

            File.Delete(source);
            var offlineNative = await InvestigationStore.LoadAsync(path);
            Assert(offlineNative.Verification == "source_unavailable" && !offlineNative.CanRecover && offlineNative.Report.Candidates.Count == 1, "A disconnected original leaves native findings readable without enabling recovery.");
            var offlineSpecialist = await InvestigationStore.LoadAsync(specialistPath);
            Assert(offlineSpecialist.Verification == "verified_spool_hashes" && offlineSpecialist.CanRecover, "Saved specialist outputs remain recoverable without the original image.");
            var export = await RecoveryExporter.ExportAsync(offlineSpecialist.Report, [specialist.Candidates[0].Id], Path.Combine(root, "offline-recovery"));
            Assert(export.Status == "complete" && File.ReadAllBytes(export.Files.Single().Path).SequenceEqual(png), "Reopened specialist bytes can actually be copied and independently verified.");

            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool saveCancelled = false;
            try { await InvestigationStore.SaveAsync(report, Path.Combine(root, "cancelled.json"), cancellationToken: cancelled.Token); }
            catch (OperationCanceledException) { saveCancelled = true; }
            Assert(saveCancelled && !File.Exists(Path.Combine(root, "cancelled.json")), "Pre-cancelled saving does not create an incomplete metadata file.");
            using var duringSave = new CancellationTokenSource();
            string duringSavePath = Path.Combine(root, "cancelled-during-write.json");
            bool writingCancelled = false;
            try
            {
                await InvestigationStore.SaveAsync(report with { Diagnostics = Enumerable.Repeat(new string('d', 8192), 32).ToList() },
                    duringSavePath, new CarverTests.InlineProgress(value => { if (value.Stage == "saving_investigation") duringSave.Cancel(); }), duringSave.Token);
            }
            catch (OperationCanceledException) { writingCancelled = true; }
            Assert(writingCancelled && !File.Exists(duringSavePath), "Cancellation during metadata writing never publishes an incomplete case.");
            Assert(!Directory.EnumerateFiles(root, ".retrace-*.pending").Any(), "Interrupted metadata writing removes only its own private temporary file.");
            using var duringSmallSave = new CancellationTokenSource();
            string smallSavePath = Path.Combine(root, "cancelled-small-write.json");
            bool smallWritingCancelled = false;
            try
            {
                await InvestigationStore.SaveAsync(report, smallSavePath,
                    new CarverTests.InlineProgress(value => { if (value.Stage == "saving_investigation") duringSmallSave.Cancel(); }), duringSmallSave.Token);
            }
            catch (OperationCanceledException) { smallWritingCancelled = true; }
            Assert(smallWritingCancelled && !File.Exists(smallSavePath) && !Directory.EnumerateFiles(root, ".retrace-*.pending").Any(),
                "Cancellation after a small metadata write also removes its owned temporary file after buffered-write cleanup.");
            bool loadCancelled = false;
            using var duringLoad = new CancellationTokenSource();
            try { await InvestigationStore.LoadAsync(specialistPath, new CarverTests.InlineProgress(_ => duringLoad.Cancel()), duringLoad.Token); }
            catch (OperationCanceledException) { loadCancelled = true; }
            Assert(loadCancelled, "Spool SHA revalidation responds to cancellation.");
            Assert(File.ReadAllBytes(recovered).SequenceEqual(png), "Cancellation never removes or changes saved specialist data.");

            if (OperatingSystem.IsWindows())
            {
                // Junctions do not require symlink privileges; test that metadata operations never
                // follow a library alias. The link itself is removed before fixture cleanup.
                string alias = Path.Combine(root, "library-alias");
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    ArgumentList = { "/d", "/c", "mklink", "/J", alias, library },
                    RedirectStandardOutput = true, RedirectStandardError = true
                });
                if (process is not null)
                {
                    await process.WaitForExitAsync();
                    if (process.ExitCode == 0)
                    {
                        try
                        {
                            await Reject(() => InvestigationStore.SaveToLibraryAsync(report, alias), "Library writes reject reparse-point aliases.");
                            await Reject(() => InvestigationStore.ListAsync(alias), "Library reads reject reparse-point aliases.");
                        }
                        finally { Directory.Delete(alias); }
                    }
                    else TestCoverage.Skip("investigation_junctions", $"The native junction fixture could not be created (mklink exit {process.ExitCode}).");
                }
                else TestCoverage.Skip("investigation_junctions", "The native junction fixture process could not be started.");
            }
            else TestCoverage.Skip("investigation_junctions", "Native Windows directory junctions are unavailable on this platform.");
        }
        finally { CarverFixtures.Cleanup(root); }
        return assertions;
    }
}
