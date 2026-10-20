using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ReTrace.Core;

namespace ReTrace.Tests;

internal static class LargeImageTests
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input,
        uint inputBytes, IntPtr output, uint outputBytes, out uint returned, IntPtr overlapped);

    private sealed class InlineProgress(Action<RecoveryProgress> callback) : IProgress<RecoveryProgress>
    {
        public void Report(RecoveryProgress value) => callback(value);
    }

    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        int assertions = 0;
        void Assert(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
        string root = Path.Combine(Path.GetTempPath(), "retrace-large-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const long lateOffset = 4L * 1024 * 1024 * 1024 * 1024 + 4096;
            byte[] png = CarverFixtures.Png();
            string image = Path.Combine(root, "synthetic-sparse.img");
            using (var output = new FileStream(image, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                // Mark this owned, new file sparse BEFORE increasing its logical length.
                // A failed sparse request must never fall back to allocating terabytes.
                if (!DeviceIoControl(output.SafeFileHandle, 0x900c4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    throw new IOException("The large-image fixture requires a sparse-capable Windows test filesystem.");
                output.SetLength(lateOffset + png.Length);
                output.Position = 4096;
                output.Write(png);
                output.Position = lateOffset;
                output.Write(png);
                output.Flush(true);
            }
            Assert(new FileInfo(image).Length > 4L * 1024 * 1024 * 1024 * 1024, "Sparse source exceeds four TiB.");
            Assert(new RecoveryOptions().MaximumSourceBytes == RecoveryLimits.MaximumSourceBytes, "All image profiles share the supported bound.");
            using var cancel = new CancellationTokenSource();
            RecoveryReport prefix = await PhotoCarver.ScanAsync(image, progress: new InlineProgress(value =>
            {
                Assert(value.BytesTotal == new FileInfo(image).Length, "Progress preserves the 64-bit logical source length.");
                if (value.BytesDone >= 1024 * 1024) cancel.Cancel();
            }), cancellationToken: cancel.Token);
            Assert(prefix.Status == "cancelled_partial" && prefix.ScannedBytes <= 2L * 1024 * 1024, "Large sources can stop after bounded real work.");
            Assert(prefix.Source.Sha256 == "" && prefix.Source.Length == lateOffset + png.Length, "A prefix is never labeled as a full large-image hash.");
            Assert(prefix.Candidates.Count == 1 && prefix.Candidates[0].Sha256 == CarverFixtures.Hash(png), "Large-source prefix keeps verified candidates.");

            SourceIdentity identity;
            using (var stream = PathSafety.OpenRead(image)) identity = PathSafety.Identity(image, stream);
            var late = new RecoveryCandidate { Id = "beyond-four-tib", Kind = "png", Offset = lateOffset,
                Length = png.Length, Sha256 = CarverFixtures.Hash(png), State = "complete" };
            var report = new RecoveryReport { Source = identity, Status = "cancelled_partial", Candidates = [late] };
            ExportReport exported = await RecoveryExporter.ExportAsync(report, [late.Id], Path.Combine(root, "export"));
            Assert(exported.Status == "complete" && exported.Files.Count == 1, "Selected recovery supports offsets beyond four TiB.");
            Assert(File.ReadAllBytes(exported.Files[0].Path).SequenceEqual(png), "High-offset export contains only exact fixture bytes.");
            Assert(exported.Files[0].Sha256 == late.Sha256 && new FileInfo(image).Length == identity.Length, "High-offset output verification leaves source bounds intact.");
            bool lowerLimit = false;
            try { await PhotoCarver.ScanAsync(image, new() { MaximumSourceBytes = 2L * 1024 * 1024 * 1024 * 1024 }); }
            catch (IOException) { lowerLimit = true; }
            Assert(lowerLimit, "A caller-selected smaller source budget remains enforced.");
            bool unsupported = false;
            try { await PhotoCarver.ScanAsync(image, new() { MaximumSourceBytes = RecoveryLimits.MaximumSourceBytes + 1 }); }
            catch (ArgumentOutOfRangeException) { unsupported = true; }
            Assert(unsupported, "Source bounds beyond the supported product limit are refused.");
        }
        finally { CarverFixtures.Cleanup(root); }
        return assertions;
    }
}
