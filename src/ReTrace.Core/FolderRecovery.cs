using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReTrace.Core;

public sealed record FolderRecoveryOptions
{
    public int MaximumFiles { get; init; } = 256;
    public int MaximumEntries { get; init; } = 10000;
    public int MaximumDepth { get; init; } = 8;
    public long MaximumSourceBytes { get; init; } = 512L * 1024 * 1024;
    public long MaximumContainerBytes { get; init; } = 128L * 1024 * 1024;
    public int MaximumCandidates { get; init; } = 10000;
    public long MaximumFileBytes { get; init; } = 64L * 1024 * 1024;
    public long MaximumFragmentBytes { get; init; } = 4L * 1024 * 1024;
    public bool IncludeFragments { get; init; } = true;
}

public sealed record FolderRecoveryContainer(string Id, RecoveryReport Recovery);
public sealed record FolderRecoveryReport
{
    public string Schema { get; init; } = "retrace.folder-search.v1";
    public string Version { get; init; } = "0.1.0-alpha.3";
    public required string RootPath { get; init; }
    public string Status { get; init; } = "complete_within_profile";
    public int VisitedEntries { get; init; }
    public int ScannedFiles { get; init; }
    public long ScannedBytes { get; init; }
    public List<FolderRecoveryContainer> Containers { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
    public bool Exhaustive { get; init; } = false;
    public FolderRecoveryOptions Limits { get; init; } = new();
}

/// <summary>Explicit, bounded folder carving. Hidden files are included; links are never followed.</summary>
public static class FolderRecovery
{
    private sealed class InlineProgress(Action<RecoveryProgress> action) : IProgress<RecoveryProgress>
    {
        public void Report(RecoveryProgress value) => action(value);
    }

    public static Task<FolderRecoveryReport> ScanAsync(string folder, FolderRecoveryOptions? options = null,
        IProgress<RecoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(folder, options ?? new(), progress, cancellationToken), CancellationToken.None);

