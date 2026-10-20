using System.Buffers.Binary;
using System.Text;
using ReTrace.Core;

namespace ReTrace.Tests;

internal static class VideoCarverTests
{
    internal static byte[] Mp4(bool mov = false, bool wide = false, bool mediaFirst = true, bool zeroSizedMedia = false,
        byte[]? media = null, bool encrypted = false, bool extendedMedia = false)
    {
        byte[] ftyp = Atom("ftyp", Encoding.ASCII.GetBytes(mov ? "qt  " : "isom"), Be32(0), Encoding.ASCII.GetBytes(mov ? "qt  " : "isommp41"));
        media ??= Enumerable.Repeat((byte)0x55, 128).ToArray();
        byte[] mdat = extendedMedia ? Join(Be32(1), Encoding.ASCII.GetBytes("mdat"), Be64((ulong)media.Length + 16), media) : Atom("mdat", media);
        if (zeroSizedMedia) mdat.AsSpan(0, 4).Clear();
        byte[] Movie(ulong offset)
        {
            byte[] mvhd = new byte[100]; Be32(1000).CopyTo(mvhd, 12);
            byte[] tkhd = new byte[84]; tkhd[3] = 3;
            byte[] mdhd = new byte[24]; Be32(1000).CopyTo(mdhd, 12);
            byte[] hdlr = new byte[24]; Encoding.ASCII.GetBytes("vide").CopyTo(hdlr, 8);
            byte[] sample = new byte[86]; Be32(86).CopyTo(sample, 0); Encoding.ASCII.GetBytes(encrypted ? "encv" : "avc1").CopyTo(sample, 4);
            BinaryPrimitives.WriteUInt16BigEndian(sample.AsSpan(14), 1); BinaryPrimitives.WriteUInt16BigEndian(sample.AsSpan(32), 16); BinaryPrimitives.WriteUInt16BigEndian(sample.AsSpan(34), 16);
            byte[] stsd = Atom("stsd", Be32(0), Be32(1), sample);
            byte[] offsets = Atom(wide ? "co64" : "stco", Be32(0), Be32(1), wide ? Be64(offset) : Be32((uint)offset));
            return Atom("moov", Atom("mvhd", mvhd), Atom("trak", Atom("tkhd", tkhd),
                Atom("mdia", Atom("mdhd", mdhd), Atom("hdlr", hdlr), Atom("minf", Atom("stbl", stsd, offsets)))));
        }
        byte[] moov = Movie(0);
        ulong dataOffset = (ulong)(ftyp.Length + (mediaFirst ? 0 : moov.Length) + (extendedMedia ? 16 : 8));
        moov = Movie(dataOffset);
        return mediaFirst ? Join(ftyp, mdat, moov) : Join(ftyp, moov, mdat);
    }

