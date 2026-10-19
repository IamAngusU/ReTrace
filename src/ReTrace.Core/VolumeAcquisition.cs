using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace ReTrace.Core;

public sealed record VolumeInfo(string Id, string Label, string FileSystem, long Length, bool IsSystem, string? PhysicalDevice, string Status);
public sealed record CaptureReport(string Schema, string SourceId, string Destination, string Status, long BytesCopied, long TotalBytes, string Sha256, string Consistency, List<string> Diagnostics, bool OutputVerified = false);

/// <summary>Explicit read-only volume acquisition. No locking, dismounting, TRIM, repair or source writes.</summary>
public static class VolumeAcquisition
{
    private const uint GenericRead = 0x80000000, OpenExisting = 3;
    private const uint VolumeExtents = 0x560000, StorageQuery = 0x2d1400, DiskLength = 0x7405c, AllowExtendedRead = 0x90083;
    private const int BlockBytes = 4 * 1024 * 1024;
    private const long MaximumVolumeBytes = 2L * 1024 * 1024 * 1024 * 1024;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputBytes, byte[] output, uint outputBytes, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryDeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputBytes, byte[] output, uint outputBytes, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string path, StringBuilder volumePath, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, uint length);

    public static string NormalizeVolumeId(string value)
    {
        if (value is null || !Regex.IsMatch(value, "^[A-Za-z]:\\\\?$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Ein lokales Laufwerk wie F: auswählen.");
        return char.ToUpperInvariant(value[0]) + ":";
    }

    public static IReadOnlyList<VolumeInfo> ListVolumes()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var rows = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            var id = drive.Name[..2].ToUpperInvariant();
            try
            {
                if (!drive.IsReady) continue;
                rows.Add(new(id, drive.VolumeLabel, drive.DriveFormat, drive.TotalSize,
                    string.Equals(id, Path.GetPathRoot(Environment.SystemDirectory)?[..2], StringComparison.OrdinalIgnoreCase),
                    PhysicalDeviceForPath(drive.RootDirectory.FullName), "ready"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                rows.Add(new(id, "Nicht lesbar", "unknown", 0, false, null, "unavailable"));
            }
        }
        return rows;
    }

    /// <summary>Metadata query only. Returns null when the physical backing is unresolved.</summary>
    public static string? PhysicalDeviceForPath(string existingPath)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var mount = new StringBuilder(1024);
        if (!GetVolumePathNameW(Path.GetFullPath(existingPath), mount, (uint)mount.Capacity)) return null;
        var volume = new StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPointW(mount.ToString(), volume, (uint)volume.Capacity)) return null;
        using var handle = CreateFileW(volume.ToString().TrimEnd('\\'), 0, 7, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        // Only one direct-disk extent is supported. Do not treat two virtual
        // volumes backed by the same pool/VHD/RAID as independent hardware.
        var extent = new byte[32];
        if (!DeviceIoControl(handle, VolumeExtents, IntPtr.Zero, 0, extent, 32, out var returned, IntPtr.Zero) || returned != 32 || BitConverter.ToUInt32(extent, 0) != 1) return null;
        var descriptor = new byte[4096];
        if (!QueryDeviceIoControl(handle, StorageQuery, new byte[12], 12, descriptor, 4096, out returned, IntPtr.Zero) || returned < 36) return null;
        var bus = BitConverter.ToUInt32(descriptor, 28);
        if (bus is not (3 or 7 or 10 or 11 or 12 or 13 or 17 or 19)) return null;
        return "PhysicalDrive" + BitConverter.ToUInt32(extent, 8);
    }

    private static void CheckOrdinaryAncestors(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || full.Contains('\0')) throw new IOException("Ein lokales Ziel ohne Netzwerkpfad wählen.");
        for (var item = new DirectoryInfo(full); item is not null; item = item.Parent)
        {
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Das Ziel darf keine Verknüpfungen oder Umleitungspunkte verwenden.");
        }
    }

    public static void RequireDifferentPhysicalDevices(string? source, string? destination)
    {
        if (source is null || destination is null) throw new IOException("Die physischen Datenträger konnten nicht eindeutig bestimmt werden. Ein lokales Ziel auf einem anderen Datenträger wählen.");
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) throw new IOException("Das Abbild muss auf einen anderen physischen Datenträger geschrieben werden. Ein anderer Laufwerksbuchstabe auf derselben Platte reicht nicht.");
    }

    public static async Task<CaptureReport> CaptureAsync(string volumeId, string destination,
        IProgress<RecoveryProgress>? progress = null, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Laufwerksabbilder benötigen Windows.");
        var id = NormalizeVolumeId(volumeId);
        var output = PathSafety.CheckPath(destination, false);
        if (File.Exists(output) || Directory.Exists(output) || File.Exists(output + ".retrace.json")) throw new IOException("Ein neues Abbildziel auswählen. Vorhandene Dateien werden nicht ersetzt.");
        var parent = Path.GetDirectoryName(output) ?? throw new IOException("Ein vorhandener Zielordner ist erforderlich.");
        if (!Directory.Exists(parent)) throw new IOException("Ein vorhandener Zielordner ist erforderlich.");
        CheckOrdinaryAncestors(parent);
        var sourceInfo = ListVolumes().SingleOrDefault(v => v.Id == id) ?? throw new IOException("Das ausgewählte Laufwerk ist nicht verfügbar.");
        RequireDifferentPhysicalDevices(sourceInfo.PhysicalDevice, PhysicalDeviceForPath(parent));
        token.ThrowIfCancellationRequested();
        using var handle = CreateFileW("\\\\.\\" + id, GenericRead, 7, IntPtr.Zero, OpenExisting, 0x08000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 5) throw new UnauthorizedAccessException("Das Laufwerk benötigt Leserechte als Administrator. ReTrace über den Administrator-Button erneut öffnen oder ein vorhandenes Abbild auswählen.");
            throw new IOException("Laufwerk konnte nicht schreibgeschützt geöffnet werden (Windows " + error + ").");
        }
        var rawLength = new byte[8];
        if (!DeviceIoControl(handle, DiskLength, IntPtr.Zero, 0, rawLength, 8, out var returned, IntPtr.Zero) || returned != 8)
            throw new IOException("Die genaue Laufwerksgröße konnte nicht gelesen werden.");
        var length = BitConverter.ToInt64(rawLength);
        if (length <= 0 || length > MaximumVolumeBytes || length % 512 != 0) throw new IOException("Dieses Laufwerk liegt außerhalb des Aufnahmeprofils (maximal 2 TiB; 512-Byte-Ausrichtung).");
        // This read-only handle still enforces the driver partition boundary.
        // NTFS normally hides trailing partition sectors from ordinary reads.
        if (!DeviceIoControl(handle, AllowExtendedRead, IntPtr.Zero, 0, [], 0, out _, IntPtr.Zero))
            throw new IOException("Der vollständige schreibgeschützte Partitionszugriff wurde nicht freigegeben. Ein vorhandenes Abbild verwenden oder Administratorrechte prüfen.");
        var targetDrive = new DriveInfo(Path.GetPathRoot(parent)!);
        if (targetDrive.AvailableFreeSpace < length + Math.Min(1024L * 1024 * 1024, length / 100)) throw new IOException("Auf dem Zieldatenträger fehlt Platz für das vollständige Abbild und den Beleg.");
        using var input = new FileStream(handle, FileAccess.Read, BlockBytes, isAsync: false);
        return await CaptureStreamAsync(input, length, id, output, progress, token).ConfigureAwait(false);
    }

    /// <summary>Shared capture loop, also exercised with synthetic streams. Does not own the input.</summary>
    public static async Task<CaptureReport> CaptureStreamAsync(Stream input, long length, string sourceId, string destination,
        IProgress<RecoveryProgress>? progress = null, CancellationToken token = default)
    {
        if (length <= 0 || length > MaximumVolumeBytes) throw new ArgumentOutOfRangeException(nameof(length));
        var output = PathSafety.CheckPath(destination, false);
        var parent = Path.GetDirectoryName(output)!;
        CheckOrdinaryAncestors(parent);
        if (!Directory.Exists(parent) || File.Exists(output) || Directory.Exists(output) || File.Exists(output + ".retrace.json")) throw new IOException("Ein neues Abbild unter einem vorhandenen Zielordner auswählen.");
        var copied = 0L;
        var status = "partial";
        var diagnostics = new List<string>();
        var outputVerified = false;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, BlockBytes, FileOptions.SequentialScan);
            var buffer = new byte[BlockBytes];
            while (copied < length)
            {
                token.ThrowIfCancellationRequested();
                var wanted = (int)Math.Min(buffer.Length, length - copied);
                var read = await input.ReadAsync(buffer.AsMemory(0, wanted), token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Quelle endete vor der angegebenen Größe.");
                await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                copied += read;
                progress?.Report(new("capture", copied, length, 0, "Laufwerk wird nur gelesen; Abbild wird auf dem Zieldatenträger gespeichert."));
            }
            target.Flush(flushToDisk: true);
            status = "copied_unverified";
        }
        catch (OperationCanceledException) { status = "cancelled"; diagnostics.Add("incomplete_image_retained_do_not_treat_missing_ranges_as_zeros"); }
        catch (IOException) { diagnostics.Add("read_or_write_failed_incomplete_image_retained"); }
        var digest = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (status == "copied_unverified")
        {
            try
            {
                using var verified = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var check = PathSafety.OpenRead(output);
                var buffer = new byte[BlockBytes];
                long checkedBytes = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var count = await check.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (count == 0) break;
                    verified.AppendData(buffer, 0, count);
                    checkedBytes += count;
                    progress?.Report(new("verify_capture", checkedBytes, length, 0, "Abbild wird unabhängig erneut gelesen und per SHA-256 geprüft."));
                }
                outputVerified = checkedBytes == length && Convert.ToHexStringLower(verified.GetHashAndReset()) == digest;
                status = outputVerified ? "complete" : "partial";
                if (!outputVerified) diagnostics.Add("output_image_hash_or_length_mismatch");
            }
            catch (OperationCanceledException) { status = "cancelled"; diagnostics.Add("image_copied_but_output_verification_cancelled"); }
            catch (IOException) { status = "partial"; diagnostics.Add("image_copied_but_output_verification_failed"); }
        }
        var report = new CaptureReport("retrace.capture.v1", sourceId, output, status, copied, length, digest,
            "live_read_not_a_filesystem_snapshot", diagnostics, outputVerified);
        if (File.Exists(output))
        {
            using var manifest = new FileStream(output + ".retrace.json", FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(manifest, report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }).ConfigureAwait(false);
        }
        return report;
    }
}