    private static FolderRecoveryReport Scan(string folder, FolderRecoveryOptions options, IProgress<RecoveryProgress>? progress, CancellationToken token)
    {
        ValidateOptions(options);
        string root = PathSafety.CheckPath(folder);
        if (!Directory.Exists(root)) throw new IOException("Choose an existing ordinary local folder.");
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var files = new List<string>();
        var notes = new List<string>();
        var containers = new List<FolderRecoveryContainer>();
        int visited = 0, scannedFiles = 0, candidates = 0;
        long scanned = 0, selectedBytes = 0;
        bool omitted = false, stopInventory = false;
        string status = "complete_within_profile";
        void Note(string message) { omitted = true; if (notes.Count < 128) notes.Add(message); }
        try
        {
            progress?.Report(new("folder_inventory", 0, 0, 0, "Enumerating ordinary files, including hidden entries; no links or archive extraction."));
            while (pending.Count > 0 && !stopInventory)
            {
                token.ThrowIfCancellationRequested();
                var current = pending.Pop();
                try
                {
                    PathSafety.CheckPath(current.Path);
                    foreach (string entry in Directory.EnumerateFileSystemEntries(current.Path))
                    {
                        token.ThrowIfCancellationRequested();
                        if (visited >= options.MaximumEntries) { Note("Entry budget reached; later folders were not enumerated."); stopInventory = true; break; }
                        visited++;
                        try
                        {
                            FileAttributes attributes = File.GetAttributes(entry);
                            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) { Note("A link, reparse point or device entry was skipped."); continue; }
                            PathSafety.CheckPath(entry);
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if (current.Depth < options.MaximumDepth) pending.Push((entry, current.Depth + 1));
                                else Note("Directory depth limit reached; deeper entries were skipped.");
                                continue;
                            }
                            long length = new FileInfo(entry).Length;
                            if (length == 0) continue;
                            if (length > options.MaximumContainerBytes) { Note("A file exceeded the container-size profile and was skipped."); continue; }
                            if (files.Count >= options.MaximumFiles) { Note("File-count budget reached; later entries were not enumerated."); stopInventory = true; break; }
                            if (length > options.MaximumSourceBytes - selectedBytes) { Note("A file exceeded the remaining total-read budget and was skipped."); continue; }
                            files.Add(entry);
                            selectedBytes += length;
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Note("An unavailable or unsafe entry was skipped."); }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Note("An unavailable or unsafe directory was skipped."); }
                progress?.Report(new("folder_inventory", visited, 0, 0, $"{files.Count} files selected within the folder profile."));
            }
            foreach (string path in files)
            {
                token.ThrowIfCancellationRequested();
                if (candidates >= options.MaximumCandidates || scanned >= options.MaximumSourceBytes) { Note("Total candidate or read budget reached; later files were not examined."); break; }
                try
                {
                    long before = scanned;
                    RecoveryReport report = PhotoCarver.ScanAsync(path, new()
                    {
                        MaximumCandidates = options.MaximumCandidates - candidates,
                        MaximumSourceBytes = Math.Min(options.MaximumContainerBytes, options.MaximumSourceBytes - scanned),
                        MaximumFileBytes = options.MaximumFileBytes, MaximumFragmentBytes = options.MaximumFragmentBytes,
                        IncludeFragments = options.IncludeFragments
                    }, new InlineProgress(p => progress?.Report(new("folder_scanning", before + p.BytesDone,
                        Math.Max(selectedBytes, before + p.BytesTotal), candidates + p.Candidates, "Inspecting file bytes; embedded images may be thumbnails or ordinary copies."))), token).GetAwaiter().GetResult();
                    scannedFiles++;
                    scanned += report.ScannedBytes;
                    candidates += report.Candidates.Count;
                    if (report.Candidates.Count > 0)
                    {
                        string id = "container-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(report.Source.Path)))[..16];
                        containers.Add(new(id, report));
                    }
                    if (report.Status == "cancelled_partial") { status = "cancelled_partial"; break; }
                    if (report.Status != "complete_within_profile") Note("A container stopped within its native validation limits; its retained candidates remain partial evidence.");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Note("A selected file became unavailable, changed, or exceeded its remaining read profile."); }
            }
        }
        catch (OperationCanceledException) { status = "cancelled_partial"; }
        if (status == "complete_within_profile" && omitted) status = "partial_profile_limits";
        notes.Add("Only regular files in the explicitly selected folder were read. Hidden entries are included; alternate streams, archive extraction and snapshot acquisition are outside this profile.");
        notes.Add("Image signatures do not prove deletion. Backup copies and embedded cache thumbnails can differ from the original file and resolution.");
        notes.Add("Free/unallocated disk space requires a disk-image/filesystem recovery profile; this folder scan cannot read sectors absent from ordinary files.");
        progress?.Report(new(status, scanned, scanned, candidates, "Folder profile finished; no source files were changed."));
        return new() { RootPath = root, Status = status, VisitedEntries = visited, ScannedFiles = scannedFiles,
            ScannedBytes = scanned, Containers = containers, Diagnostics = notes, Limits = options };
    }

