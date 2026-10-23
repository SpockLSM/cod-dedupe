using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CoDDedupe.Core;

/// <summary>The few Win32 calls .NET doesn't wrap: hard links and file identity.</summary>
public static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    const uint FileReadAttributes = 0x80, ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000;

    static ByHandleFileInformation Info(string path)
    {
        using var h = CreateFileW(path, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
        if (!GetFileInformationByHandle(h, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
        return info;
    }

    /// <summary>Same key = same data on disk; the names are hard links of one file.</summary>
    public static string FileKey(string path)
    {
        var i = Info(path);
        return $"{i.VolumeSerialNumber:X8}:{i.FileIndexHigh:X8}{i.FileIndexLow:X8}";
    }

    public static string VolumeKey(string path) => Info(path).VolumeSerialNumber.ToString("X8");

    public static uint LinkCount(string path) => Info(path).NumberOfLinks;

    public static void CreateHardLink(string newName, string existing)
    {
        if (!CreateHardLinkW(newName, existing, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"link {newName} -> {existing}");
    }

    public static void Delete(string path)
    {
        var a = File.GetAttributes(path);
        if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    const int Chunk = 8 << 20;
    [ThreadStatic] static byte[]? _bufA, _bufB;

    /// <summary>Byte-for-byte compare. Fails if another program has either file open for writing.</summary>
    public static bool ContentEqual(string a, string b)
    {
        _bufA ??= new byte[Chunk];
        _bufB ??= new byte[Chunk];
        using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan);
        using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan);
        if (fa.Length != fb.Length) return false;
        while (true)
        {
            int na = ReadFull(fa, _bufA), nb = ReadFull(fb, _bufB);
            if (na != nb) return false;
            if (na == 0) return true;
            if (!_bufA.AsSpan(0, na).SequenceEqual(_bufB.AsSpan(0, nb))) return false;
        }
    }

    static int ReadFull(Stream s, byte[] buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = s.Read(buf, total, buf.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
