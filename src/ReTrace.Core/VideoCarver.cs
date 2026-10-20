using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ReTrace.Core;

/// <summary>Bounded contiguous ISO-BMFF/AVI structural carving. No decoding, repair or playback claims.</summary>
public static class VideoCarver
{
    private const int BlockBytes = 1024 * 1024, MaximumBoxes = 65536, MaximumOffsets = 262144;
    private static readonly SearchValues<byte> IsoMarkers = SearchValues.Create((ReadOnlySpan<byte>)[(byte)'f', (byte)'m', (byte)'w', (byte)'R']);
    internal static bool IsLegacyIsoStart(ReadOnlySpan<byte> type) => type.SequenceEqual("moov"u8) || type.SequenceEqual("mdat"u8) || type.SequenceEqual("wide"u8);
    internal static bool IsIsoStart(ReadOnlySpan<byte> type) => type.SequenceEqual("ftyp"u8) || IsLegacyIsoStart(type);
    private static readonly HashSet<string> VideoBrands = new(StringComparer.Ordinal)
        { "isom", "iso2", "iso3", "iso4", "iso5", "iso6", "iso7", "iso8", "iso9", "mp41", "mp42", "avc1", "hvc1", "hev1", "dash", "MSNV", "M4V ", "M4VH", "M4VP", "F4V ", "qt  " };
    private static readonly HashSet<string> TopBoxes = new(StringComparer.Ordinal)
        { "ftyp", "moov", "mdat", "free", "skip", "wide", "uuid", "meta", "udta", "pdin", "sidx", "ssix", "prft", "emsg", "moof", "mfra", "styp" };

    public static RecoveryCandidate? AssessFile(string path, string kind, long maximumFileBytes = 64L * 1024 * 1024)
    {
        if (kind is null || kind.ToLowerInvariant() is not ("mp4" or "mov" or "avi")) throw new ArgumentException("MP4, MOV or AVI is required.", nameof(kind));
        using var source = PathSafety.OpenRead(path);
        return AssessAt(source, 0, maximumFileBytes);
    }

