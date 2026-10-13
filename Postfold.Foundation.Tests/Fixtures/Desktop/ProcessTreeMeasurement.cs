using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Postfold.Foundation.Tests.Fixtures.Desktop;

internal static class ProcessTreeMeasurement
{
    internal static Snapshot Capture()
    {
        int root = Environment.ProcessId;
        if (!OperatingSystem.IsWindows())
        {
            using Process current = Process.GetCurrentProcess();
            return new Snapshot(current.PrivateMemorySize64, current.TotalProcessorTime.TotalSeconds, [root], false);
        }

        using SafeFileHandle snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid)
        {
            return new Snapshot(0, 0, [root], false);
        }

        ProcessEntry entry = new ProcessEntry
        {
            Size = (uint)Marshal.SizeOf<ProcessEntry>()
        };
        Dictionary<int, int> parents = new Dictionary<int, int>();
        bool more = Process32First(snapshot, ref entry);
        while (more)
        {
            parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            more = Process32Next(snapshot, ref entry);
        }

        bool complete = Marshal.GetLastPInvokeError() == 18;
        HashSet<int> tree = [root];
        Queue<int> remaining = new Queue<int>();
        remaining.Enqueue(root);
        while (remaining.TryDequeue(out int parent))
        {
            foreach (KeyValuePair<int, int> child in parents)
            {
                if (child.Value == parent && tree.Add(child.Key))
                {
                    remaining.Enqueue(child.Key);
                }
            }
        }

        long bytes = 0;
        double cpu = 0;
        foreach (int id in tree)
        {
            try
            {
                using Process process = Process.GetProcessById(id);
                bytes += process.PrivateMemorySize64;
                cpu += process.TotalProcessorTime.TotalSeconds;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                complete = false;
            }
        }

        return new Snapshot(bytes, cpu, [.. tree], complete);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);

    internal sealed record Snapshot(long PrivateBytes, double CpuSeconds, int[] ProcessIds, bool Complete);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr HeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? Name;
    }
}
