using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace ReTrace.Core;

public sealed record RecycleMetadata
{
    public ulong? Version { get; init; }
    public long? DeclaredBytes { get; init; }
    public DateTimeOffset? DeletedUtc { get; init; }
    public string OriginalPath { get; init; } = "";
    public List<string> Diagnostics { get; init; } = [];
    public bool IsValid => Diagnostics.Count == 0;
}

public sealed record RecycleEntry
{
    public required string Id { get; init; }
    public required string RootDirectory { get; init; }
    public string RootFileId { get; init; } = "";
    public string? MetadataName { get; init; }
    public string? PayloadName { get; init; }
    public SourceIdentity? MetadataIdentity { get; init; }
    public SourceIdentity? PayloadIdentity { get; init; }
    public RecycleMetadata? Metadata { get; init; }
    public string Name { get; init; } = "";
    public string OriginalPath { get; init; } = "";
    public DateTimeOffset? DeletedUtc { get; init; }
    public long? DeclaredBytes { get; init; }
    public long? ContentBytes { get; init; }
    public long Length => ContentBytes ?? DeclaredBytes ?? 0;
    public string MetadataSha256 => MetadataIdentity?.Sha256 ?? "";
    public bool PayloadHashVerified => PayloadIdentity?.Sha256.Length == 64;
    public string State { get; init; } = "unknown";
    public string PayloadKind { get; init; } = "missing";
    public bool IsDirectory { get; init; }
    public bool CanRecover { get; init; }
    public List<string> Diagnostics { get; init; } = [];
}

public sealed record RecycleReport
{
    public string Schema { get; init; } = "retrace.recycle.v1";
    public string Version { get; init; } = "0.1.0-alpha.3";
    public string Status { get; init; } = "complete_within_profile";
    public string Scope { get; init; } = "current-user-local-drives";
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Verification { get; init; } = "metadata_and_recorded_payload_bindings";
    public List<RecycleEntry> Entries { get; init; } = [];
    public List<string> Roots { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
    public long HashedPayloadBytes { get; init; }
    public List<string> Limitations { get; init; } =
    [
        "Only the current user's local Windows Recycle Bin or one explicitly selected copied bin directory is inventoried. Emptied bins require image or filesystem recovery.",
        "Original paths, sizes and deletion times are source claims, not proof of authorship or identity. They are never used as recovery destination paths.",
        "Directory payloads are listed but not recursively recovered by this profile. Selected ordinary files are copied without removing originals.",
        "Payloads up to 4 MiB are hashed during inventory within a 64 MiB total budget. Other files have identity bindings only until selected recovery independently hashes them.",
        "Inventory is bounded to 4,096 entries, 8,192 immediate directory entries per root and 180 seconds; reaching a limit is reported as partial coverage."
    ];
}

/// <summary>Read-only modern $I/$R inventory and byte-verified copy recovery; no Shell restore or bin mutation.</summary>
public static class RecycleBinRecovery
{
    public const int MaximumEntries = 4096;
    public const int MaximumMetadataBytes = 65536;
    public const long MaximumRecoveryBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumDirectoryEntries = 8192;
    private const long MaximumInventoryHashBytes = 64L * 1024 * 1024;
    private const long MaximumSmallFileBytes = 4L * 1024 * 1024;
    private const int BlockBytes = 1024 * 1024;
    private static readonly UnicodeEncoding StrictUtf16 = new(false, false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, MaxDepth = 24, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static RecycleMetadata ParseMetadata(ReadOnlySpan<byte> data)
    {
        var notes = new List<string>();
        if (data.Length > MaximumMetadataBytes) throw new IOException("Recycle Bin metadata exceeds 64 KiB.");
        if (data.Length < 24) return new() { Diagnostics = ["truncated_header"] };
        ulong version = BinaryPrimitives.ReadUInt64LittleEndian(data);
        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(data[8..]);
        ulong ticks = BinaryPrimitives.ReadUInt64LittleEndian(data[16..]);
        long? declared = size <= long.MaxValue ? (long)size : null;
        if (declared is null) notes.Add("invalid_declared_size");
        DateTimeOffset? deleted = null;
        try { if (ticks == 0 || ticks > long.MaxValue) throw new ArgumentOutOfRangeException(); deleted = DateTimeOffset.FromFileTime((long)ticks).ToUniversalTime(); }
        catch (ArgumentOutOfRangeException) { notes.Add("invalid_deletion_filetime"); }
        var parsed = new RecycleMetadata { Version = version, DeclaredBytes = declared, DeletedUtc = deleted, Diagnostics = notes };
        if (version is not (1 or 2)) { notes.Add("unsupported_metadata_version"); return parsed; }
        int start = version == 1 ? 24 : 28;
        if (data.Length < start) { notes.Add("truncated_path_length"); return parsed; }
        int available = data.Length - start;
        int wanted = available;
        if (version == 1 && data.Length != 544) notes.Add("invalid_v1_metadata_length");
        if (version == 2)
        {
            uint units = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]);
            if (units is < 1 or > (MaximumMetadataBytes - 28) / 2) { notes.Add("invalid_path_character_count"); return parsed; }
            wanted = checked((int)units * 2);
            if (wanted > available) notes.Add("truncated_path");
            else if (wanted < available) notes.Add("trailing_metadata_bytes");
            wanted = Math.Min(wanted, available);
        }
        if ((wanted & 1) != 0) { notes.Add("odd_utf16_path_bytes"); --wanted; }
        var raw = data.Slice(start, wanted);
        int end = -1;
        for (int at = 0; at + 1 < raw.Length; at += 2)
            if (raw[at] == 0 && raw[at + 1] == 0) { end = at; break; }
        if (end < 0) { notes.Add("missing_path_terminator"); end = raw.Length; }
        else if (version == 2 && end + 2 != raw.Length) notes.Add("embedded_path_terminator");
        else if (version == 1 && raw[(end + 2)..].IndexOfAnyExcept((byte)0) >= 0) notes.Add("nonzero_path_padding");
        string original = "";
        try
        {
            original = StrictUtf16.GetString(raw[..end]);
            if (original.Length == 0 || original.Any(c => c < 32)) { notes.Add("invalid_original_path_text"); original = ""; }
        }
        catch (DecoderFallbackException) { notes.Add("invalid_utf16_path"); }
        return parsed with { OriginalPath = original };
    }

