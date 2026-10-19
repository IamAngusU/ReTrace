using System.Security.Cryptography;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace ReTrace.Core;

public sealed record InvestigationCase(string CaseId, RecoveryReport Report, string Verification,
    DateTimeOffset SavedUtc, string Path)
{
    public bool CanRecover { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record InvestigationSummary(string CaseId, string Path, string SourcePath, string Engine,
    string Status, int CandidateCount, DateTimeOffset SavedUtc, string Verification)
{
    public string Error { get; init; } = "";
}

/// <summary>
/// Versioned, metadata-only investigations. Loading never executes a worker, repairs a file or
/// claims that recorded source hashes have been revalidated without reading those bytes.
/// </summary>
public static class InvestigationStore
{
    public const string Schema = "retrace.investigation.v1";
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    public const int MaximumLibraryCases = 512;
    private const long MaximumCandidateBytes = 1024L * 1024 * 1024;
    private const int MaximumCandidates = 100000;
    private const string Suffix = ".retrace.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    private sealed record StoredCase(string Schema, string CaseId, DateTimeOffset SavedUtc, RecoveryReport Report);

    public static string DefaultLibraryDirectory
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home)) throw new IOException("The local user home directory is unavailable.");
            return System.IO.Path.Combine(home, ".retrace", "cases");
        }
    }

    public static async Task<InvestigationCase> SaveAsync(RecoveryReport report, string path,
        IProgress<RecoveryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReport(report);
        string destination = RequireNewFile(path);
        if (SamePath(destination, report.Source.Path) || report.RunDirectory is not null && IsWithin(destination, report.RunDirectory))
            throw new IOException("Save investigation metadata outside the original source and specialist run.");
        string caseId = CaseIdFor(report);
        var savedUtc = DateTimeOffset.UtcNow;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new StoredCase(Schema, caseId, savedUtc, report), JsonOptions);
        if (bytes.Length > MaximumFileBytes) throw new IOException("The saved investigation exceeds the 16 MiB metadata limit.");
        string parent = System.IO.Path.GetDirectoryName(destination)!;
        string temporary = System.IO.Path.Combine(parent, ".retrace-" + Guid.NewGuid().ToString("N") + ".pending");
        SourceIdentity? temporaryIdentity = null;
        bool published = false;
        try
        {
            PathSafety.CheckPath(parent);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 65536, FileOptions.Asynchronous))
            {
                temporaryIdentity = PathSafety.Identity(temporary, stream);
                for (int at = 0; at < bytes.Length;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(65536, bytes.Length - at);
                    await stream.WriteAsync(bytes.AsMemory(at, count), cancellationToken);
                    at += count;
                    progress?.Report(new("saving_investigation", at, bytes.Length, report.Candidates.Count, "Saving investigation metadata to a new file."));
                }
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
                temporaryIdentity = PathSafety.Identity(temporary, stream);
            }
            cancellationToken.ThrowIfCancellationRequested();
            PathSafety.CheckPath(parent);
            using (var check = PathSafety.OpenRead(temporary))
                PathSafety.VerifyIdentity(temporaryIdentity, PathSafety.Identity(temporary, check));
            RequireNewFile(destination);
            File.Move(temporary, destination, overwrite: false);
            published = true;
        }
        finally
        {
            // Only this operation's exact private temporary file can be removed. Never remove a
            // destination, source file, recovery output, directory tree or someone else's file.
            if (!published && temporaryIdentity is not null)
            {
                try
                {
                    SourceIdentity cleanupIdentity;
                    using (var check = PathSafety.OpenRead(temporary))
                    {
                        var actual = PathSafety.Identity(temporary, check);
                        // Interrupted writes legitimately changed length/time. The Windows file ID
                        // still proves that this is the same private temporary file we created.
                        if (temporaryIdentity.FileId.Length > 0 && temporaryIdentity.FileId != actual.FileId)
                            throw new IOException("The private metadata temporary file was replaced.");
                        if (temporaryIdentity.FileId.Length == 0) PathSafety.VerifyIdentity(temporaryIdentity, actual);
                        cleanupIdentity = actual;
                    }
                    if (OperatingSystem.IsWindows()) DeleteMetadataByHandle(temporary, cleanupIdentity);
                    else File.Delete(temporary);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        progress?.Report(new("investigation_saved", bytes.Length, bytes.Length, report.Candidates.Count,
            "Only investigation metadata was saved. Candidate bytes are verified again before recovery."));
        return new(caseId, report, "not_revalidated", savedUtc, destination)
        {
            Warnings = ["Saving metadata does not revalidate source or candidate bytes. Open the investigation to check current bindings."]
        };
    }

    public static async Task<InvestigationCase> SaveToLibraryAsync(RecoveryReport report,
        string? libraryDirectory = null, IProgress<RecoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReport(report);
        string directory = LibraryDirectory(libraryDirectory, create: true);
        string path = System.IO.Path.Combine(directory, CaseIdFor(report) + Suffix);
        if (File.Exists(path))
        {
            // Identical report content has a stable case ID: a second save does not overwrite it.
            var existing = await ReadAsync(path, cancellationToken);
            if (existing.CaseId != CaseIdFor(report)) throw new IOException("An existing library entry has conflicting metadata.");
            return existing;
        }
        if (Directory.EnumerateFiles(directory, "case-*" + Suffix, SearchOption.TopDirectoryOnly).Take(MaximumLibraryCases).Count() >= MaximumLibraryCases)
            throw new IOException("The local library already contains 512 cases. Choose or archive another metadata folder.");
        return await SaveAsync(report, path, progress, cancellationToken);
    }

    public static async Task<InvestigationCase> LoadAsync(string path,
        IProgress<RecoveryProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var investigation = await ReadAsync(path, cancellationToken);
        var report = investigation.Report;
        cancellationToken.ThrowIfCancellationRequested();
            if (report.Engine is "native-photo-carver" or "native-video-carver" or "native-media-carver")
        {
            try
            {
                using var source = PathSafety.OpenRead(report.Source.Path);
                try { PathSafety.VerifyIdentity(report.Source, PathSafety.Identity(report.Source.Path, source)); }
                catch (IOException)
                {
                    return investigation with
                    {
                        Verification = "source_identity_changed", CanRecover = false,
                        Warnings = ["The source image identity changed. Findings remain readable; scan the current image before recovery."]
                    };
                }
                progress?.Report(new("investigation_loaded", 0, 0, report.Candidates.Count,
                    "Source file identity checked. Selected candidate SHA-256 is checked separately before recovery."));
                return investigation with
                {
                    Verification = "verified_metadata_only", CanRecover = true,
                    Warnings = ["Source size, timestamp and recorded file ID were checked. The whole image and candidate hashes were not read again; selected bytes are verified before recovery."]
                };
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return investigation with
                {
                    Verification = "source_unavailable", CanRecover = false,
                    Warnings = ["The original image is unavailable. Saved findings remain readable; reconnect the unchanged image and reopen before recovery."]
                };
            }
        }

        long total = 0, done = 0;
        foreach (var candidate in report.Candidates) total = checked(total + candidate.Length);
        foreach (var candidate in report.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stored = PathSafety.OpenRead(candidate.StoredPath!);
            var identity = PathSafety.Identity(candidate.StoredPath!, stored);
            if (stored.Length != candidate.Length) throw new IOException("A saved specialist output changed length: " + candidate.Id);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[1024 * 1024];
            int read;
            while ((read = await stored.ReadAsync(buffer, cancellationToken)) != 0)
            {
                hash.AppendData(buffer, 0, read);
                done += read;
                progress?.Report(new("verifying_saved_output", done, total, report.Candidates.Count,
                    "Checking saved specialist candidate bytes; the original image is not reopened."));
            }
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A saved specialist output failed SHA-256 verification: " + candidate.Id);
            PathSafety.VerifyIdentity(identity, PathSafety.Identity(candidate.StoredPath!, stored));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return investigation with
        {
            Verification = report.Candidates.Count == 0 ? "metadata_only_no_candidates" : "verified_spool_hashes", CanRecover = report.Candidates.Count != 0,
            Warnings = ["Saved specialist candidate hashes were checked. Their original image, original names, deletion status and exact original completeness are not established by this check."]
        };
    }

    public static async Task<IReadOnlyList<InvestigationSummary>> ListAsync(string? libraryDirectory = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = LibraryDirectory(libraryDirectory, create: false);
        if (!Directory.Exists(directory)) return [];
        string[] paths = Directory.EnumerateFiles(directory, "case-*" + Suffix, SearchOption.TopDirectoryOnly)
            .Take(MaximumLibraryCases + 1).ToArray();
        if (paths.Length > MaximumLibraryCases) throw new IOException("The local library exceeds 512 cases. Choose or archive another metadata folder.");
        var entries = new List<InvestigationSummary>();
        long metadataBytes = 0;
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = System.IO.Path.GetFileName(path)[..^Suffix.Length];
            if (!ValidCaseId(id)) continue;
            try
            {
                PathSafety.CheckPath(path);
                metadataBytes += new FileInfo(path).Length;
                if (metadataBytes > 64L * 1024 * 1024) throw new IOException("Library metadata exceeds the 64 MiB listing budget; open an explicit case file instead.");
                var item = await ReadAsync(path, cancellationToken);
                if (item.CaseId != id) throw new IOException("The library filename and case metadata disagree.");
                entries.Add(new(id, item.Path, item.Report.Source.Path, item.Report.Engine, item.Report.Status,
                    item.Report.Candidates.Count, item.SavedUtc, "not_revalidated"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                entries.Add(new(id, path, "", "", "invalid_saved_case", 0, DateTimeOffset.MinValue, "invalid_metadata") { Error = e.Message });
            }
            if (metadataBytes > 64L * 1024 * 1024) break;
        }
        return entries.OrderByDescending(x => x.SavedUtc).ThenBy(x => x.CaseId, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Deletes exactly one case metadata file. Never removes scans, images or recovery runs.</summary>
    public static async Task DeleteFromLibraryAsync(string caseId, string? libraryDirectory = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidCaseId(caseId)) throw new IOException("An exact local case ID is required.");
        string directory = LibraryDirectory(libraryDirectory, create: false);
        string path = System.IO.Path.Combine(directory, caseId + Suffix);
        PathSafety.CheckPath(path);
        SourceIdentity expected;
        using (var check = PathSafety.OpenRead(path))
        {
            if (check.Length > MaximumFileBytes) throw new IOException("The local file exceeds the case metadata limit.");
            expected = PathSafety.Identity(path, check);
        }
        InvestigationCase? metadata = null;
        try { metadata = await ReadAsync(path, cancellationToken); }
        catch (IOException)
        {
            // A corrupt metadata file can still be explicitly removed. Its exact case-ID filename,
            // local library scope, single-link ordinary file and live file identity remain required.
        }
        if (metadata is not null && (SamePath(metadata.Report.Source.Path, path) ||
            metadata.Report.Candidates.Any(x => x.StoredPath is not null && SamePath(x.StoredPath, path))))
            throw new IOException("Case metadata cannot be removed when it is also a recorded source.");
        cancellationToken.ThrowIfCancellationRequested();
        PathSafety.CheckPath(path);
        if (OperatingSystem.IsWindows()) DeleteMetadataByHandle(path, expected);
        else
        {
            using (var check = PathSafety.OpenRead(path)) PathSafety.VerifyIdentity(expected, PathSafety.Identity(path, check));
            File.Delete(path);
        }
    }

    private static async Task<InvestigationCase> ReadAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string full = PathSafety.CheckPath(path);
        using var stream = PathSafety.OpenRead(full);
        if (stream.Length is <= 0 or > MaximumFileBytes) throw new IOException("Choose a nonempty investigation JSON file of at most 16 MiB.");
        byte[] bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            ValidateJson(root);
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Schema", out var schema) || schema.ValueKind != JsonValueKind.String)
                throw new IOException("An explicit supported report schema is required.");
            RecoveryReport report;
            DateTimeOffset savedUtc;
            string? recordedId = null;
            if (schema.GetString() == Schema)
            {
                RequireProperties(root, "Schema", "CaseId", "SavedUtc", "Report");
                var saved = root.Deserialize<StoredCase>(JsonOptions) ?? throw new IOException("Invalid investigation metadata.");
                report = saved.Report;
                savedUtc = saved.SavedUtc;
                recordedId = saved.CaseId;
                if (!ValidCaseId(recordedId) || savedUtc.Offset != TimeSpan.Zero || savedUtc == DateTimeOffset.MinValue)
                    throw new IOException("Invalid investigation case ID or UTC save time.");
                ValidateReportJson(root.GetProperty("Report"));
            }
            else if (schema.GetString() == "retrace.recovery.v1")
            {
                // Existing native exports and PhotoRec recovery-report.json are read-only imports.
                ValidateReportJson(root);
                report = root.Deserialize<RecoveryReport>(JsonOptions) ?? throw new IOException("Invalid recovery metadata.");
                savedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(full), TimeSpan.Zero);
            }
            else throw new IOException("Unsupported investigation or recovery report schema.");
            ValidateReport(report);
            string id = CaseIdFor(report);
            if (recordedId is not null && id != recordedId) throw new IOException("Investigation metadata no longer matches its case ID.");
            return new(id, report, "not_revalidated", savedUtc, full);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or OverflowException or InvalidOperationException)
        {
            throw new IOException("Invalid or unsupported investigation JSON metadata.", e);
        }
    }

    private static void ValidateReportJson(JsonElement report)
    {
        RequireProperties(report, "Schema", "Version", "Source", "Engine", "Status", "ScannedBytes", "Candidates",
            "Diagnostics", "Limitations", "Exhaustive", "RunDirectory");
        RequireProperties(report.GetProperty("Source"), "Path", "Length", "LastWriteUtcTicks", "Sha256", "FileId");
        var candidates = report.GetProperty("Candidates");
        if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() > MaximumCandidates)
            throw new IOException("Invalid recovery candidate collection.");
        foreach (var candidate in candidates.EnumerateArray())
            RequireProperties(candidate, "Id", "Kind", "Offset", "Length", "Sha256", "State", "Validation",
                "DeletionStatus", "StoredPath", "Name", "Mapping", "Diagnostics");
    }

    private static void ValidateReport(RecoveryReport report)
    {
        if (report is null || report.Schema != "retrace.recovery.v1" || report.Source is null ||
            report.Engine is not ("native-photo-carver" or "native-video-carver" or "native-media-carver" or "photorec-7.2-windows") || report.Exhaustive ||
            report.Source.Length < 0 || report.Source.Length > RecoveryLimits.MaximumSourceBytes ||
            report.Source.LastWriteUtcTicks < DateTime.MinValue.Ticks || report.Source.LastWriteUtcTicks > DateTime.MaxValue.Ticks ||
            report.ScannedBytes < 0 || report.ScannedBytes > report.Source.Length ||
            report.Candidates is null || report.Candidates.Count > MaximumCandidates)
            throw new IOException("Invalid or unsupported recovery report profile.");
        Text(report.Version, 64); Text(report.Status, 64); Text(report.Source.FileId, 128, empty: true);
        Hash(report.Source.Sha256, empty: true);
        Text(report.Source.Path, 32760);
        string source = PathSafety.CheckPath(report.Source.Path, mustExist: false);
        if (!SamePath(source, report.Source.Path)) throw new IOException("The source path must be absolute and normalized.");
        Strings(report.Diagnostics, 256); Strings(report.Limitations, 128);
        bool native = report.Engine != "photorec-7.2-windows";
        if (native && report.RunDirectory is not null || !native && string.IsNullOrWhiteSpace(report.RunDirectory))
            throw new IOException("The recovery engine and saved-run mapping disagree.");
        if (report.RunDirectory is not null)
        {
            Text(report.RunDirectory, 32760);
            string run = PathSafety.CheckPath(report.RunDirectory, mustExist: false);
            if (!SamePath(run, report.RunDirectory) || SamePath(run, System.IO.Path.GetPathRoot(run)!))
                throw new IOException("A specialist run must be a normalized local subdirectory.");
            if (IsWithin(source, run)) throw new IOException("The original source image cannot be inside the specialist output run.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var storedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in report.Candidates)
        {
            if (candidate is null) throw new IOException("Null recovery candidates are not accepted.");
            Text(candidate.Id, 128); Text(candidate.Kind, 16); Text(candidate.State, 64);
            Text(candidate.Name, 512, empty: true); Text(candidate.Validation, 8192, empty: true);
            Text(candidate.DeletionStatus, 64); Strings(candidate.Diagnostics, 128); Hash(candidate.Sha256);
            if (!char.IsAsciiLetterOrDigit(candidate.Id[0]) || !candidate.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') || !ids.Add(candidate.Id) ||
                !candidate.Kind.All(char.IsAsciiLetterOrDigit) || candidate.Length <= 0 || candidate.Length > MaximumCandidateBytes)
                throw new IOException("Invalid or duplicate candidate IDs, format or byte lengths.");
            if (native)
            {
                if (candidate.Kind is not ("jpeg" or "png" or "mp4" or "mov" or "avi") ||
                    report.Engine == "native-photo-carver" && candidate.Kind is not ("jpeg" or "png") ||
                    report.Engine == "native-video-carver" && candidate.Kind is not ("mp4" or "mov" or "avi"))
                    throw new IOException("The native candidate format is outside its recorded engine profile.");
                if (candidate.StoredPath is not null || candidate.Mapping != "contiguous_source_bytes" || candidate.Offset < 0 ||
                    candidate.Length > report.Source.Length || candidate.Offset > report.Source.Length - candidate.Length)
                    throw new IOException("A native candidate range is outside its recorded source image.");
            }
            else
            {
                if (candidate.StoredPath is null || candidate.Mapping != "external_reconstruction" || candidate.Offset != -1)
                    throw new IOException("A specialist candidate must have an external reconstruction and no invented source offset.");
                Text(candidate.StoredPath, 32760);
                string stored = PathSafety.CheckPath(candidate.StoredPath, mustExist: false);
                if (!SamePath(stored, candidate.StoredPath) || !IsWithin(stored, report.RunDirectory!) || !storedPaths.Add(stored))
                    throw new IOException("A specialist candidate is outside its run or has a duplicate output path.");
                string parent = System.IO.Path.GetDirectoryName(stored)!;
                string folder = System.IO.Path.GetFileName(parent);
                if (!SamePath(System.IO.Path.GetDirectoryName(parent)!, report.RunDirectory!) ||
                    !folder.StartsWith("recup.", StringComparison.Ordinal) ||
                    !int.TryParse(folder.AsSpan(6), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number) || number < 1)
                    throw new IOException("A PhotoRec output must remain in a direct recup.N directory of its recorded run.");
            }
        }
    }

    private static void ValidateJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new IOException("Duplicate JSON properties are not accepted.");
                ValidateJson(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateJson(item);
    }

    private static void RequireProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object || names.Any(name => !element.TryGetProperty(name, out _)))
            throw new IOException("Required investigation metadata is missing.");
    }

    private static void Text(string? text, int maximum, bool empty = false)
    {
        if (text is null || text.Length > maximum || !empty && text.Length == 0 || text.Contains('\0'))
            throw new IOException("Invalid or oversized investigation metadata text.");
    }

    private static void Strings(List<string>? values, int maximum)
    {
        if (values is null || values.Count > maximum) throw new IOException("Oversized investigation diagnostics.");
        foreach (string value in values) Text(value, 8192, empty: true);
    }

    private static void Hash(string? value, bool empty = false)
    {
        if (value is null || !(empty && value.Length == 0) && (value.Length != 64 || !value.All(Uri.IsHexDigit)))
            throw new IOException("An exact SHA-256 byte binding is required.");
    }

    private static string CaseIdFor(RecoveryReport report)
        => "case-" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(report)));

    private static bool ValidCaseId(string? id)
        => id is not null && id.Length == 69 && id.StartsWith("case-", StringComparison.Ordinal) &&
            id.AsSpan(5).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static bool SamePath(string a, string b)
        => string.Equals(a.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
            b.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool IsWithin(string path, string directory)
        => path.StartsWith(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string RequireNewFile(string path)
    {
        string full = PathSafety.CheckPath(path, mustExist: false);
        if (File.Exists(full) || Directory.Exists(full)) throw new IOException("Choose a new investigation filename. Existing files are never overwritten.");
        string parent = System.IO.Path.GetDirectoryName(full) ?? throw new IOException("A local parent folder is required.");
        PathSafety.CheckPath(parent);
        if (!Directory.Exists(parent)) throw new IOException("The parent folder for the investigation must exist.");
        return full;
    }

    private static string LibraryDirectory(string? directory, bool create)
    {
        string full = PathSafety.CheckPath(directory ?? DefaultLibraryDirectory, mustExist: false);
        if (File.Exists(full)) throw new IOException("The case library must be a directory.");
        if (create)
        {
            Directory.CreateDirectory(full);
            PathSafety.CheckPath(full);
        }
        return full;
    }

    // Deletion is bound to the checked file handle, not a second path lookup. Exclusive sharing
    // prevents a writer/rename race between checking the metadata identity and marking it deleted.
    private static void DeleteMetadataByHandle(string path, SourceIdentity expected)
    {
        using var handle = OpenMetadataForDeletion(path, 0x80010000, 0, IntPtr.Zero, 3, 0x00200080, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("The saved case is in use or cannot be removed.", new Win32Exception(Marshal.GetLastWin32Error()));
        if (!GetFileInformationByHandle(handle, out var info) || info.Links != 1 ||
            (info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
            throw new IOException("Only an ordinary single-link case metadata file can be removed.");
        using var stream = new FileStream(handle, FileAccess.Read);
        PathSafety.VerifyIdentity(expected, PathSafety.Identity(path, stream));
        PathSafety.CheckPath(path);
        var disposition = new FileDisposition { DeleteFile = 1 };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
            throw new IOException("The checked case metadata could not be removed.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume,
            SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { public byte DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle OpenMetadataForDeletion(string path, uint access, uint sharing, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        ref FileDisposition information, uint bytes);
}
