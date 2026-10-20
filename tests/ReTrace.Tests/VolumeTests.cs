using System.Security.Cryptography;
using System.Text.Json;
using ReTrace.Core;

namespace ReTrace.Tests;

internal static class VolumeTests
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
        void Rejected(Action call) { bool rejected = false; try { call(); } catch (Exception ex) when (ex is IOException or ArgumentException) { rejected = true; } Check(rejected, "Unsafe volume selection was accepted."); }
        Check(VolumeAcquisition.NormalizeVolumeId("f:") == "F:", "Volume ID normalization failed.");
        Check(VolumeAcquisition.NormalizeVolumeId("F:\\") == "F:", "Volume root normalization failed.");
        foreach (var id in new[] { "F:\\path", "\\\\.\\PhysicalDrive0", "F:..", "https://example.com", "" }) Rejected(() => VolumeAcquisition.NormalizeVolumeId(id));
        Rejected(() => VolumeAcquisition.RequireDifferentPhysicalDevices("PhysicalDrive0", "physicaldrive0"));
        Rejected(() => VolumeAcquisition.RequireDifferentPhysicalDevices(null, "PhysicalDrive1"));
        VolumeAcquisition.RequireDifferentPhysicalDevices("PhysicalDrive0", "PhysicalDrive1"); checks++;
        var root = Path.Combine(Path.GetTempPath(), "retrace-volume-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var unsafePath in new[] { "relative.img", Path.Combine(root, "image.img:stream") })
            {
                using var stream = new MemoryStream([1, 2, 3]);
                bool rejected = false;
                try { await VolumeAcquisition.CaptureStreamAsync(stream, 3, "synthetic", unsafePath); } catch (IOException) { rejected = true; }
                Check(rejected, "Relative or alternate-stream image path was accepted.");
            }
            var data = new byte[5 * 1024 * 1024 + 512];
            new Random(17).NextBytes(data);
            var path = Path.Combine(root, "synthetic.img");
            using (var stream = new MemoryStream(data, false))
            {
                var report = await VolumeAcquisition.CaptureStreamAsync(stream, data.Length, "synthetic-stream", path);
                Check(report.Status == "complete" && report.OutputVerified, "Capture was not independently verified.");
                Check(report.BytesCopied == data.Length, "Incorrect capture byte count.");
                Check(report.Sha256 == Convert.ToHexStringLower(SHA256.HashData(data)), "Incorrect capture SHA-256.");
                Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(data), "Capture changed source bytes.");
                Check(File.Exists(path + ".retrace.json"), "Capture manifest missing.");
            }
            using (var stream = new MemoryStream(data, false))
            {
                bool rejected = false;
                try { await VolumeAcquisition.CaptureStreamAsync(stream, data.Length, "synthetic-stream", path); } catch (IOException) { rejected = true; }
                Check(rejected, "Existing capture output was overwritten.");
            }
            var shortPath = Path.Combine(root, "short.img");
            using (var stream = new MemoryStream(data, false))
            {
                var report = await VolumeAcquisition.CaptureStreamAsync(stream, data.Length + 512, "synthetic-short-stream", shortPath);
                Check(report.Status == "partial" && !report.OutputVerified && report.BytesCopied == data.Length, "Short capture was falsely complete.");
            }
            using (var cancellation = new CancellationTokenSource())
            using (var stream = new MemoryStream(data, false))
            {
                var progress = new ImmediateProgress(p => { if (p.Stage == "capture") cancellation.Cancel(); });
                var report = await VolumeAcquisition.CaptureStreamAsync(stream, data.Length, "synthetic-cancel", Path.Combine(root, "cancel.img"), progress, cancellation.Token);
                Check(report.Status == "cancelled" && !report.OutputVerified && report.BytesCopied < data.Length, "Cancelled capture was falsely complete.");
                Check(new FileInfo(report.Destination).Length == report.BytesCopied, "Cancelled output prefix length was not recorded.");
                using var manifest = JsonDocument.Parse(File.ReadAllBytes(report.Destination + ".retrace.json"));
                Check(manifest.RootElement.GetProperty("status").GetString() == "cancelled", "Capture cancellation manifest incorrect.");
            }
        }
        finally { CarverFixtures.Cleanup(root); }
        return checks;
    }
    private sealed class ImmediateProgress(Action<RecoveryProgress> action) : IProgress<RecoveryProgress>
    { public void Report(RecoveryProgress value) => action(value); }
}