    public static Task<RecycleReport> ScanAsync(string? sourceDirectory = null,
        IProgress<RecoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(sourceDirectory, progress, cancellationToken), CancellationToken.None);

    private static RecycleReport Scan(string? selected, IProgress<RecoveryProgress>? progress, CancellationToken token)
    {
        var roots = selected is null ? CurrentUserRoots() : [ExplicitRoot(selected)];
        var report = new RecycleReport { Scope = selected is null ? "current-user-local-drives" : "explicit-copied-bin-directory", Roots = roots };
        var entries = new List<RecycleEntry>();
        var notes = new List<string>();
        string status = "complete_within_profile";
        long hashed = 0;
        var watch = Stopwatch.StartNew();
        try
        {
            foreach (string root in roots)
            {
                Check(token, watch, 180);
                if (!Directory.Exists(root)) continue;
                try
                {
                    using var guard = new DirectoryLease(root);
                    var groups = new Dictionary<string, Pair>(StringComparer.OrdinalIgnoreCase);
                    int names = 0;
                    foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly))
                    {
                        Check(token, watch, 180);
                        if (++names > MaximumDirectoryEntries) { notes.Add("directory_entry_limit_reached"); status = "partial"; break; }
                        string name = Path.GetFileName(path);
                        if (name.Length < 3 || name[0] != '$' || char.ToUpperInvariant(name[1]) is not ('I' or 'R')) continue;
                        if (!ValidComponent(name)) { notes.Add("unsafe_bin_entry_name"); status = "partial"; continue; }
                        string suffix = name[2..];
                        if (!groups.TryGetValue(suffix, out var pair)) groups[suffix] = pair = new();
                        if (char.ToUpperInvariant(name[1]) == 'I') { if (pair.Metadata is not null) pair.Ambiguous = true; pair.Metadata = name; }
                        else { if (pair.Payload is not null) pair.Ambiguous = true; pair.Payload = name; }
                    }
                    string rootId = guard.FileId(root);
                    foreach (var pair in groups.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Value))
                    {
                        Check(token, watch, 180);
                        if (entries.Count >= MaximumEntries) { status = "partial"; notes.Add("maximum_entries_reached"); break; }
                        var entry = ScanEntry(root, rootId, pair, token, ref hashed);
                        entries.Add(entry);
                        if (entry.State != "paired" || entry.Diagnostics.Count != 0) status = "partial";
                        progress?.Report(new("recycle_inventory", entries.Count, 0, entries.Count, "Reading current-user Recycle Bin metadata; original files are unchanged."));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { status = "partial"; notes.Add("bin_root_unavailable: " + e.Message); }
                if (entries.Count >= MaximumEntries)
                {
                    status = "partial";
                    if (!notes.Contains("maximum_entries_reached")) notes.Add("maximum_entries_reached");
                    break;
                }
            }
        }
        catch (OperationCanceledException) { status = "cancelled_partial"; notes.Add("Inventory cancelled. Already inventoried entries remain available."); }
        catch (TimeoutException) { status = "partial"; notes.Add("Inventory time limit reached."); }
        return report with { Status = status, Entries = entries, Diagnostics = notes, HashedPayloadBytes = hashed };
    }

    private sealed class Pair { public string? Metadata, Payload; public bool Ambiguous; }

    private static RecycleEntry ScanEntry(string root, string rootId, Pair pair, CancellationToken token, ref long hashed)
    {
        var notes = new List<string>();
        SourceIdentity? metaIdentity = null, payloadIdentity = null;
        RecycleMetadata? metadata = null;
        bool directory = false;
        string payloadKind = "missing";
        if (pair.Metadata is not null)
        {
            try
            {
                string path = Path.Combine(root, pair.Metadata);
                using var input = PathSafety.OpenRead(path);
                if (input.Length > MaximumMetadataBytes) throw new IOException("Recycle Bin metadata exceeds 64 KiB.");
                // Metadata is tiny: direct handle reads avoid allocating FileStream's 1 MiB
                // image buffer separately for each of thousands of $I entries.
                byte[] bytes = new byte[(int)input.Length];
                for (int at = 0; at < bytes.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    int read = RandomAccess.Read(input.SafeFileHandle, bytes.AsSpan(at), at);
                    if (read == 0) throw new EndOfStreamException("Recycle Bin metadata ended while reading.");
                    at += read;
                }
                token.ThrowIfCancellationRequested();
                metaIdentity = PathSafety.Identity(path, input, Convert.ToHexStringLower(SHA256.HashData(bytes)));
                metadata = ParseMetadata(bytes);
                notes.AddRange(metadata.Diagnostics);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { notes.Add("metadata_unavailable: " + e.Message); }
        }
        if (pair.Payload is not null)
        {
            try
            {
                string path = PathSafety.CheckPath(Path.Combine(root, pair.Payload));
                directory = Directory.Exists(path);
                payloadKind = directory ? "directory" : "file";
                if (directory) notes.Add("directory_recovery_not_supported_by_this_profile");
                else
                {
                    using var input = PathSafety.OpenRead(path);
                    if (input.Length > MaximumRecoveryBytes) throw new IOException("Payload exceeds the 8 GiB selected-recovery profile.");
                    payloadIdentity = PathSafety.Identity(path, input);
                    if (input.Length <= MaximumSmallFileBytes && input.Length <= MaximumInventoryHashBytes - hashed)
                    {
                        string sha = PhotoCarver.HashRange(input, 0, input.Length, token);
                        PathSafety.VerifyIdentity(payloadIdentity, PathSafety.Identity(path, input));
                        payloadIdentity = payloadIdentity with { Sha256 = sha };
                        hashed += input.Length;
                    }
                    if (metadata?.DeclaredBytes is not null && metadata.DeclaredBytes != input.Length) notes.Add("declared_size_payload_mismatch");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { notes.Add("payload_unavailable: " + e.Message); }
        }
        if (pair.Ambiguous) notes.Add("ambiguous_case_insensitive_pair");
        var entry = new RecycleEntry
        {
            Id = "", RootDirectory = root, RootFileId = rootId, MetadataName = pair.Metadata, PayloadName = pair.Payload,
            MetadataIdentity = metaIdentity, PayloadIdentity = payloadIdentity, Metadata = metadata,
            Name = LastComponent(metadata?.OriginalPath) is { Length: > 0 } originalName ? originalName : pair.Payload ?? pair.Metadata ?? "unknown",
            OriginalPath = metadata?.OriginalPath ?? "", DeletedUtc = metadata?.DeletedUtc, DeclaredBytes = metadata?.DeclaredBytes,
            ContentBytes = payloadIdentity?.Length, IsDirectory = directory, PayloadKind = payloadKind,
            CanRecover = !pair.Ambiguous && !directory && payloadIdentity is not null && (pair.Metadata is null || metaIdentity is not null),
            State = pair.Ambiguous ? "ambiguous_pair" : pair.Metadata is null ? "orphan_payload" : pair.Payload is null ? "orphan_metadata" :
                metaIdentity is null ? "metadata_unavailable" : payloadIdentity is null ? directory ? "directory_unsupported" : "payload_unavailable" : metadata?.IsValid == false ? "metadata_invalid" : "paired",
            Diagnostics = notes
        };
        return entry with { Id = EntryId(entry) };
    }

    public static Task<ExportReport> RestoreAsync(RecycleReport report, string entryId, string destination,
        IProgress<RecoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        => ExportAsync(report, [entryId], destination, progress, cancellationToken);

    public static Task<ExportReport> ExportAsync(RecycleReport report, IEnumerable<string> entryIds, string destination,
        IProgress<RecoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Export(report, entryIds, destination, progress, cancellationToken), CancellationToken.None);

    private static ExportReport Export(RecycleReport report, IEnumerable<string> requestedIds, string destination,
        IProgress<RecoveryProgress>? progress, CancellationToken token)
    {
        ValidateReport(report);
        ArgumentNullException.ThrowIfNull(requestedIds);
        var requested = requestedIds.ToHashSet(StringComparer.Ordinal);
        if (requested.Count is < 1 or > 256) throw new IOException("Select between 1 and 256 individual Recycle Bin files.");
        var selected = report.Entries.Where(x => requested.Contains(x.Id)).ToArray();
        if (selected.Length != requested.Count || selected.Any(x => !x.CanRecover || x.PayloadIdentity is null || x.IsDirectory))
            throw new IOException("A selected entry is missing, ambiguous or not a recoverable ordinary file.");
        long total = selected.Sum(x => x.PayloadIdentity!.Length);
        if (total > MaximumRecoveryBytes) throw new IOException("Selected recovery exceeds the 8 GiB total-byte budget.");
        foreach (var entry in selected)
            if (!SamePath(ExplicitRoot(entry.RootDirectory), entry.RootDirectory))
                throw new IOException("Select the current user's exact SID directory, not a shared Recycle Bin root.");
        string output = PathSafety.RequireNewDirectory(destination);
        if (InsideNamedRecycleBin(output) ||
            selected.Any(x => IsWithin(output, x.RootDirectory) || SamePath(output, x.RootDirectory)))
            throw new IOException("Choose a new recovery directory outside the Recycle Bin source.");
        token.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        using var directories = new DirectoryLease([.. selected.Select(x => x.RootDirectory), Path.GetDirectoryName(output)!]);
        var sources = new List<FileStream>();
        var metadataFiles = new List<FileStream>();
        var expectedHashes = new List<string>();
        var files = new List<ExportedFile>();
        var notes = new List<string>();
        string status = "complete";
        bool created = false;
        using var outputLease = new DirectoryLease();
        long done = 0;
        try
        {
            foreach (var entry in selected)
            {
                Check(token, watch, 3600);
                if (entry.RootFileId.Length != 0 && directories.FileId(entry.RootDirectory) != entry.RootFileId)
                    throw new IOException("The selected Recycle Bin directory changed since inventory.");
                if (entry.MetadataIdentity is not null)
                {
                    var metadata = PathSafety.OpenRead(entry.MetadataIdentity.Path); metadataFiles.Add(metadata);
                    PathSafety.VerifyIdentity(entry.MetadataIdentity, PathSafety.Identity(entry.MetadataIdentity.Path, metadata));
                    if (!PhotoCarver.HashRange(metadata, 0, metadata.Length, token).Equals(entry.MetadataIdentity.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Recycle Bin metadata changed since inventory.");
                }
                else if (File.Exists(Path.Combine(entry.RootDirectory, "$I" + entry.PayloadName![2..])))
                    throw new IOException("Recycle Bin metadata appeared after this orphan payload was inventoried. Scan again.");
                var source = PathSafety.OpenRead(entry.PayloadIdentity!.Path); sources.Add(source);
                PathSafety.VerifyIdentity(entry.PayloadIdentity, PathSafety.Identity(entry.PayloadIdentity.Path, source));
                string sha = Hash(source, token, watch, progress, "recycle_verifying", selected.Length);
                if (entry.PayloadIdentity.Sha256.Length != 0 && !sha.Equals(entry.PayloadIdentity.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The selected Recycle Bin payload changed since inventory.");
                expectedHashes.Add(sha);
            }
            Check(token, watch, 3600);
            PathSafety.CreateNewDirectory(output);
            outputLease.Pin(output);
            created = true;
            byte[] buffer = new byte[BlockBytes];
            for (int index = 0; index < selected.Length; index++)
            {
                var entry = selected[index];
                var source = sources[index];
                string sha = expectedHashes[index];
                string name = $"recovered_{index + 1:D5}_{sha[..12]}{SafeExtension(entry.Name)}";
                string target = Path.Combine(output, name);
                using (var destinationStream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BlockBytes))
                using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    source.Position = 0;
                    long written = 0;
                    int read;
                    while ((read = source.Read(buffer)) != 0)
                    {
                        Check(token, watch, 3600);
                        if (written + read > entry.PayloadIdentity!.Length) throw new IOException("Source grew during recovery.");
                        destinationStream.Write(buffer, 0, read); digest.AppendData(buffer, 0, read); written += read;
                        progress?.Report(new("recycle_copying", done + written, total, files.Count, "Copying selected bytes; the original Recycle Bin entry is preserved."));
                    }
                    destinationStream.Flush(true);
                    if (written != entry.PayloadIdentity!.Length || !Convert.ToHexString(digest.GetHashAndReset()).Equals(sha, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The copied payload failed stream verification.");
                }
                using (var check = PathSafety.OpenRead(target))
                    if (!Hash(check, token, watch, progress, "recycle_verifying_output", files.Count).Equals(sha, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The written recovery file failed independent SHA-256 verification.");
                PathSafety.VerifyIdentity(entry.PayloadIdentity!, PathSafety.Identity(entry.PayloadIdentity!.Path, source));
                Check(token, watch, 3600);
                files.Add(new(entry.Id, target, entry.PayloadIdentity.Length, sha, entry.State)); done += entry.PayloadIdentity.Length;
            }
        }
        catch (OperationCanceledException) when (created) { status = "cancelled_partial"; notes.Add("Recovery cancelled. Only files listed in this receipt passed all byte checks; other output is incomplete or unverified."); }
        catch (Exception e) when (created && e is IOException or UnauthorizedAccessException or TimeoutException)
        { status = "failed_partial"; notes.Add(e.Message); notes.Add("Only listed output files passed verification. Source entries were not removed or changed."); }
        finally
        {
            foreach (var stream in sources) stream.Dispose();
            foreach (var stream in metadataFiles) stream.Dispose();
        }
        if (selected.Any(x => !x.PayloadHashVerified)) notes.Add("For large or unhashed inventory entries, the payload SHA-256 was first established immediately before copying. File identity and metadata hashes were bound at inventory; source authorship is not established.");
        var result = new ExportReport("retrace.recycle-export.v1", status, output, files, notes);
        using (var receipt = new FileStream(Path.Combine(output, "recovery.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(receipt, result, JsonOptions);
        return result;
    }

    public static async Task SaveReportAsync(RecycleReport report, string path, CancellationToken cancellationToken = default)
    {
        ValidateReport(report);
        string target = PathSafety.CheckPath(path, false);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Choose a new metadata report filename.");
        if (InsideNamedRecycleBin(target) || report.Roots.Any(root => IsWithin(target, root) || SamePath(target, root))) throw new IOException("Save metadata outside the Recycle Bin source.");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        if (bytes.Length > 16 * 1024 * 1024) throw new IOException("Recycle Bin report exceeds 16 MiB.");
        cancellationToken.ThrowIfCancellationRequested();
        string parent = PathSafety.CheckPath(Path.GetDirectoryName(target)!);
        using var guard = new DirectoryLease(parent);
        // Metadata is small and bounded. Cancellation is checked before exclusive creation; the
        // synchronous durable write then completes so a cancelled task never publishes partial JSON.
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes); output.Flush(true);
        await Task.CompletedTask;
    }

    public static async Task<RecycleReport> LoadReportAsync(string path, CancellationToken cancellationToken = default)
    {
        using var input = PathSafety.OpenRead(path);
        if (input.Length is <= 0 or > 16 * 1024 * 1024) throw new IOException("Choose a Recycle Bin JSON report up to 16 MiB.");
        var bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            RejectDuplicates(document.RootElement);
            RequireProperties(document.RootElement, "Schema", "Version", "Status", "Scope", "StartedUtc", "Verification", "Entries", "Roots", "Diagnostics", "HashedPayloadBytes", "Limitations");
            foreach (var entry in document.RootElement.GetProperty("Entries").EnumerateArray())
            {
                RequireProperties(entry, "Id", "RootDirectory", "RootFileId", "MetadataName", "PayloadName", "MetadataIdentity", "PayloadIdentity", "Metadata", "Name", "OriginalPath", "DeletedUtc", "DeclaredBytes", "ContentBytes", "State", "PayloadKind", "IsDirectory", "CanRecover", "Diagnostics");
                foreach (string name in new[] { "MetadataIdentity", "PayloadIdentity" })
                    if (entry.GetProperty(name).ValueKind != JsonValueKind.Null)
                        RequireProperties(entry.GetProperty(name), "Path", "Length", "LastWriteUtcTicks", "Sha256", "FileId");
            }
            var report = document.RootElement.Deserialize<RecycleReport>(JsonOptions) ?? throw new IOException("Invalid Recycle Bin report.");
            ValidateReport(report);
            return report with { Verification = "saved_metadata_not_revalidated" };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or OverflowException)
        { throw new IOException("Invalid Recycle Bin JSON report.", e); }
    }

    private static void ValidateReport(RecycleReport report)
    {
        if (report is null || report.Schema != "retrace.recycle.v1" || report.Entries is null || report.Roots is null || report.Diagnostics is null ||
            report.Limitations is null || report.Status is null || report.Scope is null || report.Version is null || report.Verification is null ||
            report.Entries.Count > MaximumEntries || report.Roots.Count > 26 || report.Diagnostics.Count > 256 || report.Limitations.Count > 128)
            throw new IOException("Unsupported or oversized Recycle Bin report.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (string root in report.Roots) ValidateLocalPath(root);
        foreach (var entry in report.Entries)
        {
            if (entry is null || string.IsNullOrEmpty(entry.Id) || !ids.Add(entry.Id) || entry.Diagnostics is null || entry.Diagnostics.Count > 256 ||
                entry.RootDirectory is null || entry.RootFileId is null || entry.Name is null || entry.OriginalPath is null || entry.State is null || entry.PayloadKind is null ||
                !report.Roots.Any(x => SamePath(x, entry.RootDirectory))) throw new IOException("Invalid Recycle Bin entry/root binding.");
            if (entry.Metadata is not null && (entry.Metadata.Diagnostics is null || entry.Metadata.OriginalPath is null))
                throw new IOException("Invalid parsed Recycle Bin metadata.");
            ValidateLocalPath(entry.RootDirectory);
            foreach (var (name, prefix, identity) in new[] { (entry.MetadataName, 'I', entry.MetadataIdentity), (entry.PayloadName, 'R', entry.PayloadIdentity) })
            {
                if (name is not null && (!ValidComponent(name) || name.Length < 3 || name[0] != '$' || char.ToUpperInvariant(name[1]) != prefix))
                    throw new IOException("Invalid $I/$R source name.");
                if (identity is not null)
                {
                    if (name is null || identity.Path is null || identity.Length < 0 || identity.Length > (prefix == 'I' ? MaximumMetadataBytes : MaximumRecoveryBytes) ||
                        !SamePath(identity.Path, Path.Combine(entry.RootDirectory, name)) || identity.LastWriteUtcTicks < 0 || identity.LastWriteUtcTicks > DateTime.MaxValue.Ticks ||
                        identity.Sha256 is null || identity.FileId is null || (prefix == 'I' || identity.Sha256.Length != 0) && !ValidSha(identity.Sha256))
                        throw new IOException("Invalid Recycle Bin source identity/hash.");
                    ValidateLocalPath(identity.Path);
                }
            }
            if (entry.MetadataName is not null && entry.PayloadName is not null && !entry.MetadataName[2..].Equals(entry.PayloadName[2..], StringComparison.OrdinalIgnoreCase))
                throw new IOException("Recycle Bin metadata and payload tokens do not match.");
            if (EntryId(entry) != entry.Id || entry.CanRecover && (entry.PayloadIdentity is null || entry.IsDirectory || entry.MetadataName is not null && entry.MetadataIdentity is null))
                throw new IOException("Recycle Bin entry binding changed or is not recoverable.");
        }
    }

    private static string EntryId(RecycleEntry entry)
    {
        var binding = new { entry.RootDirectory, entry.RootFileId, entry.MetadataName, entry.PayloadName, entry.MetadataIdentity, entry.PayloadIdentity,
            entry.IsDirectory, entry.Metadata, entry.Name, entry.OriginalPath, entry.DeletedUtc, entry.DeclaredBytes, entry.ContentBytes,
            entry.State, entry.PayloadKind, entry.CanRecover, entry.Diagnostics };
        return "rb-" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(binding)))[..32];
    }

    private static string Hash(FileStream source, CancellationToken token, Stopwatch watch, IProgress<RecoveryProgress>? progress, string stage, int count)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BlockBytes]; source.Position = 0;
        long done = 0; int read;
        while ((read = source.Read(buffer)) != 0)
        {
            Check(token, watch, 3600); digest.AppendData(buffer, 0, read); done += read;
            progress?.Report(new(stage, done, source.Length, count, "Verifying selected Recycle Bin bytes by SHA-256."));
        }
        Check(token, watch, 3600);
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static void Check(CancellationToken token, Stopwatch watch, int seconds)
    { token.ThrowIfCancellationRequested(); if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException("The bounded Recycle Bin operation exceeded its time budget."); }

    private static bool ValidComponent(string name) => name.Length is > 0 and <= 255 && name is not ("." or "..") &&
        !name.EndsWith(' ') && !name.EndsWith('.') && !name.Any(c => c < 32 || "\\/:<>\"|?*".Contains(c));
    private static string LastComponent(string? path) => path is null ? "" : path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
    private static string SafeExtension(string name)
    {
        string extension = Path.GetExtension(LastComponent(name));
        return extension.Length is > 1 and <= 17 && extension.AsSpan(1).ToArray().All(char.IsAsciiLetterOrDigit) ? extension.ToLowerInvariant() : ".bin";
    }
    private static bool ValidSha(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool InsideNamedRecycleBin(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase));
    private static void ValidateLocalPath(string path)
    {
        // Structural validation must not require unrelated sources to remain connected.
        // Live filesystem checks and directory/file leases are applied only to selected recovery.
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0 || !SamePath(Path.GetFullPath(path), path))
            throw new IOException("A canonical absolute local recovery binding is required.");
    }
    private static bool SamePath(string a, string b) => string.Equals(a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static bool IsWithin(string path, string root) => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static List<string> CurrentUserRoots()
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("Live Recycle Bin discovery is available only on Windows. Select an explicit copied directory for analysis.");
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new IOException("The current Windows SID is unavailable.");
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram)
                    roots.Add(Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid));
            }
            catch (IOException) { }
        }
        return roots;
    }

    private static string ExplicitRoot(string path)
    {
        string root = PathSafety.CheckPath(path);
        if (!Directory.Exists(root)) throw new IOException("Choose a copied Recycle Bin directory, not an ordinary file.");
        if (OperatingSystem.IsWindows())
        {
            string live = Path.Combine(Path.GetPathRoot(root)!, "$Recycle.Bin");
            using var identity = WindowsIdentity.GetCurrent();
            string sid = identity.User?.Value ?? throw new IOException("The current Windows SID is unavailable.");
            if (SamePath(root, live)) root = PathSafety.CheckPath(Path.Combine(root, sid));
            else if (IsWithin(root, live) && !SamePath(root, Path.Combine(live, sid)))
                throw new IOException("Live Recycle Bin access is restricted to the current Windows user's SID directory.");
        }
        return root;
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject()) { if (!names.Add(item.Name)) throw new IOException("Duplicate report JSON properties are not accepted."); RejectDuplicates(item.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }

    private static void RequireProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || names.Any(name => !value.TryGetProperty(name, out _)))
            throw new IOException("Required Recycle Bin report fields are missing.");
    }

    private sealed class DirectoryLease : IDisposable
    {
        private readonly Dictionary<string, SafeFileHandle> handles = new(StringComparer.OrdinalIgnoreCase);
        public DirectoryLease(params string[] directories) => Pin(directories);
        public void Pin(params string[] directories)
        {
            try
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string directory in directories)
                    for (string? path = PathSafety.CheckPath(directory); path is not null; path = Path.GetDirectoryName(path)) paths.Add(path);
                foreach (string path in paths.OrderBy(x => x.Length))
                {
                    if (handles.ContainsKey(path)) continue;
                    PathSafety.CheckPath(path);
                    if (!Directory.Exists(path)) throw new IOException("Recovery source/target parent must be a directory.");
                    if (!OperatingSystem.IsWindows()) continue;
                    var handle = CreateFileW(path, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (handle.IsInvalid) { handle.Dispose(); throw new IOException("The recovery directory could not be pinned.", new Win32Exception(Marshal.GetLastWin32Error())); }
                    handles.Add(path, handle);
                    if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (info.Attributes & (uint)FileAttributes.Directory) == 0)
                        throw new IOException("Recovery directory links and reparse points are not accepted.");
                }
            }
            catch { Dispose(); throw; }
        }
        public string FileId(string path)
        {
            if (!OperatingSystem.IsWindows()) return "";
            if (!handles.TryGetValue(path, out var handle) || !GetFileInformationByHandle(handle, out var info)) throw new IOException("The recovery directory identity is unavailable.");
            return $"{info.Volume:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}";
        }
        public void Dispose() { foreach (var handle in handles.Values.Reverse()) handle.Dispose(); handles.Clear(); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation { public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
