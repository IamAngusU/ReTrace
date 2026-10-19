using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ReTrace.Core;

/// <summary>Bounded, contiguous JPEG/PNG carving. Format structure is evidence, not a pixel-decoding guarantee.</summary>
public static class PhotoCarver
{
    private const int BlockBytes = 1024 * 1024;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = MakeCrcTable();
    private static readonly SearchValues<byte> MediaMarkers = SearchValues.Create((ReadOnlySpan<byte>)[0x89, 0xff, (byte)'f', (byte)'m', (byte)'w', (byte)'R']);

    public static RecoveryCandidate? AssessFile(string path, string kind, long maximumFileBytes = 64L * 1024 * 1024)
    {
        if (maximumFileBytes < 128 || maximumFileBytes > 1024L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        using FileStream source = PathSafety.OpenRead(path);
        string format = kind.ToLowerInvariant() switch { "jpg" or "jpeg" => "jpeg", "png" => "png", _ => throw new ArgumentException("JPEG or PNG is required.", nameof(kind)) };
        ParseResult assessment = format == "png" ? ParsePng(source, 0, maximumFileBytes, CancellationToken.None) : ParseJpeg(source, 0, maximumFileBytes, CancellationToken.None);
        if (!assessment.Plausible || assessment.Length <= 0) return null;
        return new() { Id = "assessment", Kind = format, Offset = 0, Length = assessment.Length, Sha256 = HashRange(source, 0, assessment.Length, CancellationToken.None),
            State = assessment.State, Validation = assessment.Validation, Diagnostics = assessment.Diagnostics };
    }

    public static Task<RecoveryReport> ScanAsync(string source, RecoveryOptions? options = null,
        IProgress<RecoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(source, options ?? new(), progress, cancellationToken), CancellationToken.None);

    internal static Task<RecoveryReport> ScanMediaAsync(string source, RecoveryOptions options, HashSet<string> types,
        IProgress<RecoveryProgress>? progress, CancellationToken token)
        => Task.Run(() => Scan(source, options, progress, token, types), CancellationToken.None);

    private static RecoveryReport Scan(string source, RecoveryOptions options, IProgress<RecoveryProgress>? progress, CancellationToken token, HashSet<string>? types = null)
    {
        if (options.MaximumFileBytes < 128 || options.MaximumFileBytes > 1024L * 1024 * 1024 ||
            options.MaximumFragmentBytes < 128 || options.MaximumFragmentBytes > 16L * 1024 * 1024 ||
            options.MaximumCandidates is < 1 or > 100000 || options.MaximumSourceBytes < 1 || options.MaximumSourceBytes > RecoveryLimits.MaximumSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(options), "Recovery limits are outside the supported profile.");
        using FileStream stream = PathSafety.OpenRead(source);
        bool videoProfile = types?.Any(t => t is "mp4" or "mov" or "avi") == true;
        SourceIdentity identity = PathSafety.Identity(source, stream);
        if (identity.Length > options.MaximumSourceBytes) throw new IOException("The source image exceeds the configured size limit.");
        var candidates = new List<RecoveryCandidate>();
        var diagnostics = new List<string>();
        using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BlockBytes + 16);
        long scanned = 0, lastSignature = -1, coveredVideoEnd = -1;
        long validationSpans = 0;
        long validationBudget = Math.Max(256L * 1024 * 1024, identity.Length * 4);
        int carry = 0, signatures = 0;
        string status = "complete_within_profile";
        bool stopped = false;
        try
        {
            progress?.Report(new("scanning", 0, identity.Length, 0, videoProfile ? "Reading one source pass and validating selected photo/video structures." : "Reading the image and validating JPEG/PNG structures."));
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, carry, BlockBytes);
                if (read == 0) break;
                sourceHash.AppendData(buffer, carry, read);
                int available = carry + read;
                long start = scanned - carry;
                scanned += read;
                for (int at = 0; at <= available - 8; at++)
                {
                    // Skip non-signature bytes with the runtime's vectorized span search.
                    // Keep the final seven bytes for cross-block signatures as before.
                    int next = videoProfile ? buffer.AsSpan(at, available - 7 - at).IndexOfAny(MediaMarkers)
                        : buffer.AsSpan(at, available - 7 - at).IndexOfAny((byte)0x89, (byte)0xff);
                    if (next < 0) break;
                    at += next;
                    byte b = buffer[at];
                    bool legacyMovie = videoProfile && types!.Contains("mov") && VideoCarver.IsLegacyIsoStart(buffer.AsSpan(at, 4));
                    bool movie = videoProfile && at >= 4 && (buffer.AsSpan(at, 4).SequenceEqual("ftyp"u8) || legacyMovie);
                    bool avi = videoProfile && types!.Contains("avi") && b == (byte)'R' && at + 12 <= available &&
                        buffer.AsSpan(at, 4).SequenceEqual("RIFF"u8) && buffer.AsSpan(at + 8, 4).SequenceEqual("AVI "u8);
                    long offset = start + at - (movie ? 4 : 0);
                    if (offset <= lastSignature || legacyMovie && offset < coveredVideoEnd) continue;
                    bool png = (types is null || types.Contains("png")) && b == 0x89 && buffer.AsSpan(at, 8).SequenceEqual(PngSignature);
                    bool jpeg = (types is null || types.Contains("jpeg")) && b == 0xff && buffer[at + 1] == 0xd8 && buffer[at + 2] == 0xff && IsJpegInitialMarker(buffer[at + 3]);
                    if (!png && !jpeg && !movie && !avi) continue;
                    lastSignature = offset;
                    if (++signatures > options.MaximumCandidates)
                    {
                        diagnostics.Add("Signature validation limit reached; later image regions were not examined.");
                        status = "partial_signature_limit";
                        stopped = true;
                        break;
                    }
                    if (movie || avi)
                    {
                        // Video assessment uses random access and leaves the streaming source cursor intact.
                        RecoveryCandidate? video = VideoCarver.AssessAt(stream, offset, options.MaximumFileBytes, token);
                        if (video is null)
                        {
                            if (!legacyMovie && movie) coveredVideoEnd = Math.Max(coveredVideoEnd,
                                VideoCarver.BrandedExtentEnd(stream, offset, options.MaximumFileBytes, token));
                            continue;
                        }
                        coveredVideoEnd = Math.Max(coveredVideoEnd, offset + video.Length);
                        validationSpans = checked(validationSpans + 2 * video.Length);
                        if (validationSpans > validationBudget)
                        {
                            diagnostics.Add("Overlapping candidate validation budget reached; later source regions were not examined.");
                            status = "partial_validation_budget"; stopped = true; break;
                        }
                        if (!types!.Contains(video.Kind) || video.State == "partial" && !options.IncludeFragments) continue;
                        long videoLength = video.State == "partial" ? Math.Min(video.Length, options.MaximumFragmentBytes) : video.Length;
                        if (videoLength != video.Length)
                        {
                            validationSpans = checked(validationSpans + videoLength);
                            video = video with { Length = videoLength, Id = $"{video.Kind}-{offset:x16}-{videoLength:x}",
                                Sha256 = HashRange(stream, offset, videoLength, token),
                                Diagnostics = [..video.Diagnostics, "Partial fragment capped by the recovery profile; bytes were not repaired or padded."] };
                        }
                        candidates.Add(video);
                        continue;
                    }
                    ParseResult parsed = png ? ParsePng(stream, offset, options.MaximumFileBytes, token) : ParseJpeg(stream, offset, options.MaximumFileBytes, token);
                    validationSpans = checked(validationSpans + parsed.Length);
                    if (validationSpans > validationBudget)
                    {
                        diagnostics.Add("Overlapping candidate validation budget reached; later image regions were not examined.");
                        status = "partial_validation_budget";
                        stopped = true;
                        break;
                    }
                    if (!parsed.Plausible || parsed.Length <= 0 || (parsed.State == "partial" && !options.IncludeFragments)) continue;
                    long length = parsed.State == "partial" ? Math.Min(parsed.Length, options.MaximumFragmentBytes) : parsed.Length;
                    validationSpans = checked(validationSpans + length);
                    var notes = new List<string>(parsed.Diagnostics);
                    if (length < parsed.Length) notes.Add("Partial fragment capped by the recovery profile; bytes were not repaired or padded.");
                    string hash = HashRange(stream, offset, length, token);
                    string kind = png ? "png" : "jpeg";
                    string id = $"{kind}-{offset:x16}-{length:x}";
                    candidates.Add(new()
                    {
                        Id = id, Kind = kind, Offset = offset, Length = length, Sha256 = hash, State = parsed.State,
                        Validation = parsed.Validation, Name = $"{kind}_{offset:x16}.{(png ? "png" : "jpg")}", Diagnostics = notes
                    });
                }
                progress?.Report(new("scanning", scanned, identity.Length, candidates.Count, "Contiguous media structures; deletion status remains unknown."));
                if (stopped) break;
                carry = Math.Min(videoProfile ? 11 : 7, available);
                buffer.AsSpan(available - carry, carry).CopyTo(buffer);
            }
        }
        catch (OperationCanceledException)
        {
            status = "cancelled_partial";
            diagnostics.Add("Scan cancelled. Only fully hashed candidates already validated are retained; the source hash is unavailable.");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        PathSafety.VerifyIdentity(identity, PathSafety.Identity(source, stream));
        string sourceSha = status == "complete_within_profile" ? Convert.ToHexString(sourceHash.GetHashAndReset()).ToLowerInvariant() : "";
        progress?.Report(new(status, scanned, identity.Length, candidates.Count, "Scan finished."));
        return new()
        {
            Source = identity with { Sha256 = sourceSha }, Engine = videoProfile ? "native-media-carver" : "native-photo-carver", Status = status, ScannedBytes = scanned, Candidates = candidates, Diagnostics = diagnostics,
            Limitations = [videoProfile ? "Only selected contiguous JPEG/PNG/MP4/MOV/AVI structures are examined in one source pass. Fragment stitching, codecs and other formats need a specialist engine." : "Only contiguous JPEG and PNG byte sequences are examined by this profile. Fragmented files and other formats need a filesystem or specialist recovery engine.",
                "A format signature does not establish that a file was deleted. Existing embedded images can also be found.",
                "Complete means supported format structure reached its boundary; pixels/frames have not been decoded, playability and authenticity are not established.",
                "Overwritten, TRIM-discarded or encrypted content cannot be reconstructed from missing plaintext bytes.",
                $"Validation is bounded to {options.MaximumCandidates} signatures, {options.MaximumFileBytes} bytes per complete candidate and {options.MaximumFragmentBytes} bytes per partial fragment.",
                $"Overlapping validation and hashing spans are limited to {validationBudget} bytes; reaching this budget produces an explicit partial report."]
        };
    }

    private static bool IsJpegInitialMarker(byte marker) => marker is >= 0xe0 and <= 0xef or 0xdb or 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc4 or 0xfe;

    private sealed record ParseResult(bool Plausible, long Length, string State, string Validation, List<string> Diagnostics);
    private static ParseResult ParsePng(FileStream source, long offset, long maximum, CancellationToken token)
    {
        var reader = new SliceReader(source, offset, Math.Min(maximum, source.Length - offset), token);
        var notes = new List<string>();
        bool plausible = false, idat = false, palette = false, afterIdat = false, damaged = false;
        byte color = 0;
        int chunks = 0;
        long idatBytes = 0;
        string reason = "The PNG stream ends before a complete IEND chunk.";
        try
        {
            Span<byte> signature = stackalloc byte[8];
            reader.ReadExactly(signature);
            if (!signature.SequenceEqual(PngSignature)) return new(false, 0, "", "", []);
            Span<byte> type = stackalloc byte[4];
            Span<byte> header = stackalloc byte[13];
            Span<byte> data = stackalloc byte[4096];
            while (reader.Remaining > 0 && chunks++ < 65536)
            {
                long chunkStart = reader.Position;
                uint length = reader.UInt32();
                reader.ReadExactly(type);
                if (length > int.MaxValue || !Letters(type) || (type[2] & 32) != 0) { reason = "Invalid PNG chunk header; subsequent bytes are a bounded fragment."; break; }
                string chunk = Encoding.ASCII.GetString(type);
                if (chunks == 1 && (chunk != "IHDR" || length != 13)) return new(false, 0, "", "", []);
                uint crc = UpdateCrc(uint.MaxValue, type);
                if (chunk == "IHDR")
                {
                    if (chunks != 1 || length != 13) { reason = "Duplicate or invalid PNG IHDR chunk."; break; }
                    reader.ReadExactly(header);
                    crc = UpdateCrc(crc, header);
                    uint width = BinaryPrimitives.ReadUInt32BigEndian(header), height = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
                    color = header[9];
                    bool valid = width is > 0 and <= 0x7fffffff && height is > 0 and <= 0x7fffffff && ValidPngDepth(header[8], color) && header[10] == 0 && header[11] == 0 && header[12] <= 1;
                    if (!valid) return new(false, 0, "", "", []);
                    plausible = true;
                }
                else
                {
                    if (chunk == "IDAT")
                    {
                        if (afterIdat) { damaged = true; notes.Add("PNG IDAT chunks are not consecutive."); }
                        idat = true;
                        idatBytes += length;
                    }
                    else if (idat && chunk != "IEND") afterIdat = true;
                    if (chunk == "PLTE")
                    {
                        if (palette || idat || length is 0 or > 768 || length % 3 != 0 || color is 0 or 4) { damaged = true; notes.Add("Invalid PNG palette ordering or length."); }
                        palette = true;
                    }
                    if ((type[0] & 32) == 0 && chunk is not ("PLTE" or "IDAT" or "IEND")) { damaged = true; notes.Add("An unsupported critical PNG chunk is present."); }
                    long left = length;
                    while (left > 0)
                    {
                        int n = (int)Math.Min(left, data.Length);
                        reader.ReadExactly(data[..n]);
                        crc = UpdateCrc(crc, data[..n]);
                        left -= n;
                    }
                }
                uint recorded = reader.UInt32();
                if ((crc ^ uint.MaxValue) != recorded) { damaged = true; if (notes.Count < 16) notes.Add($"PNG CRC mismatch in {chunk} at relative byte {chunkStart}."); }
                if (chunk == "IEND")
                {
                    if (length != 0 || !idat || idatBytes == 0 || (color == 3 && !palette)) { damaged = true; notes.Add("PNG termination, IDAT or palette structure is invalid."); }
                    return new(plausible, reader.Position, damaged ? "damaged" : "complete", "PNG chunk structure and CRCs examined through IEND; pixel decoding was not performed.", notes);
                }
            }
            if (chunks >= 65536) reason = "PNG chunk-count validation limit reached.";
        }
        catch (EndOfStreamException) { }
        notes.Add(reason);
        if (reader.Length == maximum && reader.Length < source.Length - offset) notes.Add("Candidate reached the maximum validation length.");
        return new(plausible, Math.Min(reader.Length, Math.Max(reader.Position, 33)), "partial", "Plausible PNG header; stream incomplete or malformed. Exact original bytes are retained.", notes);
    }

    private static bool Letters(ReadOnlySpan<byte> value)
    {
        foreach (byte b in value) if (b is not (>= (byte)'a' and <= (byte)'z') and not (>= (byte)'A' and <= (byte)'Z')) return false;
        return true;
    }
    private static bool ValidPngDepth(byte depth, byte color) => color switch { 0 => depth is 1 or 2 or 4 or 8 or 16, 2 or 4 or 6 => depth is 8 or 16, 3 => depth is 1 or 2 or 4 or 8, _ => false };

    private static ParseResult ParseJpeg(FileStream source, long offset, long maximum, CancellationToken token)
    {
        var reader = new SliceReader(source, offset, Math.Min(maximum, source.Length - offset), token);
        bool frame = false, scan = false, entropy = false, inScan = false;
        int markers = 0, pending = -1;
        string reason = "The JPEG stream ends before a valid end-of-image marker.";
        try
        {
            if (reader.Byte() != 0xff || reader.Byte() != 0xd8) return new(false, 0, "", "", []);
            while (reader.Remaining > 0 && markers++ < 100000)
            {
                int marker;
                if (pending >= 0) { marker = pending; pending = -1; }
                else if (inScan)
                {
                    int b;
                    do { b = reader.Byte(); if (b != 0xff) entropy = true; } while (b != 0xff);
                    do { marker = reader.Byte(); } while (marker == 0xff);
                    if (marker == 0 || marker is >= 0xd0 and <= 0xd7) continue;
                    inScan = false;
                }
                else
                {
                    if (reader.Byte() != 0xff) { reason = "Invalid JPEG marker boundary."; break; }
                    do { marker = reader.Byte(); } while (marker == 0xff);
                }
                if (marker == 0xd9)
                {
                    if (frame && scan && entropy) return new(true, reader.Position, "complete", "JPEG frame, scan marker structure and end-of-image examined; pixels have not been decoded.", []);
                    break;
                }
                if (marker is 0 or 0xd8 or >= 0xd0 and <= 0xd7 || marker == 1) { reason = "Unexpected JPEG standalone marker."; break; }
                int length = reader.UInt16();
                if (length < 2) { reason = "Invalid JPEG marker length."; break; }
                if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
                {
                    if (length < 11) break;
                    int precision = reader.Byte(), height = reader.UInt16(), width = reader.UInt16(), components = reader.Byte();
                    bool validPrecision = marker is 0xc0 ? precision == 8 : marker is 0xc3 or 0xc7 or 0xcb or 0xcf ? precision is >= 2 and <= 16 : precision is 8 or 12;
                    if (!validPrecision || height == 0 || width == 0 || components is < 1 or > 4 || length != 8 + 3 * components) break;
                    reader.Skip(length - 8);
                    frame = true;
                }
                else if (marker == 0xda)
                {
                    int components = reader.Byte();
                    if (!frame || components is < 1 or > 4 || length != 6 + 2 * components) break;
                    reader.Skip(length - 3);
                    scan = true;
                    inScan = true;
                }
                else
                {
                    if (marker is not (>= 0xe0 and <= 0xef or 0xdb or 0xc4 or 0xdd or 0xdc or 0xcc or 0xfe)) { reason = "Unsupported JPEG marker; trailing data retained as a fragment."; break; }
                    reader.Skip(length - 2);
                }
            }
            if (markers >= 100000) reason = "JPEG marker validation limit reached.";
        }
        catch (EndOfStreamException) { }
        var notes = new List<string> { reason };
        if (reader.Length == maximum && reader.Length < source.Length - offset) notes.Add("Candidate reached the maximum validation length.");
        return new(frame && scan, Math.Min(reader.Length, reader.Position), "partial", "Plausible JPEG frame and scan; incomplete or malformed stream. Exact original bytes are retained.", notes);
    }

    internal static string HashRange(FileStream source, long offset, long length, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BlockBytes);
        try
        {
            for (long at = 0; at < length;)
            {
                token.ThrowIfCancellationRequested();
                int count = RandomAccess.Read(source.SafeFileHandle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - at)), offset + at);
                if (count == 0) throw new EndOfStreamException("The source ended during candidate hashing.");
                hash.AppendData(buffer, 0, count);
                at += count;
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private sealed class SliceReader(FileStream source, long offset, long length, CancellationToken token)
    {
        private readonly byte[] buffer = new byte[65536];
        private int available, cursor;
        public long Position { get; private set; }
        public long Length => length;
        public long Remaining => length - Position;
        public byte Byte()
        {
            if (cursor == available)
            {
                token.ThrowIfCancellationRequested();
                if (Remaining <= 0) throw new EndOfStreamException();
                available = RandomAccess.Read(source.SafeFileHandle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, Remaining)), offset + Position);
                cursor = 0;
                if (available == 0) throw new EndOfStreamException();
            }
            Position++;
            return buffer[cursor++];
        }
        public void ReadExactly(Span<byte> data)
        {
            for (int i = 0; i < data.Length; i++) data[i] = Byte();
        }
        public ushort UInt16() => (ushort)((Byte() << 8) | Byte());
        public uint UInt32() => ((uint)Byte() << 24) | ((uint)Byte() << 16) | ((uint)Byte() << 8) | Byte();
        public void Skip(long count)
        {
            if (count < 0 || count > Remaining) { Position = length; cursor = available; throw new EndOfStreamException(); }
            int buffered = Math.Min(available - cursor, (int)Math.Min(int.MaxValue, count));
            cursor += buffered;
            Position += buffered;
            count -= buffered;
            if (count > 0) { Position += count; cursor = available; }
        }
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        return crc;
    }
    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1; table[n] = c; }
        return table;
    }
}
