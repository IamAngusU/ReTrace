namespace ReTrace.Core;

/// <summary>Selected photo and video formats share one bounded source pass.</summary>
public static class MediaCarver
{
    public static HashSet<string> NormalizeTypes(string[]? types)
    {
        types ??= ["jpeg", "png", "mp4", "mov", "avi"];
        if (types.Length is < 1 or > 16) throw new ArgumentException("Select supported native media formats.", nameof(types));
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? item in types)
        {
            string value = item?.ToLowerInvariant() switch { "jpg" => "jpeg", { } text => text, _ => "" };
            if (value is not ("jpeg" or "png" or "mp4" or "mov" or "avi"))
                throw new ArgumentException("Native media formats are JPEG, PNG, MP4, MOV and AVI.", nameof(types));
            result.Add(value);
        }
        return result;
    }

    public static Task<RecoveryReport> ScanAsync(string source, RecoveryOptions? options = null,
        string[]? types = null, IProgress<RecoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        => PhotoCarver.ScanMediaAsync(source, options ?? new(), NormalizeTypes(types), progress, cancellationToken);
}
