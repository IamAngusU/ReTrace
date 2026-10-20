using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReTrace.Core;

public static class RecoveryExporter
{
    public static Task<ExportReport> ExportAsync(RecoveryReport report, IEnumerable<string> candidateIds, string destination,
        IProgress<RecoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Export(report, candidateIds, destination, progress, cancellationToken), CancellationToken.None);

    private static ExportReport Export(RecoveryReport report, IEnumerable<string> candidateIds, string destination,
        IProgress<RecoveryProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(candidateIds);
        if (report.Schema != "retrace.recovery.v1" || report.Candidates.Count > 100000) throw new IOException("Unsupported recovery report.");
        var requested = candidateIds.ToHashSet(StringComparer.Ordinal);
        if (requested.Count is 0 or > 100000) throw new IOException("Select at least one recovery candidate.");
        if (report.Candidates.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != report.Candidates.Count) throw new IOException("The report contains duplicate candidate IDs.");
        var selected = report.Candidates.Where(x => requested.Contains(x.Id)).ToArray();
        if (selected.Length != requested.Count) throw new IOException("A selected candidate is not present in the report.");
        string full = PathSafety.RequireNewDirectory(destination);
        bool requireOriginal = selected.Any(x => x.StoredPath is null);
        using FileStream? source = requireOriginal ? PathSafety.OpenRead(report.Source.Path) : null;
        if (source is not null) PathSafety.VerifyIdentity(report.Source, PathSafety.Identity(report.Source.Path, source));
        long total = 0;
        foreach (var candidate in selected)
        {
            if (candidate.Length <= 0 || candidate.Length > 1024L * 1024 * 1024 || candidate.Sha256.Length != 64 || !candidate.Sha256.All(Uri.IsHexDigit))
                throw new IOException("Invalid candidate length or SHA-256 binding.");
            total = checked(total + candidate.Length);
            if (candidate.StoredPath is null)
            {
                if (candidate.Mapping != "contiguous_source_bytes" || candidate.Offset < 0 || candidate.Offset > source!.Length - candidate.Length)
                    throw new IOException("Candidate bytes are outside the source image.");
            }
            else
            {
                if (candidate.Mapping != "external_reconstruction" || report.RunDirectory is null) throw new IOException("Unknown specialist recovery mapping.");
                string stored = PathSafety.CheckPath(candidate.StoredPath);
                string root = PathSafety.CheckPath(report.RunDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!stored.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Specialist recovery file is outside its recorded run directory.");
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Recovery output must be outside the specialist source run.");
            }
        }
        token.ThrowIfCancellationRequested();
        PathSafety.CreateNewDirectory(full);
        var files = new List<ExportedFile>();
        var notes = new List<string>();
        if (!requireOriginal) notes.Add("Specialist recovery bytes are independently verified from their saved run. The original image was not reopened or revalidated during this export.");
        string status = "complete";
        long done = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            for (int index = 0; index < selected.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                RecoveryCandidate candidate = selected[index];
                string kind = candidate.Kind.ToLowerInvariant();
                string extension = kind switch { "jpeg" => "jpg", "tiff" => "tif", _ => kind.Length is >= 1 and <= 10 && kind.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9') ? kind : "bin" };
                string name = $"recovered_{index + 1:D5}_{candidate.Sha256[..12].ToLowerInvariant()}.{extension}";
                string output = Path.Combine(full, name);
                FileStream? stored = null;
                SourceIdentity? storedIdentity = null;
                try
                {
                    FileStream input = source!;
                    long offset = candidate.Offset;
                    if (candidate.StoredPath is not null)
                    {
                        stored = PathSafety.OpenRead(candidate.StoredPath);
                        storedIdentity = PathSafety.Identity(candidate.StoredPath, stored);
                        if (stored.Length != candidate.Length) throw new IOException("The specialist recovery file changed length.");
                        input = stored;
                        offset = 0;
                    }
                    // Check before writing any candidate bytes; no unverified candidate is published as recovered.
                    string before = PhotoCarver.HashRange(input, offset, candidate.Length, token);
                    if (!before.Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("The selected candidate changed since the scan.");
                    using (var outputStream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
                    using (var copyHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                    {
                        for (long at = 0; at < candidate.Length;)
                        {
                            token.ThrowIfCancellationRequested();
                            int read = RandomAccess.Read(input.SafeFileHandle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, candidate.Length - at)), offset + at);
                            if (read == 0) throw new EndOfStreamException("The source ended during recovery.");
                            outputStream.Write(buffer, 0, read);
                            copyHash.AppendData(buffer, 0, read);
                            at += read;
                            progress?.Report(new("recovering", done + at, total, files.Count, $"Copying candidate {index + 1} of {selected.Length}."));
                        }
                        outputStream.Flush(true);
                        if (!Convert.ToHexString(copyHash.GetHashAndReset()).Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Recovery stream verification failed.");
                    }
                    using (FileStream check = PathSafety.OpenRead(output))
                        if (!PhotoCarver.HashRange(check, 0, check.Length, token).Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Written recovery file failed SHA-256 verification.");
                    if (storedIdentity is not null) PathSafety.VerifyIdentity(storedIdentity, PathSafety.Identity(candidate.StoredPath!, stored!));
                    if (source is not null) PathSafety.VerifyIdentity(report.Source, PathSafety.Identity(report.Source.Path, source));
                    files.Add(new(candidate.Id, output, candidate.Length, candidate.Sha256.ToLowerInvariant(), candidate.State));
                    done += candidate.Length;
                }
                finally { stored?.Dispose(); }
            }
        }
        catch (OperationCanceledException)
        {
            status = "cancelled_partial";
            notes.Add("Recovery cancelled. Verified files are listed; any other file in this directory is incomplete and unverified.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            status = "failed_partial";
            notes.Add(e.Message);
            notes.Add("Only files in this report passed verification; any other output is incomplete or unverified. Source files were not modified.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        var result = new ExportReport("retrace.export.v1", status, full, files, notes);
        string receipt = Path.Combine(full, "recovery.json");
        using (var manifest = new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(manifest, result, new JsonSerializerOptions { WriteIndented = true });
        progress?.Report(new(status, done, total, files.Count, "Recovery receipt saved. Source bytes were not changed."));
        return result;
    }
}
