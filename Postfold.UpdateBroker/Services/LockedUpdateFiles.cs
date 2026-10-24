using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Postfold.UpdateBroker.Services;

/// <summary>
/// Windows sharing locks pin each component from the volume root down before opening the next.
/// All locks are inheritable so native consumption remains protected if the broker terminates.
/// </summary>
public sealed class LockedUpdateFiles : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];
    private readonly Dictionary<string, SafeFileHandle> _directories = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<SafeFileHandle> Handles => _handles;

    public void Dispose()
    {
        for (int index = _handles.Count - 1; index >= 0; index--)
        {
            _handles[index].Dispose();
        }
    }

    /// <summary>Retains guards but prevents an ordinary managed child launch from inheriting their ownership.</summary>
    public void PreventInheritance()
    {
        foreach (SafeFileHandle handle in _handles)
        {
            if (!SetHandleInformation(handle, 1, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }

    /// <summary>Rejects aliases/reparse paths and pins a file against write/delete through every alias.</summary>
    public FileStream OpenRead(string path)
    {
        path = CanonicalPath(path);
        PinDirectory(Path.GetDirectoryName(path)!);
        SafeFileHandle handle = Open(path, directory: false);
        _handles.Add(handle);
        return new FileStream(new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false), FileAccess.Read);
    }

    /// <summary>Pins existing directory identities without blocking changes to their ordinary children.</summary>
    public void PinDirectory(string path)
    {
        path = CanonicalPath(path);
        if (_directories.ContainsKey(path))
        {
            return;
        }
        string? parent = Path.GetDirectoryName(path);
        if (parent is not null)
        {
            PinDirectory(parent);
        }
        SafeFileHandle handle = Open(path, directory: true);
        _handles.Add(handle);
        _directories.Add(path, handle);
    }

    /// <summary>
    /// Allows the installer's destination-parent write handle while retaining rename protection.
    /// A pinned child keeps this directory nonempty, preventing in-place reparse conversion under NTFS rules.
    /// Acquire both replacement guards before releasing the stricter initial directory handle.
    /// </summary>
    public void AllowChildRenames(string path)
    {
        path = CanonicalPath(path);
        SafeFileHandle strict = _directories[path];
        SafeFileHandle sentinel = Open(Path.Combine(path, ".postfold-apply-" + Guid.NewGuid().ToString("N") + ".guard"), directory: false, createNew: true);
        _handles.Add(sentinel);
        SafeFileHandle writableParent = Open(path, directory: true, share: 3);
        _handles.Add(writableParent);
        _directories[path] = writableParent;
        _handles.Remove(strict);
        strict.Dispose();
    }

    /// <summary>Checks a trusted digest on the same locked handle that protects later path opens.</summary>
    public FileStream Verify(string path, string sha256, long maximumBytes = 134217728)
    {
        FileStream stream = OpenRead(path);
        try
        {
            if (stream.Length <= 0 || stream.Length > maximumBytes || sha256.Length != 64 || Convert.ToHexString(SHA256.HashData(stream)) != sha256)
            {
                throw new InvalidDataException("Executable or installed identity changed.");
            }
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static string CanonicalPath(string path)
    {
        if (!OperatingSystem.IsWindows() || path.Length < 3 || !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' || path[2] != '\\' || path.Contains('/') || path[2..].Contains(':') ||
            !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase) ||
            (path.Length > 3 && path.Split('\\').Skip(1).Any(component =>
                component.Length == 0 || component.EndsWith('.') || component.EndsWith(' ') ||
                component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)))
        {
            throw new InvalidDataException("Only canonical local Windows paths are allowed.");
        }
        return path;
    }

    private static SafeFileHandle Open(string path, bool directory, uint share = 1, bool createNew = false)
    {
        SecurityAttributes security = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            Inherit = 1
        };
        SafeFileHandle handle = CreateFileW(
            "\\\\?\\" + path,
            // FILE_LIST_DIRECTORY participates in sharing checks; READ_ATTRIBUTES alone does not.
            directory ? 0x81U : 0x80000000U,
            share,
            ref security,
            createNew ? 1U : 3U,
            0x02200000,
            IntPtr.Zero
        );
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (!GetFileInformationByHandle(handle, out FileInformation information) ||
            (information.Attributes & 0x400) != 0 || ((information.Attributes & 0x10) != 0) != directory)
        {
            handle.Dispose();
            throw new InvalidDataException("Reparse points or unexpected path identities are forbidden.");
        }
        return handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr Descriptor;
        internal int Inherit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Access;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Write;
        internal uint Volume;
        internal uint SizeHigh;
        internal uint SizeLow;
        internal uint Links;
        internal uint IndexHigh;
        internal uint IndexLow;
    }
}
