using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

[assembly: InternalsVisibleTo("ReTrace.Tests")]

namespace ReTrace.Core;

public sealed record PhotoRecOptions
{
    public long MaximumOutputBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public int MaximumFiles { get; init; } = 10000;
    public long MaximumFileBytes { get; init; } = 64L * 1024 * 1024;
    public int TimeoutSeconds { get; init; } = 3600;
    public bool IncludeBroken { get; init; } = true;
    public string[] Types { get; init; } = ["jpg", "png"];
    public string? BundleArchivePath { get; init; }
}

/// <summary>Image-only PhotoRec worker. It never mounts or opens a live device.</summary>
public static class PhotoRecEngine
{
    private const long MaximumSourceBytes = 2L * 1024 * 1024 * 1024 * 1024;
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
        { "jpg", "png", "pdf", "zip", "wav" };

    public static async Task<RecoveryReport> RunAsync(string source, string workDirectory,
        PhotoRecOptions? options = null, IProgress<RecoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();
        source = PhotoRecPaths.RequireAbsolute(source);
        workDirectory = PhotoRecPaths.RequireAbsolute(workDirectory);
        PhotoRecPaths.RequireRegularFile(source);
        await using var input = PathSafety.OpenRead(source);
        var identity = PathSafety.Identity(source, input);
        if (identity.Length <= 0 || identity.Length > MaximumSourceBytes)
            throw new IOException("Choose a nonempty ordinary image file of at most 2 TiB.");
        // On Windows this sharing mode denies writes and replacement throughout the job.
        if (input.Length != identity.Length)
            throw new IOException("The source changed while opening it.");
        PhotoRecPaths.CreateNewDirectory(workDirectory);
        var diagnostics = new List<string>();
        var status = "failed";
        var candidates = new List<RecoveryCandidate>();
        var completedScan = false;
        try
        {
            identity = identity with { Sha256 = await HashSourceAsync(input, progress, cancellationToken) };
            var bundle = await PhotoRecBundle.EnsureAsync(workDirectory, progress, cancellationToken,
                options.BundleArchivePath);
            using var componentLease = await PhotoRecBundle.LockForExecutionAsync(bundle, cancellationToken);
            var start = BuildStartInfo(bundle.ExecutablePath, source, workDirectory, options);
            using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            if (!process.Start()) throw new IOException("The pinned PhotoRec worker did not start.");
            process.StandardInput.Close();
            var stdout = DrainAsync(process.StandardOutput);
            var stderr = DrainAsync(process.StandardError);
            var started = Stopwatch.StartNew();
            string? stopReason = null;
            progress?.Report(new("deep_scan", 0, 0, 0,
                "PhotoRec is scanning. Scanned-byte percentage is unavailable; recovered output is reported live."));
            try
            {
                while (!process.HasExited)
                {
                    if (cancellationToken.IsCancellationRequested) stopReason = "cancelled";
                    else if (started.Elapsed.TotalSeconds > options.TimeoutSeconds) stopReason = "timeout";
                    var inventory = InspectOutput(workDirectory, options);
                    stopReason ??= inventory.LimitReason;
                    progress?.Report(new("deep_scan", inventory.Bytes, 0,
                        inventory.Files.Count, "Recovered output bytes; this is not a source-scan percentage."));
                    if (stopReason is not null)
                    {
                        Stop(process);
                        break;
                    }
                    await Task.Delay(100, CancellationToken.None);
                }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            finally
            {
                if (!process.HasExited) Stop(process);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(stdout, stderr);
            var finalInventory = InspectOutput(workDirectory, options);
            stopReason ??= finalInventory.LimitReason;
            if (stopReason is not null)
            {
                status = stopReason == "cancelled" ? "cancelled" : "incomplete";
                diagnostics.Add("PhotoRec stopped: " + stopReason + ". Existing recovery output was preserved.");
            }
            else if (process.ExitCode != 0)
            {
                status = "incomplete";
                diagnostics.Add("PhotoRec exited with code " + process.ExitCode + ".");
            }
            else
            {
                completedScan = true;
                status = "complete_within_profile";
            }
            // Cancellation stops the worker promptly and also interrupts assessment.
            // Already indexed candidates and all raw worker outputs remain available.
            foreach (var file in finalInventory.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var kind = KindForPath(file.Path);
                if (kind is null || file.Length == 0 || file.Length > options.MaximumFileBytes) continue;
                progress?.Report(new("assessing_output", candidates.Count, finalInventory.Files.Count,
                    candidates.Count, "Checking recovered file structure and SHA-256."));
                candidates.Add(await AssessOutputAsync(file, kind, candidates.Count + 1,
                    options.MaximumFileBytes, cancellationToken));
            }
            PhotoRecPaths.RequireRegularFile(source);
            if (input.Length != identity.Length)
            {
                status = "source_changed";
                diagnostics.Add("Source identity changed; the recovered run requires review.");
            }
            PathSafety.VerifyIdentity(identity, PathSafety.Identity(source, input));
        }
        catch (OperationCanceledException)
        {
            status = "cancelled";
            diagnostics.Add("Recovery was cancelled. Already indexed candidates and existing run files were preserved.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            System.ComponentModel.Win32Exception)
        {
            status = "failed";
            diagnostics.Add(ex.Message);
        }
        var report = new RecoveryReport
        {
            Source = identity, Engine = "photorec-7.2-windows", Status = status,
            ScannedBytes = completedScan ? identity.Length : 0, Candidates = candidates,
            Diagnostics = diagnostics, RunDirectory = workDirectory,
            Limitations = [
                "Signature-carved candidates have unknown deletion/allocation status and usually lack original names and folder paths.",
                "A complete scan within the selected file-type profile is not exhaustive recovery. Overwritten, TRIM-discarded or encrypted bytes may be unavailable.",
                "Partial or damaged outputs may contain only a fragment; no missing pixels or bytes are invented.",
                "Output budgets are polled every 100 ms; an external worker may exceed a budget before it can be stopped.",
                "Independent image checks validate structure, not the identity or exact original completeness of a file.",
                $"Profile: {string.Join(",", options.Types)}; output bytes {options.MaximumOutputBytes}; files {options.MaximumFiles}; bytes per file {options.MaximumFileBytes}; timeout seconds {options.TimeoutSeconds}."
            ]
        };
        await WriteReportAsync(workDirectory, report);
        progress?.Report(new("finished", 0, 0, candidates.Count, status));
        return report;
    }

    private static async Task<string> HashSourceAsync(Stream source,
        IProgress<RecoveryProgress>? progress, CancellationToken cancellationToken)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            digest.AppendData(buffer, 0, read);
            done += read;
            progress?.Report(new("hashing_source", done, source.Length, 0, "Reading the image without changing it."));
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    internal static void ValidateOptions(PhotoRecOptions options)
    {
        if (options.MaximumOutputBytes is <= 0 or > 1024L * 1024 * 1024 * 1024 ||
            options.MaximumFiles is < 1 or > 100000 ||
            options.MaximumFileBytes is <= 0 or > 1024L * 1024 * 1024 ||
            options.TimeoutSeconds is < 1 or > 7 * 24 * 3600 ||
            options.Types is null || options.Types.Length is < 1 or > 5 ||
            (!(options.Types.Length == 1 && options.Types[0] == "all") &&
                options.Types.Any(x => x is null || !AllowedTypes.Contains(x))) ||
            options.Types.Distinct(StringComparer.Ordinal).Count() != options.Types.Length)
            throw new ArgumentException("The PhotoRec options exceed the supported recovery profile.");
    }

    internal static ProcessStartInfo BuildStartInfo(string tool, string source,
        string workDirectory, PhotoRecOptions options)
    {
        ValidateOptions(options);
        var all = options.Types.Length == 1 && options.Types[0] == "all";
        var command = new List<string> { "partition_none", "options",
            options.IncludeBroken ? "keep_corrupted_file" : "keep_corrupted_file_no",
            "paranoid", "fileopt", "everything", all ? "enable" : "disable" };
        foreach (var type in all ? [] : options.Types)
        {
            command.Add(type == "wav" ? "riff" : type);
            command.Add("enable");
        }
        command.AddRange(["wholespace", "search"]);
        var start = new ProcessStartInfo(tool)
        {
            WorkingDirectory = workDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (var arg in new[] { "/log", "/logname", Path.Combine(workDirectory, "photorec.log"),
            "/d", Path.Combine(workDirectory, "recup"), "/cmd", source, string.Join(',', command) })
            start.ArgumentList.Add(arg);
        start.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        start.Environment["PATH"] = Path.GetDirectoryName(tool) + Path.PathSeparator + Environment.SystemDirectory;
        return start;
    }

    internal sealed record OutputFile(string Path, long Length);
    internal sealed record OutputInventory(List<OutputFile> Files, long Bytes, string? LimitReason);

    internal static OutputInventory InspectOutput(string workDirectory, PhotoRecOptions options)
    {
        PhotoRecPaths.RequireDirectory(workDirectory);
        var files = new List<OutputFile>();
        long bytes = 0;
        foreach (var directory in Directory.EnumerateDirectories(workDirectory, "recup.*", SearchOption.TopDirectoryOnly))
        {
            var suffix = Path.GetFileName(directory)["recup.".Length..];
            if (!int.TryParse(suffix, out var number) || number < 1)
                return new(files, bytes, "unexpected_output_directory");
            try { PhotoRecPaths.RequireDirectory(directory); }
            catch (IOException) { return new(files, bytes, "unsafe_output_entry"); }
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                try { PhotoRecPaths.RequireRegularFile(entry); }
                catch (IOException) { return new(files, bytes, "unsafe_output_entry"); }
                var info = new FileInfo(entry);
                bytes = checked(bytes + info.Length);
                files.Add(new(entry, info.Length));
                if (info.Length > options.MaximumFileBytes) return new(files, bytes, "maximum_file_bytes");
                if (files.Count > options.MaximumFiles) return new(files, bytes, "maximum_files");
                if (bytes > options.MaximumOutputBytes) return new(files, bytes, "maximum_output_bytes");
            }
        }
        return new(files, bytes, null);
    }

    private static async Task<RecoveryCandidate> AssessOutputAsync(OutputFile file, string kind,
        int number, long maximumFileBytes, CancellationToken cancellationToken)
    {
        PhotoRecPaths.RequireRegularFile(file.Path);
        using var stream = PathSafety.OpenRead(file.Path);
        var hash = await PhotoRecPaths.HashAsync(stream, cancellationToken);
        var assessment = kind is "jpg" or "png" ? PhotoCarver.AssessFile(file.Path, kind, maximumFileBytes) : null;
        var name = Path.GetFileName(file.Path);
        var state = assessment?.State ?? "unverified_carved_candidate";
        var notes = assessment?.Diagnostics.ToList() ?? [];
        if (name.StartsWith('b'))
        {
            notes.Add("PhotoRec classified this output as a broken file or fragment.");
            if (state is "complete" or "unverified_carved_candidate") state = "partial";
        }
        if (name.StartsWith('t'))
        {
            notes.Add("PhotoRec classified this output as an embedded JPEG thumbnail, not the full original photo.");
            if (state == "complete") state = "thumbnail";
        }
        if (assessment is not null && assessment.Length != file.Length)
        {
            notes.Add("Recovered output includes bytes outside the assessed image span.");
            if (state == "complete") state = "damaged";
        }
        return new RecoveryCandidate
        {
            Id = $"photorec-{number:000000}", Kind = kind, Offset = -1, Length = file.Length,
            Sha256 = hash, State = state, StoredPath = file.Path, Name = name,
            Mapping = "external_reconstruction", DeletionStatus = "unknown",
            Validation = assessment?.Validation ?? "PhotoRec output; independent format validation unavailable.",
            Diagnostics = notes
        };
    }

    private static string? KindForPath(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length < 2 || !"fbt".Contains(name[0]) || !char.IsAsciiDigit(name[1])) return null;
        var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "jpg" or "jpeg" => "jpg", "png" => "png", "pdf" => "pdf",
            "zip" or "docx" or "xlsx" or "pptx" or "odt" => "zip", "wav" => "wav",
            _ => extension.Length is > 0 and <= 16 && extension.All(char.IsAsciiLetterOrDigit) ? extension : "bin"
        };
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer) != 0) { }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private static async Task WriteReportAsync(string root, RecoveryReport report)
    {
        PhotoRecPaths.RequireDirectory(root);
        await using var output = new FileStream(Path.Combine(root, "recovery-report.json"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions { WriteIndented = true });
    }
}