    /// <summary>Assesses and hashes one source span without changing FileStream.Position.</summary>
    public static RecoveryCandidate? AssessAt(FileStream source, long offset, long maximumFileBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maximumFileBytes < 128 || maximumFileBytes > 1024L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        if (offset < 0 || offset > source.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        cancellationToken.ThrowIfCancellationRequested();
        long end = offset + Math.Min(maximumFileBytes, source.Length - offset);
        Span<byte> header = stackalloc byte[12];
        if (!Read(source, offset, header, end, cancellationToken)) return null;
        Parsed? parsed = header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("AVI "u8)
            ? ParseAvi(source, offset, end, cancellationToken)
            : IsIsoStart(header[4..8]) ? ParseIso(source, offset, end, cancellationToken) : null;
        if (parsed is null || parsed.Length <= 0) return null;
        string hash = PhotoCarver.HashRange(source, offset, parsed.Length, cancellationToken);
        string id = $"{parsed.Kind}-{offset:x16}-{parsed.Length:x}";
        return new()
        {
            Id = id, Kind = parsed.Kind, Offset = offset, Length = parsed.Length, Sha256 = hash,
            State = parsed.State, Name = $"{parsed.Kind}_{offset:x16}.{parsed.Kind}", Diagnostics = parsed.Notes,
            Validation = "Container structure and available byte range checked. Compressed samples, frame decoding, playback and exact original completeness are not established."
        };
    }

    public static Task<RecoveryReport> ScanAsync(string source, RecoveryOptions? options = null,
        IProgress<RecoveryProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(source, options ?? new(), progress, cancellationToken), CancellationToken.None);

    private static RecoveryReport Scan(string source, RecoveryOptions options, IProgress<RecoveryProgress>? progress, CancellationToken token)
    {
        if (options.MaximumFileBytes < 128 || options.MaximumFileBytes > 1024L * 1024 * 1024 || options.MaximumFragmentBytes < 128 ||
            options.MaximumFragmentBytes > 16L * 1024 * 1024 || options.MaximumCandidates is < 1 or > 100000 ||
            options.MaximumSourceBytes < 1 || options.MaximumSourceBytes > RecoveryLimits.MaximumSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(options));
        using var stream = PathSafety.OpenRead(source);
        var identity = PathSafety.Identity(source, stream);
        if (identity.Length > options.MaximumSourceBytes) throw new IOException("The source exceeds the selected video profile.");
        using var fullHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var candidates = new List<RecoveryCandidate>();
        var notes = new List<string>();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BlockBytes + 16);
        long scanned = 0, lastSignature = -1, candidateSpans = 0, coveredVideoEnd = -1;
        long budget = Math.Max(256L * 1024 * 1024, identity.Length * 4);
        int carry = 0, signatures = 0;
        string status = "complete_within_profile";
        bool stopped = false;
        try
        {
            progress?.Report(new("scanning", 0, identity.Length, 0, "Reading source bytes and validating MP4/MOV/AVI containers."));
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, carry, BlockBytes);
                if (read == 0) break;
                fullHash.AppendData(buffer, carry, read);
                int available = carry + read;
                long start = scanned - carry; scanned += read;
                for (int at = 0; at <= available - 4; at++)
                {
                    int next = buffer.AsSpan(at, available - 3 - at).IndexOfAny(IsoMarkers);
                    if (next < 0) break;
                    at += next;
                    long offset = -1;
                    bool legacy = IsLegacyIsoStart(buffer.AsSpan(at, 4));
                    if (IsIsoStart(buffer.AsSpan(at, 4)) && at >= 4) offset = start + at - 4;
                    else if (buffer.AsSpan(at, 4).SequenceEqual("RIFF"u8) && at + 12 <= available && buffer.AsSpan(at + 8, 4).SequenceEqual("AVI "u8)) offset = start + at;
                    if (offset < 0 || offset <= lastSignature || legacy && offset < coveredVideoEnd) continue;
                    lastSignature = offset;
                    if (++signatures > options.MaximumCandidates) { status = "partial_signature_limit"; notes.Add("Video signature validation limit reached; later regions were not inspected."); stopped = true; break; }
                    var candidate = AssessAt(stream, offset, options.MaximumFileBytes, token);
                    if (candidate is null)
                    {
                        if (!legacy && buffer.AsSpan(at, 4).SequenceEqual("ftyp"u8))
                            coveredVideoEnd = Math.Max(coveredVideoEnd, BrandedExtentEnd(stream, offset, options.MaximumFileBytes, token));
                        continue;
                    }
                    coveredVideoEnd = Math.Max(coveredVideoEnd, offset + candidate.Length);
                    candidateSpans += candidate.Length;
                    if (candidateSpans > budget) { status = "partial_validation_budget"; notes.Add("Overlapping video hashing budget reached; later regions were not inspected."); stopped = true; break; }
                    if (candidate.State == "partial" && !options.IncludeFragments) continue;
                    if (candidate.State == "partial" && candidate.Length > options.MaximumFragmentBytes)
                    {
                        long length = options.MaximumFragmentBytes;
                        candidate = candidate with { Length = length, Id = $"{candidate.Kind}-{offset:x16}-{length:x}",
                            Sha256 = PhotoCarver.HashRange(stream, offset, length, token),
                            Diagnostics = [.. candidate.Diagnostics, "Partial video span capped by the fragment profile. Bytes were not padded or repaired."] };
                    }
                    candidates.Add(candidate);
                }
                progress?.Report(new("scanning", scanned, identity.Length, candidates.Count, "Video container structures; deletion and playability remain unknown."));
                if (stopped) break;
                carry = Math.Min(15, available); buffer.AsSpan(available - carry, carry).CopyTo(buffer);
            }
        }
        catch (OperationCanceledException) { status = "cancelled_partial"; notes.Add("Video scan cancelled. Only already hashed candidates are retained; no full-source hash is claimed."); }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        PathSafety.VerifyIdentity(identity, PathSafety.Identity(source, stream));
        progress?.Report(new(status, scanned, identity.Length, candidates.Count, "Video profile finished without changing source bytes."));
        return new()
        {
            Engine = "native-video-carver", Source = identity with { Sha256 = status == "complete_within_profile" ? Convert.ToHexStringLower(fullHash.GetHashAndReset()) : "" },
            Status = status, ScannedBytes = scanned, Candidates = candidates, Diagnostics = notes,
            Limitations =
            [
                "Only contiguous branded MP4/MOV ISO-BMFF and RIFF AVI are assessed. Legacy unbranded MOV, MKV/WebM, fragmented media addressing and filesystem reconstruction need specialist tools.",
                "Complete means the supported container structures and observed sample-address ranges are bounded, not that frames decode or that the exact original file was recovered.",
                "ISO-BMFF has no universal file terminator. The recorded span ends at supported bounded top-level boxes; unknown adjacent bytes and a later ftyp start are excluded.",
                "Source deletion status and authorship remain unknown. Overwritten, encrypted or TRIM-discarded plaintext cannot be invented.",
                $"Profile: {options.MaximumCandidates} signatures, {options.MaximumFileBytes} bytes per candidate, {options.MaximumFragmentBytes} bytes per partial candidate, {MaximumBoxes} structural boxes and {MaximumOffsets} chunk offsets per assessment; overlapping candidate hash budget {budget} bytes."
            ]
        };
    }

    private sealed record Parsed(string Kind, string State, long Length, List<string> Notes);
    private sealed class Track
    {
        public bool Header, MediaHeader, Handler, Video, SampleDescription, VisualDescription;
        public readonly List<ulong> Offsets = [];
    }
    private sealed class IsoState
    {
        public int Boxes, Offsets, Descriptions;
        public bool Damaged, Partial, MovieHeader, Fragmented;
        public readonly List<Track> Tracks = [];
        public readonly List<(long Start, long End)> Data = [];
        public readonly List<string> Notes = [];
        public void Note(string text) { if (!Notes.Contains(text) && Notes.Count < 32) Notes.Add(text); }
    }
    private readonly record struct Atom(long Start, long End, int Header, string Type, bool ZeroSize);

    internal static long BrandedExtentEnd(FileStream source, long origin, long maximum, CancellationToken token)
    {
        long limit = origin + Math.Min(maximum, source.Length - origin);
        Atom? first = AtomAt(source, origin, limit, true, token);
        if (first is null || first.Value.Type != "ftyp" || first.Value.ZeroSize || first.Value.End > limit ||
            first.Value.End - origin > 4096 || first.Value.End - origin < first.Value.Header + 8 ||
            ((first.Value.End - origin - first.Value.Header) & 3) != 0) return origin;
        long end = first.Value.End;
        for (int count = 0; count < MaximumBoxes && end + 8 <= limit; count++)
        {
            token.ThrowIfCancellationRequested();
            Atom? next = AtomAt(source, end, limit, true, token);
            if (next is null || !TopBoxes.Contains(next.Value.Type) || next.Value.Type == "ftyp") break;
            end = Math.Min(next.Value.End, limit);
            if (next.Value.ZeroSize || next.Value.End > limit) break;
        }
        return end;
    }

    private static Parsed? ParseIso(FileStream source, long origin, long limit, CancellationToken token)
    {
        var state = new IsoState();
        Atom? first = AtomAt(source, origin, limit, true, token);
        if (first is null || first.Value.Type is not ("ftyp" or "moov" or "mdat" or "wide")) return null;
        bool legacy = first.Value.Type != "ftyp";
        string kind = "mov";
        long cursor = origin, end = origin;
        if (!legacy)
        {
            var ftyp = first.Value;
            if (ftyp.ZeroSize || ftyp.End > limit || ftyp.End - origin > 4096 || ftyp.End - origin < ftyp.Header + 8) return null;
            long brandBytes = ftyp.End - origin - ftyp.Header;
            if ((brandBytes & 3) != 0) return null;
            Span<byte> brand = stackalloc byte[4];
            Read(source, origin + ftyp.Header, brand, ftyp.End, token);
            string major = Encoding.ASCII.GetString(brand);
            if (major is "avif" or "avis" or "mif1" or "msf1" or "heic" or "heix" or "hevc" or "hevx" or "M4A " or "M4B " or "M4P ") return null;
            bool recognized = VideoBrands.Contains(major);
            for (long at = origin + ftyp.Header + 8; at + 4 <= ftyp.End; at += 4)
            { Read(source, at, brand, ftyp.End, token); recognized |= VideoBrands.Contains(Encoding.ASCII.GetString(brand)); }
            if (!recognized) return null;
            kind = major == "qt  " ? "mov" : "mp4";
            cursor = end = ftyp.End;
        }
        bool moov = false;
        while (cursor + 8 <= limit)
        {
            token.ThrowIfCancellationRequested();
            if (++state.Boxes > MaximumBoxes) { state.Partial = true; state.Note("Container box quota reached."); break; }
            Atom? value = AtomAt(source, cursor, limit, true, token);
            if (value is null) break;
            var atom = value.Value;
            if (!TopBoxes.Contains(atom.Type) || atom.Type == "ftyp") break;
            if (atom.End > limit)
            {
                state.Partial = true; state.Note("A declared top-level box extends beyond available bytes or the selected file budget.");
                if (atom.Type == "mdat" && cursor + atom.Header < limit) state.Data.Add((cursor + atom.Header - origin, limit - origin));
                end = limit; break;
            }
            if (atom.ZeroSize) { state.Partial = true; state.Note("A size-zero box uses the remaining source as its extent; the original file boundary is not independently established."); }
            if (atom.Type == "moov")
            {
                if (moov) { state.Damaged = true; state.Note("Duplicate movie metadata boxes observed."); }
                moov = true; WalkIso(source, atom.Start + atom.Header, atom.End, 0, "moov", null, state, token);
            }
            else if (atom.Type == "mdat" && atom.End > atom.Start + atom.Header) state.Data.Add((atom.Start + atom.Header - origin, atom.End - origin));
            else if (atom.Type == "moof") state.Fragmented = true;
            end = atom.End; cursor = atom.End;
            if (atom.ZeroSize) break;
            // With no ftyp delimiter, stop at the first independently bounded group
            // that supplies the observed tracks. Do not consume a neighboring movie.
            if (legacy && moov && state.MovieHeader && !state.Damaged && !state.Partial && state.Tracks.Any(x => x.Video) &&
                state.Tracks.All(x => x.Handler && x.Offsets.Count > 0 && x.Offsets.All(offset =>
                    offset <= long.MaxValue && InMediaRange((long)offset, state.Data)))) break;
        }
        var videos = state.Tracks.Where(x => x.Video).ToArray();
        if (legacy)
        {
            // An mdat/moov string alone is ambiguous. Require independent nested movie,
            // video sample-description and addressing evidence before retaining a span.
            if (!moov || !state.MovieHeader || state.Data.Count == 0 || !videos.Any(x =>
                x.Header && x.MediaHeader && x.SampleDescription && x.VisualDescription && x.Offsets.Count > 0)) return null;
            state.Note("Legacy QuickTime-compatible structure without an ftyp atom; exact original brand and any preceding padding are unknown.");
            state.Note("The retained legacy span stops after the required observed movie/media atoms; following optional metadata and other preceding atoms may be outside this candidate.");
        }
        if (moov && state.MovieHeader && state.Tracks.Count > 0 && state.Tracks.All(x => x.Handler && !x.Video) && !state.Damaged && !state.Partial) return null;
        if (state.Data.Count == 0 && videos.Length == 0) return null;
        if (!moov || !state.MovieHeader || videos.Length == 0 || state.Data.Count == 0)
        { state.Partial = true; state.Note("Required movie header, video track metadata or media data is unavailable; this may be a media fragment rather than a complete video."); }
        foreach (var video in videos)
        {
            if (!video.Header || !video.MediaHeader || !video.SampleDescription || !video.VisualDescription || video.Offsets.Count == 0)
            { state.Partial = true; state.Note("A video track lacks supported headers, sample description or chunk addressing."); }
            foreach (ulong offset in video.Offsets)
                if (offset > long.MaxValue || !InMediaRange((long)offset, state.Data))
                { state.Damaged = true; state.Note("A recorded video chunk offset is outside the observed media-data ranges."); break; }
        }
        if (state.Fragmented) { state.Partial = true; state.Note("Fragmented MP4 addressing was observed but not reconstructed or validated by this profile."); }
        return new(kind, state.Partial ? "partial" : state.Damaged ? "damaged" : "complete", end - origin, state.Notes);
    }

    private static Atom? AtomAt(FileStream source, long at, long bound, bool top, CancellationToken token)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!Read(source, at, bytes[..8], bound, token)) return null;
        uint small = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        string type = Encoding.ASCII.GetString(bytes[4..8]);
        ulong size = small;
        int header = 8;
        if (small == 1)
        {
            if (!Read(source, at + 8, bytes[8..16], bound, token)) return null;
            size = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..16]); header = 16;
        }
        if (small == 0) { if (!top) return null; size = (ulong)(source.Length - at); }
        if (size < (ulong)header || size > long.MaxValue || (long)size > long.MaxValue - at) return null;
        return new(at, at + (long)size, header, type, small == 0);
    }

    private static void WalkIso(FileStream source, long start, long end, int depth, string context, Track? track, IsoState state, CancellationToken token)
    {
        if (depth > 16) { state.Partial = true; state.Note("Movie metadata nesting quota reached."); return; }
        Span<byte> header = stackalloc byte[24];
        for (long at = start; at < end;)
        {
            token.ThrowIfCancellationRequested();
            if (++state.Boxes > MaximumBoxes) { state.Partial = true; state.Note("Container box quota reached."); return; }
            Atom? value = AtomAt(source, at, end, false, token);
            if (value is null || value.Value.End > end) { state.Damaged = true; state.Note("A nested movie box has invalid bounds."); return; }
            var atom = value.Value; long payload = at + atom.Header; long length = atom.End - payload;
            if (context == "moov" && atom.Type == "mvhd")
            {
                if (length >= 24 && Read(source, payload, header, atom.End, token) && header[0] is 0 or 1 && length >= (header[0] == 0 ? 100 : 112) &&
                    BinaryPrimitives.ReadUInt32BigEndian(header.Slice(header[0] == 0 ? 12 : 20, 4)) > 0) state.MovieHeader = true;
                else { state.Damaged = true; state.Note("Invalid or unsupported movie header structure."); }
            }
            else if (context == "moov" && atom.Type == "trak")
            {
                if (state.Tracks.Count >= 1024) { state.Partial = true; state.Note("Movie track quota reached."); return; }
                var child = new Track(); state.Tracks.Add(child); WalkIso(source, payload, atom.End, depth + 1, "trak", child, state, token);
            }
            else if (context == "moov" && atom.Type == "mvex") state.Fragmented = true;
            else if (context == "trak" && atom.Type == "tkhd" && track is not null)
            {
                if (length >= 1 && Read(source, payload, header[..1], atom.End, token) && header[0] is 0 or 1 && length >= (header[0] == 0 ? 84 : 96)) track.Header = true;
                else { state.Damaged = true; state.Note("Invalid track header structure."); }
            }
            else if (context == "trak" && atom.Type == "mdia" || context == "mdia" && atom.Type == "minf" || context == "minf" && atom.Type == "stbl")
                WalkIso(source, payload, atom.End, depth + 1, atom.Type, track, state, token);
            else if (context == "mdia" && atom.Type == "mdhd" && track is not null)
            {
                if (length >= 24 && Read(source, payload, header, atom.End, token) && header[0] is 0 or 1 && length >= (header[0] == 0 ? 24 : 36) &&
                    BinaryPrimitives.ReadUInt32BigEndian(header.Slice(header[0] == 0 ? 12 : 20, 4)) > 0) track.MediaHeader = true;
                else { state.Damaged = true; state.Note("Invalid media header structure."); }
            }
            else if (context == "mdia" && atom.Type == "hdlr" && track is not null)
            {
                if (length >= 24 && Read(source, payload, header[..12], atom.End, token) && header[0] == 0)
                { track.Handler = true; track.Video |= header[8..12].SequenceEqual("vide"u8); }
                else { state.Partial = true; state.Note("Unsupported or truncated media handler metadata."); }
            }
            else if (context == "stbl" && atom.Type == "stsd" && track is not null) ParseDescriptions(source, payload, atom.End, track, state, token);
            else if (context == "stbl" && atom.Type is "stco" or "co64" && track is not null) ParseOffsets(source, payload, atom.End, atom.Type == "co64", track, state, token);
            at = atom.End;
        }
    }

    private static void ParseDescriptions(FileStream source, long start, long end, Track track, IsoState state, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[16];
        Span<byte> dimensions = stackalloc byte[4];
        if (!Read(source, start, header[..8], end, token) || header[0] != 0) { state.Damaged = true; state.Note("Invalid sample description header."); return; }
        uint count = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
        if (count > 4096 || count > MaximumBoxes - state.Descriptions) { state.Partial = true; state.Note("Sample description quota reached."); return; }
        state.Descriptions += (int)count;
        long at = start + 8;
        for (int index = 0; index < count; index++)
        {
            if (!Read(source, at, header, end, token)) { state.Damaged = true; state.Note("A sample description entry is truncated."); return; }
            uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (size < 16 || size > end - at) { state.Damaged = true; state.Note("A sample description entry has invalid bounds."); return; }
            if (header[4..8].SequenceEqual("encv"u8)) state.Note("Encrypted video sample descriptions were observed; no keys or decryption are provided.");
            if (size >= 86 && BinaryPrimitives.ReadUInt16BigEndian(header[14..16]) > 0)
            {
                if (Read(source, at + 32, dimensions, at + size, token) && BinaryPrimitives.ReadUInt16BigEndian(dimensions[..2]) > 0 &&
                    BinaryPrimitives.ReadUInt16BigEndian(dimensions[2..]) > 0) track.VisualDescription = true;
            }
            track.SampleDescription = true; at += size;
        }
        if (at != end) { state.Damaged = true; state.Note("Unexpected bytes follow the declared sample descriptions."); }
    }

    private static void ParseOffsets(FileStream source, long start, long end, bool wide, Track track, IsoState state, CancellationToken token)
    {
        Span<byte> data = stackalloc byte[4096];
        if (!Read(source, start, data[..8], end, token) || data[0] != 0) { state.Damaged = true; state.Note("Invalid chunk-offset table header."); return; }
        uint count = BinaryPrimitives.ReadUInt32BigEndian(data[4..8]); int width = wide ? 8 : 4;
        if (count > MaximumOffsets - state.Offsets) { state.Partial = true; state.Note("Chunk-offset table quota reached."); return; }
        if ((long)count * width != end - start - 8) { state.Damaged = true; state.Note("Chunk-offset count exceeds or disagrees with its bounded table."); return; }
        state.Offsets += (int)count;
        for (int index = 0; index < count;)
        {
            int entries = Math.Min(data.Length / width, (int)count - index); int bytes = entries * width;
            if (!Read(source, start + 8 + (long)index * width, data[..bytes], end, token)) { state.Damaged = true; return; }
            for (int local = 0; local < entries; local++) track.Offsets.Add(wide ? BinaryPrimitives.ReadUInt64BigEndian(data.Slice(local * width, width)) : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(local * width, width)));
            index += entries;
        }
    }

    private sealed class AviState
    {
        public int Chunks, Streams, MovieLists;
        public bool MainHeader, MovieData, Damaged, Partial;
        public readonly HashSet<int> VideoStreams = [], VideoFormats = [], DataStreams = [], ParsedHeaders = [];
        public readonly List<string> Notes = [];
        public void Note(string text) { if (!Notes.Contains(text) && Notes.Count < 32) Notes.Add(text); }
    }

    private static Parsed? ParseAvi(FileStream source, long origin, long limit, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[12];
        if (!Read(source, origin, header, limit, token) || !header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("AVI "u8)) return null;
        long declared = 8L + BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        if (declared < 12) return null;
        var state = new AviState();
        long end = origin + Math.Min(declared, limit - origin);
        if (declared > limit - origin) { state.Partial = true; state.Note("RIFF extent exceeds available bytes or the selected file budget."); }
        WalkAvi(source, origin + 12, end, "AVI ", -1, 0, state, token);
        while (end + 12 <= limit && Read(source, end, header, limit, token) && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("AVIX"u8))
        {
            if (++state.Chunks > MaximumBoxes) { state.Partial = true; state.Note("AVI continuation/chunk quota reached."); break; }
            long nextSize = 8L + BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            if (nextSize < 12) { state.Damaged = true; state.Note("Invalid OpenDML continuation size."); break; }
            long nextEnd = end + Math.Min(nextSize, limit - end);
            if (nextSize > limit - end) { state.Partial = true; state.Note("An OpenDML continuation is truncated."); }
            int previousLists = state.MovieLists;
            WalkAvi(source, end + 12, nextEnd, "AVIX", -1, 0, state, token); end = nextEnd;
            if (state.MovieLists == previousLists) { state.Partial = true; state.Note("An OpenDML continuation lacks a supported movie-data list."); }
        }
        if (!state.MainHeader && !state.MovieData) return null;
        if (state.Streams > 0 && state.ParsedHeaders.Count == state.Streams && state.VideoStreams.Count == 0 && !state.Damaged && !state.Partial) return null;
        if (!state.MainHeader || !state.MovieData || !state.VideoStreams.Any(index => state.VideoFormats.Contains(index) && state.DataStreams.Contains(index)))
        { state.Partial = true; state.Note("Required AVI headers, a video stream format or matching movie data are missing."); }
        return new("avi", state.Partial ? "partial" : state.Damaged ? "damaged" : "complete", end - origin, state.Notes);
    }

    private static void WalkAvi(FileStream source, long start, long end, string context, int stream, int depth, AviState state, CancellationToken token)
    {
        if (depth > 16) { state.Partial = true; state.Note("AVI list nesting quota reached."); return; }
        Span<byte> bytes = stackalloc byte[56];
        for (long at = start; at < end;)
        {
            token.ThrowIfCancellationRequested();
            if (++state.Chunks > MaximumBoxes) { state.Partial = true; state.Note("AVI chunk quota reached."); return; }
            if (!Read(source, at, bytes[..8], end, token)) { state.Partial = true; state.Note("AVI chunk header is truncated."); return; }
            string type = Encoding.ASCII.GetString(bytes[..4]); uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]);
            long payload = at + 8; long dataEnd = payload + Math.Min((long)length, end - payload);
            bool truncated = length > end - payload;
            if (truncated) { state.Partial = true; state.Note("An AVI chunk extends beyond its available parent extent."); }
            if (type == "LIST")
            {
                if (!Read(source, payload, bytes[..4], dataEnd, token)) { state.Damaged = true; state.Note("Invalid AVI LIST header."); return; }
                string list = Encoding.ASCII.GetString(bytes[..4]);
                int childStream = stream;
                if (context == "hdrl" && list == "strl")
                {
                    if (!state.MainHeader) { state.Damaged = true; state.Note("An AVI stream list precedes the required main header."); }
                    if (state.Streams >= 100) { state.Partial = true; state.Note("AVI stream quota reached."); return; }
                    childStream = state.Streams++;
                }
                if (list == "movi" && context is "AVI " or "AVIX") { state.MovieData = true; state.MovieLists++; if (!state.MainHeader) { state.Damaged = true; state.Note("Movie data precedes the expected AVI main header."); } }
                if (context is "AVI " or "AVIX" && list is "hdrl" or "movi" || context == "hdrl" && list == "strl" || context is "movi" or "rec " && list == "rec ")
                    WalkAvi(source, payload + 4, dataEnd, list, childStream, depth + 1, state, token);
            }
            else if (context == "hdrl" && type == "avih")
            {
                if (length >= 56 && Read(source, payload, bytes, dataEnd, token) && BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]) > 0 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..28]) is >= 1 and <= 100 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..36]) is >= 1 and <= 65535 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..40]) is >= 1 and <= 65535) state.MainHeader = true;
                else { state.Damaged = true; state.Note("Invalid AVI main header or dimensions."); }
            }
            else if (context == "strl" && type == "strh")
            {
                if (length >= 56 && Read(source, payload, bytes, dataEnd, token))
                {
                    if (!state.ParsedHeaders.Add(stream)) { state.Damaged = true; state.Note("Duplicate AVI stream headers observed."); }
                    if (bytes[..4].SequenceEqual("vids"u8))
                    {
                        state.VideoStreams.Add(stream);
                        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..24]) == 0 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..28]) == 0)
                        { state.Damaged = true; state.Note("Invalid AVI video stream time base."); }
                    }
                }
                else { state.Damaged = true; state.Note("Invalid AVI stream header."); }
            }
            else if (context == "strl" && type == "strf" && state.VideoStreams.Contains(stream))
            {
                if (length >= 40 && Read(source, payload, bytes[..40], dataEnd, token) && BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]) is >= 40 and <= 4096 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes[..4]) <= length &&
                    BinaryPrimitives.ReadInt32LittleEndian(bytes[4..8]) is >= 1 and <= 65535 && Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(bytes[8..12])) is >= 1 and <= 65535 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..14]) == 1) state.VideoFormats.Add(stream);
                else { state.Damaged = true; state.Note("Invalid AVI video format header."); }
            }
            else if (context is "movi" or "rec " && type.Length == 4 && type[0] is >= '0' and <= '9' && type[1] is >= '0' and <= '9' && type[2..] is "dc" or "db" && length > 0)
                state.DataStreams.Add((type[0] - '0') * 10 + type[1] - '0');
            if (truncated) return;
            long padded = dataEnd + (length & 1);
            if (padded > end) { state.Damaged = true; state.Note("AVI word-alignment padding is missing."); return; }
            at = padded;
        }
    }

    private static bool Read(FileStream source, long offset, Span<byte> bytes, long end, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (offset < 0 || offset > end || bytes.Length > end - offset) return false;
        int done = 0;
        while (done < bytes.Length)
        {
            int read = RandomAccess.Read(source.SafeFileHandle, bytes[done..], offset + done);
            if (read == 0) return false;
            done += read; token.ThrowIfCancellationRequested();
        }
        return true;
    }

    private static bool InMediaRange(long offset, List<(long Start, long End)> ranges)
    {
        int first = 0, last = ranges.Count - 1;
        while (first <= last)
        {
            int middle = first + (last - first) / 2;
            if (offset < ranges[middle].Start) last = middle - 1;
            else if (offset >= ranges[middle].End) first = middle + 1;
            else return true;
        }
        return false;
    }
}
