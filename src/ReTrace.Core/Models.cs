namespace ReTrace.Core;

public static class RecoveryLimits
{
    // Shared 64-bit bound for ordinary images, specialist scans and acquisition.
    public const long MaximumSourceBytes = 16L * 1024 * 1024 * 1024 * 1024;
}

public sealed record RecoveryProgress(string Stage, long BytesDone, long BytesTotal, int Candidates = 0, string Message = "");
public sealed record SourceIdentity(string Path, long Length, long LastWriteUtcTicks, string Sha256 = "")
{
    public string FileId { get; init; } = "";
}
public sealed record RecoveryCandidate
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public long Offset { get; init; }
    public long Length { get; init; }
    public required string Sha256 { get; init; }
    public required string State { get; init; }
    public string Validation { get; init; } = "";
    public string DeletionStatus { get; init; } = "unknown";
    public string? StoredPath { get; init; }
    public string Name { get; init; } = "";
    public string Mapping { get; init; } = "contiguous_source_bytes";
    public List<string> Diagnostics { get; init; } = [];
}
public sealed record RecoveryReport
{
    public string Schema { get; init; } = "retrace.recovery.v1";
    public string Version { get; init; } = "0.1.0-alpha.4";
    public required SourceIdentity Source { get; init; }
    public string Engine { get; init; } = "native-photo-carver";
    public string Status { get; init; } = "complete_within_profile";
    public long ScannedBytes { get; init; }
    public List<RecoveryCandidate> Candidates { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    public bool Exhaustive { get; init; } = false;
    public string? RunDirectory { get; init; }
}
public sealed record RecoveryOptions
{
    public long MaximumFileBytes { get; init; } = 64L * 1024 * 1024;
    public int MaximumCandidates { get; init; } = 10000;
    public long MaximumSourceBytes { get; init; } = RecoveryLimits.MaximumSourceBytes;
    public bool IncludeFragments { get; init; } = true;
    public long MaximumFragmentBytes { get; init; } = 4L * 1024 * 1024;
}
public sealed record ExportedFile(string CandidateId, string Path, long Bytes, string Sha256, string State);
public sealed record ExportReport(string Schema, string Status, string Destination, List<ExportedFile> Files, List<string> Diagnostics);