    internal static byte[] Avi()
    {
        byte[] avih = new byte[56]; Le32(33333).CopyTo(avih, 0); Le32(1).CopyTo(avih, 16); Le32(1).CopyTo(avih, 24); Le32(16).CopyTo(avih, 32); Le32(16).CopyTo(avih, 36);
        byte[] strh = new byte[56]; Encoding.ASCII.GetBytes("vidsMJPG").CopyTo(strh, 0); Le32(1).CopyTo(strh, 20); Le32(30).CopyTo(strh, 24); Le32(1).CopyTo(strh, 32);
        byte[] strf = new byte[40]; Le32(40).CopyTo(strf, 0); Le32(16).CopyTo(strf, 4); Le32(16).CopyTo(strf, 8); BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(12), 1); BinaryPrimitives.WriteUInt16LittleEndian(strf.AsSpan(14), 24); Encoding.ASCII.GetBytes("MJPG").CopyTo(strf, 16);
        byte[] header = List("hdrl", Chunk("avih", avih), List("strl", Chunk("strh", strh), Chunk("strf", strf)));
        byte[] movie = List("movi", Chunk("00dc", [1, 2, 3, 4]));
        byte[] body = Join(Encoding.ASCII.GetBytes("AVI "), header, movie);
        return Join(Encoding.ASCII.GetBytes("RIFF"), Le32((uint)body.Length), body);
    }

    internal static byte[] LegacyMov(bool mediaFirst = true, bool padding = false, bool wideOffsets = false)
    {
        byte[] modern = Mp4(mov: true, mediaFirst: mediaFirst, wide: wideOffsets);
        int removed = checked((int)BinaryPrimitives.ReadUInt32BigEndian(modern));
        byte[] prefix = padding ? Atom("wide") : [];
        byte[] legacy = Join(prefix, modern[removed..]);
        int table = Find(legacy, wideOffsets ? "co64" : "stco") + 12;
        long delta = prefix.Length - removed;
        if (wideOffsets) BinaryPrimitives.WriteUInt64BigEndian(legacy.AsSpan(table), checked((ulong)((long)BinaryPrimitives.ReadUInt64BigEndian(legacy.AsSpan(table)) + delta)));
        else BinaryPrimitives.WriteUInt32BigEndian(legacy.AsSpan(table), checked((uint)((long)BinaryPrimitives.ReadUInt32BigEndian(legacy.AsSpan(table)) + delta)));
        return legacy;
    }

    internal static byte[] Atom(string type, params byte[][] data)
    { byte[] payload = Join(data); return Join(Be32((uint)(8 + payload.Length)), Encoding.ASCII.GetBytes(type), payload); }
    private static byte[] Chunk(string type, byte[] data) => Join(Encoding.ASCII.GetBytes(type), Le32((uint)data.Length), data, data.Length % 2 != 0 ? new byte[1] : []);
    private static byte[] List(string type, params byte[][] data) => Chunk("LIST", Join(Encoding.ASCII.GetBytes(type), Join(data)));
    private static byte[] Be32(uint value) { byte[] result = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(result, value); return result; }
    private static byte[] Be64(ulong value) { byte[] result = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(result, value); return result; }
    private static byte[] Le32(uint value) { byte[] result = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(result, value); return result; }
    private static byte[] Join(params byte[][] parts)
    { using var stream = new MemoryStream(); foreach (byte[] part in parts) stream.Write(part); return stream.ToArray(); }
    private static int Find(byte[] bytes, string text) => bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text));

    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Assert(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        string root = Path.Combine(Path.GetTempPath(), "retrace-video-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string Save(string name, byte[] bytes) { string path = Path.Combine(root, name); File.WriteAllBytes(path, bytes); return path; }
            async Task<RecoveryReport> Scan(string name, byte[] bytes, RecoveryOptions? options = null) => await VideoCarver.ScanAsync(Save(name, bytes), options);
            byte[] mp4 = Mp4(), mov = Mp4(mov: true), avi = Avi();
            foreach (var (kind, bytes) in new[] { ("mp4", mp4), ("mov", mov), ("avi", avi) })
            {
                var report = await Scan(kind + ".img", bytes);
                Assert(report.Engine == "native-video-carver" && report.Status == "complete_within_profile" && report.ScannedBytes == bytes.Length, "Native video reports use the standard bounded report contract.");
                var item = report.Candidates.Single();
                Assert(item.Kind == kind && item.State == "complete" && item.Offset == 0 && item.Length == bytes.Length, "Supported container structures have an exact contiguous byte span.");
                Assert(item.Sha256 == CarverFixtures.Hash(bytes) && report.Source.Sha256 == item.Sha256, "Candidate and full-source hashes cover exact bytes.");
                Assert(!report.Exhaustive && item.DeletionStatus == "unknown" && item.Validation.Contains("playback"), "Container structure does not claim decoding, deletion or exhaustive recovery.");
            }
            Assert((await Scan("wide-offset.img", Mp4(wide: true))).Candidates.Single().State == "complete", "64-bit chunk offsets are read without truncation.");
            Assert((await Scan("extended-mdat.img", Mp4(extendedMedia: true))).Candidates.Single().State == "complete", "Extended-size media atoms retain correct physical bounds.");
            Assert((await Scan("moov-first.img", Mp4(mediaFirst: false))).Candidates.Single().State == "complete", "Fast-start movies validate chunk ranges after walking later media data.");
            foreach (bool mediaFirst in new[] { true, false })
                foreach (bool padding in new[] { true, false })
                    foreach (bool wideOffsets in new[] { true, false })
                    {
                        byte[] legacy = LegacyMov(mediaFirst, padding, wideOffsets);
                        var item = (await Scan($"legacy-{mediaFirst}-{padding}-{wideOffsets}.img", legacy)).Candidates.Single();
                        Assert(item.Kind == "mov" && item.State == "complete" && item.Offset == 0 && item.Length == legacy.Length && item.Sha256 == CarverFixtures.Hash(legacy), "Legacy QuickTime without ftyp preserves supported atom order, wide padding and 32/64-bit chunk mapping.");
                        Assert(item.Diagnostics.Any(x => x.Contains("without an ftyp")) && item.DeletionStatus == "unknown", "Legacy recovery explains its evidence and original-boundary uncertainty.");
                    }
            byte[] legacyFirst = LegacyMov(), legacySecond = LegacyMov(mediaFirst: false);
            var legacyPair = await Scan("neighboring-legacy.img", Join(legacyFirst, legacySecond));
            Assert(legacyPair.Candidates.Count == 2 && legacyPair.Candidates[0].Length == legacyFirst.Length && legacyPair.Candidates[1].Offset == legacyFirst.Length, "Independently bounded neighboring legacy movies are kept separate.");
            byte[] orphanData = Atom("mdat", Encoding.ASCII.GetBytes("moov trak mdia hdlr vide stco not nested movie structures"));
            Assert((await Scan("orphan-legacy-data.img", orphanData)).Candidates.Count == 0, "Ambiguous orphan mdat bytes and handler-like strings are not invented into a legacy movie.");
            byte[] legacyAudio = LegacyMov(); Encoding.ASCII.GetBytes("soun").CopyTo(legacyAudio, Find(legacyAudio, "hdlr") + 12);
            Assert((await Scan("legacy-audio-only.img", legacyAudio)).Candidates.Count == 0, "A brandless audio-only container does not enter the native video profile.");
            byte[] legacyBadAddress = LegacyMov(); BinaryPrimitives.WriteUInt32BigEndian(legacyBadAddress.AsSpan(Find(legacyBadAddress, "stco") + 12), uint.MaxValue);
            Assert((await Scan("legacy-bad-address.img", legacyBadAddress)).Candidates.Single().State == "damaged", "A coherent legacy video with out-of-range sample addresses remains damaged evidence.");
            byte[] legacyTruncated = LegacyMov(mediaFirst: false)[..^20];
            Assert((await Scan("legacy-truncated-media.img", legacyTruncated)).Candidates.Single().State == "partial", "Valid legacy metadata with truncated media retains an honest partial span.");
            Assert((await Scan("legacy-fragments-disabled.img", legacyTruncated, new() { IncludeFragments = false })).Candidates.Count == 0, "Legacy partial candidates honor the fragment option.");
            var legacyCapped = await Scan("legacy-fragment-cap.img", legacyTruncated, new() { MaximumFragmentBytes = 128 });
            Assert(legacyCapped.Candidates.Single().Length == 128 && legacyCapped.Candidates[0].Sha256 == CarverFixtures.Hash(legacyTruncated[..128]), "Legacy partial fragment length and SHA-256 remain bound to retained bytes.");
            var zero = await Scan("zero-size.img", Mp4(mediaFirst: false, zeroSizedMedia: true));
            Assert(zero.Candidates.Single().State == "partial" && zero.Candidates[0].Diagnostics.Any(x => x.Contains("size-zero")), "A size-zero extent is retained with an explicit unresolved original file boundary.");
            var encrypted = await Scan("encrypted-description.img", Mp4(encrypted: true));
            Assert(encrypted.Candidates.Single().Diagnostics.Any(x => x.Contains("Encrypted")), "Encrypted sample descriptions are identified without claiming decryption.");

            var concatenated = await Scan("two-movies.img", Join(mp4, mov));
            Assert(concatenated.Candidates.Count == 2 && concatenated.Candidates[0].Length == mp4.Length && concatenated.Candidates[1].Offset == mp4.Length, "A later ftyp starts a separate movie instead of being consumed into the earlier span.");
            var trailing = await Scan("trailing.img", Join(mp4, new byte[25]));
            Assert(trailing.Candidates.Single().Length == mp4.Length, "Unrecognized adjacent bytes are excluded from a structurally bounded movie.");
            byte[] crossing = new byte[3 * 1024 * 1024 + avi.Length + 128]; int mp4Offset = 1024 * 1024 - 6, aviOffset = 2 * 1024 * 1024 - 2;
            mp4.CopyTo(crossing, mp4Offset); avi.CopyTo(crossing, aviOffset);
            var boundary = await Scan("boundary.img", crossing);
            Assert(boundary.Candidates.Count == 2 && boundary.Candidates[0].Offset == mp4Offset && boundary.Candidates[1].Offset == aviOffset, "Both ftyp and RIFF signatures crossing streaming boundaries are found once.");
            Assert(boundary.Candidates[0].Sha256 == CarverFixtures.Hash(mp4) && boundary.Candidates[1].Sha256 == CarverFixtures.Hash(avi), "Cross-block hashes remain exact.");

            byte[] badOffset = mp4.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(badOffset.AsSpan(Find(badOffset, "stco") + 12), uint.MaxValue);
            var invalidAddress = await Scan("outside-chunk.img", badOffset);
            Assert(invalidAddress.Candidates.Single().State == "damaged" && invalidAddress.Candidates[0].Diagnostics.Any(x => x.Contains("outside")), "A sample chunk outside media data is never labeled structurally complete.");
            byte[] badTable = mp4.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(badTable.AsSpan(Find(badTable, "stco") + 8), uint.MaxValue);
            Assert((await Scan("offset-quota.img", badTable)).Candidates.Single().State == "partial", "Oversized sample tables stop within a quota without allocating from untrusted counts.");
            byte[] badNested = mp4.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(badNested.AsSpan(Find(badNested, "trak") - 4), uint.MaxValue);
            Assert((await Scan("nested-overflow.img", badNested)).Candidates.Single().State != "complete", "A nested movie box cannot escape its parent bounds.");
            byte[] noVideo = mp4.ToArray(); Encoding.ASCII.GetBytes("soun").CopyTo(noVideo, Find(noVideo, "hdlr") + 12);
            Assert((await Scan("audio-only.img", noVideo)).Candidates.Count == 0, "Fully parsed audio-only ISO media is excluded from the video profile.");
            byte[] imageBrand = mp4.ToArray(); Encoding.ASCII.GetBytes("avif").CopyTo(imageBrand, 8);
            Assert((await Scan("image-brand.img", imageBrand)).Candidates.Count == 0, "AVIF/HEIF brands are not treated as video solely because their containers resemble MP4.");
            byte[] bare = Atom("ftyp", Encoding.ASCII.GetBytes("isom"), Be32(0), Encoding.ASCII.GetBytes("mp41"));
            Assert((await Scan("bare-brand.img", bare)).Candidates.Count == 0, "A branded header alone is insufficient evidence of a recoverable movie.");
            byte[] fakeInsideData = Join(bare, Atom("mdat", Encoding.ASCII.GetBytes("moov trak mdia hdlr vide stco not actual nested boxes")), Atom("moov", Atom("free", new byte[20])));
            Assert((await Scan("fake-track-in-data.img", fakeInsideData)).Candidates.Single().State == "partial", "Handler-like bytes in compressed media do not substitute for actual nested video metadata.");
            byte[] overflow = Join(bare, Be32(1), Encoding.ASCII.GetBytes("mdat"), Be64(ulong.MaxValue), new byte[32]);
            Assert((await Scan("extended-overflow.img", overflow)).Candidates.Count == 0, "An impossible 64-bit atom size cannot wrap into a valid candidate range.");

            var truncated = await Scan("truncated-mov.img", mov[..^20]);
            Assert(truncated.Candidates.Single().State == "partial" && truncated.Candidates[0].Length == mov.Length - 20, "A truncated movie preserves only available bytes without padding or repair.");
            Assert((await Scan("fragments-off.img", mov[..^20], new() { IncludeFragments = false })).Candidates.Count == 0, "Partial movie recovery can be disabled.");
            var capped = await Scan("fragment-cap.img", mov[..^20], new() { MaximumFragmentBytes = 128 });
            Assert(capped.Candidates.Single().Length == 128 && capped.Candidates[0].Sha256 == CarverFixtures.Hash(mov[..128]), "Partial movie caps bind the exact retained prefix bytes.");
            var fileBudget = await Scan("file-budget.img", mp4, new() { MaximumFileBytes = 128 });
            Assert(fileBudget.Candidates.Single().State == "partial" && fileBudget.Candidates[0].Length == 128, "The maximum file budget never labels a truncated extent complete.");
            var signatureBudget = await Scan("signature-budget.img", Join(mp4, avi), new() { MaximumCandidates = 1 });
            Assert(signatureBudget.Status == "partial_signature_limit" && signatureBudget.Candidates.Count == 1 && signatureBudget.Source.Sha256 == "", "Signature quotas visibly stop validation and do not claim a full-source hash.");

            byte[] wrongRiff = avi.ToArray(); Encoding.ASCII.GetBytes("WAVE").CopyTo(wrongRiff, 8);
            Assert((await Scan("wave.img", wrongRiff)).Candidates.Count == 0, "Other RIFF forms are not mislabeled AVI.");
            var partialAvi = await Scan("truncated-avi.img", avi[..^2]);
            Assert(partialAvi.Candidates.Single().State == "partial" && partialAvi.Candidates[0].Length == avi.Length - 2, "A truncated AVI frame produces an explicitly partial candidate.");
            byte[] badFormat = avi.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(badFormat.AsSpan(Find(badFormat, "strf") + 8), 4096);
            Assert((await Scan("bad-format.img", badFormat)).Candidates.Single().State != "complete", "A BITMAPINFO header cannot claim more bytes than its parent stream-format chunk.");
            byte[] audioAvi = avi.ToArray(); Encoding.ASCII.GetBytes("auds").CopyTo(audioAvi, Find(audioAvi, "strh") + 8);
            Assert((await Scan("audio-avi.img", audioAvi)).Candidates.Count == 0, "Fully parsed AVI with only audio streams is excluded.");
            byte[] continuationBody = Join(Encoding.ASCII.GetBytes("AVIX"), List("movi", Chunk("00dc", [5, 6, 7, 8])));
            byte[] continuation = Join(Encoding.ASCII.GetBytes("RIFF"), Le32((uint)continuationBody.Length), continuationBody);
            Assert((await Scan("opendml.img", Join(avi, continuation))).Candidates.Single().Length == avi.Length + continuation.Length, "Bounded OpenDML continuations remain in the recovered AVI span.");
            byte[] emptyContinuation = Join(Encoding.ASCII.GetBytes("RIFF"), Le32(4), Encoding.ASCII.GetBytes("AVIX"));
            Assert((await Scan("empty-opendml.img", Join(avi, emptyContinuation))).Candidates.Single().State == "partial", "An OpenDML continuation without movie data is never called complete.");
            byte[] continuationFlood = Join(avi, Join(Enumerable.Repeat(emptyContinuation, 65540).ToArray()));
            var boundedContinuations = await Scan("opendml-quota.img", continuationFlood);
            Assert(boundedContinuations.Candidates.Single().State == "partial" && boundedContinuations.Candidates[0].Length < continuationFlood.Length &&
                boundedContinuations.Candidates[0].Diagnostics.Any(x => x.Contains("quota")), "Even empty RIFF continuations count toward the structural work quota.");
            Assert((await Scan("fragmented-metadata.img", Join(mp4, Atom("moof", [])))).Candidates.Single().State == "partial", "Fragmented addressing remains explicitly unvalidated even with an otherwise complete base movie.");

            string exactPath = Save("assessment.bin", mp4);
            using (var input = PathSafety.OpenRead(exactPath))
            {
                input.Position = 17; var assessed = VideoCarver.AssessAt(input, 0, 1024 * 1024);
                Assert(input.Position == 17 && assessed?.Sha256 == CarverFixtures.Hash(mp4), "Shared media assessment preserves the sequential full-source hashing position.");
            }
            Assert(VideoCarver.AssessFile(exactPath, "mp4")?.State == "complete", "Specialist file assessment shares the exact native structural parser.");
            var recovered = await RecoveryExporter.ExportAsync(await VideoCarver.ScanAsync(exactPath), [(await VideoCarver.ScanAsync(exactPath)).Candidates[0].Id], Path.Combine(root, "export"));
            Assert(recovered.Status == "complete" && File.ReadAllBytes(recovered.Files.Single().Path).SequenceEqual(mp4), "Standard recovery exports native video as exact selected bytes.");
            using var cancel = new CancellationTokenSource();
            byte[] cancelSource = new byte[2 * 1024 * 1024]; mp4.CopyTo(cancelSource, 32);
            var cancelled = await VideoCarver.ScanAsync(Save("cancel.img", cancelSource), progress: new CarverTests.InlineProgress(p => { if (p.BytesDone >= 1024 * 1024) cancel.Cancel(); }), cancellationToken: cancel.Token);
            Assert(cancelled.Status == "cancelled_partial" && cancelled.Source.Sha256 == "", "Cancellation never creates a false full-source hash.");
            Assert(cancelled.Candidates.Count == 1 && cancelled.Candidates[0].Sha256 == CarverFixtures.Hash(mp4), "Cancellation retains only already verified video candidates.");
            bool invalid = false; try { await VideoCarver.ScanAsync(exactPath, new() { MaximumCandidates = 0 }); } catch (ArgumentOutOfRangeException) { invalid = true; }
            Assert(invalid, "Invalid video recovery options are rejected.");
        }
        finally { CarverFixtures.Cleanup(root); }
        return checks;
    }
}
