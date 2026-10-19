using System.IO.Compression;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ReTrace.Core;

public sealed record PhotoRecInstallation(string Directory, string ExecutablePath,
    IReadOnlyDictionary<string, string> FileHashes);

/// <summary>The unchanged upstream ZIP is pinned before any executable is extracted.</summary>
public static class PhotoRecBundle
{
    public const string ArchiveName = "testdisk-7.2.win64.zip";
    public const long ArchiveBytes = 27354549;
    public const string ArchiveSha256 = "e97e203ce77b6b1a3a37d01beccf069dc6c4632b579ffbb82ae739cdda229f38";
    public const string UpstreamUrl = "https://www.cgsecurity.org/testdisk-7.2.win64.zip";

    public static async Task<PhotoRecInstallation> EnsureAsync(string workDirectory,
        IProgress<RecoveryProgress>? progress = null, CancellationToken cancellationToken = default,
        string? archivePath = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PhotoRecPaths.RequireDirectory(workDirectory);
        archivePath ??= Path.Combine(AppContext.BaseDirectory, "components", ArchiveName);
        PhotoRecPaths.RequireRegularFile(archivePath);
        using var archiveStream = PathSafety.OpenRead(archivePath);
        if (archiveStream.Length != ArchiveBytes)
            throw new InvalidDataException("The supplied PhotoRec archive has an unexpected size.");
        var hash = await PhotoRecPaths.HashAsync(archiveStream, cancellationToken);
        if (!StringComparer.Ordinal.Equals(hash, ArchiveSha256))
            throw new InvalidDataException("The supplied PhotoRec archive does not match the upstream SHA-256 pin.");
        progress?.Report(new("preparing_engine", ArchiveBytes, ArchiveBytes, 0, "Verified official PhotoRec 7.2 archive."));
        archiveStream.Position = 0;
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count != 45)
            throw new InvalidDataException("The pinned PhotoRec archive has an unexpected entry count.");
        var output = Path.Combine(workDirectory, "tools");
        PhotoRecPaths.CreateNewDirectory(output);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (!name.StartsWith("testdisk-7.2/", StringComparison.Ordinal) ||
                name.Contains('\\') || name.Contains(':') || name.StartsWith('/') ||
                name.Split('/').Any(x => x is "." or "..") || !seen.Add(name.TrimEnd('/')))
                throw new InvalidDataException("An unsafe path occurred in the PhotoRec archive.");
            var target = Path.GetFullPath(Path.Combine(output, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The PhotoRec archive path escapes its owned directory.");
            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                PhotoRecPaths.RequireDirectory(target);
                continue;
            }
            total = checked(total + entry.Length);
            if (entry.Length > 32L * 1024 * 1024 || total > 128L * 1024 * 1024)
                throw new InvalidDataException("The PhotoRec archive exceeds its extraction budget.");
            var parent = Path.GetDirectoryName(target)!;
            if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
            PhotoRecPaths.RequireDirectory(parent);
            await using (var source = entry.Open())
            await using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(destination, 128 * 1024, cancellationToken);
                if (destination.Length != entry.Length)
                    throw new InvalidDataException("PhotoRec extraction produced an unexpected length.");
            }
            await using var verified = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            hashes[Path.GetRelativePath(output, target)] = await PhotoRecPaths.HashAsync(verified, cancellationToken);
        }
        var executable = Path.Combine(output, "testdisk-7.2", "photorec_win.exe");
        PhotoRecPaths.RequireRegularFile(executable);
        return new(output, executable, hashes);
    }

    internal static async Task<IDisposable> LockForExecutionAsync(PhotoRecInstallation installation,
        CancellationToken cancellationToken)
    {
        var streams = new List<FileStream>();
        try
        {
            foreach (var file in installation.FileHashes)
            {
                var path = Path.Combine(installation.Directory, file.Key);
                PhotoRecPaths.RequireRegularFile(path);
                var stream = PathSafety.OpenRead(path);
                streams.Add(stream);
                if (!StringComparer.Ordinal.Equals(await PhotoRecPaths.HashAsync(stream, cancellationToken), file.Value))
                    throw new InvalidDataException("A prepared PhotoRec component changed before execution.");
            }
            return new FileLease(streams);
        }
        catch
        {
            foreach (var stream in streams) stream.Dispose();
            throw;
        }
    }

    private sealed class FileLease(List<FileStream> streams) : IDisposable
    {
        public void Dispose() { foreach (var stream in streams) stream.Dispose(); }
    }
}

internal static class PhotoRecPaths
{
    internal static string RequireAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(x => x is "." or ".."))
            throw new IOException("Choose an absolute local path without devices, links or traversal.");
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && (full.Length < 3 || full[1] != ':' || full[2] != '\\' ||
            full[3..].Contains(':')))
            throw new IOException("Device paths and alternate data streams are not supported.");
        return full;
    }

    private static FileAttributes RequirePath(string path)
    {
        var full = RequireAbsolute(path);
        var cursor = full;
        FileAttributes final = default;
        var first = true;
        while (!string.IsNullOrEmpty(cursor))
        {
            var attributes = File.GetAttributes(cursor);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new IOException("Reparse points and device paths are not supported.");
            if (first) { final = attributes; first = false; }
            else if ((attributes & FileAttributes.Directory) == 0)
                throw new IOException("A path ancestor is not an ordinary directory.");
            var parent = Path.GetDirectoryName(cursor.TrimEnd(Path.DirectorySeparatorChar));
            if (parent == cursor) break;
            cursor = parent;
        }
        return final;
    }

    internal static void RequireDirectory(string path)
    {
        if ((RequirePath(path) & FileAttributes.Directory) == 0)
            throw new IOException("An ordinary directory is required.");
    }

    internal static void RequireRegularFile(string path)
    {
        if ((RequirePath(path) & FileAttributes.Directory) != 0)
            throw new IOException("An ordinary image or component file is required.");
    }

    internal static void CreateNewDirectory(string path)
    {
        var full = RequireAbsolute(path);
        RequireDirectory(Path.GetDirectoryName(full)!);
        if (Path.Exists(full) || File.Exists(full) || Directory.Exists(full))
            throw new IOException("Choose a new recovery directory; existing paths are refused.");
        if (!CreateDirectory(full, IntPtr.Zero))
            throw new IOException("The new recovery directory could not be reserved.", new Win32Exception(Marshal.GetLastWin32Error()));
        RequireDirectory(full);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);

    internal static async Task<string> HashAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
            digest.AppendData(buffer, 0, count);
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }
}
