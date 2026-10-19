using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ReTrace.Core;

/// <summary>Local image paths only. Reparse points and device paths are never followed.</summary>
public static class PathSafety
{
    public static string CheckPath(string path, bool mustExist = true)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new IOException("An absolute local path is required.");
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0)
            throw new IOException("Network paths, device paths and alternate data streams are not accepted.");
        string full = Path.GetFullPath(path);
        string? current = full;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Symbolic links and reparse points are not accepted.");
            }
            catch (FileNotFoundException) when (!mustExist || current != full) { }
            catch (DirectoryNotFoundException) when (!mustExist || current != full) { }
            current = Path.GetDirectoryName(current);
        }
        if (mustExist && !File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException("The source does not exist.", full);
        return full;
    }

    public static string RequireNewDirectory(string destination)
    {
        string full = CheckPath(destination, false);
        if (File.Exists(full) || Directory.Exists(full))
            throw new IOException("Choose a new recovery folder. Existing folders are never reused.");
        string parent = Path.GetDirectoryName(full) ?? throw new IOException("A parent folder is required.");
        CheckPath(parent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The parent recovery folder must exist.");
        return full;
    }

    public static string CreateNewDirectory(string destination)
    {
        string full = RequireNewDirectory(destination);
        if (OperatingSystem.IsWindows())
        {
            if (!CreateDirectory(full, IntPtr.Zero)) throw new IOException("The new recovery directory could not be created exclusively.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
        else
        {
            // The distributed desktop product targets Windows; retain a conservative local test fallback.
            if (Directory.Exists(full) || File.Exists(full)) throw new IOException("The recovery directory already exists.");
            Directory.CreateDirectory(full);
        }
        CheckPath(full);
        return full;
    }

    public static FileStream OpenRead(string source)
    {
        string path = CheckPath(source);
        if (Directory.Exists(path)) throw new IOException("Choose an ordinary image file, not a directory or drive.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var info = GetInformation(stream.SafeFileHandle);
                if ((info.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
                    throw new IOException("Only ordinary image files are accepted.");
                if (info.Links != 1) throw new IOException("Hard-linked image files are not accepted.");
            }
            CheckPath(path);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    public static SourceIdentity Identity(string source, FileStream stream, string sha256 = "")
    {
        string path = CheckPath(source);
        long ticks = File.GetLastWriteTimeUtc(path).Ticks;
        string id = "";
        if (OperatingSystem.IsWindows())
        {
            var info = GetInformation(stream.SafeFileHandle);
            id = $"{info.Volume:x8}:{info.IndexHigh:x8}{info.IndexLow:x8}";
            ticks = DateTime.FromFileTimeUtc(((long)info.WriteHigh << 32) | info.WriteLow).Ticks;
        }
        return new SourceIdentity(path, stream.Length, ticks, sha256) { FileId = id };
    }

    public static void VerifyIdentity(SourceIdentity expected, SourceIdentity actual)
    {
        if (!string.Equals(expected.Path, actual.Path, StringComparison.OrdinalIgnoreCase) || expected.Length != actual.Length ||
            expected.LastWriteUtcTicks != actual.LastWriteUtcTicks || (!string.IsNullOrEmpty(expected.FileId) && expected.FileId != actual.FileId))
            throw new IOException("The source image changed. Scan it again before recovery.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDirectoryW")]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);
    private static FileInformation GetInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("The source file identity could not be verified.", new Win32Exception(Marshal.GetLastWin32Error()));
        return info;
    }
}