    public static async Task<ExportReport> RestoreAsync(FolderRecoveryReport report, IEnumerable<string> candidateIds,
        string destination, IProgress<RecoveryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(candidateIds);
        if (report.Schema != "retrace.folder-search.v1" || report.Exhaustive || report.Containers.Count > 10000)
            throw new IOException("Unsupported folder recovery report.");
        var requested = candidateIds.ToHashSet(StringComparer.Ordinal);
        var known = new Dictionary<string, (FolderRecoveryContainer Container, RecoveryCandidate Candidate)>();
        foreach (var container in report.Containers)
        {
            if (container is null || container.Id.Length != 26 || !container.Id.StartsWith("container-", StringComparison.Ordinal) ||
                !container.Id.AsSpan(10).ToArray().All(Uri.IsHexDigit) || container.Recovery.Engine != "native-photo-carver")
                throw new IOException("Invalid folder container binding.");
            foreach (var candidate in container.Recovery.Candidates)
                if (!known.TryAdd(container.Id + ":" + candidate.Id, (container, candidate))) throw new IOException("Duplicate folder candidate binding.");
        }
        if (requested.Count is 0 or > 100000 || requested.Any(id => !known.ContainsKey(id))) throw new IOException("Select valid folder recovery candidates.");
        long totalBytes = 0;
        foreach (string id in requested)
        {
            long length = known[id].Candidate.Length;
            if (length <= 0 || length > 1024L * 1024 * 1024) throw new IOException("Invalid folder candidate byte length.");
            totalBytes = checked(totalBytes + length);
        }
        if (totalBytes > 8L * 1024 * 1024 * 1024) throw new IOException("Selected folder recovery exceeds the 8 GiB copy budget. Select fewer files.");
        string output = PathSafety.RequireNewDirectory(destination);
        RequireOutsideSource(report.RootPath, output);
        cancellationToken.ThrowIfCancellationRequested();
        PathSafety.CreateNewDirectory(output);
        var exported = new List<ExportedFile>();
        var diagnostics = new List<string>();
        string status = "complete";
        long completedBytes = 0;
        try
        {
            foreach (var container in report.Containers)
            {
                var selected = container.Recovery.Candidates.Where(candidate => requested.Contains(container.Id + ":" + candidate.Id)).ToArray();
                if (selected.Length == 0) continue;
                cancellationToken.ThrowIfCancellationRequested();
                long before = completedBytes;
                int completedFiles = exported.Count;
                var receipt = await RecoveryExporter.ExportAsync(container.Recovery, selected.Select(candidate => candidate.Id),
                    Path.Combine(output, container.Id), new InlineProgress(p => progress?.Report(new("folder_recovering",
                        before + p.BytesDone, totalBytes, completedFiles + p.Candidates, p.Message))), cancellationToken);
                exported.AddRange(receipt.Files.Select(file => file with { CandidateId = container.Id + ":" + file.CandidateId }));
                completedBytes += receipt.Files.Sum(file => file.Bytes);
                diagnostics.AddRange(receipt.Diagnostics);
                if (receipt.Status != "complete") { status = receipt.Status; break; }
            }
        }
        catch (OperationCanceledException) { status = "cancelled_partial"; diagnostics.Add("Only files listed in this receipt passed verification."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { status = "failed_partial"; diagnostics.Add(e.Message); }
        var result = new ExportReport("retrace.export.v1", status, output, exported, diagnostics);
        await using var manifest = new FileStream(Path.Combine(output, "recovery.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(manifest, result, new JsonSerializerOptions { WriteIndented = true }, CancellationToken.None);
        progress?.Report(new(status, completedBytes, totalBytes, exported.Count, "Only listed output files passed independent verification."));
        return result;
    }

    public static async Task SaveReportAsync(FolderRecoveryReport report, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        string full = PathSafety.CheckPath(path, mustExist: false);
        RequireOutsideSource(report.RootPath, full);
        PathSafety.CheckPath(Path.GetDirectoryName(full)!);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > 16 * 1024 * 1024) throw new IOException("The folder metadata exceeds the 16 MiB report limit.");
        cancellationToken.ThrowIfCancellationRequested();
        // Exclusive publication is small and non-cancellable after creation: never publish a cancelled partial JSON file.
        await using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, CancellationToken.None);
        stream.Flush(true);
    }

    private static void RequireOutsideSource(string source, string destination)
    {
        string root = PathSafety.CheckPath(source, mustExist: false).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(destination.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose an output outside the investigated folder so its source tree remains unchanged.");
    }

    private static void ValidateOptions(FolderRecoveryOptions options)
    {
        if (options.MaximumFiles is < 1 or > 10000 || options.MaximumEntries is < 1 or > 100000 || options.MaximumDepth is < 0 or > 32 ||
            options.MaximumSourceBytes < 1 || options.MaximumSourceBytes > 16L * 1024 * 1024 * 1024 ||
            options.MaximumContainerBytes < 1 || options.MaximumContainerBytes > 1024L * 1024 * 1024 ||
            options.MaximumCandidates is < 1 or > 100000 || options.MaximumFileBytes < 128 || options.MaximumFileBytes > 1024L * 1024 * 1024 ||
            options.MaximumFragmentBytes < 128 || options.MaximumFragmentBytes > 16L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "Folder recovery limits are outside the supported profile.");
    }
}
